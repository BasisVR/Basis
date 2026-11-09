namespace Basis.Network.Core
{
    public class BasisNetworkVersion
    {
        // 52: restricted-DOF bone encoding — 2-DOF limb/extremity joints and 1-DOF toes ship
        // quantized angles instead of smallest-three quaternions (wire-format change).
        // 53: hybrid avatar-bundle codec and developer CompactMerged framing. Byte 0 of the
        // channel-52 bundle header was a message count that every decoder documented as a hint
        // and none read; it now carries the codec id and dictionary generation.
        // 54: CompactMerged mixed framing adds raw Ack/Channeled entries (wire-format change).
        // 55: connection request carries the client's company and product name after the
        // protocol version; the server rejects any pair it does not support (wire-format change).
        // ServerUUID is an optional trailing ServerMetaDataMessage field: protocol-55 clients
        // ignore it, while newer clients treat its absence from a protocol-55 server as empty.
        // 56: DID login is bound to the connection. The connection request carries the client's
        // X25519 key before the ReadyMessage, the challenge adds the server's X25519 key and
        // ServerUUID, the signature covers BasisDidLoginTranscript, and client-to-server user data
        // is sealed with the derived key (wire-format change).
        // Opt-ins on 56: the connection request may end with one capability byte after the
        // ReadyMessage. Servers that predate it stop reading at the end of the ReadyMessage and
        // never see it, so they keep sending the original format.
        // CapabilityBatchedOwnership: the ownership list a joining player receives is batched,
        // several entries per packet on ChangeCurrentOwnerRequestChannel; clients read entries
        // until the packet is empty, which also reads the one-entry packets older servers send.
        // CapabilityProtectionSettings: the client understands GlobalGetProtectionSettings.
        // 57: GlobalGetLockState ends with the shared image resolution limit (after VideoClipsLocked),
        // alongside the video clip, avatar hosting and screen share messages. The capability byte above
        // still applies.
        // 58: avatar, prop, world and share records no longer carry the unlock password. Receivers ask
        // through the basis.contentkeys plugin and get the file's keys from anyone holding them, sealed
        // to the requester and checked against the hash the content's origin published.
        public static ushort ServerVersion = 58;
        public static ushort LegacyLoginVersion = 55;
        // Newest protocol whose records still carry unlock passwords. A current client joins a server
        // on it with the current login, asks for this version, and sends passwords the old way.
        public static ushort PasswordRecordsVersion = 57;
        public static bool TryGetFallback(ushort serverVersion, out bool legacyLogin, out ushort protocolVersion)
        {
            legacyLogin = serverVersion == LegacyLoginVersion;
            protocolVersion = serverVersion == PasswordRecordsVersion ? serverVersion : (ushort)0;
            return legacyLogin || protocolVersion != 0;
        }
        public static bool SendsUnlockPasswords(bool legacyLogin, ushort protocolVersion) => legacyLogin || (protocolVersion != 0 && protocolVersion <= PasswordRecordsVersion);
        public const byte CapabilityBatchedOwnership = 1, CapabilityProtectionSettings = 2;
        public const byte ClientCapabilities = CapabilityBatchedOwnership | CapabilityProtectionSettings;
        public static byte ReadCapabilities(NetDataReader reader) => reader != null && reader.TryGetByte(out byte flags) ? flags : (byte)0;
    }
}
