using NUnit.Framework;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System;

namespace Basis.ZeroMessenger.Tests
{
    public class HandlerTests
    {
        [Test]
        public void Test_CustomHandlerClass()
        {
            using var broker = new MessageBroker<int>();
            var receivedValues = new List<int>();

            var handler = new CollectingHandler(receivedValues);
            var subscription = broker.Subscribe(handler);

            for (int i = 0; i < 5; i++)
            {
                broker.Publish(i);
            }

            Assert.That(receivedValues.Count, Is.EqualTo(5));
            Assert.That(receivedValues, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));

            subscription.Dispose();
        }

        [Test]
        public async Task Test_CustomAsyncHandlerClass()
        {
            using var broker = new MessageBroker<string>();
            var processedMessages = new List<string>();

            var handler = new ProcessingAsyncHandler(processedMessages);
            var subscription = broker.SubscribeAwait(handler);

            await broker.PublishAsync("Hello");
            await broker.PublishAsync("World");

            Assert.That(processedMessages.Count, Is.EqualTo(2));
            Assert.That(processedMessages[0], Is.EqualTo("PROCESSED: Hello"));
            Assert.That(processedMessages[1], Is.EqualTo("PROCESSED: World"));

            subscription.Dispose();
        }

        [Test]
        public async Task Test_HandlerWithState()
        {
            using var broker = new MessageBroker<int>();
            var statefulHandler = new StatefulHandler();

            var subscription = broker.Subscribe(statefulHandler);

            broker.Publish(5);
            broker.Publish(10);
            broker.Publish(15);

            Assert.That(statefulHandler.Count, Is.EqualTo(3));
            Assert.That(statefulHandler.Sum, Is.EqualTo(30));
            Assert.That(statefulHandler.Average, Is.EqualTo(10.0).Within(0.001));

            subscription.Dispose();
        }

        [Test]
        public async Task Test_AsyncHandlerWithCancellation()
        {
            using var broker = new MessageBroker<int>();
            using var cts = new CancellationTokenSource();
            var handler = new CancellableAsyncHandler(cts.Token);

            var subscription = broker.SubscribeAwait(handler);

            await broker.PublishAsync(1);
            Assert.That(handler.ProcessedCount, Is.EqualTo(1));

            cts.Cancel();

            try
            {
                await broker.PublishAsync(2, cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            // Count shouldn't increase after cancellation
            Assert.That(handler.ProcessedCount, Is.EqualTo(1));

            subscription.Dispose();
        }

        [Test]
        public async Task Test_HandlerWithErrorHandling()
        {
            using var broker = new MessageBroker<int>();
            var errorHandler = new ErrorHandlingHandler();

            var subscription = broker.Subscribe(errorHandler);

            broker.Publish(5);  // Normal
            broker.Publish(0);  // Will throw
            broker.Publish(10); // Normal again

            Assert.That(errorHandler.SuccessCount, Is.EqualTo(2));
            Assert.That(errorHandler.ErrorCount, Is.EqualTo(1));

            subscription.Dispose();
        }

        [Test]
        public async Task Test_MultipleHandlersSameType()
        {
            using var broker = new MessageBroker<int>();
            var list1 = new List<int>();
            var list2 = new List<int>();
            var list3 = new List<int>();

            var sub1 = broker.Subscribe(new CollectingHandler(list1));
            var sub2 = broker.Subscribe(new CollectingHandler(list2));
            var sub3 = broker.Subscribe(new CollectingHandler(list3));

            broker.Publish(42);

            Assert.That(list1.Count, Is.EqualTo(1));
            Assert.That(list2.Count, Is.EqualTo(1));
            Assert.That(list3.Count, Is.EqualTo(1));
            Assert.That(list1[0], Is.EqualTo(42));
            Assert.That(list2[0], Is.EqualTo(42));
            Assert.That(list3[0], Is.EqualTo(42));

            sub1.Dispose();
            sub2.Dispose();
            sub3.Dispose();
        }

        [Test]
        public async Task Test_AsyncHandlerSequentialStrategy()
        {
            using var broker = new MessageBroker<int>();
            var executionLog = new List<string>();
            var handler = new DelayedHandler(executionLog, delayMs: 50);

            var subscription = broker.SubscribeAwait(handler, AsyncSubscribeStrategy.Sequential);

            var task1 = broker.PublishAsync(1, AsyncPublishStrategy.Sequential);
            var task2 = broker.PublishAsync(2, AsyncPublishStrategy.Sequential);

            await task1;
            await task2;

            // With sequential strategy, handler should complete each message before starting next
            Assert.That(executionLog.Count, Is.EqualTo(4));
            Assert.That(executionLog[0], Is.EqualTo("Start: 1"));
            Assert.That(executionLog[1], Is.EqualTo("End: 1"));
            Assert.That(executionLog[2], Is.EqualTo("Start: 2"));
            Assert.That(executionLog[3], Is.EqualTo("End: 2"));

            subscription.Dispose();
        }

        [Test]
        public void Test_HandlerDisposal()
        {
            using var broker = new MessageBroker<int>();
            var callCount = 0;

            var subscription = broker.Subscribe(x => callCount++);

            broker.Publish(1);
            Assert.That(callCount, Is.EqualTo(1));

            subscription.Dispose();

            broker.Publish(2);
            Assert.That(callCount, Is.EqualTo(1)); // Should not increase
        }

        [Test]
        public void Test_HandlerWithComplexMessageType()
        {
            using var broker = new MessageBroker<PlayerEvent>();
            var receivedEvents = new List<PlayerEvent>();
            var handler = new PlayerEventHandler(receivedEvents);

            var subscription = broker.Subscribe(handler);

            broker.Publish(new PlayerEvent
            {
                PlayerId = 1,
                Action = "Jump",
                Timestamp = DateTime.UtcNow
            });

            broker.Publish(new PlayerEvent
            {
                PlayerId = 2,
                Action = "Attack",
                Timestamp = DateTime.UtcNow
            });

            Assert.That(receivedEvents.Count, Is.EqualTo(2));
            Assert.That(receivedEvents[0].PlayerId, Is.EqualTo(1));
            Assert.That(receivedEvents[1].PlayerId, Is.EqualTo(2));

            subscription.Dispose();
        }

        #region Test Handler Implementations

        private class CollectingHandler : MessageHandler<int>
        {
            private readonly List<int> collection;

            public CollectingHandler(List<int> collection)
            {
                this.collection = collection;
            }

            protected override void HandleCore(int message)
            {
                collection.Add(message);
            }
        }

        private class ProcessingAsyncHandler : AsyncMessageHandler<string>
        {
            private readonly List<string> collection;

            public ProcessingAsyncHandler(List<string> collection)
            {
                this.collection = collection;
            }

            protected override async ValueTask HandleAsyncCore(string message, CancellationToken cancellationToken)
            {
                await Task.Delay(10, cancellationToken);
                collection.Add($"PROCESSED: {message}");
            }
        }

        private class StatefulHandler : MessageHandler<int>
        {
            public int Count { get; private set; }
            public int Sum { get; private set; }
            public double Average => Count > 0 ? (double)Sum / Count : 0;

            protected override void HandleCore(int message)
            {
                Count++;
                Sum += message;
            }
        }

        private class CancellableAsyncHandler : AsyncMessageHandler<int>
        {
            private readonly CancellationToken externalToken;
            public int ProcessedCount { get; private set; }

            public CancellableAsyncHandler(CancellationToken externalToken)
            {
                this.externalToken = externalToken;
            }

            protected override async ValueTask HandleAsyncCore(int message, CancellationToken cancellationToken)
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, externalToken);
                await Task.Delay(10, linkedCts.Token);
                ProcessedCount++;
            }
        }

        private class ErrorHandlingHandler : MessageHandler<int>
        {
            public int SuccessCount { get; private set; }
            public int ErrorCount { get; private set; }

            protected override void HandleCore(int message)
            {
                try
                {
                    if (message == 0)
                    {
                        throw new DivideByZeroException("Cannot process zero");
                    }
                    var result = 100 / message;
                    SuccessCount++;
                }
                catch (Exception)
                {
                    ErrorCount++;
                }
            }
        }

        private class DelayedHandler : AsyncMessageHandler<int>
        {
            private readonly List<string> executionLog;
            private readonly int delayMs;

            public DelayedHandler(List<string> executionLog, int delayMs)
            {
                this.executionLog = executionLog;
                this.delayMs = delayMs;
            }

            protected override async ValueTask HandleAsyncCore(int message, CancellationToken cancellationToken)
            {
                executionLog.Add($"Start: {message}");
                await Task.Delay(delayMs, cancellationToken);
                executionLog.Add($"End: {message}");
            }
        }

        private class PlayerEventHandler : MessageHandler<PlayerEvent>
        {
            private readonly List<PlayerEvent> collection;

            public PlayerEventHandler(List<PlayerEvent> collection)
            {
                this.collection = collection;
            }

            protected override void HandleCore(PlayerEvent message)
            {
                collection.Add(message);
            }
        }

        private class PlayerEvent
        {
            public int PlayerId { get; set; }
            public string Action { get; set; }
            public DateTime Timestamp { get; set; }
        }

        #endregion
    }
}
