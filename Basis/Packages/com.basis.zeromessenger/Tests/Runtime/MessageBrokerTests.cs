using System.Threading.Tasks;
using NUnit.Framework;

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
            });

            for (int i = 0; i < 100; i++)
            {
                await broker.PublishAsync(i);
                Assert.That(result, Is.EqualTo(i));
            }

            result = -1;
            subscription.Dispose();

            await broker.PublishAsync(100);
            Assert.That(result, Is.EqualTo(-1));
        }
    }
}