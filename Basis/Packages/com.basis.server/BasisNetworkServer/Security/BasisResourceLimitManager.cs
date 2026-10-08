using Basis.Network.Core;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Server-defined caps that bound per-client resource use (content-share spheres).
    /// Seeded from Configuration at boot, editable live from the admin panel (gated by
    /// basis.moderation.globallock), persisted to config.xml, and broadcast so
    /// admin panels stay in sync.
    /// </summary>
    public static class BasisResourceLimitManager
    {
        private const int DefaultMaxContentSpheresPerPlayer = 32;

        private const int AbsoluteMaxContentSpheresPerPlayer = 4096;

        private const int DefaultContentSphereLeaveTimeoutSeconds = 60;

        private const int AbsoluteMaxContentSphereTimerSeconds = 86400;

        private static int _maxContentSpheresPerPlayer = DefaultMaxContentSpheresPerPlayer;

        private static int _contentSphereLeaveTimeoutSeconds = DefaultContentSphereLeaveTimeoutSeconds;

        private static int _contentSphereDeletionTimerSeconds;

        public static int MaxContentSpheresPerPlayer => Interlocked.CompareExchange(ref _maxContentSpheresPerPlayer, 0, 0);

        public static int ContentSphereLeaveTimeoutSeconds => Interlocked.CompareExchange(ref _contentSphereLeaveTimeoutSeconds, 0, 0);

        public static int ContentSphereDeletionTimerSeconds => Interlocked.CompareExchange(ref _contentSphereDeletionTimerSeconds, 0, 0);

        public static void InitializeFromConfig(Configuration config)
        {
            SetLimits(config.MaxContentSpheresPerPlayer);
            SetContentSphereTimers(config.ContentSphereLeaveTimeoutSeconds, config.ContentSphereDeletionTimerSeconds);
        }

        public static bool SetLimits(int maxContentSpheresPerPlayer)
        {
            Sanitize(ref maxContentSpheresPerPlayer);
            int prevSpheres = Interlocked.Exchange(ref _maxContentSpheresPerPlayer, maxContentSpheresPerPlayer);
            return prevSpheres != maxContentSpheresPerPlayer;
        }

        public static bool SetContentSphereTimers(int leaveTimeoutSeconds, int deletionTimerSeconds)
        {
            SanitizeTimer(ref leaveTimeoutSeconds);
            SanitizeTimer(ref deletionTimerSeconds);
            int prevLeave = Interlocked.Exchange(ref _contentSphereLeaveTimeoutSeconds, leaveTimeoutSeconds);
            int prevDeletion = Interlocked.Exchange(ref _contentSphereDeletionTimerSeconds, deletionTimerSeconds);
            return prevLeave != leaveTimeoutSeconds || prevDeletion != deletionTimerSeconds;
        }

        public static void SendStateToPeer(NetPeer peer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                Write(writer);
                NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }

        public static void BroadcastState()
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            try
            {
                Write(writer);
                NetworkServer.BroadcastMessageToClients(
                    writer,
                    BasisNetworkCommons.AdminChannel,
                    NetworkServer.PeerSnapshot,
                    DeliveryMethod.ReliableOrdered);
            }
            finally
            {
                NetworkServer.ReturnWriter(writer);
            }
        }

        private static void Write(NetDataWriter writer)
        {
            new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetResourceLimits);
            writer.Put(MaxContentSpheresPerPlayer);
            writer.Put(ContentSphereLeaveTimeoutSeconds);
            writer.Put(ContentSphereDeletionTimerSeconds);
        }

        private static void Sanitize(ref int spheres)
        {
            if (spheres < 1) spheres = DefaultMaxContentSpheresPerPlayer;
            if (spheres > AbsoluteMaxContentSpheresPerPlayer) spheres = AbsoluteMaxContentSpheresPerPlayer;
        }

        private static void SanitizeTimer(ref int seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds > AbsoluteMaxContentSphereTimerSeconds) seconds = AbsoluteMaxContentSphereTimerSeconds;
        }
    }
}
