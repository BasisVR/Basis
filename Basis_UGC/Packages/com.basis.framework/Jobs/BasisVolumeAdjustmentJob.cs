using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;

[BurstCompile]
public struct BasisVolumeAdjustmentJob : IJobParallelFor
{
    [NativeDisableParallelForRestriction]
    public NativeArray<float> processBufferArray;

    // Recommended range: 0.0f (mute) to 1.0f (full volume)
    public float Volume;

    public void Execute(int index)
    {
        float sample = processBufferArray[index];

        // Apply a perceptual volume adjustment using a logarithmic scale
        if (Volume <= 0.0f)
        {
            processBufferArray[index] = 0.0f; // Mute case
            return;
        }

        float adjustedVolume = Mathf.Pow(Volume, 2.2f); // Perceptual adjustment
        processBufferArray[index] = sample * adjustedVolume;
    }
}
