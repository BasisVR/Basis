using System;
using System.Text;
using UnityEngine;

public static class BasisSerialization
{
    public static byte[] SerializeValue<T>(T value)
    {
        // Unity JsonUtility needs a wrapper for non-class roots sometimes
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

        var json = Encoding.UTF8.GetString(data);
        var wrapper = JsonUtility.FromJson<Wrapper<T>>(json);
        if (wrapper != null)
        {
            return wrapper.Value;
        }
        else
        {
            return default;
        }
    }
    [Serializable]
    private class Wrapper<TW>
    {
        public TW Value;
    }
}
