using NUnit.Framework;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System;

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

		[Test]
		public void Test_MultipleFilters_ChainExecution()
		{
			var broker = new MessageBroker<int>();
			var result = 0;

			// Add multiple filters - they should chain
			broker.AddFilter(new TestMessageFilter<int>(x => x + 10)); // First: add 10
			broker.AddFilter(new TestMessageFilter<int>(x => x * 2));  // Second: multiply by 2

			var subscription = broker.Subscribe(x => result = x);

			broker.Publish(5);
			// (5 + 10) * 2 = 30
			Assert.That(result, Is.EqualTo(30));

			subscription.Dispose();
		}

		[Test]
		public void Test_PredicateFilter_BlocksMessages()
		{
			var broker = new MessageBroker<int>();
			var receivedMessages = new List<int>();

			// Only allow even numbers
			broker.AddFilter((int x) => x % 2 == 0);

			var subscription = broker.Subscribe(x => receivedMessages.Add(x));

			for (int i = 0; i < 10; i++)
			{
				broker.Publish(i);
			}

			Assert.That(receivedMessages.Count, Is.EqualTo(5));
			Assert.That(receivedMessages, Is.EqualTo(new[] { 0, 2, 4, 6, 8 }));

			subscription.Dispose();
		}

		[Test]
		public void Test_AnonymousFilter()
		{
			var broker = new MessageBroker<int>();
			var result = 0;
			var filterExecutionCount = 0;

			broker.AddFilter(async (msg, ct, next) =>
			{
				filterExecutionCount++;
				await next(msg * 3, ct);
			});

			var subscription = broker.Subscribe(x => result = x);

			broker.Publish(10);
			Assert.That(result, Is.EqualTo(30));
			Assert.That(filterExecutionCount, Is.EqualTo(1));

			subscription.Dispose();
		}

		[Test]
		public async Task Test_Filter_WithAsyncHandler()
		{
			var broker = new MessageBroker<int>();
			var result = 0;

			broker.AddFilter(new TestMessageFilter<int>(x => x + 100));

			var subscription = broker.SubscribeAwait(async (x, ct) =>
			{
				await Task.Delay(1, ct);
				result = x;
			});

			await broker.PublishAsync(50);
			Assert.That(result, Is.EqualTo(150));

			subscription.Dispose();
		}

		[Test]
		public void Test_FilterWithMultipleSubscribers()
		{
			var broker = new MessageBroker<int>();
			var result1 = 0;
			var result2 = 0;
			var result3 = 0;

			// Global filter applies to all subscribers
			broker.AddFilter(new TestMessageFilter<int>(x => x * 10));

			var sub1 = broker.Subscribe(x => result1 = x);
			var sub2 = broker.Subscribe(x => result2 = x + 1);
			var sub3 = broker.Subscribe(x => result3 = x + 2);

			broker.Publish(5);

			Assert.That(result1, Is.EqualTo(50));
			Assert.That(result2, Is.EqualTo(51));
			Assert.That(result3, Is.EqualTo(52));

			sub1.Dispose();
			sub2.Dispose();
			sub3.Dispose();
		}

		[Test]
		public void Test_PerSubscriberFilter()
		{
			var broker = new MessageBroker<int>();
			var result1 = 0;
			var result2 = 0;

			// Each subscriber has its own filter
			var sub1 = broker
				.WithFilter(new TestMessageFilter<int>(x => x * 2))
				.Subscribe(x => result1 = x);

			var sub2 = broker
				.WithFilter(new TestMessageFilter<int>(x => x * 3))
				.Subscribe(x => result2 = x);

			broker.Publish(10);

			Assert.That(result1, Is.EqualTo(20));
			Assert.That(result2, Is.EqualTo(30));

			sub1.Dispose();
			sub2.Dispose();
		}

		[Test]
		public void Test_CombinedGlobalAndPerSubscriberFilters()
		{
			var broker = new MessageBroker<int>();
			var result1 = 0;
			var result2 = 0;

			// Global filter
			broker.AddFilter(new TestMessageFilter<int>(x => x + 5));

			// Per-subscriber filters
			var sub1 = broker
				.WithFilter(new TestMessageFilter<int>(x => x * 2))
				.Subscribe(x => result1 = x);

			var sub2 = broker.Subscribe(x => result2 = x);

			broker.Publish(10);

			// Sub1: (10 + 5) * 2 = 30 (global + per-subscriber filter)
			Assert.That(result1, Is.EqualTo(30));
			// Sub2: 10 + 5 = 15 (only global filter)
			Assert.That(result2, Is.EqualTo(15));

			sub1.Dispose();
			sub2.Dispose();
		}

		[Test]
		public void Test_IgnoreFilter_BlocksAllMessages()
		{
			var broker = new MessageBroker<int>();
			var messageCount = 0;

			broker.AddFilter(new IgnoreMessageFilter<int>());

			var subscription = broker.Subscribe(x => messageCount++);

			for (int i = 0; i < 100; i++)
			{
				broker.Publish(i);
			}

			Assert.That(messageCount, Is.EqualTo(0));

			subscription.Dispose();
		}

		[Test]
		public void Test_ConditionalFilter()
		{
			var broker = new MessageBroker<string>();
			var receivedMessages = new List<string>();

			// Filter that transforms only messages starting with "IMPORTANT:"
			broker.AddFilter(async (msg, ct, next) =>
			{
				if (msg.StartsWith("IMPORTANT:"))
				{
					await next(msg.ToUpper(), ct);
				}
				else
				{
					await next(msg, ct);
				}
			});

			var subscription = broker.Subscribe(x => receivedMessages.Add(x));

			broker.Publish("Hello");
			broker.Publish("IMPORTANT: Alert");
			broker.Publish("Normal message");

			Assert.That(receivedMessages.Count, Is.EqualTo(3));
			Assert.That(receivedMessages[0], Is.EqualTo("Hello"));
			Assert.That(receivedMessages[1], Is.EqualTo("IMPORTANT: ALERT"));
			Assert.That(receivedMessages[2], Is.EqualTo("Normal message"));

			subscription.Dispose();
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