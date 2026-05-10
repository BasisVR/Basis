using Basis.Network.Core;
public static partial class SerializableBasis
{
    /// <summary>
    /// Single library entry the server pushes to a client on connect.
    /// Mode follows BundledContentHolder.Mode on the client: 0=Avatar, 1=World, 2=Prop.
    /// </summary>
    public struct ServerLibraryItem
    {
        public byte Mode;
        public string Url;
        public string Password;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(Mode);
            writer.Put(Url ?? string.Empty);
            writer.Put(Password ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            Mode = reader.GetByte();
            Url = reader.GetString();
            Password = reader.GetString();
        }
    }

    /// <summary>
    /// Wraps the full default-library list. Sent once per client on connect over
    /// BasisNetworkCommons.ServerLibraryChannel. Empty array is valid (means: no defaults).
    /// </summary>
    public struct ServerLibraryMessage
    {
        public ServerLibraryItem[] Items;

        public void Serialize(NetDataWriter writer)
        {
            int count = Items?.Length ?? 0;
            writer.Put((ushort)count);
            for (int i = 0; i < count; i++)
            {
                Items[i].Serialize(writer);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            ushort count = reader.GetUShort();
            Items = new ServerLibraryItem[count];
            for (int i = 0; i < count; i++)
            {
                Items[i].Deserialize(reader);
            }
        }
    }
}
