using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Burrow.Tests.Extras.RabbitSetupTests;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Burrow.Tests.RabbitTunnelTests
{
    public class RabbitTunnelForTest : RabbitTunnel
    {
        public RabbitTunnelForTest(IRouteFinder routeFinder, IDurableConnection connection) : base(routeFinder, connection)
        {
        }

        public RabbitTunnelForTest(IConsumerManager consumerManager, IRabbitWatcher watcher, IRouteFinder routeFinder, IDurableConnection connection, ISerializer serializer, ICorrelationIdGenerator correlationIdGenerator, bool setPersistent) 
            : base(consumerManager, watcher, routeFinder, connection, serializer, correlationIdGenerator, setPersistent)
        {
        }

        public static RabbitTunnel CreateTunnel(IModel channel, out IDurableConnection durableConnection, bool isChannelOpen = true)
        {
            if (channel != null)
            {
                channel.IsOpen.Returns(isChannelOpen);
            }

            var routeFinder = Substitute.For<IRouteFinder>();
            routeFinder.FindQueueName<Customer>(null).ReturnsForAnyArgs("Queue");
            durableConnection = Substitute.For<IDurableConnection>();
            //durableConnection.ConnectionFactory.Returns(Substitute.For<ConnectionFactory>());
            var conn = durableConnection;
            durableConnection.When(x => x.Connect()).Do(callInfo => // Because this is a mock objectd
            {
                //Rase connected event
                conn.Connected += Raise.Event<Action>();
                conn.IsConnected.Returns(true);
            });
            durableConnection.CreateChannel().Returns(channel);

            var tunnel = new RabbitTunnelForTest(routeFinder, durableConnection);
            tunnel.OnOpened += () => { };
            tunnel.OnClosed += () => { };
            return tunnel;
        }

        public bool? OnBrokerReceivedMessageIsCall;
        protected override void OnBrokerReceivedMessage(object sender, BasicAckEventArgs args)
        {
            OnBrokerReceivedMessageIsCall = true;
        }

        public ConcurrentDictionary<Guid, Action> SubscribeActions
        {
            get { return _subscribeActions; }
        }

        public List<IModel> CreatedChannels
        {
            get { return _createdChannels; }
        }

        public IConsumerManager ConsumerManager
        {
            get { return _consumerManager; }
        }

        public bool? OnBrokerRejectedMessageIsCall;
        protected override void OnBrokerRejectedMessage(object sender, BasicNackEventArgs args)
        {
            OnBrokerRejectedMessageIsCall = true;
        }


        public bool? OnMessageIsUnroutedIsCall;
        protected override void OnMessageIsUnrouted(object sender, BasicReturnEventArgs args)
        {
            OnMessageIsUnroutedIsCall = true;
        }

        public void TrySubscribeForTest(Action subscription)
        {
            TrySubscribe(subscription);
        }

        public void PublicRaiseConsumerDisconnectedEvent(Subscription subscription)
        {
            RaiseConsumerDisconnectedEvent(subscription);
        }

        public void PublicTryReconnect(IModel disconnectedChannel, Guid id, ShutdownEventArgs eventArgs)
        {
            TryReconnect(disconnectedChannel, id, eventArgs);
        }
    }
}