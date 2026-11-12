using System.Collections.Concurrent;

namespace Basis.Network.Server.Generic
{
    /// <summary>
    /// Which connected players turned on Media Persistence, so their shared images and clips are handed
    /// to a remaining player when they leave instead of being removed. Each client reports its own choice
    /// over the image and clip channels; the entry is dropped once the player's teardown has run.
    /// </summary>
    public static class BasisMediaPersistenceRegistry
    {
        public const ushort NoSuccessor = ushort.MaxValue;

        private static readonly ConcurrentDictionary<ushort, byte> Persistent = new ConcurrentDictionary<ushort, byte>();
        private static readonly ConcurrentDictionary<ushort, byte> Removed = new ConcurrentDictionary<ushort, byte>();

        /// <summary>A kicked or banned player's media is always removed, whatever either toggle says.</summary>
        public static void MarkRemoved(int peerId)
        {
            if (peerId >= 0 && peerId <= ushort.MaxValue) Removed[(ushort)peerId] = 1;
        }

        public static void Set(ushort peerId, bool enabled)
        {
            if (enabled) Persistent[peerId] = 1;
            else Persistent.TryRemove(peerId, out _);
        }

        public static bool IsPersistent(int peerId)
        {
            return peerId >= 0 && peerId <= ushort.MaxValue && Persistent.ContainsKey((ushort)peerId);
        }

        /// <summary>Whether a leaver's media should outlive them: their own choice or the admin toggle, never after a kick or ban.</summary>
        public static bool ShouldKeep(int peerId)
        {
            if (peerId < 0 || peerId > ushort.MaxValue || Removed.ContainsKey((ushort)peerId)) return false;
            return BasisNetworkServer.Security.BasisGlobalLockManager.KeepSharedMediaOnLeave || IsPersistent(peerId);
        }

        public static void RemovePeer(int peerId)
        {
            if (peerId < 0 || peerId > ushort.MaxValue) return;
            Persistent.TryRemove((ushort)peerId, out _);
            Removed.TryRemove((ushort)peerId, out _);
        }

        public static void Reset()
        {
            Persistent.Clear();
            Removed.Clear();
        }
    }
}
