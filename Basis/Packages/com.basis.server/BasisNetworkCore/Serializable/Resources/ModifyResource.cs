using Basis.Network.Core;
public static partial class SerializableBasis
{
    /// <summary>
    /// Client→server request to change a flag on an already-spawned resource, and the
    /// server→client broadcast that applies the change on every client. Carries the static-lock
    /// state: <see cref="Static"/> freezes the item for everyone; <see cref="StaticAdminLocked"/>
    /// marks it as the admin tier (only a moderator, not the creator, may change or clear it).
    /// The trailing block (<see cref="Op"/>, <see cref="SavedToDisk"/>, optional transform) is
    /// optional on the wire so older peers that only send or read the first four fields keep working.
    /// </summary>
    public struct ModifyResource
    {
        public const byte OpStatic = 0, OpSaveToDisk = 1, OpRemoveFromDisk = 2, OpDiskState = 3;

        /// <summary>Unique network id of the spawned resource to modify.</summary>
        public string LoadedNetID;
        /// <summary>0 = GameObject, 1 = Scene (matches <see cref="LocalLoadResource.Mode"/>).</summary>
        public byte Mode;
        /// <summary>Desired frozen state (pickup disabled + frozen for props, locked out for vehicles).</summary>
        public bool Static;
        /// <summary>Desired admin tier: when true only a moderator may change it, and it implies Static.</summary>
        public bool StaticAdminLocked;
        /// <summary>What this message does: <see cref="OpStatic"/>, save to / remove from the server's disk, or a disk state update.</summary>
        public byte Op;
        /// <summary>Server→client: whether the resource is saved in the server's initial resources and returns after a restart.</summary>
        public bool SavedToDisk;
        /// <summary>True when the trailing block was present on the wire (false from older peers).</summary>
        public bool HasExtension;
        /// <summary>Client→server with <see cref="OpSaveToDisk"/>: the item's current transform, so a moved prop is saved where it is now.</summary>
        public bool HasTransform;
        public float PositionX, PositionY, PositionZ;
        public float QuaternionX, QuaternionY, QuaternionZ, QuaternionW;
        public float ScaleX, ScaleY, ScaleZ;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(LoadedNetID);
            writer.Put(Mode);
            writer.Put(Static);
            writer.Put(StaticAdminLocked);
            writer.Put(Op);
            writer.Put(SavedToDisk);
            writer.Put(HasTransform);
            if (HasTransform)
            {
                writer.Put(PositionX); writer.Put(PositionY); writer.Put(PositionZ);
                writer.Put(QuaternionX); writer.Put(QuaternionY); writer.Put(QuaternionZ); writer.Put(QuaternionW);
                writer.Put(ScaleX); writer.Put(ScaleY); writer.Put(ScaleZ);
            }
        }
        public void Deserialize(NetDataReader reader)
        {
            LoadedNetID = reader.GetString();
            Mode = reader.GetByte();
            Static = reader.GetBool();
            StaticAdminLocked = reader.GetBool();
            HasExtension = reader.AvailableBytes >= 3;
            if (!HasExtension)
            {
                Op = OpStatic;
                SavedToDisk = false;
                HasTransform = false;
                return;
            }
            Op = reader.GetByte();
            SavedToDisk = reader.GetBool();
            HasTransform = reader.GetBool() && reader.AvailableBytes >= 40;
            if (HasTransform)
            {
                PositionX = reader.GetFloat(); PositionY = reader.GetFloat(); PositionZ = reader.GetFloat();
                QuaternionX = reader.GetFloat(); QuaternionY = reader.GetFloat(); QuaternionZ = reader.GetFloat(); QuaternionW = reader.GetFloat();
                ScaleX = reader.GetFloat(); ScaleY = reader.GetFloat(); ScaleZ = reader.GetFloat();
            }
        }
    }
}
