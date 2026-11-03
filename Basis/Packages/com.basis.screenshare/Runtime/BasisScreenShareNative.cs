using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

[NoAutoStaticsCleanup]
public static class BasisScreenShareNative
{
    const string Dll = "basis_media";
    public const int TooSmall = -4;

    [StructLayout(LayoutKind.Sequential)]
    public struct EncodedFrame
    {
        public long PtsUs;
        public uint Length, Key, Width, Height;
    }

    [Serializable]
    public class Source
    {
        public string id, kind, name;
        public int width, height;
        public bool primary;
        public bool IsMonitor => kind == "monitor";
        public bool IsPortal => id == PortalSource;
    }

    [Serializable]
    class SourceList
    {
        public List<Source> items = new List<Source>();
    }

    [Serializable]
    public class Status
    {
        public string state, error, encoder, audio_error;
        public int width, height, bitrate_kbps;
        public bool hardware, audio;
        public long captured, encoded, dropped;
        public bool Running => state == "running";
        public bool Failed => state == "error" || state == "ended";
    }

    public struct Config
    {
        public string SourceId;
        public int MaxWidth, MaxHeight, Fps, BitrateKbps, KeyframeIntervalMs, AudioBitrateKbps;
        public long PtsBaseUs;
        public string AudioSource;
        public bool Cursor, Audio;
    }

    [DllImport(Dll)] static extern uint bm_capture_supported();
    [DllImport(Dll)] static extern unsafe int bm_capture_sources(byte* buffer, UIntPtr capacity);
    [DllImport(Dll)] static extern int bm_capture_open(byte[] descriptor, UIntPtr length, out ulong handle);
    [DllImport(Dll)] public static extern int bm_capture_close(ulong handle);
    [DllImport(Dll)] static extern unsafe int bm_capture_status(ulong handle, byte* buffer, UIntPtr capacity);
    [DllImport(Dll)] static extern unsafe int bm_capture_read_video(ulong handle, byte* buffer, UIntPtr capacity, out EncodedFrame info);
    [DllImport(Dll)] static extern unsafe int bm_capture_read_audio(ulong handle, byte* buffer, UIntPtr capacity, out long ptsUs);
    [DllImport(Dll)] public static extern int bm_capture_request_keyframe(ulong handle);
    [DllImport(Dll)] public static extern int bm_capture_set_bitrate(ulong handle, uint kbps);
    [DllImport(Dll)] public static extern int bm_capture_set_preview(ulong handle, ulong session);
    [DllImport(Dll)] public static extern int bm_capture_wait(ulong handle, uint timeoutMs);

    static int supported = -1, current = -1;
    public static bool IsWindows => Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;
    public static bool IsLinux => Application.platform == RuntimePlatform.LinuxPlayer || Application.platform == RuntimePlatform.LinuxEditor;
    public const string PortalSource = "portal";

    public static bool PluginCurrent
    {
        get
        {
            if (current < 0)
            {
                current = 0;
                try
                {
                    bm_capture_wait(0, 0);
                    bm_capture_set_preview(0, 0);
                    current = 1;
                }
                catch (EntryPointNotFoundException) { }
                catch (DllNotFoundException) { }
            }
            return current > 0;
        }
    }

    public static bool CanCapture
    {
        get
        {
            if (supported < 0)
            {
                supported = 0;
                if (IsWindows || IsLinux)
                {
                    try { supported = bm_capture_supported() != 0 ? 1 : 0; }
                    catch (EntryPointNotFoundException) { }
                    catch (DllNotFoundException) { }
                }
            }
            return supported > 0;
        }
    }

    static unsafe string ReadJson(Func<IntPtr, UIntPtr, int> call)
    {
        int needed = call(IntPtr.Zero, UIntPtr.Zero);
        if (needed <= 0) return null;
        byte[] buffer = new byte[needed];
        fixed (byte* p = buffer)
        {
            int written = call((IntPtr)p, (UIntPtr)buffer.Length);
            if (written != needed) return null;
        }
        return Encoding.UTF8.GetString(buffer);
    }

    public static unsafe List<Source> Sources()
    {
        if (!CanCapture) return new List<Source>();
        string json = ReadJson((p, n) => bm_capture_sources((byte*)p, n));
        if (string.IsNullOrEmpty(json)) return new List<Source>();
        SourceList list = JsonUtility.FromJson<SourceList>("{\"items\":" + json + "}");
        return list?.items ?? new List<Source>();
    }

    public static unsafe Status ReadStatus(ulong handle)
    {
        string json = ReadJson((p, n) => bm_capture_status(handle, (byte*)p, n));
        return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Status>(json);
    }

    static void AppendString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (char c in value ?? string.Empty)
        {
            if (c == '"' || c == '\\') json.Append('\\').Append(c);
            else if (c < ' ') json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else json.Append(c);
        }
        json.Append('"');
    }

    public static string Describe(Config config)
    {
        StringBuilder json = new StringBuilder(256);
        json.Append("{\"source_id\":");
        AppendString(json, config.SourceId);
        json.Append(",\"max_width\":").Append(config.MaxWidth.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"max_height\":").Append(config.MaxHeight.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"fps\":").Append(config.Fps.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"bitrate_kbps\":").Append(config.BitrateKbps.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"keyframe_interval_ms\":").Append(config.KeyframeIntervalMs.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"audio_bitrate_kbps\":").Append(config.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"cursor\":").Append(config.Cursor ? "true" : "false");
        json.Append(",\"audio\":").Append(config.Audio ? "true" : "false");
        json.Append(",\"audio_source\":");
        AppendString(json, string.IsNullOrEmpty(config.AudioSource) ? "auto" : config.AudioSource);
        json.Append(",\"pts_base_us\":").Append(Math.Max(0L, config.PtsBaseUs).ToString(CultureInfo.InvariantCulture));
        json.Append('}');
        return json.ToString();
    }

    public static bool Open(Config config, out ulong handle)
    {
        handle = 0;
        if (!CanCapture) return false;
        byte[] descriptor = Encoding.UTF8.GetBytes(Describe(config));
        return bm_capture_open(descriptor, (UIntPtr)descriptor.Length, out handle) == 0;
    }

    public static unsafe int ReadVideo(ulong handle, ref byte[] buffer, out EncodedFrame info)
    {
        int result;
        fixed (byte* p = buffer) result = bm_capture_read_video(handle, p, (UIntPtr)buffer.Length, out info);
        if (result != TooSmall) return result;
        buffer = new byte[Mathf.NextPowerOfTwo((int)info.Length)];
        fixed (byte* p = buffer) return bm_capture_read_video(handle, p, (UIntPtr)buffer.Length, out info);
    }

    public static unsafe int ReadAudio(ulong handle, byte[] buffer, out long ptsUs)
    {
        fixed (byte* p = buffer) return bm_capture_read_audio(handle, p, (UIntPtr)buffer.Length, out ptsUs);
    }
}
