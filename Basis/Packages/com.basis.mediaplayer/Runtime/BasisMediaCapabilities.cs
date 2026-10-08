using Unity.Scripting.LifecycleManagement;
using System;
using System.Text;
using UnityEngine;

/// <summary>
/// The engine-declared capability set: what this basis_media
/// build will decode and play, queried once after the ABI probe and
/// cached. Consumers (the resolver's format selection, UI greying out
/// unsupported sources) read the typed <see cref="Set"/> or the raw
/// <see cref="Json"/>. The set is a snapshot: a DecodeFallbackHwToSw
/// diagnostics event (code 3) advises calling <see cref="Requery"/>.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisMediaCapabilities
{
    static BmCapabilitySet _set;
    static string _json;
    static bool _queried;

    /// <summary>The cached capability set, or null when the engine is
    /// unavailable or the ABI mismatched.</summary>
    public static BmCapabilitySet Set
    {
        get
        {
            if (!_queried) Requery();
            return _set;
        }
    }

    /// <summary>The raw versioned JSON blob, or null.</summary>
    public static string Json
    {
        get
        {
            if (!_queried) Requery();
            return _json;
        }
    }

    /// <summary>Drop the cache and query the engine again (the
    /// engine re-probes on every call). Returns the fresh set or null.</summary>
    public static unsafe BmCapabilitySet Requery()
    {
        _queried = true;
        _set = null;
        _json = null;
        try
        {
            if (BasisMediaNative.bm_abi_version() != BasisMediaNative.AbiVersion)
                return null;
            int length = BasisMediaNative.bm_capabilities(null, UIntPtr.Zero);
            if (length <= 0)
                return null;
            var buffer = new byte[length];
            fixed (byte* p = buffer)
            {
                int written = BasisMediaNative.bm_capabilities(p, (UIntPtr)length);
                if (written != length)
                    return null;
            }
            _json = Encoding.UTF8.GetString(buffer);
            _set = JsonUtility.FromJson<BmCapabilitySet>(_json);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        return _set;
    }
}

// Field names mirror the JSON contract 1:1 (JsonUtility matches by
// exact name). Identifier strings are documented on bm_capabilities
// in the native crate. Unknown strings are later additions: skip them
// rather than failing.
[Serializable]
public class BmCapabilitySet
{
    public uint version;
    public string platform;
    public BmVideoCap[] video;
    public BmAudioCap[] audio;
    public BmTransportCap[] transports;
    public string[] containers;
}

[Serializable]
public class BmVideoCap
{
    public string codec;
    /// <summary>"hardware" | "software". Software routes state no
    /// ceilings (0 = best effort), so rank them conservatively.</summary>
    public string route;
    public uint max_width;
    public uint max_height;
    public uint max_fps;
}

[Serializable]
public class BmAudioCap
{
    public string codec;
    public uint max_channels;
}

[Serializable]
public class BmTransportCap
{
    public string scheme;
    public string note;
}
