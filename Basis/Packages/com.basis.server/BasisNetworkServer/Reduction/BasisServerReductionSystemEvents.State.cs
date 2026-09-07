using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        // Maintained incrementally via ProcessMessage/ProcessPendingRemovals instead of rebuilt every tick.
        private static readonly List<(int id, PlayerState state)> _activePlayers = new();
        private static readonly object _activePlayersLock = new();
        private static (int id, PlayerState state)[] _activePlayersSnapshot = Array.Empty<(int, PlayerState)>();
        private static volatile bool _activePlayersDirty = false;

        private static readonly ConcurrentQueue<int> playersToRemove = new();

        // Lets the tick loop park (~0% CPU) when the server is empty instead of
        // polling at 250Hz. Set() the moment the first packet arrives so there is
        // no join latency. Same approach LiteNetLib's logic thread already uses.
        private static readonly AutoResetEvent _tickWake = new(false);
        private static int _activePlayerCount;

        // Reusable snapshot list for draining currentMessages each tick  avoids allocation per tick.
        private static readonly List<QueuedMessage> _messagesSnapshot = new(1024);

        // Static delegates — avoid closure allocation every tick. The index form is what the tick
        // loop uses (see the range-partitioning note there); the message form is kept for callers
        // that already hold the message.
        private static readonly Action<QueuedMessage> s_processMessageAction = msg =>
        {
            try
            {
                ProcessMessage(msg);
            }
            catch (Exception ex)
            {
                BNL.LogError($"[ProcessMessage] Exception: {ex}");
            }
        };

        private static readonly Action<int> s_processMessageByIndexAction = i =>
        {
            try
            {
                ProcessMessage(_messagesSnapshot[i]);
            }
            catch (Exception ex)
            {
                BNL.LogError($"[ProcessMessage] Exception: {ex}");
            }
        };

        // Distance -> Quality thresholds (squared meters)
        public static float HighDistanceSq = 100f;      // 10m
        public static float MediumDistanceSq = 900f;    // 30m
        public static float LowDistanceSq = 2500f;      // 50m

        public static long intervalMs = 10;

        // 4 ms (250 Hz) absolute floor: no pair is ever scheduled faster than 20 Hz, so going below
        // this only adds barriers. 20 ms (50 Hz) ceiling: still comfortably under the 50 ms shortest
        // interval, so even fully backed off the tick never becomes the thing limiting delivery.
        public const long MinTickIntervalMs = 4;
        public const long MaxTickIntervalMs = 20;

        private const int TicksPerSendInterval = 4;

        private static long AdaptiveMinIntervalMs =>
            Math.Max(MinTickIntervalMs, BSRSMillisecondDefaultInterval / TicksPerSendInterval);
        // Fallback wake while the server is empty; _tickWake.Set() does the real wake.
        private const int IdleWaitMs = 250;
        // Load-adaptive inter-tick wait: if the tick left more than this much of its budget
        // unused (light load), block on WaitOne (~0% CPU); if less (heavy load, near the
        // budget), busy-spin the small remainder to hit the rate precisely. Doubles as the
        // spin cap — the loop never spins more than this per tick. Set to 0 for pure WaitOne
        // (lowest CPU, looser rate under load); raise toward intervalMs to favor a tight
        // rate at higher load. Saturated ticks (no slack) never wait or spin regardless.
        public static double MaxSpinMs = 2.5;
    }
}
