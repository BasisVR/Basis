using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisAvatarRecorder
{
    private static bool _isRecording;
    [NoAutoStaticsCleanup] private static FileStream filestream;
    private static Thread writeThread;
    [NoAutoStaticsCleanup] private static AutoResetEvent writeSignal;
    private static volatile bool writeRunning;
    private static readonly ConcurrentQueue<byte[]> pendingFrames = new ConcurrentQueue<byte[]>();
    private static readonly ConcurrentQueue<byte[]> framePool = new ConcurrentQueue<byte[]>();
    private static float[] staging = new float[FloatsPerFrame];
    private static int frameFloats = FloatsPerFrame;
    private static int frameBytes = BytesPerFrame;
    private static int boneCount;
    private static bool recordRoot;

    // Public so tools (like your editor window) can reason about the file format
    public const int MuscleCount = 95;
    // IntervalSeconds(1) + Rotation(4) + Position(3) + Muscles(95) + Scale(1)
    public const int FloatsPerFrame = 1 + 4 + 3 + MuscleCount + 1;
    public const int BytesPerFrame = FloatsPerFrame * sizeof(float);
    // Version 2 appends RootPosition(3) + RootRotation(4) + RootScale(1) and per bone LocalPosition(3) + LocalRotation(4) + LocalScale(3)
    public const int RootFloats = 3 + 4 + 1;
    public const int BoneFloats = 3 + 4 + 3;
    public const int FileMagic = 0x32524142;
    public const int FileVersion = 2;
    public const int FlagRootMotion = 1;

    public static bool IsRecording => _isRecording;

    public static int GetFloatsPerFrame(int bones) => FloatsPerFrame + RootFloats + bones * BoneFloats;

    public static void WriteHeader(BinaryWriter writer, float humanScale, bool rootMotion, string[] bonePaths)
    {
        int count = bonePaths?.Length ?? 0;
        writer.Write(FileMagic);
        writer.Write(FileVersion);
        writer.Write(rootMotion ? FlagRootMotion : 0);
        writer.Write(humanScale);
        writer.Write(count);
        for (int i = 0; i < count; i++) writer.Write(bonePaths[i] ?? string.Empty);
    }

    public static void StartRecording(float humanScale, bool rootMotion, string[] bonePaths)
    {
        if (_isRecording)
            return;

        // Create a timestamp safe for all filesystems
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");

        // Default directory: persistentDataPath/AvatarRecordings
        string directory = Path.Combine(Application.persistentDataPath, "AvatarRecordings");

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string filePath = Path.Combine(directory, $"AvatarRecord_{timestamp}.dat");

        boneCount = bonePaths?.Length ?? 0;
        recordRoot = rootMotion;
        frameFloats = GetFloatsPerFrame(boneCount);
        frameBytes = frameFloats * sizeof(float);
        staging = new float[frameFloats];
        for (int o = FloatsPerFrame + RootFloats; o < frameFloats; o += BoneFloats) { staging[o + 6] = 1f; staging[o + 7] = 1f; staging[o + 8] = 1f; staging[o + 9] = 1f; }
        while (framePool.TryDequeue(out _)) { }

        filestream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        using (BinaryWriter header = new BinaryWriter(filestream, System.Text.Encoding.UTF8, true)) WriteHeader(header, humanScale, rootMotion, bonePaths);
        writeSignal = new AutoResetEvent(false);
        writeRunning = true;
        writeThread = new Thread(WriteLoop) { IsBackground = true, Name = "BasisAvatarRecorder" };
        writeThread.Start();
        _isRecording = true;

        BasisDebug.Log($"Avatar recording started: {filePath} ({boneCount} additional bones, root motion {(rootMotion ? "on" : "off")})", BasisDebug.LogTag.Device);
    }

    public static void StopRecording()
    {
        if (!_isRecording)
        {
            return;
        }

        _isRecording = false;
        writeRunning = false;
        writeSignal?.Set();
        try { writeThread?.Join(); } catch { }
        writeThread = null;

        try
        {
            while (pendingFrames.TryDequeue(out byte[] frame))
            {
                filestream?.Write(frame, 0, frameBytes);
            }
            filestream?.Flush();
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"BasisAvatarRecorder: final flush failed: {ex.Message}", BasisDebug.LogTag.Device);
        }
        filestream?.Dispose();
        filestream = null;
        writeSignal?.Dispose();
        writeSignal = null;
        while (framePool.TryDequeue(out _)) { }

        BasisDebug.Log("Avatar recording stopped.", BasisDebug.LogTag.Device);
    }

    /// <summary>
    /// Writes one frame to disk, after the header written by <see cref="WriteHeader"/>.
    /// Layout (per frame, in this exact order):
    ///   float IntervalSeconds
    ///   Quaternion bodyRotation (x, y, z, w), relative to the avatar root
    ///   Vector3 bodyPosition (x, y, z), relative to the avatar root, normalized by human scale
    ///   float muscles[95]
    ///   float scale
    ///   Vector3 rootPosition (world)
    ///   Quaternion rootRotation (world)
    ///   float rootScale (lossy y)
    ///   per additional bone: Vector3 localPosition, Quaternion localRotation, Vector3 localScale
    ///
    /// Version 1 files have no header and stop after scale (104 floats = 416 bytes).
    ///
    /// The frame is packed on the caller's thread into a pooled buffer and written by
    /// a dedicated writer thread, so the per-frame capture path never touches the
    /// FileStream (little-endian floats).
    /// </summary>
    public static void StoreData(
        float intervalSeconds,
        Quaternion rotation,
        Vector3 position,
        float[] muscles,
        float scale,
        Transform root,
        Transform[] bones)
    {
        if (!_isRecording || !writeRunning || filestream == null)
        {
            BasisDebug.LogError("BasisAvatarRecorder.StoreData called while not recording (Missing Writer)!");
            return;
        }

        if (muscles == null || muscles.Length < MuscleCount)
        {
            BasisDebug.LogError(
                $"BasisAvatarRecorder.StoreData: muscles array is null or too small. " +
                $"Expected {MuscleCount}, got {muscles?.Length ?? 0}");
            return;
        }

        staging[0] = intervalSeconds;
        staging[1] = rotation.x;
        staging[2] = rotation.y;
        staging[3] = rotation.z;
        staging[4] = rotation.w;
        staging[5] = position.x;
        staging[6] = position.y;
        staging[7] = position.z;
        Array.Copy(muscles, 0, staging, 8, MuscleCount);
        staging[8 + MuscleCount] = scale;

        int o = FloatsPerFrame;
        if (recordRoot && root != null)
        {
            root.GetPositionAndRotation(out Vector3 rp, out Quaternion rq);
            staging[o] = rp.x; staging[o + 1] = rp.y; staging[o + 2] = rp.z;
            staging[o + 3] = rq.x; staging[o + 4] = rq.y; staging[o + 5] = rq.z; staging[o + 6] = rq.w;
            staging[o + 7] = root.lossyScale.y;
        }
        else
        {
            staging[o] = 0f; staging[o + 1] = 0f; staging[o + 2] = 0f;
            staging[o + 3] = 0f; staging[o + 4] = 0f; staging[o + 5] = 0f; staging[o + 6] = 1f;
            staging[o + 7] = 1f;
        }
        o += RootFloats;

        int count = bones == null ? 0 : Mathf.Min(bones.Length, boneCount);
        for (int i = 0; i < count; i++, o += BoneFloats)
        {
            Transform bone = bones[i];
            if (bone == null) continue;
            bone.GetLocalPositionAndRotation(out Vector3 lp, out Quaternion lq);
            Vector3 ls = bone.localScale;
            staging[o] = lp.x; staging[o + 1] = lp.y; staging[o + 2] = lp.z;
            staging[o + 3] = lq.x; staging[o + 4] = lq.y; staging[o + 5] = lq.z; staging[o + 6] = lq.w;
            staging[o + 7] = ls.x; staging[o + 8] = ls.y; staging[o + 9] = ls.z;
        }

        if (!framePool.TryDequeue(out byte[] frame) || frame.Length != frameBytes)
        {
            frame = new byte[frameBytes];
        }
        Buffer.BlockCopy(staging, 0, frame, 0, frameBytes);
        pendingFrames.Enqueue(frame);
        writeSignal?.Set();
    }

    private static void WriteLoop()
    {
        while (writeRunning)
        {
            writeSignal.WaitOne();
            try
            {
                while (pendingFrames.TryDequeue(out byte[] frame))
                {
                    filestream.Write(frame, 0, frameBytes);
                    if (framePool.Count < 64)
                    {
                        framePool.Enqueue(frame);
                    }
                }
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"BasisAvatarRecorder: write failed: {ex.Message}", BasisDebug.LogTag.Device);
                writeRunning = false;
                return;
            }
        }
    }
}
