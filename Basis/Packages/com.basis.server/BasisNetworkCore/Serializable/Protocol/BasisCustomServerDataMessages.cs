using System;
using Basis.Network.Core;

public static partial class SerializableBasis
{
    [Serializable]
    public struct CustomServerDataSubscribeRequest
    {
        public string ChannelName;
        public Guid RequestID;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ChannelName);
            writer.Put(RequestID);
        }

        public bool Deserialize(NetDataReader reader)
        {
            if (reader.TryGetString(out ChannelName) && reader.AvailableBytes >= 16)
            {
                RequestID = reader.GetGuid();
                return true;
            }

            return false;
        }
    }

    [Serializable]
    public struct CustomServerDataUnsubscribeRequest
    {
        public string ChannelName;
        public Guid RequestID;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ChannelName);
            writer.Put(RequestID);
        }

        public bool Deserialize(NetDataReader reader)
        {
            if (reader.TryGetString(out ChannelName) && reader.AvailableBytes >= 16)
            {
                RequestID = reader.GetGuid();
                return true;
            }

            return false;
        }
    }

    [Serializable]
    public struct CustomServerDataMessage
    {
        public string ChannelName;
        public byte[] Data;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ChannelName);
            writer.PutBytesWithLength(Data);
        }

        public bool Deserialize(NetDataReader reader)
        {
            return reader.TryGetString(out ChannelName) && reader.TryGetBytesWithLength(out Data);
        }
    }

    [Serializable]
    public struct CustomServerDataInitialState
    {
        public string ChannelName;
        public byte[] Data;
        public Guid RequestID;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(ChannelName);
            writer.PutBytesWithLength(Data);
            writer.Put(RequestID);
        }

        public bool Deserialize(NetDataReader reader)
        {
            if (reader.TryGetString(out ChannelName) && reader.TryGetBytesWithLength(out Data) && reader.AvailableBytes >= 16)
            {
                RequestID = reader.GetGuid();
                return true;
            }

            return false;
        }
    }
}