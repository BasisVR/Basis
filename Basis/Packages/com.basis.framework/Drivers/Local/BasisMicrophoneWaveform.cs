using Unity.Scripting.LifecycleManagement;
#if !BASIS_DISABLE_MICROPHONE
using System;
using System.Threading;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisMicrophoneWaveform
{
    public const int Columns = 256;
    public const int Rows = 3;
    public const int RowStride = Columns * 4;
    public const int ColumnsPerSecond = 50;
    public const float FloorDb = -90f;
    public const float GridDb = 12f;

    public const int FlagSent = 1;
    public const int FlagMuted = 2;
    public const int FlagDenoiser = 4;
    public const int FlagGate = 8;
    public const int FlagAutoGate = 16;
    public const int FlagAgc = 32;

    [NoAutoStaticsCleanup] private static readonly float[] data = new float[Columns * Rows * 4];

    private static int subscribers;
    private static int writeGate;
    private static int writeSlot;
    private static long published;
    private static long captured;
    private static float idleColumns;
    private static float inputPeak;
    private static float inputRms;
    private static float denoisedRms;
    private static float preLimitPeak;

    public static bool Enabled => Volatile.Read(ref subscribers) > 0;
    public static long Published => Interlocked.Read(ref published);
    public static long Captured => Interlocked.Read(ref captured);
    public static float[] Data => data;
    public static int Oldest => Volatile.Read(ref writeSlot);

    public static void AddSubscriber()
    {
        if (Interlocked.Increment(ref subscribers) == 1) Clear();
    }

    public static void RemoveSubscriber()
    {
        if (Interlocked.Decrement(ref subscribers) < 0) Interlocked.Exchange(ref subscribers, 0);
    }

    public static float ToUnit(float amplitude)
    {
        if (!(amplitude > 1e-9f)) return 0f;
        float db = 20f * Mathf.Log10(amplitude);
        return Mathf.Clamp01((db - FloorDb) / -FloorDb);
    }

    public static void MeasureInput(float[] samples, int count)
    {
        Measure(samples, count, out inputPeak, out inputRms);
        denoisedRms = inputRms;
    }

    public static void MeasureDenoised(float[] samples, int count)
    {
        Measure(samples, count, out _, out denoisedRms);
    }

    public static void MeasurePreLimit(float[] samples, int count)
    {
        Measure(samples, count, out preLimitPeak, out _);
    }

    public static void CommitFrame(float[] samples, int count, float gateThreshold, float gateGain, float agcGainDb, int flags)
    {
        if (Volatile.Read(ref subscribers) <= 0) return;

        Measure(samples, count, out float outputPeak, out float outputRms);
        if (Interlocked.CompareExchange(ref writeGate, 1, 0) != 0) return;

        try
        {
            WriteColumn(
                new Vector4(ToUnit(inputPeak), ToUnit(inputRms), ToUnit(outputPeak), ToUnit(outputRms)),
                new Vector4(ToUnit(denoisedRms), ToUnit(preLimitPeak), ToUnit(gateThreshold), 0f),
                new Vector4(agcGainDb, flags, Mathf.Clamp01(gateGain), 1f));
            Interlocked.Increment(ref captured);
            idleColumns = 0f;
        }
        finally
        {
            Volatile.Write(ref writeGate, 0);
        }
    }

    public static void PushIdle(float seconds)
    {
        if (seconds <= 0f || Volatile.Read(ref subscribers) <= 0) return;
        if (Interlocked.CompareExchange(ref writeGate, 1, 0) != 0) return;

        try
        {
            idleColumns += seconds * ColumnsPerSecond;
            if (idleColumns > Columns) idleColumns = Columns;

            while (idleColumns >= 1f)
            {
                idleColumns -= 1f;
                WriteColumn(Vector4.zero, Vector4.zero, Vector4.zero);
            }
        }
        finally
        {
            Volatile.Write(ref writeGate, 0);
        }
    }

    public static void Clear()
    {
        if (Interlocked.CompareExchange(ref writeGate, 1, 0) != 0) return;

        try
        {
            Array.Clear(data, 0, data.Length);
            Volatile.Write(ref writeSlot, 0);
            idleColumns = 0f;
            Interlocked.Increment(ref published);
        }
        finally
        {
            Volatile.Write(ref writeGate, 0);
        }
    }

    private static void Measure(float[] samples, int count, out float peak, out float rms)
    {
        peak = 0f;
        rms = 0f;
        if (samples == null) return;
        if (count > samples.Length) count = samples.Length;
        if (count <= 0) return;

        float maximum = 0f;
        double square = 0d;
        for (int i = 0; i < count; i++)
        {
            float value = samples[i];
            float magnitude = value < 0f ? -value : value;
            if (magnitude > maximum) maximum = magnitude;
            square += (double)value * value;
        }

        peak = maximum;
        rms = (float)Math.Sqrt(square / count);
    }

    private static void WriteColumn(Vector4 levels, Vector4 stages, Vector4 meta)
    {
        int slot = writeSlot;
        int offset = slot * 4;
        Store(offset, levels);
        Store(RowStride + offset, stages);
        Store(RowStride * 2 + offset, meta);

        Volatile.Write(ref writeSlot, slot + 1 == Columns ? 0 : slot + 1);
        Interlocked.Increment(ref published);
    }

    private static void Store(int index, Vector4 value)
    {
        data[index] = value.x;
        data[index + 1] = value.y;
        data[index + 2] = value.z;
        data[index + 3] = value.w;
    }
}
#endif
