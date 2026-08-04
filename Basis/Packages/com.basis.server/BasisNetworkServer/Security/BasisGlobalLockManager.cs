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
        private static int _additionalAvatarDataLock;
        private static int _cameraMetadataDisallowMask;
        private static int _playspaceMoverLocked;
        private static int _directConnectLocked;
        private static int _cilboxLocked;
        private static int _imagesLocked;
        private static int _textChatLocked;
        private static int _voiceChatLocked;
        private static int _mediaPlayerLocked;
        private static int _cameraCaptureLocked;
        private static int _propGrabbingLocked;
        private static int _safeDisplayNamesForced;
        // 0 = feature on (default), 1 = admin-disabled. Inverted vs the locks above — this is a default-on feature.
        private static int _endEffectorIKDisabled;

        public static bool AvatarsLocked => Interlocked.CompareExchange(ref _avatarsLocked, 0, 0) == 1;
        public static bool PropsLocked => Interlocked.CompareExchange(ref _propsLocked, 0, 0) == 1;
        public static bool WorldsLocked => Interlocked.CompareExchange(ref _worldsLocked, 0, 0) == 1;
        public static bool ServersLocked => Interlocked.CompareExchange(ref _serversLocked, 0, 0) == 1;
        public static bool ThirdPersonDisabled => Interlocked.CompareExchange(ref _thirdPersonDisabled, 0, 0) == 1;
        public static bool AdditionalAvatarDataLock => Interlocked.CompareExchange(ref _additionalAvatarDataLock, 0, 0) == 1;
        public static byte CameraMetadataDisallowMask => (byte)Interlocked.CompareExchange(ref _cameraMetadataDisallowMask, 0, 0);
        public static bool PlayspaceMoverLocked => Interlocked.CompareExchange(ref _playspaceMoverLocked, 0, 0) == 1;
        public static bool DirectConnectLocked => Interlocked.CompareExchange(ref _directConnectLocked, 0, 0) == 1;
        public static bool CilboxLocked => Interlocked.CompareExchange(ref _cilboxLocked, 0, 0) == 1;
        public static bool ImagesLocked => Interlocked.CompareExchange(ref _imagesLocked, 0, 0) == 1;
        public static bool TextChatLocked => Interlocked.CompareExchange(ref _textChatLocked, 0, 0) == 1;
        public static bool VoiceChatLocked => Interlocked.CompareExchange(ref _voiceChatLocked, 0, 0) == 1;
        public static bool MediaPlayerLocked => Interlocked.CompareExchange(ref _mediaPlayerLocked, 0, 0) == 1;
        public static bool CameraCaptureLocked => Interlocked.CompareExchange(ref _cameraCaptureLocked, 0, 0) == 1;
        public static bool PropGrabbingLocked => Interlocked.CompareExchange(ref _propGrabbingLocked, 0, 0) == 1;
        public static bool SafeDisplayNamesForced => Interlocked.CompareExchange(ref _safeDisplayNamesForced, 0, 0) == 1;
        public static bool EndEffectorIKDisabled => Interlocked.CompareExchange(ref _endEffectorIKDisabled, 0, 0) == 1;

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
            Interlocked.Exchange(ref _additionalAvatarDataLock, config.AdditionalAvatarDataLock ? 1 : 0);
            Interlocked.Exchange(ref _cameraMetadataDisallowMask, config.CameraMetadataDisallowMask);
            Interlocked.Exchange(ref _playspaceMoverLocked, config.PlayspaceMoverLocked ? 1 : 0);
            Interlocked.Exchange(ref _directConnectLocked, config.DirectConnectLocked ? 1 : 0);
            Interlocked.Exchange(ref _cilboxLocked, config.CilboxLocked ? 1 : 0);
            Interlocked.Exchange(ref _imagesLocked, config.ImagesLocked ? 1 : 0);
            Interlocked.Exchange(ref _textChatLocked, config.TextChatLocked ? 1 : 0);
            Interlocked.Exchange(ref _voiceChatLocked, config.VoiceChatLocked ? 1 : 0);
            Interlocked.Exchange(ref _mediaPlayerLocked, config.MediaPlayerLocked ? 1 : 0);
            Interlocked.Exchange(ref _cameraCaptureLocked, config.CameraCaptureLocked ? 1 : 0);
            Interlocked.Exchange(ref _propGrabbingLocked, config.PropGrabbingLocked ? 1 : 0);
            Interlocked.Exchange(ref _safeDisplayNamesForced, config.SafeDisplayNamesForced ? 1 : 0);
            Interlocked.Exchange(ref _endEffectorIKDisabled, config.EndEffectorIKDisabled ? 1 : 0);
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

        /// <summary>
        /// Toggle the network-side strip of AdditionalAvatarDatas on inbound avatar
        /// sync messages. Returns the new state (true = additional data stripped).
        /// </summary>
        public static bool ToggleAdditionalAvatarDataLock() => Toggle(ref _additionalAvatarDataLock);

        /// <summary>
        /// Toggle the non-admin playspace-mover lockout. Returns the new state (true = locked).
        /// </summary>
        public static bool TogglePlayspaceMover() => Toggle(ref _playspaceMoverLocked);

        /// <summary>
        /// Toggle the non-admin direct-connect (P2P) lockout. Returns the new state (true = locked).
        /// </summary>
        public static bool ToggleDirectConnect() => Toggle(ref _directConnectLocked);

        /// <summary>
        /// Toggle the global Cilbox lock. Returns the new state (true = sandboxed Cilbox code blocked).
        /// </summary>
        public static bool ToggleCilbox() => Toggle(ref _cilboxLocked);

        /// <summary>
        /// Toggle the global shared-image lock. Returns the new state (true = sharing/showing new
        /// image pickups blocked for non-bypass clients). Enforced client-side: image pickups ride
        /// the generic scene relay, so the server can't single them out the way it blocks content
        /// shares — clients honor the broadcast flag instead.
        /// </summary>
        public static bool ToggleImages() => Toggle(ref _imagesLocked);

        /// <summary>
        /// Toggle the global text-chat lock. Returns the new state (true = chat messages and typing
        /// state from peers without <c>basis.chat.lockbypass</c> are dropped at the server).
        /// </summary>
        public static bool ToggleTextChat() => Toggle(ref _textChatLocked);

        /// <summary>
        /// Toggle the global voice lock. Returns the new state (true = normal and shout voice from
        /// peers without <c>basis.voice.lockbypass</c> are dropped at the server).
        /// </summary>
        public static bool ToggleVoiceChat() => Toggle(ref _voiceChatLocked);

        /// <summary>
        /// Toggle the global media-player lock. Returns the new state (true = non-bypass clients
        /// neither load new URLs nor accept inbound ones). Enforced client-side: media player state
        /// rides the generic scene relay, so the server can't single it out.
        /// </summary>
        public static bool ToggleMediaPlayer() => Toggle(ref _mediaPlayerLocked);

        /// <summary>
        /// Toggle the global camera-capture lock. Returns the new state (true = non-bypass clients
        /// can't take photos). Enforced client-side: capture is entirely local, nothing reaches the
        /// server to block.
        /// </summary>
        public static bool ToggleCameraCapture() => Toggle(ref _cameraCaptureLocked);

        /// <summary>
        /// Toggle the global prop-grabbing lock. Returns the new state (true = non-bypass clients
        /// can't pick up props). Enforced client-side: grabbing is local interaction logic and the
        /// resulting motion is indistinguishable from ordinary transform sync.
        /// </summary>
        public static bool TogglePropGrabbing() => Toggle(ref _propGrabbingLocked);

        /// <summary>
        /// Toggle forced safe display names. Returns the new state (true = clients strip rich-text
        /// markup from display names). Enforced client-side.
        /// </summary>
        public static bool ToggleSafeDisplayNames() => Toggle(ref _safeDisplayNamesForced);

        /// <summary>
        /// Toggle the global remote end-effector IK disable. Returns the new state (true = disabled;
        /// clients fall back to pure-FK playback for remote hands/feet). Enforced client-side.
        /// </summary>
        public static bool ToggleEndEffectorIK() => Toggle(ref _endEffectorIKDisabled);

        /// <summary>
        /// Set the per-category camera photo-metadata disallow mask (set bit = disallowed).
        /// </summary>
        public static void SetCameraMetadataDisallowMask(byte mask) => Interlocked.Exchange(ref _cameraMetadataDisallowMask, mask);

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
            // Appended after ThirdPersonDisabled — older clients parsing 5 bools still work.
            writer.Put(AdditionalAvatarDataLock);
            // Appended after AdditionalAvatarDataLock (1 byte) — older clients parsing 6 bools still work.
            writer.Put(CameraMetadataDisallowMask);
            // Appended after CameraMetadataDisallowMask (1 byte) — older clients that stop reading earlier still parse.
            writer.Put((byte)NetworkServer.Configuration.BasisUserRestrictionMode);
            // Appended after BasisUserRestrictionMode — older clients that stop reading earlier still parse.
            writer.Put(PlayspaceMoverLocked);
            writer.Put(DirectConnectLocked);
            // Appended after DirectConnectLocked — older clients that stop reading earlier still parse.
            writer.Put(CilboxLocked);
            // Appended after CilboxLocked — older clients that stop reading earlier still parse.
            writer.Put(ImagesLocked);
            // Appended after ImagesLocked — older clients that stop reading earlier still parse.
            writer.Put(EndEffectorIKDisabled);
            // Appended after EndEffectorIKDisabled — older clients that stop reading earlier still parse.
            writer.Put(TextChatLocked);
            // Appended after TextChatLocked — older clients that stop reading earlier still parse.
            writer.Put(VoiceChatLocked);
            writer.Put(MediaPlayerLocked);
            writer.Put(CameraCaptureLocked);
            writer.Put(PropGrabbingLocked);
            // Appended after PropGrabbingLocked — older clients that stop reading earlier still parse.
            writer.Put(SafeDisplayNamesForced);
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
            // Appended after ThirdPersonDisabled — older clients parsing 5 bools still work.
            writer.Put(AdditionalAvatarDataLock);
            // Appended after AdditionalAvatarDataLock (1 byte) — older clients parsing 6 bools still work.
            writer.Put(CameraMetadataDisallowMask);
            // Appended after CameraMetadataDisallowMask (1 byte) — older clients that stop reading earlier still parse.
            writer.Put((byte)NetworkServer.Configuration.BasisUserRestrictionMode);
            // Appended after BasisUserRestrictionMode — older clients that stop reading earlier still parse.
            writer.Put(PlayspaceMoverLocked);
            writer.Put(DirectConnectLocked);
            // Appended after DirectConnectLocked — older clients that stop reading earlier still parse.
            writer.Put(CilboxLocked);
            // Appended after CilboxLocked — older clients that stop reading earlier still parse.
            writer.Put(ImagesLocked);
            // Appended after ImagesLocked — older clients that stop reading earlier still parse.
            writer.Put(EndEffectorIKDisabled);
            // Appended after EndEffectorIKDisabled — older clients that stop reading earlier still parse.
            writer.Put(TextChatLocked);
            // Appended after TextChatLocked — older clients that stop reading earlier still parse.
            writer.Put(VoiceChatLocked);
            writer.Put(MediaPlayerLocked);
            writer.Put(CameraCaptureLocked);
            writer.Put(PropGrabbingLocked);
            // Appended after PropGrabbingLocked — older clients that stop reading earlier still parse.
            writer.Put(SafeDisplayNamesForced);
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
