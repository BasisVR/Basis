using Unity.Scripting.LifecycleManagement;
#if !BASIS_DISABLE_MICROPHONE
using System;
using System.Threading;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisMicrophoneWaveform
{
    public const int Columns = 192;
    public const int ColumnsPerSecond = 50;
    public const int MaxChannels = 2;

    [NoAutoStaticsCleanup] private static readonly Vector4[] packed = new Vector4[Columns];

    private static int subscribers;
    private static int writeGate;
    private static int writeSlot;
    private static int channels = 1;
    private static long published;
    private static long captured;
    private static float pendingLeftMinimum;
    private static float pendingLeftMaximum;
    private static float pendingRightMinimum;
    private static float pendingRightMaximum;
    private static int pendingSamples;
    private static float idleColumns;

    public static bool Enabled => Volatile.Read(ref subscribers) > 0;
    public static long Published => Interlocked.Read(ref published);
    public static long Captured => Interlocked.Read(ref captured);

    /// <summary>
    /// Ring storage already in the layout the shader reads: one element per time column,
    /// xy = left trough/peak, zw = right trough/peak. Handed straight to
    /// Material.SetVectorArray, so a redraw copies and allocates nothing — pair it with
    /// <see cref="Oldest"/> and unwrap the ring in the shader.
    /// </summary>
    public static Vector4[] Packed => packed;

    /// <summary>Ring slot the next column lands in, which is also the oldest column on screen.</summary>
    public static int Oldest => Volatile.Read(ref writeSlot);

    /// <summary>Channels actually captured, 1 or 2. Drives the red/blue split.</summary>
    public static int Channels => Volatile.Read(ref channels);

    public static void AddSubscriber()
    {
        if (Interlocked.Increment(ref subscribers) == 1) Clear();
    }

    public static void RemoveSubscriber()
    {
        if (Interlocked.Decrement(ref subscribers) < 0) Interlocked.Exchange(ref subscribers, 0);
    }

    /// <summary>
    /// Feeds one interleaved capture chunk straight off the device, before the mono downmix —
    /// the only point in the pipeline where the channels still exist separately.
    /// </summary>
    public static void Push(float[] interleaved, int frames, int sourceChannels, bool silent)
    {
        if (interleaved == null || frames <= 0 || sourceChannels <= 0) return;
        if (Volatile.Read(ref subscribers) <= 0) return;

        int used = sourceChannels < MaxChannels ? sourceChannels : MaxChannels;
        if ((long)frames * sourceChannels > interleaved.Length) frames = interleaved.Length / sourceChannels;
        if (frames <= 0) return;
        if (Interlocked.CompareExchange(ref writeGate, 1, 0) != 0) return;

        try
        {
            Volatile.Write(ref channels, used);

            int columnLength = LocalOpusSettings.MicrophoneSampleRate / ColumnsPerSecond;
            if (columnLength < 1) columnLength = 1;

            for (int frame = 0; frame < frames; frame++)
            {
                if (!silent)
                {
                    int offset = frame * sourceChannels;

                    float left = interleaved[offset];
                    if (left < pendingLeftMinimum) pendingLeftMinimum = left;
                    else if (left > pendingLeftMaximum) pendingLeftMaximum = left;

                    if (used > 1)
                    {
                        float right = interleaved[offset + 1];
                        if (right < pendingRightMinimum) pendingRightMinimum = right;
                        else if (right > pendingRightMaximum) pendingRightMaximum = right;
                    }
                }

                if (++pendingSamples < columnLength) continue;

                Commit();
                Interlocked.Increment(ref captured);
            }

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
                ResetPending();
                Commit();
            }

            ResetPending();
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
            Array.Clear(packed, 0, Columns);
            Volatile.Write(ref writeSlot, 0);
            idleColumns = 0f;
            ResetPending();
            Interlocked.Increment(ref published);
        }
        finally
        {
            Volatile.Write(ref writeGate, 0);
        }
    }

    private static void Commit()
    {
        int slot = writeSlot;
        packed[slot] = new Vector4(pendingLeftMinimum, pendingLeftMaximum, pendingRightMinimum, pendingRightMaximum);

        Volatile.Write(ref writeSlot, slot + 1 == Columns ? 0 : slot + 1);
        ResetPending();
        Interlocked.Increment(ref published);
    }

    private static void ResetPending()
    {
        pendingLeftMinimum = 0f;
        pendingLeftMaximum = 0f;
        pendingRightMinimum = 0f;
        pendingRightMaximum = 0f;
        pendingSamples = 0;
    }
}
#endif
