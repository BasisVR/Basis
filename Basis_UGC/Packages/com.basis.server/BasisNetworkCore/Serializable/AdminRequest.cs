using LiteNetLib.Utils;
namespace BasisNetworkCore.Serializable
{
    public static partial class SerializableBasis
    {
        public struct AdminRequest
        {
            private byte messageIndex;
            public AdminRequestMode GetAdminRequestMode()
            {
                return (AdminRequestMode)messageIndex;
            }
            public void Deserialize(NetDataReader reader)
            {
                int bytesAvailable = reader.AvailableBytes;
                if (bytesAvailable > 0)
                {
                    messageIndex = reader.GetByte();
                }
                else
                {
                    BNL.LogError($"Unable to read remaining bytes, available: {bytesAvailable}");
                }
            }

            public void Serialize(NetDataWriter writer, AdminRequestMode AdminRequestMode)
            {
                messageIndex = (byte)AdminRequestMode;
                writer.Put(messageIndex);
            }
        }
        public enum AdminRequestMode : byte
        {
            Ban,//bans a player
            Kick,//kicks a player
            IpAndBan,// bans and ip bans a player
            Message,// sends a message to a user
            MessageAll,// sends a message to all users
            UnBanIP,// unbans a user and unbans a associated ip
            UnBan,// unbans a user
          //  RequestBannedPlayers,// gets a list of banned players
           // TeleportTo,// teleport to a player
            TeleportAll,// teleports everyone
            AddAdmin,
            RemoveAdmin,
            TeleportPlayer,
        }
    }
}
