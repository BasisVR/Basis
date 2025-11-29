using System;
using System.IO;
using UnityEngine;

public static class BasisAvatarRecorder
{
    private static bool _isRecording;
    private static FileStream filestream;
    private static BinaryWriter writer;

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
        writer = new BinaryWriter(filestream);
        _isRecording = true;

        BasisDebug.Log($"Avatar recording started: {filePath}", BasisDebug.LogTag.Device);
    }

    public static void StopRecording()
    {
        if (!_isRecording)
        {
            return;
        }

        writer?.Flush();
        writer?.Dispose();
        filestream?.Dispose();

        writer = null;
        filestream = null;
        _isRecording = false;

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
        if (!_isRecording || writer == null)
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

        // Interval between this frame and the previous one
        writer.Write(intervalSeconds);

        // Rotation (4 floats)
        writer.Write(rotation.x);
        writer.Write(rotation.y);
        writer.Write(rotation.z);
        writer.Write(rotation.w);

        // Position (3 floats)
        writer.Write(position.x);
        writer.Write(position.y);
        writer.Write(position.z);

        // Muscles (95 floats)
        for (int i = 0; i < MuscleCount; i++)
        {
            writer.Write(muscles[i]);
        }

        // Scale (1 float)
        writer.Write(scale);
    }
}
