using NUnit.Framework;

namespace Basis.ZeroMessenger.Tests 
{
	
	public class FilterTests
	{
		[Test]
		public void Test_MessgeBrokerAddFilter()
		{
			var broker = new MessageBroker<int>();
			var result = 0;
			broker.AddFilter(new TestMessageFilter<int>(x => x * 2));

			var subscription = broker.Subscribe(x => result = x);

			for (int i = 0; i < 10000; i++)
			{
				broker.Publish(i);
				Assert.That(result, Is.EqualTo(i * 2));
			}

			subscription.Dispose();

			result = -1;
			broker.Publish(100);
			Assert.That(result, Is.EqualTo(-1));
		}

		[Test]
		public void Test_SubscribeWithFilter()
		{
			var broker = new MessageBroker<int>();
			var result = 0;

			var subscription = broker
				.WithFilter(new TestMessageFilter<int>(x => x * 2))
				.Subscribe(x => result = x);

			for (int i = 0; i < 10000; i++)
			{
				broker.Publish(i);
				Assert.That(result, Is.EqualTo(i * 2));
			}

			subscription.Dispose();

			result = -1;
			broker.Publish(100);
			Assert.That(result, Is.EqualTo(-1));
		}

		// [Test]
		// public void Test_DI_Filter()
		// {
		// 	var services = new ServiceCollection();
		// 	services.AddZeroMessenger(x =>
		// 	{
		// 		x.AddFilter(new TestMessageFilter<int>(x => x * 2));
		// 	});
		// 	var serviceProvider = services.BuildServiceProvider();

		// 	var publisher = serviceProvider.GetRequiredService<IMessagePublisher<int>>();
		// 	var subscriber = serviceProvider.GetRequiredService<IMessageSubscriber<int>>();
		// 	var result = 0;

		// 	var subscription = subscriber.Subscribe(x =>
		// 	{
		// 		result = x;
		// 	});

		// 	for (int i = 0; i < 10000; i++)
		// 	{
		// 		publisher.Publish(i);
		// 		Assert.That(result, Is.EqualTo(i * 2));
		// 	}

		// 	result = -1;
		// 	subscription.Dispose();

		// 	publisher.Publish(100);
		// 	Assert.That(result, Is.EqualTo(-1));
		// }

		// [Test]
		// public void Test_DI_Filter_OpenGenerics()
		// {
		// 	var services = new ServiceCollection();
		// 	services.AddZeroMessenger(x =>
		// 	{
		// 		x.AddFilter(typeof(IgnoreMessageFilter<>));
		// 	});
		// 	var serviceProvider = services.BuildServiceProvider();

		// 	var publisher = serviceProvider.GetRequiredService<IMessagePublisher<int>>();
		// 	var subscriber = serviceProvider.GetRequiredService<IMessageSubscriber<int>>();
		// 	var result = -1;

		// 	var subscription = subscriber.Subscribe(x =>
		// 	{
		// 		result = x;
		// 	});

		// 	publisher.Publish(100);
		// 	Assert.That(result, Is.EqualTo(-1));
		// }
	}

}