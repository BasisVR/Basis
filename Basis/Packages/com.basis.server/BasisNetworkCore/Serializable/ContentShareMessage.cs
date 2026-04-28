using Basis.Network.Core;

public static partial class SerializableBasis
{
    /// <summary>
    /// Content types that can be shared via a content sphere.
    /// </summary>
    public enum ContentShareType : byte
    {
        Avatar = 0,
        Prop = 1,
        World = 2
    }

    /// <summary>
    /// Sent by a client to drop a content share sphere into the world.
    /// Contains everything another player needs to load the content.
    /// </summary>
    public struct ContentShareMessage
    {
        /// <summary>
        /// Unique ID for this sphere instance (GUID string).
        /// </summary>
        public string SphereNetID;

        /// <summary>
        /// The URL to the content bundle.
        /// </summary>
        public string ContentURL;

        /// <summary>
        /// Password to unlock/decrypt the content bundle.
        /// </summary>
        public string UnlockPassword;

        /// <summary>
        /// What kind of content this sphere represents.
        /// </summary>
        public ContentShareType ContentType;

        /// <summary>
        /// World position where the sphere was dropped.
        /// </summary>
        public float PositionX;
        public float PositionY;
        public float PositionZ;

        public void Deserialize(NetDataReader reader)
        {
            SphereNetID = reader.GetString();
            ContentURL = reader.GetString();
            UnlockPassword = reader.GetString();
            ContentType = (ContentShareType)reader.GetByte();
            PositionX = reader.GetFloat();
            PositionY = reader.GetFloat();
            PositionZ = reader.GetFloat();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(SphereNetID);
            writer.Put(ContentURL);
            writer.Put(UnlockPassword);
            writer.Put((byte)ContentType);
            writer.Put(PositionX);
            writer.Put(PositionY);
            writer.Put(PositionZ);
        }
    }

    /// <summary>
    /// Server wraps the client's ContentShareMessage with the sender's player ID
    /// and authoritative identity (UUID + display name) resolved from saved state.
    /// </summary>
    public struct ServerContentShareMessage
    {
        public PlayerIdMessage playerIdMessage;
        public string SharerUUID;
        public string SharerDisplayName;
        public ContentShareMessage contentShareMessage;

        public void Deserialize(NetDataReader reader)
        {
            playerIdMessage.Deserialize(reader);
            SharerUUID = reader.GetString();
            SharerDisplayName = reader.GetString();
            contentShareMessage.Deserialize(reader);
        }

        public void Serialize(NetDataWriter writer)
        {
            playerIdMessage.Serialize(writer);
            writer.Put(SharerUUID ?? string.Empty);
            writer.Put(SharerDisplayName ?? string.Empty);
            contentShareMessage.Serialize(writer);
        }
    }

    /// <summary>
    /// Sent to remove a content share sphere from the world.
    /// </summary>
    public struct ContentShareCleanupMessage
    {
        public string SphereNetID;

        public void Deserialize(NetDataReader reader)
        {
            SphereNetID = reader.GetString();
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(SphereNetID);
        }
    }

    /// <summary>
    /// Server wraps cleanup with sender player ID.
    /// </summary>
    public struct ServerContentShareCleanupMessage
    {
        public PlayerIdMessage playerIdMessage;
        public ContentShareCleanupMessage contentShareCleanupMessage;

        public void Deserialize(NetDataReader reader)
        {
            playerIdMessage.Deserialize(reader);
            contentShareCleanupMessage.Deserialize(reader);
        }

        public void Serialize(NetDataWriter writer)
        {
            playerIdMessage.Serialize(writer);
            contentShareCleanupMessage.Serialize(writer);
        }
    }
}
