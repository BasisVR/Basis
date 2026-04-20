using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.ZeroMessenger.Samples
{
    public class GameEventSystemExample : MonoBehaviour
    {
        private MessageBroker<GameEvent> eventBroker;
        private List<IDisposable> subscriptions = new List<IDisposable>();

        void Start()
        {
            // Create the message broker
            eventBroker = new MessageBroker<GameEvent>();

            // Add a global logging filter that logs all events
            eventBroker.AddFilter(new LoggingFilter());

            // Add a global priority filter that only allows high priority events through
            // Comment this out to see all events
            // eventBroker.AddFilter(new PriorityFilter(EventPriority.High));

            // Subscribe to events with different handlers
            SetupEventHandlers();

            // Simulate some game events
            SimulateGameEvents();
        }

        void SetupEventHandlers()
        {
            // Simple synchronous handler for player damage
            var damageSub = eventBroker
                .WithFilter(new EventTypeFilter(EventType.PlayerDamage))
                .Subscribe(evt =>
                {
                    BasisDebug.Log($"[DamageHandler] Player took {evt.Value} damage!");
                });
            subscriptions.Add(damageSub);

            // Async handler for score updates with processing delay
            var scoreSub = eventBroker
                .WithFilter(new EventTypeFilter(EventType.ScoreUpdate))
                .SubscribeAwait(async (evt, ct) =>
                {
                    BasisDebug.Log($"[ScoreHandler] Processing score update: +{evt.Value}");
                    await Task.Delay(100, ct); // Simulate async processing
                    BasisDebug.Log($"[ScoreHandler] Score saved to leaderboard!");
                });
            subscriptions.Add(scoreSub);

            // Handler that validates and transforms events
            var validationSub = eventBroker
                .WithFilter(new ValidationFilter())
                .Subscribe(evt =>
                {
                    BasisDebug.Log($"[ValidatedHandler] Validated event: {evt.Type}");
                });
            subscriptions.Add(validationSub);

            // Handler with rate limiting filter
            var rateLimitedSub = eventBroker
                .WithFilter(new RateLimitFilter(maxPerSecond: 2))
                .Subscribe(evt =>
                {
                    BasisDebug.Log($"[RateLimited] Event passed rate limit: {evt.Type}");
                });
            subscriptions.Add(rateLimitedSub);
        }

        async void SimulateGameEvents()
        {
            BasisDebug.Log("=== Starting Game Event Simulation ===");

            // Send various events
            eventBroker.Publish(new GameEvent
            {
                Type = EventType.PlayerDamage,
                Value = 25,
                Priority = EventPriority.High,
                Message = "Hit by enemy"
            });

            await Task.Delay(500);

            eventBroker.Publish(new GameEvent
            {
                Type = EventType.ScoreUpdate,
                Value = 100,
                Priority = EventPriority.Medium,
                Message = "Enemy defeated"
            });

            await Task.Delay(500);

            eventBroker.Publish(new GameEvent
            {
                Type = EventType.ItemCollected,
                Value = 1,
                Priority = EventPriority.Low,
                Message = "Health potion collected"
            });

            // Test rate limiting by sending multiple events quickly
            for (int i = 0; i < 5; i++)
            {
                eventBroker.Publish(new GameEvent
                {
                    Type = EventType.PlayerDamage,
                    Value = 5,
                    Priority = EventPriority.Medium,
                    Message = $"Quick hit {i + 1}"
                });
            }

            BasisDebug.Log("=== Event Simulation Complete ===");
        }

        void OnDestroy()
        {
            // Clean up all subscriptions
            foreach (var sub in subscriptions)
            {
                sub.Dispose();
            }
            subscriptions.Clear();

            eventBroker?.Dispose();
        }
    }

    #region Message Types

    public enum EventType
    {
        PlayerDamage,
        ScoreUpdate,
        ItemCollected,
        LevelComplete
    }

    public enum EventPriority
    {
        Low,
        Medium,
        High,
        Critical
    }

    public class GameEvent
    {
        public EventType Type { get; set; }
        public int Value { get; set; }
        public EventPriority Priority { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    #endregion

    #region Custom Filters

    /// <summary>
    /// Filter that logs all events passing through it
    /// </summary>
    public class LoggingFilter : IMessageFilter<GameEvent>
    {
        public async ValueTask InvokeAsync(GameEvent message, CancellationToken cancellationToken, Func<GameEvent, CancellationToken, ValueTask> next)
        {
            BasisDebug.Log($"[LogFilter] Event: {message.Type} | Priority: {message.Priority} | Value: {message.Value}");
            await next(message, cancellationToken);
        }
    }

    /// <summary>
    /// Filter that only allows events of a specific type
    /// </summary>
    public class EventTypeFilter : IMessageFilter<GameEvent>
    {
        private readonly EventType allowedType;

        public EventTypeFilter(EventType allowedType)
        {
            this.allowedType = allowedType;
        }

        public async ValueTask InvokeAsync(GameEvent message, CancellationToken cancellationToken, Func<GameEvent, CancellationToken, ValueTask> next)
        {
            if (message.Type == allowedType)
            {
                await next(message, cancellationToken);
            }
            // Otherwise, don't call next - message is filtered out
        }
    }

    /// <summary>
    /// Filter that only allows events with priority greater than or equal to threshold
    /// </summary>
    public class PriorityFilter : IMessageFilter<GameEvent>
    {
        private readonly EventPriority minPriority;

        public PriorityFilter(EventPriority minPriority)
        {
            this.minPriority = minPriority;
        }

        public async ValueTask InvokeAsync(GameEvent message, CancellationToken cancellationToken, Func<GameEvent, CancellationToken, ValueTask> next)
        {
            if (message.Priority >= minPriority)
            {
                await next(message, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Filter that validates events and can transform them
    /// </summary>
    public class ValidationFilter : IMessageFilter<GameEvent>
    {
        public async ValueTask InvokeAsync(GameEvent message, CancellationToken cancellationToken, Func<GameEvent, CancellationToken, ValueTask> next)
        {
            // Validate the event
            if (string.IsNullOrEmpty(message.Message))
            {
                message.Message = $"Auto-generated message for {message.Type}";
            }

            // Clamp negative values
            if (message.Value < 0)
            {
                BasisDebug.LogWarning($"[ValidationFilter] Negative value detected: {message.Value}, clamping to 0");
                message.Value = 0;
            }

            await next(message, cancellationToken);
        }
    }

    /// <summary>
    /// Filter that implements rate limiting
    /// </summary>
    public class RateLimitFilter : IMessageFilter<GameEvent>
    {
        private readonly int maxPerSecond;
        private readonly Queue<DateTime> messageTimes = new Queue<DateTime>();

        public RateLimitFilter(int maxPerSecond)
        {
            this.maxPerSecond = maxPerSecond;
        }

        public async ValueTask InvokeAsync(GameEvent message, CancellationToken cancellationToken, Func<GameEvent, CancellationToken, ValueTask> next)
        {
            var now = DateTime.UtcNow;
            var oneSecondAgo = now.AddSeconds(-1);

            // Remove old entries
            while (messageTimes.Count > 0 && messageTimes.Peek() < oneSecondAgo)
            {
                messageTimes.Dequeue();
            }

            // Check if we're under the rate limit
            if (messageTimes.Count < maxPerSecond)
            {
                messageTimes.Enqueue(now);
                await next(message, cancellationToken);
            }
            else
            {
                BasisDebug.LogWarning($"[RateLimitFilter] Message dropped due to rate limiting");
            }
        }
    }

    #endregion
}
