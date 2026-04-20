using Basis.Network.Core;

public static partial class SerializableBasis
{
    /// <summary>
    /// Server -> clients: a player took a photo. No position needed —
    /// receivers look up the PIP camera transform they already track.
    /// </summary>
    public struct CameraShutterSoundMessage
    {
        public ushort PlayerID;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerID);
        }

        public void Deserialize(NetDataReader reader)
        {
            PlayerID = reader.GetUShort();
        }
    }

    /// <summary>
    /// Server -> clients: a player started a countdown timer.
    /// Receivers replay the same tick/shutter timing locally.
    /// </summary>
    public struct CameraCountdownMessage
    {
        public ushort PlayerID;
        public byte Seconds;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerID);
            writer.Put(Seconds);
        }

        public void Deserialize(NetDataReader reader)
        {
            PlayerID = reader.GetUShort();
            Seconds = reader.GetByte();
        }
    }

    /// <summary>
    /// Client -> server: local player started a countdown timer.
    /// </summary>
    public struct ClientCameraCountdownMessage
    {
        public byte Seconds;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Seconds);
        }

        public void Deserialize(NetDataReader reader)
        {
            Seconds = reader.GetByte();
        }
    }
}
