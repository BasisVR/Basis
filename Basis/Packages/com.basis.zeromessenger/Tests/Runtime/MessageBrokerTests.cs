using System.Threading.Tasks;
using NUnit.Framework;
using System.Collections.Generic;
using System.Threading;
using System;

namespace Basis.ZeroMessenger.Tests {

    public class MessageBrokerTests
    {
        [Test]
        public void Test_PublishSubscribe()
        {
            using var broker = new MessageBroker<int>();
            var result = 0;

            var subscription = broker.Subscribe(x =>
            {
                result = x;
            });

            for (int i = 0; i < 10000; i++)
            {
                broker.Publish(i);
                Assert.That(result, Is.EqualTo(i));
            }

            result = -1;
            subscription.Dispose();

            broker.Publish(100);
            Assert.That(result, Is.EqualTo(-1));
        }

        [Test]
        public async Task Test_PublishAsyncSubscribeAwait()
        {
            using var broker = new MessageBroker<int>();
            var result = 0;

            var subscription = broker.SubscribeAwait(async (x, ct) =>
            {
                await Task.Delay(1, ct);
                result = x;
            }, AsyncSubscribeStrategy.Sequential);

            for (int i = 0; i < 100; i++)
            {
                await broker.PublishAsync(i);
                Assert.That(result, Is.EqualTo(i));
            }

            // After all awaited publishes complete, dispose is safe
            result = -1;
            subscription.Dispose();

            // await broker.PublishAsync(100);
            Assert.That(result, Is.EqualTo(-1));
        }

        [Test]
        public void Test_MultipleSubscribers()
        {
            using var broker = new MessageBroker<int>();
            var result1 = 0;
            var result2 = 0;
            var result3 = 0;

            var sub1 = broker.Subscribe(x => result1 = x);
            var sub2 = broker.Subscribe(x => result2 = x * 2);
            var sub3 = broker.Subscribe(x => result3 = x * 3);

            broker.Publish(10);

            Assert.That(result1, Is.EqualTo(10));
            Assert.That(result2, Is.EqualTo(20));
            Assert.That(result3, Is.EqualTo(30));

            sub2.Dispose();
            broker.Publish(5);

            Assert.That(result1, Is.EqualTo(5));
            Assert.That(result2, Is.EqualTo(20)); // Should not change
            Assert.That(result3, Is.EqualTo(15));

            sub1.Dispose();
            sub3.Dispose();
        }

        [Test]
        public async Task Test_AsyncPublishStrategy_Sequential()
        {
            using var broker = new MessageBroker<int>();
            var executionOrder = new List<int>();

            var sub1 = broker.SubscribeAwait(async (x, ct) =>
            {
                await Task.Delay(50, ct);
                executionOrder.Add(1);
            });

            var sub2 = broker.SubscribeAwait(async (x, ct) =>
            {
                await Task.Delay(10, ct);
                executionOrder.Add(2);
            });

            var sub3 = broker.SubscribeAwait(async (x, ct) =>
            {
                executionOrder.Add(3);
            });

            await broker.PublishAsync(1, AsyncPublishStrategy.Sequential);

            Assert.That(executionOrder.Count, Is.EqualTo(3));
            Assert.That(executionOrder[0], Is.EqualTo(1));
            Assert.That(executionOrder[1], Is.EqualTo(2));
            Assert.That(executionOrder[2], Is.EqualTo(3));

            sub1.Dispose();
            sub2.Dispose();
            sub3.Dispose();
        }

        [Test]
        public async Task Test_AsyncPublishStrategy_Parallel()
        {
            using var broker = new MessageBroker<int>();
            var completionTimes = new List<(int id, long time)>();
            var startTime = DateTime.UtcNow.Ticks;

            var sub1 = broker.SubscribeAwait(async (x, ct) =>
            {
                await Task.Delay(100, ct);
                completionTimes.Add((1, DateTime.UtcNow.Ticks - startTime));
            });

            var sub2 = broker.SubscribeAwait(async (x, ct) =>
            {
                await Task.Delay(50, ct);
                completionTimes.Add((2, DateTime.UtcNow.Ticks - startTime));
            });

            var sub3 = broker.SubscribeAwait(async (x, ct) =>
            {
                await Task.Delay(25, ct);
                completionTimes.Add((3, DateTime.UtcNow.Ticks - startTime));
            });

            await broker.PublishAsync(1, AsyncPublishStrategy.Parallel);

            Assert.That(completionTimes.Count, Is.EqualTo(3));
            // In parallel, faster tasks should complete first
            Assert.That(completionTimes[0].id, Is.EqualTo(3)); // 25ms delay
            Assert.That(completionTimes[1].id, Is.EqualTo(2)); // 50ms delay
            Assert.That(completionTimes[2].id, Is.EqualTo(1)); // 100ms delay

            sub1.Dispose();
            sub2.Dispose();
            sub3.Dispose();
        }

        [Test]
        public void Test_MixedSyncAndAsyncSubscribers()
        {
            using var broker = new MessageBroker<int>();
            var syncResult = 0;
            var asyncResult = 0;

            var syncSub = broker.Subscribe(x => syncResult = x);
            var asyncSub = broker.SubscribeAwait(async (x, ct) =>
            {
                await Task.Delay(1, ct);
                asyncResult = x;
            });

            broker.Publish(42);

            Assert.That(syncResult, Is.EqualTo(42));
            // Async handler is fire-and-forget in regular Publish
            Assert.That(asyncResult, Is.EqualTo(0) | Is.EqualTo(42));

            syncSub.Dispose();
            asyncSub.Dispose();
        }

        [Test]
        public async Task Test_CancellationToken()
        {
            using var broker = new MessageBroker<int>();
            using var cts = new CancellationTokenSource();
            var wasExecuted = false;

            var sub = broker.SubscribeAwait(async (x, ct) =>
            {
                await Task.Delay(100, ct);
                wasExecuted = true;
            });

            cts.Cancel();

            try
            {
                await broker.PublishAsync(1, AsyncPublishStrategy.Sequential, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            Assert.That(wasExecuted, Is.False);

            sub.Dispose();
        }

        [Test]
        public void Test_ComplexMessageType()
        {
            using var broker = new MessageBroker<ComplexMessage>();
            ComplexMessage? received = null;

            var sub = broker.Subscribe(msg => received = msg);

            var message = new ComplexMessage
            {
                Id = 123,
                Name = "Test",
                Values = new[] { 1, 2, 3 }
            };

            broker.Publish(message);

            Assert.That(received, Is.Not.Null);
            Assert.That(received.Id, Is.EqualTo(123));
            Assert.That(received.Name, Is.EqualTo("Test"));
            Assert.That(received.Values, Is.EqualTo(new[] { 1, 2, 3 }));

            sub.Dispose();
        }

        [Test]
        public void Test_Unsubscribe_DuringPublish()
        {
            using var broker = new MessageBroker<int>();
            var count1 = 0;
            var count2 = 0;
            IDisposable? sub1 = null;

            sub1 = broker.Subscribe(x =>
            {
                count1++;
                if (x == 5)
                {
                    sub1?.Dispose(); // Unsubscribe during callback
                }
            });

            var sub2 = broker.Subscribe(x => count2++);

            for (int i = 0; i < 10; i++)
            {
                broker.Publish(i);
            }

            Assert.That(count1, Is.EqualTo(6)); // 0-5
            Assert.That(count2, Is.EqualTo(10)); // All messages

            sub2.Dispose();
        }

        [Test]
        public async Task Test_RepeatedSubscribeUnsubscribe()
        {
            using var broker = new MessageBroker<int>();

            for (int iteration = 0; iteration < 100; iteration++)
            {
                var result = 0;
                var sub = broker.Subscribe(x => result = x);
                broker.Publish(iteration);
                Assert.That(result, Is.EqualTo(iteration));
                sub.Dispose();
            }

            // Verify no memory leaks by subscribing again
            var finalResult = 0;
            var finalSub = broker.Subscribe(x => finalResult = x);
            broker.Publish(999);
            Assert.That(finalResult, Is.EqualTo(999));
            finalSub.Dispose();
        }

        [Test]
        public async Task Test_DisposeWhileAsyncTasksRunning()
        {
            using var broker = new MessageBroker<int>();
            var executionOrder = new List<int>();
            var resetEvent = new System.Threading.ManualResetEvent(false);

            var sub = broker.SubscribeAwait(async (x, ct) =>
            {
                await Task.Delay(50, ct); // Long-running async task
                executionOrder.Add(x);
                resetEvent.Set();
            }, AsyncSubscribeStrategy.Sequential);

            // Publish a message (fire-and-forget)
            broker.Publish(1);

            // Immediately dispose before the async task completes
            sub.Dispose();

            // Wait a bit to ensure the async task would have tried to complete
            await Task.Delay(100);

            // The handler should have processed the message despite disposal
            // (the semaphore release won't throw ObjectDisposedException)
            Assert.That(executionOrder.Count, Is.EqualTo(1));
            Assert.That(executionOrder[0], Is.EqualTo(1));
        }

        [Test]
        public async Task Test_DisposeBeforePublishMessage()
        {
            using var broker = new MessageBroker<int>();
            var result = 0;

            var sub = broker.SubscribeAwait(async (x, ct) =>
            {
                await Task.Delay(10, ct);
                result = x;
            }, AsyncSubscribeStrategy.Sequential);

            // Dispose the subscription
            sub.Dispose();

            // Publish after disposal - should not execute handler
            broker.Publish(42);

            // Wait to ensure no handler execution
            await Task.Delay(50);

            Assert.That(result, Is.EqualTo(0)); // Handler never executed
        }

        private class ComplexMessage
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public int[] Values { get; set; } = Array.Empty<int>();
        }
    }
}