using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using UnityEngine;

public static class BasisAvatarRecorder
{
    private static bool _isRecording;
    private static FileStream filestream;
    private static Thread writeThread;
    private static AutoResetEvent writeSignal;
    private static volatile bool writeRunning;
    private static readonly ConcurrentQueue<byte[]> pendingFrames = new ConcurrentQueue<byte[]>();
    private static readonly ConcurrentQueue<byte[]> framePool = new ConcurrentQueue<byte[]>();
    private static readonly float[] staging = new float[FloatsPerFrame];

    // Public so tools (like your editor window) can reason about the file format
    public const int MuscleCount = 95;
    // IntervalSeconds(1) + Rotation(4) + Position(3) + Muscles(95) + Scale(1)
    public const int FloatsPerFrame = 1 + 4 + 3 + MuscleCount + 1;
    public const int BytesPerFrame = FloatsPerFrame * sizeof(float);

    public static bool IsRecording => _isRecording;

    public static void StartRecording()
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

        filestream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        writeSignal = new AutoResetEvent(false);
        writeRunning = true;
        writeThread = new Thread(WriteLoop) { IsBackground = true, Name = "BasisAvatarRecorder" };
        writeThread.Start();
        _isRecording = true;

        BasisDebug.Log($"Avatar recording started: {filePath}", BasisDebug.LogTag.Device);
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
                filestream?.Write(frame, 0, BytesPerFrame);
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
    /// Writes one frame to disk.
    /// Layout (per frame, in this exact order):
    ///   float IntervalSeconds
    ///   Quaternion rotation (x, y, z, w)
    ///   Vector3 position (x, y, z)
    ///   float muscles[95]
    ///   float scale
    ///
    /// Total: 104 floats = 416 bytes.
    ///
    /// The frame is packed on the caller's thread into a pooled buffer and written by
    /// a dedicated writer thread, so the per-frame capture path never touches the
    /// FileStream. Byte layout is identical to the old BinaryWriter output
    /// (little-endian floats).
    /// </summary>
    /// <param name="intervalSeconds">Time since previous frame, in seconds.</param>
    /// <param name="rotation">Root rotation.</param>
    /// <param name="position">Root position.</param>
    /// <param name="muscles">Humanoid muscle values (length 95 expected).</param>
    /// <param name="scale">Avatar scale.</param>
    public static void StoreData(
        float intervalSeconds,
        Quaternion rotation,
        Vector3 position,
        float[] muscles,
        float scale)
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

        if (!framePool.TryDequeue(out byte[] frame))
        {
            frame = new byte[BytesPerFrame];
        }
        Buffer.BlockCopy(staging, 0, frame, 0, BytesPerFrame);
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
                    filestream.Write(frame, 0, BytesPerFrame);
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
