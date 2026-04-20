namespace Basis.Network.Core
{
    public static class BasisNetworkCommons
    {
        /// <summary>
        /// this is the maximum Connections that can occur under the hood.
        /// </summary>
        public const int MaxConnections = ushort.MaxValue;

        public const int NetworkIntervalPoll = 2;
        public const int PingInterval = 1500;
        public const int ReceivePollingTime = 50000;
        /// <summary>
        /// LiteNetLib packet pool size. Must be large enough to avoid allocating new NetPacket
        /// objects during high-throughput send loops. With 1000 players at ~4M sends/sec,
        /// packets cycle through pool rapidly. 65536 keeps the pool warm and avoids GC pressure.
        /// </summary>
        public const int PacketPoolSize = 65536;
        /// <summary>
        /// when adding a new message we need to increase this
        /// will function up to 64
        /// </summary>
        public const byte TotalChannels = 64;

        // ── Connection lifecycle ─────────────────────────────────────────────
        /// <summary>Auth Identity Message</summary>
        public const byte AuthIdentityChannel = 0;
        /// <summary>Player metadata (UUID, display name, permissions)</summary>
        public const byte metaDataChannel = 1;
        /// <summary>Removes a player entity</summary>
        public const byte DisconnectionChannel = 2;

        // ── Voice ────────────────────────────────────────────────────────────
        /// <summary>Spatialized voice data</summary>
        public const byte VoiceChannel = 3;
        /// <summary>Shout mode voice. Non-spatialized audio broadcast to all clients.</summary>
        public const byte ShoutVoiceChannel = 4;
        /// <summary>Voice recipient list (byte count, ≤255 recipients)</summary>
        public const byte AudioRecipientsChannel = 5;
        /// <summary>Voice recipient list (ushort count, >255 recipients)</summary>
        public const byte AudioRecipientsLargeChannel = 39;
        /// <summary>Spatialized voice data (ushort playerID, for IDs >255)</summary>
        public const byte VoiceLargeChannel = 40;
        /// <summary>Voice excluded list (byte count, ≤255 excluded). Server sends to everyone EXCEPT listed IDs.</summary>
        public const byte AudioRecipientsInvertedChannel = 49;
        /// <summary>Voice excluded list (ushort count, >255 excluded). Server sends to everyone EXCEPT listed IDs.</summary>
        public const byte AudioRecipientsInvertedLargeChannel = 50;
        /// <summary>Voice recipients as a bitfield. Bit at position playerID = recipient.</summary>
        public const byte AudioRecipientsBitfieldChannel = 51;

        // ── Per-quality avatar channels ──────────────────────────────────────
        // Layout: PlayerAvatarVeryLowChannel + quality * 2 + hasAdditional
        //   6  = VeryLow               7  = VeryLow + Additional
        //   8  = Low                   9  = Low + Additional
        //   10 = Medium               11 = Medium + Additional
        //   12 = High                 13 = High + Additional
        public const byte PlayerAvatarVeryLowChannel = 6;
        public const byte PlayerAvatarVeryLowAdditionalChannel = 7;
        public const byte PlayerAvatarLowChannel = 8;
        public const byte PlayerAvatarLowAdditionalChannel = 9;
        public const byte PlayerAvatarMediumChannel = 10;
        public const byte PlayerAvatarMediumAdditionalChannel = 11;
        public const byte PlayerAvatarHighChannel = 12;
        public const byte PlayerAvatarHighAdditionalChannel = 13;

        // ── Avatar management ────────────────────────────────────────────────
        /// <summary>Swap to a different avatar</summary>
        public const byte AvatarChangeMessageChannel = 14;
        /// <summary>Generic avatar script data</summary>
        public const byte AvatarChannel = 15;

        // ── Player management ────────────────────────────────────────────────
        /// <summary>Create a remote player entity</summary>
        public const byte CreateRemotePlayerChannel = 16;
        /// <summary>Create remote player entities for a newly joined peer</summary>
        public const byte CreateRemotePlayersForNewPeerChannel = 17;
        /// <summary>Chat text messages displayed above player nameplates</summary>
        public const byte ChatChannel = 18;

        // ── Ownership ────────────────────────────────────────────────────────
        /// <summary>Get the current owner of a network object</summary>
        public const byte GetCurrentOwnerRequestChannel = 19;
        /// <summary>Transfer ownership of a network object</summary>
        public const byte ChangeCurrentOwnerRequestChannel = 20;
        /// <summary>Remove current ownership</summary>
        public const byte RemoveCurrentOwnerRequestChannel = 21;

        // ── Net IDs ──────────────────────────────────────────────────────────
        /// <summary>Assign a net id (string to ushort)</summary>
        public const byte netIDAssignChannel = 22;
        /// <summary>Assign an array of net ids (string to ushort)</summary>
        public const byte NetIDAssignsChannel = 23;

        // ── Scene & resources ────────────────────────────────────────────────
        /// <summary>Scene script data</summary>
        public const byte SceneChannel = 24;
        /// <summary>Load a resource (scene, gameobject, script, asset)</summary>
        public const byte LoadResourceChannel = 25;
        /// <summary>Unload a resource</summary>
        public const byte UnloadResourceChannel = 26;
        /// <summary>Client tells server it has finished preloading a resource (ready or failed).</summary>
        public const byte PreloadReadyChannel = 27;
        /// <summary>Server tells all clients to spawn a previously preloaded resource.</summary>
        public const byte SpawnPreloadedChannel = 28;

        // ── Content sharing ──────────────────────────────────────────────────
        /// <summary>Drop content spheres</summary>
        public const byte ContentShareChannel = 29;
        /// <summary>Remove content spheres</summary>
        public const byte ContentShareCleanupChannel = 30;

        // ── Server-bound ─────────────────────────────────────────────────────
        /// <summary>Developer hook — data only delivered to the server</summary>
        public const byte ServerBoundChannel = 31;

        // ── Database & admin ─────────────────────────────────────────────────
        /// <summary>Store data to the server-side database</summary>
        public const byte StoreDatabaseChannel = 32;
        /// <summary>Request data from the server-side database by id</summary>
        public const byte RequestStoreDatabaseChannel = 33;
        /// <summary>Admin messages from client</summary>
        public const byte AdminChannel = 34;

        // ── Stats, camera & events ───────────────────────────────────────────
        /// <summary>Server statistics</summary>
        public const byte ServerStatisticsChannel = 35;
        /// <summary>PIP camera created/destroyed state (reliable, per-player).</summary>
        public const byte CameraPIPStateChannel = 36;
        /// <summary>PIP camera position updates (sequenced, position only).</summary>
        public const byte CameraPIPPositionChannel = 37;
        /// <summary>
        /// Generic low-priority events channel. The first byte of the payload
        /// identifies the event type (see EventType constants below).
        /// </summary>
        public const byte EventsChannel = 38;

        // ── Event type sub-bytes for EventsChannel ──
        /// <summary>Camera shutter sound fired when a player takes a photo.</summary>
        public const byte EventType_CameraShutterSound = 0;
        /// <summary>Camera countdown started — remote clients replay the tick/shutter timing.</summary>
        public const byte EventType_CameraCountdown = 1;
        /// <summary>
        /// Session-scoped "temp block" notification. Sender tells a specific target peer
        /// that it has (or has not) blocked them locally, so the target can mirror the
        /// block and hide the sender's avatar/audio/nameplate on their client. Not persisted.
        /// </summary>
        public const byte EventType_PlayerTempBlock = 2;

        // ── Per-quality avatar channels (ushort playerID, for IDs >255) ──
        // Same layout as byte-ID channels: base + quality * 2 + hasAdditional
        //   41 = VeryLow              42 = VeryLow + Additional
        //   43 = Low                  44 = Low + Additional
        //   45 = Medium              46 = Medium + Additional
        //   47 = High                48 = High + Additional
        public const byte PlayerAvatarVeryLowLargeChannel = 41;
        public const byte PlayerAvatarVeryLowAdditionalLargeChannel = 42;
        public const byte PlayerAvatarLowLargeChannel = 43;
        public const byte PlayerAvatarLowAdditionalLargeChannel = 44;
        public const byte PlayerAvatarMediumLargeChannel = 45;
        public const byte PlayerAvatarMediumAdditionalLargeChannel = 46;
        public const byte PlayerAvatarHighLargeChannel = 47;
        public const byte PlayerAvatarHighAdditionalLargeChannel = 48;

        /// <summary>
        /// Maps quality index (0‑3) + additional data presence → byte-ID channel.
        /// </summary>
        public static byte GetPlayerAvatarChannelForQuality(int qualityIndex, bool hasAdditionalData)
        {
            return (byte)(PlayerAvatarVeryLowChannel + qualityIndex * 2 + (hasAdditionalData ? 1 : 0));
        }

        /// <summary>
        /// Maps quality index (0‑3) + additional data presence → ushort-ID channel (for playerIDs >255).
        /// </summary>
        public static byte GetPlayerAvatarLargeChannelForQuality(int qualityIndex, bool hasAdditionalData)
        {
            return (byte)(PlayerAvatarVeryLowLargeChannel + qualityIndex * 2 + (hasAdditionalData ? 1 : 0));
        }

        /// <summary>
        /// Returns true if this channel uses ushort playerID (large variant).
        /// </summary>
        public static bool IsLargePlayerIdChannel(byte channel)
        {
            return channel == VoiceLargeChannel
                || (channel >= PlayerAvatarVeryLowLargeChannel && channel <= PlayerAvatarHighAdditionalLargeChannel);
        }

        /// <summary>
        /// Reverse mapping: channel → quality index (0‑3).
        /// Works for both byte-ID and ushort-ID avatar channels.
        /// </summary>
        public static byte GetQualityFromChannel(byte channel)
        {
            if (channel >= PlayerAvatarVeryLowLargeChannel)
                return (byte)((channel - PlayerAvatarVeryLowLargeChannel) / 2);
            return (byte)((channel - PlayerAvatarVeryLowChannel) / 2);
        }

        /// <summary>
        /// Reverse mapping: channel → has additional data.
        /// Odd offset channels carry additional data, even do not.
        /// Works for both byte-ID and ushort-ID avatar channels.
        /// </summary>
        public static bool ChannelHasAdditionalData(byte channel)
        {
            if (channel >= PlayerAvatarVeryLowLargeChannel)
                return ((channel - PlayerAvatarVeryLowLargeChannel) & 1) == 1;
            return ((channel - PlayerAvatarVeryLowChannel) & 1) == 1;
        }

        /// <summary>
        /// All 16 per-quality avatar channels (byte-ID + ushort-ID) for aggregate congestion checks.
        /// </summary>
        public static readonly byte[] PlayerAvatarQualityChannels = new byte[]
        {
            PlayerAvatarVeryLowChannel,
            PlayerAvatarVeryLowAdditionalChannel,
            PlayerAvatarLowChannel,
            PlayerAvatarLowAdditionalChannel,
            PlayerAvatarMediumChannel,
            PlayerAvatarMediumAdditionalChannel,
            PlayerAvatarHighChannel,
            PlayerAvatarHighAdditionalChannel,
            PlayerAvatarVeryLowLargeChannel,
            PlayerAvatarVeryLowAdditionalLargeChannel,
            PlayerAvatarLowLargeChannel,
            PlayerAvatarLowAdditionalLargeChannel,
            PlayerAvatarMediumLargeChannel,
            PlayerAvatarMediumAdditionalLargeChannel,
            PlayerAvatarHighLargeChannel,
            PlayerAvatarHighAdditionalLargeChannel,
        };
    }
}
