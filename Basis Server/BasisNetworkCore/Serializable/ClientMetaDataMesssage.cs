using System;
using System.Collections.Generic;
using Basis.Network.Core;
public static partial class SerializableBasis
{
    /// <summary>
    /// contains all necessary data to go along with the players return message locally.
    /// this message is more async and the goal is that you can use it to change things about a local player.
    /// </summary>
    public struct ServerMetaDataMessage
    {
        public ClientMetaDataMessage ClientMetaDataMessage;

        public int SyncInterval;
        public int BaseMultiplier;
        public float IncreaseRate;
        public float SlowestSendRate;
        public int PeerLimit;
        //want to include what permissions this player has to the client
        public byte[] PermissionsBitset;     // fast, fixed — known nodes as bits
        public string[] ExtraPermissions;    // dynamic fallback — compressed on the wire

        /// <summary>
        /// Populate bitset + extras from a collection of allowed permission node strings.
        /// Call this on the server before serializing.
        /// </summary>
        public void SetPermissions(IReadOnlyCollection<string> allowedNodes, IReadOnlyCollection<string> deniedNodes = null)
        {
            PermissionBitsetMap.Encode(allowedNodes, out PermissionsBitset, out ExtraPermissions, deniedNodes);
        }

        /// <summary>
        /// Decode bitset + extras back into the full set of permission node strings.
        /// Call this on the client after deserializing.
        /// </summary>
        public HashSet<string> GetPermissions()
        {
            return PermissionBitsetMap.Decode(PermissionsBitset, ExtraPermissions);
        }

        public void Deserialize(NetDataReader Writer)
        {
            ClientMetaDataMessage.Deserialize(Writer);

            Writer.Get(out SyncInterval);
            Writer.Get(out BaseMultiplier);
            Writer.Get(out IncreaseRate);
            Writer.Get(out SlowestSendRate);
            Writer.Get(out PeerLimit);

            // Permissions (backward compatible — skip if no more data)
            if (Writer.AvailableBytes > 0)
            {
                PermissionsBitset = Writer.GetBytesWithLength();

                Writer.Get(out ushort extraCount);
                if (extraCount > 0)
                {
                    byte[] compressed = Writer.GetBytesWithLength();
                    ExtraPermissions = PermissionCompression.DecompressExtras(compressed, extraCount);
                }
                else
                {
                    ExtraPermissions = Array.Empty<string>();
                }
            }
            else
            {
                PermissionsBitset = Array.Empty<byte>();
                ExtraPermissions = Array.Empty<string>();
            }
        }
        public void Serialize(NetDataWriter Writer)
        {
            ClientMetaDataMessage.Serialize(Writer);

            if (SyncInterval == 0)
            {
                SyncInterval = 50;
                BNL.LogError("SyncInterval was not set! ");
            }
            if (BaseMultiplier == 0)
            {
                BaseMultiplier = 1;
                BNL.LogError("Base Multiplier was not set! ");
            }
            if (IncreaseRate == 0)
            {
                IncreaseRate = 0.005f;
                BNL.LogError("IncreaseRate was not set! ");
            }
            if (SlowestSendRate == 0)
            {
                SlowestSendRate = 2.55f;
                BNL.LogError("Slowest Send Rate was not set!");
            }

            Writer.Put(SyncInterval);
            Writer.Put(BaseMultiplier);
            Writer.Put(IncreaseRate);
            Writer.Put(SlowestSendRate);
            Writer.Put(PeerLimit);

            // Permissions bitset (ushort length prefix + raw bytes via PutBytesWithLength)
            Writer.PutBytesWithLength(PermissionsBitset ?? Array.Empty<byte>());

            // Extra permissions (compressed)
            ushort extraCount = (ushort)(ExtraPermissions != null ? ExtraPermissions.Length : 0);
            Writer.Put(extraCount);
            if (extraCount > 0)
            {
                byte[] compressed = PermissionCompression.CompressExtras(ExtraPermissions);
                Writer.PutBytesWithLength(compressed);
            }
        }
    }
}
