using Unity.Scripting.LifecycleManagement;
using Basis.Network.Core;
using System;
using System.Collections.Concurrent;

namespace Basis.Scripts.Networking
{
    [AutoStaticsCleanup]
    public static partial class BasisAvatarRateRegistry
    {
        private static readonly ConcurrentDictionary<ushort, int> _ratePerSender = new();

        private static int _lastAnnouncedMs = -1;
        private static double _lastAnnounceTime;

        private const int ChangeThresholdMs = 5;
        private const double PeriodicResyncSec = 5.0;

        public static void UpdateRemoteRate(ushort senderId, ushort intervalMs)
        {
            _ratePerSender[senderId] = intervalMs;
        }

        public static bool TryGetRate(ushort senderId, out int intervalMs)
        {
            if (_ratePerSender.TryGetValue(senderId, out int v))
            {
                intervalMs = v;
                return true;
            }
            intervalMs = 0;
            return false;
        }

        public static void RemoveSender(ushort senderId)
        {
            _ratePerSender.TryRemove(senderId, out _);
        }

        public static void Reset()
        {
            _ratePerSender.Clear();
            _lastAnnouncedMs = -1;
            _lastAnnounceTime = 0;
        }

        public static void ForceNextAnnouncement()
        {
            _lastAnnouncedMs = -1;
            _lastAnnounceTime = 0;
        }

        public static void MaybeAnnounceLocalRate(float intervalSeconds)
        {
            if (BasisNetworkConnection.LocalPlayerPeer == null) return;

            int currentMs = (int)Math.Round(intervalSeconds * 1000.0);
            if (currentMs < 1) currentMs = 1;
            else if (currentMs > ushort.MaxValue) currentMs = ushort.MaxValue;

            double now = UnityEngine.Time.timeAsDouble;
            bool changed = Math.Abs(currentMs - _lastAnnouncedMs) > ChangeThresholdMs;
            bool periodic = (now - _lastAnnounceTime) >= PeriodicResyncSec;

            if (!changed && !periodic) return;

            var writer = new NetDataWriter();
            writer.Put(BasisNetworkCommons.EventType_AvatarRateChange);
            writer.Put((ushort)currentMs);

            BasisNetworkConnection.LocalPlayerPeer.Send(writer, BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);

            _lastAnnouncedMs = currentMs;
            _lastAnnounceTime = now;
        }
    }
}
