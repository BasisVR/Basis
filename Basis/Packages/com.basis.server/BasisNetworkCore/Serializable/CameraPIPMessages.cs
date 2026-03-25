using Basis.Network.Core;

public static partial class SerializableBasis
{
    /// <summary>
    /// Sent reliably when a player's PIP camera is created or destroyed.
    /// Server stores this and replays to late joiners.
    /// </summary>
    public struct CameraPIPStateMessage
    {
        public ushort PlayerID;
        public bool IsActive;
        // Position and rotation only sent when IsActive == true (initial spawn)
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float RotationX;
        public float RotationY;
        public float RotationZ;
        public float RotationW;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerID);
            writer.Put(IsActive);
            if (IsActive)
            {
                writer.Put(PositionX);
                writer.Put(PositionY);
                writer.Put(PositionZ);
                writer.Put(RotationX);
                writer.Put(RotationY);
                writer.Put(RotationZ);
                writer.Put(RotationW);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            PlayerID = reader.GetUShort();
            IsActive = reader.GetBool();
            if (IsActive)
            {
                PositionX = reader.GetFloat();
                PositionY = reader.GetFloat();
                PositionZ = reader.GetFloat();
                RotationX = reader.GetFloat();
                RotationY = reader.GetFloat();
                RotationZ = reader.GetFloat();
                RotationW = reader.GetFloat();
            }
        }
    }

    /// <summary>
    /// Position and rotation update for PIP camera.
    /// </summary>
    public struct CameraPIPPositionMessage
    {
        public ushort PlayerID;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float RotationX;
        public float RotationY;
        public float RotationZ;
        public float RotationW;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PlayerID);
            writer.Put(PositionX);
            writer.Put(PositionY);
            writer.Put(PositionZ);
            writer.Put(RotationX);
            writer.Put(RotationY);
            writer.Put(RotationZ);
            writer.Put(RotationW);
        }

        public void Deserialize(NetDataReader reader)
        {
            PlayerID = reader.GetUShort();
            PositionX = reader.GetFloat();
            PositionY = reader.GetFloat();
            PositionZ = reader.GetFloat();
            RotationX = reader.GetFloat();
            RotationY = reader.GetFloat();
            RotationZ = reader.GetFloat();
            RotationW = reader.GetFloat();
        }
    }

    /// <summary>
    /// Client -> server: camera state change (no PlayerID, server fills it from peer).
    /// </summary>
    public struct ClientCameraPIPStateMessage
    {
        public bool IsActive;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float RotationX;
        public float RotationY;
        public float RotationZ;
        public float RotationW;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(IsActive);
            if (IsActive)
            {
                writer.Put(PositionX);
                writer.Put(PositionY);
                writer.Put(PositionZ);
                writer.Put(RotationX);
                writer.Put(RotationY);
                writer.Put(RotationZ);
                writer.Put(RotationW);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            IsActive = reader.GetBool();
            if (IsActive)
            {
                PositionX = reader.GetFloat();
                PositionY = reader.GetFloat();
                PositionZ = reader.GetFloat();
                RotationX = reader.GetFloat();
                RotationY = reader.GetFloat();
                RotationZ = reader.GetFloat();
                RotationW = reader.GetFloat();
            }
        }
    }

    /// <summary>
    /// Client -> server: position and rotation update (no PlayerID, server fills it from peer).
    /// </summary>
    public struct ClientCameraPIPPositionMessage
    {
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public float RotationX;
        public float RotationY;
        public float RotationZ;
        public float RotationW;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(PositionX);
            writer.Put(PositionY);
            writer.Put(PositionZ);
            writer.Put(RotationX);
            writer.Put(RotationY);
            writer.Put(RotationZ);
            writer.Put(RotationW);
        }

        public void Deserialize(NetDataReader reader)
        {
            PositionX = reader.GetFloat();
            PositionY = reader.GetFloat();
            PositionZ = reader.GetFloat();
            RotationX = reader.GetFloat();
            RotationY = reader.GetFloat();
            RotationZ = reader.GetFloat();
            RotationW = reader.GetFloat();
        }
    }
}
