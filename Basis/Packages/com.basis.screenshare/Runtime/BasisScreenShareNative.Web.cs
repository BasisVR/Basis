#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Text;

public static partial class BasisScreenShareNative
{
    [DllImport("__Internal")] static extern int BasisWebShare_Supported();
    [DllImport("__Internal")] static extern int BasisWebShare_Open(byte[] descriptor, int length);
    [DllImport("__Internal")] static extern void BasisWebShare_Close(int id);
    [DllImport("__Internal")] static extern unsafe int BasisWebShare_Status(int id, byte* buffer, int capacity);
    [DllImport("__Internal")] static extern int BasisWebShare_Pending(int id);
    [DllImport("__Internal")] static extern unsafe int BasisWebShare_ReadVideo(int id, byte* buffer, int capacity, double[] info);
    [DllImport("__Internal")] static extern unsafe int BasisWebShare_ReadAudio(int id, byte* buffer, int capacity, double[] info);
    [DllImport("__Internal")] static extern void BasisWebShare_RequestKeyframe(int id);
    [DllImport("__Internal")] static extern void BasisWebShare_SetBitrate(int id, int kbps);
    [DllImport("__Internal")] static extern void BasisWebShare_SetPreview(int id, int session);
    static readonly byte[] webSources = Encoding.UTF8.GetBytes("[{\"id\":\"" + PortalSource + "\",\"kind\":\"window\",\"name\":\"\",\"width\":0,\"height\":0,\"primary\":true}]");
    static readonly double[] frameInfo = new double[5];
    static uint bm_capture_supported() => BasisWebShare_Supported() != 0 ? 1u : 0u;
    static unsafe int bm_capture_sources(byte* buffer, UIntPtr capacity)
    {
        if (buffer == null || (int)capacity < webSources.Length) return webSources.Length;
        for (int i = 0; i < webSources.Length; i++) buffer[i] = webSources[i];
        return webSources.Length;
    }
    static int bm_capture_open(byte[] descriptor, UIntPtr length, out ulong handle)
    {
        int id = BasisWebShare_Open(descriptor, (int)length);
        handle = id > 0 ? (ulong)id : 0;
        return id > 0 ? 0 : -1;
    }
    public static int bm_capture_close(ulong handle)
    {
        BasisWebShare_Close((int)handle);
        return 0;
    }
    static unsafe int bm_capture_status(ulong handle, byte* buffer, UIntPtr capacity) => BasisWebShare_Status((int)handle, buffer, (int)capacity);
    static unsafe int bm_capture_read_video(ulong handle, byte* buffer, UIntPtr capacity, out EncodedFrame info)
    {
        int result = BasisWebShare_ReadVideo((int)handle, buffer, (int)capacity, frameInfo);
        info = result > 0 || result == TooSmall ? new EncodedFrame { PtsUs = (long)frameInfo[0], Length = (uint)frameInfo[1], Key = (uint)frameInfo[2], Width = (uint)frameInfo[3], Height = (uint)frameInfo[4] } : default;
        return result;
    }
    static unsafe int bm_capture_read_audio(ulong handle, byte* buffer, UIntPtr capacity, out long ptsUs)
    {
        int result = BasisWebShare_ReadAudio((int)handle, buffer, (int)capacity, frameInfo);
        ptsUs = result > 0 ? (long)frameInfo[0] : 0;
        return result;
    }
    public static int bm_capture_request_keyframe(ulong handle)
    {
        BasisWebShare_RequestKeyframe((int)handle);
        return 0;
    }
    public static int bm_capture_set_bitrate(ulong handle, uint kbps)
    {
        BasisWebShare_SetBitrate((int)handle, (int)kbps);
        return 0;
    }
    public static int bm_capture_set_preview(ulong handle, ulong session)
    {
        if (handle != 0) BasisWebShare_SetPreview((int)handle, (int)session);
        return 0;
    }
    public static int bm_capture_wait(ulong handle, uint timeoutMs) => handle != 0 && BasisWebShare_Pending((int)handle) > 0 ? 1 : 0;
}
#endif
