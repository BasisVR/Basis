/*
 * BasicOneEuroFilterParallelJob.cs
 * Author: Dario Mazzanti (dario.mazzanti@iit.it), 2016
 *
 * This Unity C# utility is based on the C++ implementation of the OneEuroFilter algorithm by Nicolas Roussel (http://www.lifl.fr/~casiez/1euro/OneEuroFilter.cc)
 * More info on the 1€ filter by Géry Casiez at http://www.lifl.fr/~casiez/1euro/
 *
 */

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
/*
// A job struct for processing OneEuroFilter in parallel
[BurstCompile]
public struct BasisOneEuroFilterParallelJob : IJobParallelFor
{
    public NativeArray<float> Values;

    public float MinCutoff;
    public float Beta;
    public float DerivativeCutoff;
    public float DeltaTime;

    public NativeArray<float2> PositionFilters;
    public NativeArray<float2> DerivativeFilters;

    public void Execute(int index)
    {
        float frequency = 1.0f / DeltaTime[index];

        // Estimate variation
        float inputValue = Values[index];
        float dValue = (inputValue - PositionFilters[index].x) * frequency;
        float edValue = FilterDerivativeWithAlpha(dValue, Alpha(DerivativeCutoff, frequency), index);
        DerivativeFilters[index] = new float2(dValue, edValue);

        // Update cutoff frequency
        float cutoff = MinCutoff + Beta * Mathf.Abs(edValue);

        // Filter input value
        Values[index] = FilterPositionWithAlpha(inputValue, Alpha(cutoff, frequency), index);
        PositionFilters[index] = new float2(inputValue, Values[index]);
    }

    private float Alpha(float cutoff, float frequency)
    {
        float te = 1.0f / frequency;
        float tau = 1.0f / (2.0f * Mathf.PI * cutoff);
        return 1.0f / (1.0f + tau / te);
    }

    private float FilterPositionWithAlpha(float inputValue, float alpha, int index)
    {
        return alpha * inputValue + (1.0f - alpha) * PositionFilters[index].y;
    }

    private float FilterDerivativeWithAlpha(float dValue, float alpha, int index)
    {
        return alpha * dValue + (1.0f - alpha) * DerivativeFilters[index].y;
    }
}
*/
/*
 * BasisOneEuroFilterParallelJob.cs
 * Author of original 1€ filter concept: Géry Casiez et al.
 * Unity C# adaptation for parallel jobs.
 */
[BurstCompile]
public struct BasisOneEuroFilterParallelJob : IJobParallelFor
{
    // Input signal (flattened: players * muscles)
    [ReadOnly] public NativeArray<float> InputValues;

    // Output signal (flattened: players * muscles)
    public NativeArray<float> OutputValues;

    // Per-player deltaTime (or sampling period proxy). Length == numPlayers
    [ReadOnly] public NativeArray<float> DeltaTime;

    // Filter state per flattened channel (same length as OutputValues)
    public NativeArray<float2> PositionFilters;   // x = previous input, y = previous output
    public NativeArray<float2> DerivativeFilters; // x = previous derivative input, y = previous derivative output

    // Parameters
    public float MinCutoff;
    public float Beta;
    public float DerivativeCutoff;

    // Stride to recover the player index from the flattened channel index
    // i.e., the number of muscles per avatar
    [ReadOnly] public int MuscleCountPerAvatar;

    public void Execute(int index)
    {
        // 2D indexing: player + muscle
        int playerIndex = MuscleCountPerAvatar > 0 ? (index / MuscleCountPerAvatar) : 0;

        // Defensive: if something is off, early-out safely (keeps Burst happy).
        if ((uint)playerIndex >= (uint)DeltaTime.Length) return;
        if ((uint)index >= (uint)InputValues.Length) return;
        if ((uint)index >= (uint)OutputValues.Length) return;
        if ((uint)index >= (uint)PositionFilters.Length) return;
        if ((uint)index >= (uint)DerivativeFilters.Length) return;

        float dt = DeltaTime[playerIndex];
        // Guard against zero/very small dt to avoid div-by-zero or huge frequency
        if (dt <= 0f) dt = 1e-3f;

        float frequency = 1.0f / dt;

        // Current raw input for this (player,muscle) channel
        float inputValue = InputValues[index];

        // Previous filtered output stored in PositionFilters[index].y
        float prevFiltered = PositionFilters[index].y;
        float prevRaw = PositionFilters[index].x;

        // Derivative estimate from raw inputs
        float dValue = (inputValue - prevRaw) * frequency;

        // Filter derivative
        float alphaD = Alpha(DerivativeCutoff, frequency);
        float prevDerivFiltered = DerivativeFilters[index].y;
        float edValue = alphaD * dValue + (1.0f - alphaD) * prevDerivFiltered;

        // Update cutoff based on filtered derivative magnitude
        float cutoff = MinCutoff + Beta * Mathf.Abs(edValue);

        // Filter the input value
        float alphaX = Alpha(cutoff, frequency);
        float filtered = alphaX * inputValue + (1.0f - alphaX) * prevFiltered;

        // Write output
        OutputValues[index] = filtered;

        // Update state
        PositionFilters[index] = new float2(inputValue, filtered);
        DerivativeFilters[index] = new float2(dValue, edValue);
    }

    private static float Alpha(float cutoff, float frequency)
    {
        // cutoff in Hz, frequency in Hz
        float te = 1.0f / frequency;
        float tau = 1.0f / (2.0f * Mathf.PI * math.max(cutoff, 1e-4f));
        return 1.0f / (1.0f + tau / te);
    }
}
