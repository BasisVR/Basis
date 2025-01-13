/*
 * (c) Luke Doolan 2025-present.
 * This file is released under the terms of the MIT license, and contains code written by Dario Mazzanti released under the same terms.
 * Original implementation sourced from https://github.com/DarioMazzanti/OneEuroFilterUnity
 * This Unity C# utility is based on the C++ implementation of the OneEuroFilter algorithm by Nicolas Roussel (http://www.lifl.fr/~casiez/1euro/OneEuroFilter.cc)
 * More info on the 1€ filter by Géry Casiez at http://www.lifl.fr/~casiez/1euro/
 *
 */

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// A job struct for processing OneEuroFilter in parallel
[BurstCompile]
public struct BasisOneEuroFilterParallelJob : IJobParallelFor
{
    [ReadOnly]
    public NativeArray<float> InputValues;
    public NativeArray<float> OutputValues;

    public float MinCutoff;
    public float Beta;
    public float DerivativeCutoff;
    public float DeltaTime;

    public NativeArray<float2> PositionFilters;
    public NativeArray<float2> DerivativeFilters;

    public void Execute(int index)
    {
	    float frequency = 1.0f / DeltaTime;

        // Estimate variation
        float inputValue = InputValues[index];
        float dValue = (inputValue - PositionFilters[index].x) * frequency;
        float edValue = FilterDerivativeWithAlpha(dValue, Alpha(DerivativeCutoff, frequency), index);
        DerivativeFilters[index] = new float2(dValue, edValue);

        // Update cutoff frequency
        float cutoff = MinCutoff + Beta * Mathf.Abs(edValue);

        // Filter input value
        OutputValues[index] = FilterPositionWithAlpha(inputValue, Alpha(cutoff, frequency), index);
        PositionFilters[index] = new float2(inputValue, OutputValues[index]);
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
