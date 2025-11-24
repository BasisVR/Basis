using LiteNetLib.Utils;
public static partial class SerializableBasis
{
    public struct VoiceReceiversMessage
    {
        public ushort[] Users;

        public void Deserialize(NetDataReader reader)
        {
            int remainingBytes = reader.AvailableBytes;

            // Optional sanity check: ensure whole ushorts
            if ((remainingBytes & (sizeof(ushort) - 1)) != 0)
            {
                BNL.LogError($"VoiceReceiversMessage: remaining bytes ({remainingBytes}) " +
                     "not a multiple of sizeof(ushort).");
            }
            else
            {
                Users = reader.GetUShortArray();
            }
        }

        public void Serialize(NetDataWriter writer)
        {
            if (Users == null || Users.Length == 0)
            {
                return;
            }
            writer.PutArray(Users);
        }
    }
}
