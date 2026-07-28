using Basis.Network.Core;
public static partial class SerializableBasis
{
    /// <summary>
    /// we call this from a client to the server
    /// </summary>
    public struct LocalLoadResource
    {
        /// <summary>
        /// 0 = Game object under prop content limits, 1 = Scene, 2 = Game object under avatar content limits.
        /// Position/rotation/scale are only serialized for mode 0.
        /// </summary>
        public byte Mode;
        /// <summary>
        /// this is a unique string that this Object is linked with over the network.
        /// </summary>
        public string LoadedNetID;
        public string UnlockPassword;
        public string CombinedURL;

        //will never remove this item from the server,
        //if off when player count on server is zero it will be removed.
        public string UUIDOfCreator;
        /// <summary>
        /// normal users can't remove these items
        /// never net written just handled by server
        /// </summary>
        public bool IsAdminLocked;

        public float PositionX;
        public float PositionY;
        public float PositionZ;

        public float QuaternionX;
        public float QuaternionY;
        public float QuaternionZ;
        public float QuaternionW;

        public float ScaleX;
        public float ScaleY;
        public float ScaleZ;

        public bool Persist;
        /// <summary>
        /// when true this item is "static": pickup is disabled for everyone and the object is
        /// frozen in place (vehicles are locked out). Server-authoritative, set via ModifyResource.
        /// </summary>
        public bool Static;
        /// <summary>
        /// when true the static lock is "admin tier": only a moderator (not the item's creator) may
        /// change or clear it, and it implies <see cref="Static"/> is also true. Distinct from
        /// <see cref="IsAdminLocked"/>, which is the removal lock.
        /// </summary>
        public bool StaticAdminLocked;
        /// <summary>
        /// this is used to state if the scale should be set or
        /// just use whatever scale it thinks it is.
        /// </summary>
        public bool ModifyScale;
        /// <summary>
        /// Determines how the resource should be loaded on clients.
        /// 0 = Immediate (spawn right away, existing behavior),
        /// 2 = Synchronized (download, report readiness, spawn when all ready or 5-min timeout),
        /// 3 = Predownload (download and cache to disc on every client, never spawn).
        /// </summary>
        public byte LoadStrategy;
        public void Deserialize(NetDataReader Writer)
        {
            Mode = Writer.GetByte();
            LoadedNetID = Writer.GetString();
            UnlockPassword = Writer.GetString();
            CombinedURL = Writer.GetString();
            UUIDOfCreator = Writer.GetString();
            IsAdminLocked = Writer.GetBool();

            Persist = Writer.GetBool();
            Static = Writer.GetBool();
            StaticAdminLocked = Writer.GetBool();
            ModifyScale = Writer.GetBool();
            LoadStrategy = Writer.GetByte();
            if (Mode == 0)
            {
                PositionX = Writer.GetFloat();
                PositionY = Writer.GetFloat();
                PositionZ = Writer.GetFloat();

                QuaternionX = Writer.GetFloat();
                QuaternionY = Writer.GetFloat();
                QuaternionZ = Writer.GetFloat();
                QuaternionW = Writer.GetFloat();

                ScaleX = Writer.GetFloat();
                ScaleY = Writer.GetFloat();
                ScaleZ = Writer.GetFloat();
            }

        }
        public void Serialize(NetDataWriter Writer)
        {
            Writer.Put(Mode);
            Writer.Put(LoadedNetID);
            Writer.Put(UnlockPassword);
            Writer.Put(CombinedURL);
            Writer.Put(UUIDOfCreator);
            Writer.Put(IsAdminLocked);
            Writer.Put(Persist);
            Writer.Put(Static);
            Writer.Put(StaticAdminLocked);
            Writer.Put(ModifyScale);
            Writer.Put(LoadStrategy);
            if (Mode == 0)
            {
                Writer.Put(PositionX);
                Writer.Put(PositionY);
                Writer.Put(PositionZ);

                Writer.Put(QuaternionX);
                Writer.Put(QuaternionY);
                Writer.Put(QuaternionZ);
                Writer.Put(QuaternionW);

                Writer.Put(ScaleX);
                Writer.Put(ScaleY);
                Writer.Put(ScaleZ);
            }
        }
    }

    /// <summary>
    /// Sent from client to server to report preload readiness for a synchronized load.
    /// </summary>
    public struct PreloadReadyMessage
    {
        /// <summary>
        /// The LoadedNetID of the resource this readiness report is for.
        /// </summary>
        public string LoadedNetID;
        /// <summary>
        /// True if the client successfully preloaded the content, false if it failed or timed out.
        /// </summary>
        public bool IsReady;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(LoadedNetID);
            writer.Put(IsReady);
        }
        public void Deserialize(NetDataReader reader)
        {
            LoadedNetID = reader.GetString();
            IsReady = reader.GetBool();
        }
    }

    /// <summary>
    /// Sent from server to all clients to signal that a preloaded resource should now be spawned.
    /// </summary>
    public struct SpawnPreloadedMessage
    {
        /// <summary>
        /// The LoadedNetID of the resource to spawn.
        /// </summary>
        public string LoadedNetID;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(LoadedNetID);
        }
        public void Deserialize(NetDataReader reader)
        {
            LoadedNetID = reader.GetString();
        }
    }
}
