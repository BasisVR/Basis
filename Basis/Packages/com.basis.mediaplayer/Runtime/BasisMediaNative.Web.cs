#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using UnityEngine;

public static partial class BasisMediaNative
{
    [DllImport("__Internal")] static extern int BasisWebMedia_Supported();
    [DllImport("__Internal")] static extern int BasisWebMedia_Open(byte[] descriptor, int length);
    [DllImport("__Internal")] static extern void BasisWebMedia_Close(int id);
    [DllImport("__Internal")] static extern int BasisWebMedia_Poll(int id, double[] output);
    [DllImport("__Internal")] static extern void BasisWebMedia_Play(int id);
    [DllImport("__Internal")] static extern void BasisWebMedia_Pause(int id);
    [DllImport("__Internal")] static extern void BasisWebMedia_Seek(int id, double seconds);
    [DllImport("__Internal")] static extern void BasisWebMedia_SetSyncTarget(int id, double seconds);
    [DllImport("__Internal")] static extern void BasisWebMedia_SetGain(int id, float gain);
    [DllImport("__Internal")] static extern void BasisWebMedia_SetTexture(int id, int texture);
    [DllImport("__Internal")] static extern int BasisWebMedia_Present(int id, int width, int height);
    [DllImport("__Internal")] static extern int BasisWebMedia_NextEvent(int id, byte[] detail, int capacity, int[] info);
    [DllImport("__Internal")] static extern unsafe int BasisWebMedia_PushVideo(int id, byte* data, int length, double ptsUs, int key, int width, int height);
    [DllImport("__Internal")] static extern unsafe int BasisWebMedia_PushAudio(int id, byte* data, int length, double ptsUs, int sampleRate, int channels);
    const int EventDetailBytes = 116;
    static readonly double[] pollOutput = new double[11];
    static readonly byte[] eventDetail = new byte[EventDetailBytes];
    static readonly int[] eventInfo = new int[3];
    static int webSupport = -1;
    static int WebSupport => webSupport >= 0 ? webSupport : webSupport = BasisWebMedia_Supported();
    public static bool WebCanDecode => (WebSupport & 2) != 0;
    public static uint bm_abi_version() => AbiVersion;
    public static unsafe int bm_capabilities(byte* buffer, UIntPtr capacity) => -1;
    public static int bm_session_open(byte[] descriptorUtf8, UIntPtr descriptorLen, out ulong handle)
    {
        int id = BasisWebMedia_Open(descriptorUtf8, (int)descriptorLen);
        handle = id > 0 ? (ulong)id : 0;
        return id > 0 ? 0 : id == 0 ? -1 : id;
    }
    public static int bm_session_close(ulong handle)
    {
        BasisWebMedia_Close((int)handle);
        return 0;
    }
    public static int bm_session_poll(ulong handle, out BmSnapshot snapshot)
    {
        snapshot = default;
        if (BasisWebMedia_Poll((int)handle, pollOutput) != 0) return -1;
        snapshot.AbiVersion = AbiVersion;
        snapshot.State = (uint)pollOutput[0];
        snapshot.Width = (uint)pollOutput[1];
        snapshot.Height = (uint)pollOutput[2];
        snapshot.PositionUs = (long)pollOutput[3];
        snapshot.DurationUs = (long)pollOutput[4];
        snapshot.FramesDecoded = (ulong)Math.Max(0d, pollOutput[5]);
        snapshot.FramesPresented = (ulong)Math.Max(0d, pollOutput[6]);
        snapshot.ErrorCode = (int)pollOutput[7];
        snapshot.ErrorCategory = (uint)pollOutput[8];
        snapshot.BankedMs = (long)pollOutput[9];
        snapshot.SyncRatePpm = (int)pollOutput[10];
        snapshot.AvOffsetUs = int.MinValue;
        return 0;
    }
    public static int bm_session_play(ulong handle)
    {
        BasisWebMedia_Play((int)handle);
        return 0;
    }
    public static int bm_session_pause(ulong handle)
    {
        BasisWebMedia_Pause((int)handle);
        return 0;
    }
    public static int bm_session_seek(ulong handle, long positionUs)
    {
        BasisWebMedia_Seek((int)handle, positionUs / 1e6);
        return 0;
    }
    public static unsafe int bm_session_read_audio(ulong handle, float* buffer, uint maxSamples) => 0;
    public static int bm_session_set_audio_latency(ulong handle, long latencyUs) => 0;
    public static int bm_session_set_sync_target(ulong handle, long positionUs)
    {
        BasisWebMedia_SetSyncTarget((int)handle, positionUs < 0 ? -1d : positionUs / 1e6);
        return 0;
    }
    public static unsafe int bm_session_drain_events(ulong handle, BmEvent* events, uint capacity)
    {
        int count = 0;
        while (count < capacity)
        {
            int length = BasisWebMedia_NextEvent((int)handle, eventDetail, EventDetailBytes, eventInfo);
            if (length < 0) break;
            BmEvent* e = events + count;
            e->WallUs = eventInfo[2] * 1000L;
            e->Code = (uint)eventInfo[0];
            e->Stage = (uint)eventInfo[1];
            e->DetailLen = (uint)length;
            for (int i = 0; i < length; i++) e->Detail[i] = eventDetail[i];
            count++;
        }
        return count;
    }
    public static unsafe int bm_drain_log(BmLogRecord* records, uint capacity, ulong* evicted) => 0;
    public static unsafe int bm_session_drain_captions(ulong handle, BmCaption* captions, uint capacity) => 0;
    public static unsafe int bm_session_drain_user_data(ulong handle, BmUserData* records, uint capacity, byte* bytes, uint bytesCapacity) => 0;
    public static int bm_session_audio_track_count(ulong handle) => 0;
    public static unsafe int bm_session_get_audio_tracks(ulong handle, BmAudioTrack* tracks, uint capacity) => 0;
    public static int bm_session_artwork_len(ulong handle) => 0;
    public static unsafe int bm_session_get_artwork(ulong handle, byte* data, uint capacity, byte* mime, uint mimeCapacity) => 0;
    public static int bm_session_set_output_texture(ulong handle, IntPtr texture)
    {
        BasisWebMedia_SetTexture((int)handle, (int)texture);
        return 0;
    }
    public static IntPtr bm_render_event_func() => IntPtr.Zero;
    public static uint bm_push_version() => WebCanDecode ? 1u : 0u;
    public static unsafe int bm_session_take_bgra(ulong handle, byte* dst, UIntPtr capacity, out uint width, out uint height)
    {
        width = height = 0;
        return 0;
    }
    public static unsafe int bm_session_push_video(ulong handle, byte* data, UIntPtr length, long ptsUs, uint key, uint width, uint height) => BasisWebMedia_PushVideo((int)handle, data, (int)length, ptsUs, (int)key, (int)width, (int)height);
    public static unsafe int bm_session_push_audio(ulong handle, byte* data, UIntPtr length, long ptsUs, uint sampleRate, uint channels) => BasisWebMedia_PushAudio((int)handle, data, (int)length, ptsUs, (int)sampleRate, (int)channels);
    public static void PresentWeb(ulong handle, Texture texture)
    {
        if (texture != null) BasisWebMedia_Present((int)handle, texture.width, texture.height);
    }
    public static void SetWebGain(ulong handle, float gain) => BasisWebMedia_SetGain((int)handle, gain);
}
#endif
