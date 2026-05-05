using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;

//
// BasisVolumeAdjustmentJob (revised: linear gain + soft limiter)
//
[BurstCompile]
public struct BasisVolumeAdjustmentJob : IJobParallelFor
{
    [NativeDisableParallelForRestriction]
    public NativeArray<float> processBufferArray;

    /// <summary>End-of-frame amplitude multiplier (already mapped from UI in driver).</summary>
    public float Volume;

    /// <summary>
    /// Start-of-frame amplitude multiplier. Gain is linearly ramped from this
    /// to <see cref="Volume"/> across <see cref="FrameLength"/> samples so UI
    /// volume changes do not step between 20 ms frames (= audible click).
    /// </summary>
    public float VolumePrev;

    /// <summary>Total samples in the processed frame (&gt;= 1). Used to drive the ramp.</summary>
    public int FrameLength;

    /// <summary>Limiter threshold (e.g., 0.95) and soft knee width (e.g., 0.05).</summary>
    public float LimitThreshold;
    public float LimitKnee;

    public void Execute(int index)
    {
        float rampT = FrameLength > 1 ? (float)index / (FrameLength - 1) : 1f;
        float gain = math.lerp(VolumePrev, Volume, rampT);
        float x = processBufferArray[index] * gain;

        // Soft limiter:
        // Below (T): passthrough
        // Within knee: smooth cubic
        // Above knee end: hard cap at T + K
        float ax = math.abs(x);
        float T = LimitThreshold;
        float K = math.max(1e-6f, LimitKnee); // avoid div by zero

        if (ax <= T)
        {
            // no change
        }
        else if (ax >= T + K)
        {
            x = math.sign(x) * (T + K);
        }
        else
        {
            // soft knee curve in [T, T+K]
            float t = (ax - T) / K;                 // 0..1
            float y = T + K * (1f - math.pow(1f - t, 3f)); // smooth S
            x = math.sign(x) * y;
        }

        processBufferArray[index] = x;
    }
}
