using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

public static class BasisSerialization
{
    private static readonly JsonSerializer _reader = CreateReader();

    private static JsonSerializer CreateReader()
    {
        var s = new JsonSerializer();
        s.Converters.Add(new Vector3ReadConverter());
        s.Converters.Add(new UnityObjectNullReadConverter());
        return s;
    }

    public static byte[] SerializeValue<T>(T value)
    {
        // Keep JsonUtility here: it is the producer of the on-disk wrapped format
        // ({"Value":{...}}). New bundles must stay byte-identical to old ones.
        var json = JsonUtility.ToJson(new Wrapper<T> { Value = value }, prettyPrint: false);
        return Encoding.UTF8.GetBytes(json);
    }

    public static T DeserializeValue<T>(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            BasisDebug.Log("Data was null or empty!");
            return default;
        }

        // Stream the bytes directly — avoids the ~2x UTF-16 string materialization
        // that Encoding.UTF8.GetString(data) + JsonUtility.FromJson would cost.
        using (var ms = new MemoryStream(data, 0, data.Length, writable: false))
        using (var sr = new StreamReader(ms, Encoding.UTF8))
        using (var jr = new JsonTextReader(sr))
        {
            var wrapper = _reader.Deserialize<Wrapper<T>>(jr);
            return wrapper != null ? wrapper.Value : default;
        }
    }

    [Serializable]
    private class Wrapper<TW>
    {
        public TW Value;
    }

    private sealed class Vector3ReadConverter : JsonConverter<Vector3>
    {
        public override bool CanWrite => false;

        public override Vector3 ReadJson(JsonReader reader, Type objectType, Vector3 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return default;
            }
            if (reader.TokenType != JsonToken.StartObject)
            {
                throw new JsonSerializationException($"Expected Vector3 object, got {reader.TokenType}");
            }

            float x = 0f, y = 0f, z = 0f;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.EndObject)
                {
                    break;
                }
                if (reader.TokenType != JsonToken.PropertyName)
                {
                    continue;
                }
                var name = (string)reader.Value;
                if (!reader.Read())
                {
                    break;
                }
                if (reader.TokenType != JsonToken.Float && reader.TokenType != JsonToken.Integer)
                {
                    continue;
                }
                float v = Convert.ToSingle(reader.Value, CultureInfo.InvariantCulture);
                if (name == "x") x = v;
                else if (name == "y") y = v;
                else if (name == "z") z = v;
            }
            return new Vector3(x, y, z);
        }

        public override void WriteJson(JsonWriter writer, Vector3 value, JsonSerializer serializer)
            => throw new NotSupportedException();
    }

    private sealed class UnityObjectNullReadConverter : JsonConverter
    {
        public override bool CanWrite => false;
        public override bool CanConvert(Type objectType) => typeof(UnityEngine.Object).IsAssignableFrom(objectType);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            reader.Skip();
            return null;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            => throw new NotSupportedException();
    }
}
