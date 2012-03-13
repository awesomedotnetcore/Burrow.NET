﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Burrow.Internal;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Burrow
{
    /// <summary>
    /// In RabbitMQ, they call a channel but in the RabbitMQ client, they name it IModel -,-
    /// </summary>
    public class RabbitTunnel : ITunnel, IDisposable
    {
        private readonly IConsumerManager _consumerManager;
        private readonly IRabbitWatcher _watcher;
        private readonly IDurableConnection _connection;
        private readonly ICorrelationIdGenerator _correlationIdGenerator;
        
        private readonly List<IModel> _createdChannels = new List<IModel>();
        private readonly ConcurrentBag<Action> _subscribeActions;
        
        private readonly object _tunnelGate = new object();

        private ISerializer _serializer;
        private IRouteFinder _routeFinder;
        private IModel _publishChannel;
        private bool _setPersistent;
        
        public event Action OnOpened;
        public event Action OnClosed;

        public RabbitTunnel(IRouteFinder routeFinder,
                            IDurableConnection connection)
            : this(new ConsumerManager(Global.DefaultWatcher, new ConsumerErrorHandler(connection.ConnectionFactory, Global.DefaultSerializer, Global.DefaultWatcher), Global.DefaultSerializer, Global.DefaultConsumerBatchSize),
                   Global.DefaultWatcher, 
                   routeFinder, 
                   connection,
                   Global.DefaultSerializer, 
                   Global.DefaultCorrelationIdGenerator,
                   Global.SetDefaultPersistentMode)
        {
        }

        public RabbitTunnel(IConsumerManager consumerManager,
                            IRabbitWatcher watcher,
                            IRouteFinder routeFinder,
                            IDurableConnection connection, 
                            ISerializer serializer, 
                            ICorrelationIdGenerator correlationIdGenerator,
                            bool setPersistent)
        {
            if (consumerManager == null)
            {
                throw new ArgumentNullException("consumerManager");
            }
            if (watcher == null)
            {
                throw new ArgumentNullException("watcher");
            }
            if (serializer == null)
            {
                throw new ArgumentNullException("serializer");
            }
            if (connection == null)
            {
                throw new ArgumentNullException("connection");
            }
            if (routeFinder == null)
            {
                throw new ArgumentNullException("routeFinder");
            }
            if (correlationIdGenerator == null)
            {
                throw new ArgumentNullException("correlationIdGenerator");
            }

            _consumerManager = consumerManager;
            _watcher = watcher;
            _routeFinder = routeFinder;
            _connection = connection;
            _serializer = serializer;
            _correlationIdGenerator = correlationIdGenerator;
            _setPersistent = setPersistent;

            _connection.Connected += OpenTunnel;
            _connection.Disconnected += CloseTunnel;

            CreatePublishChannel();

            _subscribeActions = new ConcurrentBag<Action>();
            
        }

        private void CloseTunnel()
        {
            if (_publishChannel != null)
            {
                _publishChannel.BasicAcks -= OnBrokerReceivedMessage;
                _publishChannel.BasicNacks -= OnBrokerRejectedMessage;
                _publishChannel.BasicReturn -= OnMessageIsUnrouted;
            }
            
            _consumerManager.ClearConsumers();
            
            //NOTE: Sometimes, disposing the channel blocks current thread
            var task = new Task(() => _createdChannels.ForEach(DisposeChannel));
            task.ContinueWith(t => _createdChannels.Clear());
            task.Start();
            
            if (OnClosed != null)
            {
                OnClosed();
            }
        }

        private void OpenTunnel()
        {
            CreatePublishChannel();

            _watcher.DebugFormat("Re-subscribe to queues");
            foreach (var subscription in _subscribeActions)
            {
                TrySubscribe(subscription);
            }
            if (OnOpened != null)
            {
                OnOpened();
            }
        }

        private void CreatePublishChannel()
        {
            _publishChannel = _connection.CreateChannel();
            _createdChannels.Add(_publishChannel);

            _publishChannel.BasicAcks += OnBrokerReceivedMessage;
            _publishChannel.BasicNacks += OnBrokerRejectedMessage;
            _publishChannel.BasicReturn += OnMessageIsUnrouted;
        }

        /// <summary>
        /// Note that for unroutable messages are not considered failures and are both Basic.Return’ed and
        /// Basic.Ack’ed. So, if the "mandatory" or "immediate" are used, the client must also listen for returns
        /// by setting the IModel.BasicReturn handler.
        /// </summary>
        /// <param name="model"></param>
        /// <param name="args"></param>
        protected virtual void OnMessageIsUnrouted(IModel model, RabbitMQ.Client.Events.BasicReturnEventArgs args)
        {
        }

        /// <summary>
        /// If a broker rejects a message via the BasicNacks handler, the publisher may assume that the message
        /// was lost or otherwise undeliverable.
        /// </summary>
        /// <param name="model"></param>
        /// <param name="args"></param>
        protected virtual void OnBrokerRejectedMessage(IModel model, RabbitMQ.Client.Events.BasicNackEventArgs args)
        {
        }

        /// <summary>
        /// Once a broker acknowledges a message via the BasicAcks handler, it has taken responsibility for
        /// keeping the message on disk and on the target queue until some other application retrieves and
        /// acknowledges the message.
        /// </summary>
        /// <param name="model"></param>
        /// <param name="args"></param>
        protected virtual void OnBrokerReceivedMessage(IModel model, RabbitMQ.Client.Events.BasicAckEventArgs args)
        {
        }

        public bool IsOpenning
        {
            get
            {
                return _connection != null && _connection.IsConnected;
            }
        }

        public void Publish<T>(T rabbit)
        {
            Publish(rabbit, _routeFinder.FindRoutingKey<T>());
        }

        public void Publish<T>(T rabbit, string routingKey)
        {
            if (_publishChannel == null || !_publishChannel.IsOpen)
            {
                throw new Exception("Publish failed. No channel to rabbit server established.");
            }

            try
            {
                byte[] msgBody = _serializer.Serialize(rabbit);
                IBasicProperties properties = _publishChannel.CreateBasicProperties();
                properties.SetPersistent(_setPersistent); // false = Transient
                properties.Type = Global.DefaultTypeNameSerializer.Serialize(typeof(T));
                properties.CorrelationId = _correlationIdGenerator.GenerateCorrelationId();

                var exchangeName = _routeFinder.FindExchangeName<T>();
                lock (_tunnelGate)
                {
                    _publishChannel.BasicPublish(exchangeName, routingKey, properties, msgBody);
                }
                _watcher.DebugFormat("Published to {0}, CorrelationId {1}", exchangeName, properties.CorrelationId);
            }
            catch (Exception ex)
            {
                throw new Exception(string.Format("Publish failed: '{0}'", ex.Message), ex);
            }
        }

        public void Subscribe<T>(string subscriptionName, Action<T> onReceiveMessage)
        {
            Func<IModel, string, IBasicConsumer> createConsumer = (channel, consumerTag) => _consumerManager.CreateConsumer(channel, subscriptionName, consumerTag, onReceiveMessage);
            CreateSubscription<T>(subscriptionName, createConsumer);
        }

        public void SubscribeAsync<T>(string subscriptionName, Action<T> onReceiveMessage)
        {
            Func<IModel, string, IBasicConsumer> createConsumer = (channel, consumerTag) => _consumerManager.CreateAsyncConsumer(channel, subscriptionName, consumerTag, onReceiveMessage);
            CreateSubscription<T>(subscriptionName, createConsumer);
        }

        private void CreateSubscription<T>(string subscriptionName, Func<IModel, string, IBasicConsumer> createConsumer)
        {
            Action subscription = () =>
                                      {
                                          var queueName = _routeFinder.FindQueueName<T>(subscriptionName);
                                          var consumerTag = string.Format("{0}-{1}", subscriptionName, Guid.NewGuid());

                                          var channel = _connection.CreateChannel();
                                          channel.BasicQos(0, Global.DefaultConsumerBatchSize, false);

                                          _createdChannels.Add(channel);
                                          var consumer = createConsumer(channel, consumerTag);

                                          //NOTE: The message will still be on the Unacknowledged list until it's processed and the method
                                          //      DoAck is call.
                                          channel.BasicConsume(queueName, false /* noAck, must be false */, consumerTag,
                                                               consumer);
                                      };

            _subscribeActions.Add(subscription);
            TrySubscribe(subscription);
        }

        private void TrySubscribe(Action subscription)
        {
            try
            {
                subscription();
            }
            catch (OperationInterruptedException)
            {
            }
            catch (Exception ex)
            {
                _watcher.Error(ex);
            }
        }

        public void SetRouteFinder(IRouteFinder routeFinder)
        {
            if (routeFinder == null)
            {
                throw new ArgumentNullException("routeFinder");
            }
            _routeFinder = routeFinder;
        }

        public void SetSerializer(ISerializer serializer)
        {
            if (serializer == null)
            {
                throw new ArgumentNullException("serializer");
            }
            _serializer = serializer;
        }

        public void SetPersistentMode(bool persistentMode)
        {
            _setPersistent = persistentMode;
        }

        public void Dispose()
        {
            //NOTE: Sometimes, disposing the channel blocks current thread
            var task = new Task(() => _createdChannels.ForEach(DisposeChannel));
            task.ContinueWith(t => _createdChannels.Clear());
            task.Start();

            if (_connection.IsConnected)
            {
                _connection.Dispose();
            }
            _consumerManager.Dispose();
        }

        private void DisposeChannel(IModel model)
        {
            if (model != null && model.IsOpen)
            {
                try
                {
                    model.Abort(); // To kill all consumer queues
                    model.Dispose();
                }
                catch(System.IO.IOException)
                {
                    //Channel has been closed by remote host
                }
                catch (Exception ex)
                {
                    _watcher.Error(ex);
                }
            }
        }
    }
}