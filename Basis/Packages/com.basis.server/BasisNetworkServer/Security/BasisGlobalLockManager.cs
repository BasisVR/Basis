using Basis.Network.Core;
using System.Threading;
using static BasisNetworkCore.Serializable.SerializableBasis;

namespace BasisNetworkServer.Security
{
    /// <summary>
    /// Server-wide toggles that admins can flip to globally disable
    /// avatar, prop, or world loading for all non-admin players.
    /// Thread-safe — reads/writes use interlocked operations.
    /// </summary>
    public static class BasisGlobalLockManager
    {
        // 0 = unlocked (loading allowed), 1 = locked (loading blocked)
        private static int _avatarsLocked;
        private static int _propsLocked;
        private static int _worldsLocked;
        private static int _serversLocked;
        private static int _thirdPersonDisabled;

        public static bool AvatarsLocked => Interlocked.CompareExchange(ref _avatarsLocked, 0, 0) == 1;
        public static bool PropsLocked => Interlocked.CompareExchange(ref _propsLocked, 0, 0) == 1;
        public static bool WorldsLocked => Interlocked.CompareExchange(ref _worldsLocked, 0, 0) == 1;
        public static bool ServersLocked => Interlocked.CompareExchange(ref _serversLocked, 0, 0) == 1;
        public static bool ThirdPersonDisabled => Interlocked.CompareExchange(ref _thirdPersonDisabled, 0, 0) == 1;

        /// <summary>
        /// Seed the initial lock state from the server configuration.
        /// Call once at startup before any client threads are running.
        /// </summary>
        public static void InitializeFromConfig(Configuration config)
        {
            Interlocked.Exchange(ref _avatarsLocked, config.AvatarsLocked ? 1 : 0);
            Interlocked.Exchange(ref _propsLocked, config.PropsLocked ? 1 : 0);
            Interlocked.Exchange(ref _worldsLocked, config.WorldsLocked ? 1 : 0);
            Interlocked.Exchange(ref _serversLocked, config.ServersLocked ? 1 : 0);
            Interlocked.Exchange(ref _thirdPersonDisabled, config.ThirdPersonDisabled ? 1 : 0);
        }

        /// <summary>
        /// Toggle avatar loading. Returns the new state (true = locked).
        /// </summary>
        public static bool ToggleAvatars() => Toggle(ref _avatarsLocked);

        /// <summary>
        /// Toggle prop loading. Returns the new state (true = locked).
        /// </summary>
        public static bool ToggleProps() => Toggle(ref _propsLocked);

        /// <summary>
        /// Toggle world loading. Returns the new state (true = locked).
        /// </summary>
        public static bool ToggleWorlds() => Toggle(ref _worldsLocked);

        /// <summary>
        /// Toggle server-share dropping. Returns the new state (true = locked).
        /// </summary>
        public static bool ToggleServers() => Toggle(ref _serversLocked);

        /// <summary>
        /// Toggle third-person camera availability. Returns the new state (true = disabled).
        /// </summary>
        public static bool ToggleThirdPerson() => Toggle(ref _thirdPersonDisabled);

        private static bool Toggle(ref int field)
        {
            int prev, next;
            do
            {
                prev = field;
                next = prev == 0 ? 1 : 0;
            }
            while (Interlocked.CompareExchange(ref field, next, prev) != prev);
            return next == 1;
        }

        /// <summary>
        /// Sends the current global lock state to a specific peer.
        /// Used when a new player connects so they know what's locked.
        /// </summary>
        public static void SendLockStateToPeer(NetPeer peer)
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetLockState);
            writer.Put(AvatarsLocked);
            writer.Put(PropsLocked);
            writer.Put(WorldsLocked);
            writer.Put(ServersLocked);
            // Appended after ServersLocked so older clients reading 4 bools still parse cleanly.
            writer.Put(ThirdPersonDisabled);
            NetworkServer.TrySend(peer, writer, BasisNetworkCommons.AdminChannel, DeliveryMethod.ReliableOrdered);
            NetworkServer.ReturnWriter(writer);
        }

        /// <summary>
        /// Broadcasts the current lock state to all connected clients.
        /// </summary>
        public static void BroadcastLockState()
        {
            NetDataWriter writer = NetworkServer.RentWriter();
            new AdminRequest().Serialize(writer, AdminRequestMode.GlobalGetLockState);
            writer.Put(AvatarsLocked);
            writer.Put(PropsLocked);
            writer.Put(WorldsLocked);
            writer.Put(ServersLocked);
            // Appended after ServersLocked so older clients reading 4 bools still parse cleanly.
            writer.Put(ThirdPersonDisabled);
            NetworkServer.BroadcastMessageToClients(
                writer,
                BasisNetworkCommons.AdminChannel,
                NetworkServer.PeerSnapshot,
                DeliveryMethod.ReliableOrdered
            );
            NetworkServer.ReturnWriter(writer);
        }
    }
}
