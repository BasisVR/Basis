using LiteNetLib.Utils;
using System;
using System.Collections.Concurrent;

public static partial class SerializableBasis
{
    public struct DatabasePrimativeMessage
    {
        public string DatabaseID;
        public ConcurrentDictionary<string, object> jsonPayload;

        private enum SerializedType : byte
        {
            Null = 0,
            String = 1,
            Int = 2,
            Bool = 3,
            Float = 4,
            Double = 5,
            Long = 6,
            ULong = 7,
            Short = 8,
            UShort = 9,
            Byte = 10,
            SByte = 11,
            Char = 12,
            Decimal = 13
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(DatabaseID);

            if (jsonPayload == null)
            {
                writer.Put(0);
                return;
            }

            writer.Put(jsonPayload.Count);

            foreach (var kvp in jsonPayload)
            {
                writer.Put(kvp.Key);

                if (kvp.Value == null)
                {
                    writer.Put((byte)SerializedType.Null);
                    continue;
                }

                Type type = kvp.Value.GetType();

                if (type == typeof(string))
                {
                    writer.Put((byte)SerializedType.String);
                    writer.Put((string)kvp.Value);
                }
                else if (type == typeof(int))
                {
                    writer.Put((byte)SerializedType.Int);
                    writer.Put((int)kvp.Value);
                }
                else if (type == typeof(bool))
                {
                    writer.Put((byte)SerializedType.Bool);
                    writer.Put((bool)kvp.Value);
                }
                else if (type == typeof(float))
                {
                    writer.Put((byte)SerializedType.Float);
                    writer.Put((float)kvp.Value);
                }
                else if (type == typeof(double))
                {
                    writer.Put((byte)SerializedType.Double);
                    writer.Put((double)kvp.Value);
                }
                else if (type == typeof(long))
                {
                    writer.Put((byte)SerializedType.Long);
                    writer.Put((long)kvp.Value);
                }
                else if (type == typeof(ulong))
                {
                    writer.Put((byte)SerializedType.ULong);
                    writer.Put((ulong)kvp.Value);
                }
                else if (type == typeof(short))
                {
                    writer.Put((byte)SerializedType.Short);
                    writer.Put((short)kvp.Value);
                }
                else if (type == typeof(ushort))
                {
                    writer.Put((byte)SerializedType.UShort);
                    writer.Put((ushort)kvp.Value);
                }
                else if (type == typeof(byte))
                {
                    writer.Put((byte)SerializedType.Byte);
                    writer.Put((byte)kvp.Value);
                }
                else if (type == typeof(sbyte))
                {
                    writer.Put((byte)SerializedType.SByte);
                    writer.Put((sbyte)kvp.Value);
                }
                else if (type == typeof(char))
                {
                    writer.Put((byte)SerializedType.Char);
                    writer.Put((char)kvp.Value);
                }
                else if (type == typeof(decimal))
                {
                    writer.Put((byte)SerializedType.Decimal);
                    // No direct Put for decimal, serialize as string
                    writer.Put(kvp.Value.ToString());
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported type in jsonPayload: {type}");
                }
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            DatabaseID = reader.GetString();

            int count = reader.GetInt();
            jsonPayload = new ConcurrentDictionary<string, object>();

            for (int Index = 0; Index < count; Index++)
            {
                string key = reader.GetString();
                SerializedType type = (SerializedType)reader.GetByte();

                switch (type)
                {
                    case SerializedType.Null:
                        jsonPayload[key] = null;
                        break;
                    case SerializedType.String:
                        jsonPayload[key] = reader.GetString();
                        break;
                    case SerializedType.Int:
                        jsonPayload[key] = reader.GetInt();
                        break;
                    case SerializedType.Bool:
                        jsonPayload[key] = reader.GetBool();
                        break;
                    case SerializedType.Float:
                        jsonPayload[key] = reader.GetFloat();
                        break;
                    case SerializedType.Double:
                        jsonPayload[key] = reader.GetDouble();
                        break;
                    case SerializedType.Long:
                        jsonPayload[key] = reader.GetLong();
                        break;
                    case SerializedType.ULong:
                        jsonPayload[key] = reader.GetULong();
                        break;
                    case SerializedType.Short:
                        jsonPayload[key] = reader.GetShort();
                        break;
                    case SerializedType.UShort:
                        jsonPayload[key] = reader.GetUShort();
                        break;
                    case SerializedType.Byte:
                        jsonPayload[key] = reader.GetByte();
                        break;
                    case SerializedType.SByte:
                        jsonPayload[key] = reader.GetSByte();
                        break;
                    case SerializedType.Char:
                        jsonPayload[key] = reader.GetChar();
                        break;
                    case SerializedType.Decimal:
                        // Read decimal from string
                        jsonPayload[key] = decimal.Parse(reader.GetString());
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported type marker in jsonPayload: {(byte)type}");
                }
            }
        }
    }
    public struct DataBaseRequest
    {
        public string DatabaseID;
        public void Serialize(NetDataWriter writer)
        {
            writer.Put(DatabaseID);
        }

        public void Deserialize(NetDataReader reader)
        {
            DatabaseID = reader.GetString();
        }
    }
}
