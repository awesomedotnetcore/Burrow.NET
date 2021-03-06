﻿using Burrow.Extras.Internal;
using NUnit.Framework;

// ReSharper disable InconsistentNaming
namespace Burrow.Tests.Extras.Internal.InMemoryPriorityQueueTests
{
    [TestFixture]
    public class MethodDeleteAll
    {
        [Test]
        public void Should_delete_all_items_match_predicate()
        {
            // Arrange
            var queue = new InMemoryPriorityQueue<GenericPriorityMessage<string>>(20, new PriorityComparer<GenericPriorityMessage<string>>());
            queue.Enqueue(new GenericPriorityMessage<string>("", 1));
            queue.Enqueue(new GenericPriorityMessage<string>("", 1));
            queue.Enqueue(new GenericPriorityMessage<string>("", 1));
            queue.Enqueue(new GenericPriorityMessage<string>("", 1));
            queue.Enqueue(new GenericPriorityMessage<string>("VanTheShark", 1));

            // Action
            queue.DeleteAll(msg => msg.Message == "");


            // Assert
            Assert.AreEqual(1, queue.Count);
        }

    }
}
// ReSharper restore InconsistentNaming