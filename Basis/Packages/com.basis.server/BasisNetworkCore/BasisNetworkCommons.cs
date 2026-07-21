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

        // ── Avatar send-interval byte ────────────────────────────────────────
        // The per-receiver interval byte in avatar keyframe/delta frames encodes the send
        // cadence relative to the server's base interval. 0..199 map 1:1 (base+b ms, the
        // pre-v42 range); 200..255 step 12 ms each (base+200 .. base+860 ms) so very distant
        // receivers can drop below the old 3.3 Hz floor while staying inside the receiver's
        // 1 s interpolation-window clamp.
        public const byte AvatarIntervalExtendedStart = 200;
        public const int AvatarIntervalExtendedStepMs = 12;

        public static byte EncodeAvatarIntervalByte(int intervalMs, int baseIntervalMs)
        {
            int rel = intervalMs - baseIntervalMs;
            if (rel <= 0) return 0;
            if (rel < AvatarIntervalExtendedStart) return (byte)rel;
            int steps = (rel - AvatarIntervalExtendedStart + (AvatarIntervalExtendedStepMs >> 1)) / AvatarIntervalExtendedStepMs;
            int maxSteps = byte.MaxValue - AvatarIntervalExtendedStart;
            if (steps > maxSteps) steps = maxSteps;
            return (byte)(AvatarIntervalExtendedStart + steps);
        }

        public static int DecodeAvatarIntervalMs(byte encoded, int baseIntervalMs)
        {
            if (encoded < AvatarIntervalExtendedStart) return baseIntervalMs + encoded;
            return baseIntervalMs + AvatarIntervalExtendedStart
                 + (encoded - AvatarIntervalExtendedStart) * AvatarIntervalExtendedStepMs;
        }

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

        // AvatarChangeMessageChannel carries two message kinds, discriminated by a leading byte, because
        // all 64 LiteNetLib channels are assigned and a body-fit update has nowhere else to go that is
        // both server-stored and replayed to late joiners. Both kinds update the same saved record.
        /// <summary>Full avatar change: ClientAvatarChangeMessage / ServerAvatarChangeMessage.</summary>
        public const byte AvatarChangeKindFull = 0;
        /// <summary>Body-fit-only update: ClientBodyFitMessage / ServerBodyFitMessage. No avatar reload.</summary>
        public const byte AvatarChangeKindBodyFit = 1;
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
        /// <summary>
        /// Modify an already-spawned resource's flags (e.g. Static). Client→server request;
        /// the server authorizes (item creator or moderator) then rebroadcasts to all clients.
        /// Id is 55 (not contiguous with the other resource channels) because 29-54 were
        /// already taken when this was added.
        /// </summary>
        public const byte ModifyResourceChannel = 55;

        // ── Content sharing (multiplexed: first payload byte = ContentShareSub_*) ──
        /// <summary>Content share operations. First payload byte selects a ContentShareSub_* sub-type.</summary>
        public const byte ContentShareChannel = 29;
        /// <summary>ContentShareChannel sub-type: drop a content sphere.</summary>
        public const byte ContentShareSub_Drop = 0;
        /// <summary>ContentShareChannel sub-type: remove/cleanup content spheres.</summary>
        public const byte ContentShareSub_Cleanup = 1;

        // ── Avatar delta (server → client only) ──────────────────────────────
        /// <summary>
        /// Server→client avatar delta frames (reclaimed from the former ContentShareCleanupChannel).
        /// Keyframes stay on the per-quality avatar channels (6-13 / 41-48) unchanged; this channel
        /// carries only deltas against each sender's last keyframe. Wire:
        ///   [header:1][playerId:1|2][interval:1][sequence:1][baseSeq:1][delta body]
        /// header bits: quality(2) | hasAdditional&lt;&lt;2 | largeId&lt;&lt;3. Unreliable, server-only.
        /// </summary>
        public const byte DeltaAvatarChannel = 30;

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
        // Wire (client→server): [eventType:1][intervalMs:2]
        // Wire (server→client): [eventType:1][senderId:2][intervalMs:2]
        public const byte EventType_AvatarRateChange = 3;
        /// <summary>Per-player talk mode for nameplate coloring (Normal/Private/Direct/ThisPerson/Shout).</summary>
        // Wire (client→server): [eventType:1][modeByte:1]
        // Wire (server→client): [eventType:1][senderId:2][modeByte:1]
        public const byte EventType_TalkModeChanged = 4;
        /// <summary>Per-player self-mute state for nameplate coloring.</summary>
        // Wire (client→server): [eventType:1][muted:1]
        // Wire (server→client): [eventType:1][senderId:2][muted:1]
        public const byte EventType_MuteStateChanged = 5;
        /// <summary>Transient chat typing state for a remote player.</summary>
        public const byte EventType_PlayerChatTyping = 6;
        /// <summary>
        /// Client→server one-shot error/exception report (first sighting only). The server
        /// attaches identity from connect metadata and stores it to disk; never rebroadcast.
        /// Wire: [eventType:1][severity:1][lenPrefixed PermissionCompression blob of (system, message, stack)]
        /// </summary>
        public const byte EventType_ErrorReport = 7;
        /// <summary>
        /// Recorder→recordee request to record the recordee's voice. Forwarded to the
        /// target peer only, stamped with the requester's peer id.
        /// </summary>
        // Wire (client→server): [eventType:1][targetId:2]
        // Wire (server→target): [eventType:1][requesterId:2]
        public const byte EventType_VoiceRecordRequest = 8;
        /// <summary>
        /// Recordee→recorder consent decision for a voice-record request
        /// (0 = denied, 1 = granted, 2 = revoked). Forwarded to the target peer only,
        /// stamped with the responder's peer id.
        /// </summary>
        // Wire (client→server): [eventType:1][targetId:2][state:1]
        // Wire (server→target): [eventType:1][responderId:2][state:1]
        public const byte EventType_VoiceRecordConsent = 9;

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

        // ── Compressed avatar bundle (server → client only) ──────────────────
        /// <summary>
        /// Server-only outbound channel carrying multiple avatar quality messages
        /// to a single receiver, LZ4-compressed into one UDP datagram that fits the peer MTU.
        /// Wire format:
        ///   [count:1][rawLen:2-LE][LZ4 block( [origChannel:1][msgLen:2-LE][bytes]* )]
        /// Each inner [origChannel] is the byte-id or ushort-id avatar quality channel
        /// the message would have been sent on individually (channels 6-13 / 41-48).
        /// Compression: LZ4Codec.Encode at LZ4Level.L00_FAST (K4os.Compression.LZ4 1.3.x).
        /// </summary>
        public const byte CompressedAvatarBundleChannel = 52;

        // ── Server-provided default library ──────────────────────────────────
        /// <summary>
        /// Server pushes a list of default library entries (avatars / props / worlds)
        /// to each client on connect. Items live in the client's library only while
        /// connected to that server and are cleared on disconnect.
        /// </summary>
        public const byte ServerLibraryChannel = 53;

        // ── Peer-to-peer direct connection ───────────────────────────────────
        // First byte of payload selects a P2PSub_* sub-type; remaining bytes are
        // the BasisP2PSignalMessage body. Reliable-ordered.
        public const byte P2PChannel = 54;

        public const byte P2PSub_Request = 0;
        public const byte P2PSub_Accept = 1;
        public const byte P2PSub_Decline = 2;
        public const byte P2PSub_Cancel = 3;
        public const byte P2PSub_LinkLost = 4;
        public const byte P2PSub_ServerArmed = 5;
        public const byte P2PSub_LinkUp = 6;
        // Server → both peers once both sides reported LinkUp and it began offloading the
        // pair. Positive confirmation the direct link is fully up; a client that stays
        // Connected without ever seeing this treats its link as partial (server fallback).
        public const byte P2PSub_Offloaded = 7;

        // ── Direct-connect custom data (P2P-first, server fallback) ──────────
        /// <summary>P2P world/prop direct custom data. Frame: [messageIndex:2][payload].</summary>
        public const byte DirectSceneChannel = 56;
        /// <summary>Server relay of a direct-origin scene message (recipients with no direct link).</summary>
        public const byte DirectSceneServerChannel = 57;
        /// <summary>P2P avatar direct custom data. Frame: [messageIndex:1][avatarLinkIndex:1][payload].</summary>
        public const byte DirectAvatarChannel = 58;
        /// <summary>Server relay of a direct-origin avatar message (recipients with no direct link).</summary>
        public const byte DirectAvatarServerChannel = 59;

        // ── Dynamic message registry (subscribe & supply) ────────────────────
        // Channels 60-63 carry the dynamic message layer negotiated at connect.
        // 60 negotiates the registry; 61-63 multiplex plugin payloads keyed by a
        // ushort message id, one channel per delivery semantic. Core channels
        // 0-59 keep their own dedicated channels and ordering streams.
        /// <summary>Registry handshake. First payload byte is a RegistrySub_* sub-type.</summary>
        public const byte RegistryControlChannel = 60;
        /// <summary>Reliable-ordered plugin payloads. Frame: [messageId:2][payload].</summary>
        public const byte PluginReliableChannel = 61;
        /// <summary>Sequenced plugin payloads. Frame: [messageId:2][payload].</summary>
        public const byte PluginSequencedChannel = 62;
        /// <summary>Unreliable plugin payloads. Frame: [messageId:2][payload].</summary>
        public const byte PluginUnreliableChannel = 63;

        /// <summary>RegistryControlChannel sub-type: server to client full descriptor manifest.</summary>
        public const byte RegistrySub_Supply = 0;
        /// <summary>RegistryControlChannel sub-type: client to server list of message ids it can handle.</summary>
        public const byte RegistrySub_Subscribe = 1;

        /// <summary>Maps a plugin DeliveryMethod to its multiplexed channel (61-63). Returns RegistryControlChannel for unmapped values.</summary>
        public static byte GetPluginChannelForDelivery(DeliveryMethod delivery)
        {
            switch (delivery)
            {
                case DeliveryMethod.ReliableOrdered:
                case DeliveryMethod.ReliableUnordered:
                case DeliveryMethod.ReliableSequenced:
                    return PluginReliableChannel;
                case DeliveryMethod.Sequenced:
                    return PluginSequencedChannel;
                case DeliveryMethod.Unreliable:
                    return PluginUnreliableChannel;
                default:
                    return RegistryControlChannel;
            }
        }

        /// <summary>Canonical DeliveryMethod for a multiplexed plugin channel (reverse of GetPluginChannelForDelivery).</summary>
        public static DeliveryMethod GetDeliveryForPluginChannel(byte channel)
        {
            switch (channel)
            {
                case PluginSequencedChannel:
                    return DeliveryMethod.Sequenced;
                case PluginUnreliableChannel:
                    return DeliveryMethod.Unreliable;
                default:
                    return DeliveryMethod.ReliableOrdered;
            }
        }

        /// <summary>True if the channel is one of the multiplexed plugin channels (61-63) carrying a [messageId:2] prefix.</summary>
        public static bool IsPluginChannel(byte channel)
        {
            return channel >= PluginReliableChannel && channel <= PluginUnreliableChannel;
        }

        // ── Server info unconnected query ────────────────────────────────────
        // Out-of-band UDP probe: a client can hit the server's port without
        // authenticating and get back a name/online/max/MOTD payload — same
        // shape as a Minecraft server-list-ping. Travels via LiteNetLib's
        // SendUnconnectedMessage so it never enters the channel/peer pipeline.
        /// <summary>Magic header for the unconnected info query packet from the client.</summary>
        public const uint ServerInfoQueryMagic = 0xBA515101u;
        /// <summary>Magic header for the unconnected info response packet from the server.</summary>
        public const uint ServerInfoResponseMagic = 0xBA515102u;
        /// <summary>Wire-format version for the info query payload. Bump when the layout changes.</summary>
        public const ushort ServerInfoProtocolVersion = 1;
        /// <summary>Hard cap on the server name length the client/server will read or write.</summary>
        public const int ServerInfoNameMaxLength = 64;
        /// <summary>Hard cap on the MOTD length the client/server will read or write.</summary>
        public const int ServerInfoMotdMaxLength = 256;
        /// <summary>
        /// Minimum total request size (in bytes) the server will accept on an
        /// unconnected info query. Clients pad their query up to this size with
        /// zeros so the response is never larger than the request — that removes
        /// the bandwidth-amplification factor that makes UDP discovery protocols
        /// attractive as DDoS reflectors. Worst-case response is ~340 bytes
        /// (full-length name + MOTD), so 384 keeps the amp ratio &lt; 1.
        /// </summary>
        public const int ServerInfoMinRequestBytes = 384;

        // ── Structured connection-reject payload ─────────────────────────────
        // Attached by the server to ConnectionRequest.Reject so the client can react specifically
        // (e.g. an "Update Required" screen, a "Server Full" notice) instead of printing a bare reason
        // string. A 4-byte magic distinguishes a structured reject from a plain reason string — older
        // servers send a bare string (no magic), which the client still surfaces via the legacy path.
        // Wire: [magic:uint][kind:byte][aux0:ushort][aux1:ushort][message:string]
        //   VersionMismatch → aux0 = server protocol version, aux1 = client protocol version.
        //   ServerFull      → aux0/aux1 unused (0); any counts are in the message.
        /// <summary>Bare rejection reason emitted when password authentication fails.</summary>
        public const string AuthenticationRejectedReason = "Authentication failed, Auth rejected";
        /// <summary>Marker for a structured reject payload. "BA51 5CE1" ≈ "Basis reject".</summary>
        public const uint RejectMagic = 0xBA515CE1u;
        /// <summary>RejectKind: the client's protocol version does not match the server's.</summary>
        public const byte RejectKind_VersionMismatch = 1;
        /// <summary>RejectKind: the server has reached its player limit.</summary>
        public const byte RejectKind_ServerFull = 2;

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

        // ── DeltaAvatarChannel header helpers ────────────────────────────────
        // The delta channel is a single channel for all quality/id-width/additional combinations;
        // that metadata (which is channel-encoded for keyframes) lives in a 1-byte header instead.
        /// <summary>Packs quality(0-3) + additional + large-id into the DeltaAvatarChannel header byte.</summary>
        public static byte BuildDeltaHeader(int qualityIndex, bool hasAdditionalData, bool largeId)
        {
            return (byte)((qualityIndex & 0x3) | (hasAdditionalData ? 0x4 : 0) | (largeId ? 0x8 : 0));
        }
        /// <summary>Quality index (0-3) from a DeltaAvatarChannel header byte.</summary>
        public static byte DeltaHeaderQuality(byte header) => (byte)(header & 0x3);
        /// <summary>Additional-data flag from a DeltaAvatarChannel header byte.</summary>
        public static bool DeltaHeaderHasAdditionalData(byte header) => (header & 0x4) != 0;
        /// <summary>Large (ushort) player-id flag from a DeltaAvatarChannel header byte.</summary>
        public static bool DeltaHeaderLargeId(byte header) => (header & 0x8) != 0;

        // Control frames on DeltaAvatarChannel (v42): header bit 7 marks a non-delta control
        // message so keyframe recovery is request-driven instead of purely periodic.
        //   client→server KeyframeRequest: [hdr=0x80][senderId:ushort]  "my baseline for this
        //     sender is missing/stale — force my next frame from them to be a keyframe".
        //   server→client UplinkKeyframeRequest: [hdr=0xC0]  "your uplink baseline is missing —
        //     make your next avatar send a full keyframe".
        public const byte DeltaHeaderControlBit = 0x80;
        public const byte DeltaControlKeyframeRequest = 0x80;
        public const byte DeltaControlUplinkKeyframeRequest = 0xC0;
        /// <summary>True when a DeltaAvatarChannel first byte is a control frame, not a delta.</summary>
        public static bool IsDeltaControlHeader(byte header) => (header & DeltaHeaderControlBit) != 0;

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
