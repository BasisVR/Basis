using Basis.Network.Core;
using System;

public static partial class SerializableBasis
{
    [Flags]
    public enum BasisMessageFlags : byte
    {
        None = 0,
        /// <summary>Rides a shared plugin channel (61-63) with a leading [messageId:2] prefix; core messages clear this and own a dedicated channel.</summary>
        Multiplexed = 1 << 0,
        /// <summary>The client must bind a handler for this id or the server disconnects it.</summary>
        Required = 1 << 1,
        /// <summary>The server is allowed to send this message to clients.</summary>
        ServerToClient = 1 << 2,
        /// <summary>Clients are allowed to send this message to the server.</summary>
        ClientToServer = 1 << 3,
    }

    /// <summary>
    /// One row of the message registry the server supplies to a client on connect.
    /// Binds a stable Name to a wire Id/Channel so handlers can be added or removed
    /// without recompiling a shared constant table.
    /// </summary>
    [System.Serializable]
    public struct BasisMessageDescriptor
    {
        /// <summary>Flat message id. For a core message this equals its dedicated Channel (0-59); for a multiplexed plugin message it is a dense ushort used as the [messageId:2] payload prefix.</summary>
        public ushort Id;
        /// <summary>Payload schema version; a client compares it against its local handler and ignores or refuses the id on mismatch.</summary>
        public byte Version;
        /// <summary>LiteNetLib channel this message travels on (its own for core, one of 61-63 for a multiplexed plugin).</summary>
        public byte Channel;
        /// <summary>BasisMessageFlags bitfield.</summary>
        public byte Flags;
        /// <summary>Stable string identity, e.g. "basis.core.voice" or "com.acme.plugin.foo".</summary>
        public string Name;

        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(Id);
            writer.Put(Version);
            writer.Put(Channel);
            writer.Put(Flags);
            writer.Put(Name);
        }

        public bool Deserialize(NetDataReader reader)
        {
            if (!reader.TryGetUShort(out Id)) { BNL.LogError("BasisMessageDescriptor: missing Id"); return false; }
            if (!reader.TryGetByte(out Version)) { BNL.LogError("BasisMessageDescriptor: missing Version"); return false; }
            if (!reader.TryGetByte(out Channel)) { BNL.LogError("BasisMessageDescriptor: missing Channel"); return false; }
            if (!reader.TryGetByte(out Flags)) { BNL.LogError("BasisMessageDescriptor: missing Flags"); return false; }
            if (!reader.TryGetString(out Name)) { BNL.LogError("BasisMessageDescriptor: missing Name"); return false; }
            return true;
        }
    }

    /// <summary>
    /// Server to client on RegistryControlChannel (sub-type RegistrySub_Supply): the full
    /// set of message types this server understands this session. The client binds each
    /// descriptor to a local handler by Name; anything it cannot bind it will not decode.
    /// </summary>
    [System.Serializable]
    public struct BasisMessageSupply
    {
        public BasisMessageDescriptor[] Descriptors;

        public readonly void Serialize(NetDataWriter writer)
        {
            ushort count = (ushort)(Descriptors?.Length ?? 0);
            writer.Put(count);
            for (int i = 0; i < count; i++)
            {
                Descriptors[i].Serialize(writer);
            }
        }

        public bool Deserialize(NetDataReader reader)
        {
            if (!reader.TryGetUShort(out ushort count))
            {
                BNL.LogError("BasisMessageSupply: missing count");
                Descriptors = Array.Empty<BasisMessageDescriptor>();
                return false;
            }

            // Each descriptor costs at least a byte on the wire.
            if (count > reader.AvailableBytes)
            {
                BNL.LogError($"BasisMessageSupply: count {count} exceeds available {reader.AvailableBytes}");
                Descriptors = Array.Empty<BasisMessageDescriptor>();
                return false;
            }

            Descriptors = new BasisMessageDescriptor[count];
            for (int i = 0; i < count; i++)
            {
                if (!Descriptors[i].Deserialize(reader))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Client to server on RegistryControlChannel (sub-type RegistrySub_Subscribe): the
    /// message ids the client has a handler for. The server records this per-peer so it can
    /// skip payloads a client cannot decode and enforce Required ids.
    /// </summary>
    [System.Serializable]
    public struct BasisMessageSubscribe
    {
        public ushort[] Ids;

        public readonly void Serialize(NetDataWriter writer)
        {
            ushort count = (ushort)(Ids?.Length ?? 0);
            writer.Put(count);
            for (int i = 0; i < count; i++)
            {
                writer.Put(Ids[i]);
            }
        }

        public bool Deserialize(NetDataReader reader)
        {
            if (!reader.TryGetUShort(out ushort count))
            {
                BNL.LogError("BasisMessageSubscribe: missing count");
                Ids = Array.Empty<ushort>();
                return false;
            }

            // Each id is 2 bytes on the wire.
            if (count * 2 > reader.AvailableBytes)
            {
                BNL.LogError($"BasisMessageSubscribe: count {count} exceeds available {reader.AvailableBytes}");
                Ids = Array.Empty<ushort>();
                return false;
            }

            Ids = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                if (!reader.TryGetUShort(out Ids[i]))
                {
                    BNL.LogError("BasisMessageSubscribe: truncated id list");
                    return false;
                }
            }
            return true;
        }
    }
}
