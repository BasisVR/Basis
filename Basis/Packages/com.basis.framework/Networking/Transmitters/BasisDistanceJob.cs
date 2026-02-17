using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Parallel + Burst: computes distance, hysteresis ranges, LOD.
/// Writes per-index change masks + per-index min d2 for reduction.
/// Mask bits: 0=mic, 1=hearing, 2=avatar, 3=lod.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
public struct BasisDistanceJobParallel : IJobParallelFor
{
    public float SquaredVoiceDistance;
    public float SquaredHearingDistance;
    public float SquaredAvatarDistance;
    public bool ComputeRange;
    /// <summary>Multiplier for exit threshold (use > 1 for hysteresis, e.g. 1.10f)</summary>
    public float HysteresisPercent;

    /// <summary>Normalized = d2 * ReductionMultiplier (caller defines scaling)</summary>
    public float ReductionMultiplier;

    [ReadOnly] public float3 referencePosition;
    [ReadOnly] public NativeArray<float3> targetPositions;

    [ReadOnly] public NativeArray<bool> PrevInMicrophoneRange;
    [ReadOnly] public NativeArray<bool> PrevInHearingRange;
    [ReadOnly] public NativeArray<bool> PrevInAvatarRange;

    [ReadOnly] public NativeArray<short> PrevMeshLodLevel;

    [WriteOnly] public NativeArray<float> distanceSq;
    [WriteOnly] public NativeArray<short> MeshLodLevel;

    [WriteOnly] public NativeArray<bool> MicrophoneRange;
    [WriteOnly] public NativeArray<bool> hearingRange;
    [WriteOnly] public NativeArray<bool> AvatarRange;

    /// <summary>Per-index: true if LOD changed vs previous</summary>
    [WriteOnly] public NativeArray<bool> MeshLodRange;

    [WriteOnly] public NativeArray<float> PerIndexMinD2;
    [WriteOnly] public NativeArray<int> PerIndexMask;

    public void Execute(int i)
    {
        float3 diff = targetPositions[i] - referencePosition;
        float d2 = math.lengthsq(diff);
        distanceSq[i] = d2;

        float voiceEnter = SquaredVoiceDistance;
        float hearEnter = SquaredHearingDistance;
        float avEnter = SquaredAvatarDistance;

        float voiceExit = voiceEnter * HysteresisPercent;
        float hearExit = hearEnter * HysteresisPercent;
        float avExit = avEnter * HysteresisPercent;

        bool prevVoice = PrevInMicrophoneRange[i];
        bool prevHearing = PrevInHearingRange[i];
        bool prevAvatar = PrevInAvatarRange[i];

        bool voice = prevVoice ? (d2 < voiceExit) : (d2 < voiceEnter);
        bool hearing = prevHearing ? (d2 < hearExit) : (d2 < hearEnter);
        bool avatar = prevAvatar ? (d2 < avExit) : (d2 < avEnter);

        MicrophoneRange[i] = voice;
        hearingRange[i] = hearing;
        AvatarRange[i] = avatar;

        float normalized = d2 * ReductionMultiplier;
        int lod = (int)math.floor(normalized * 4f);
        lod = math.clamp(lod, 0, 3);
        short newLod = (short)lod;

        MeshLodLevel[i] = newLod;

        bool lodChanged = newLod != PrevMeshLodLevel[i];
        MeshLodRange[i] = lodChanged;

        int mask = 0;
        if (voice != prevVoice)
        {
            mask |= 1;
        }

        if (hearing != prevHearing)
        {
            mask |= 2;
        }

        if (avatar != prevAvatar)
        {
            mask |= 4;
        }

        if (lodChanged)
        {
            mask |= 8;
        }

        PerIndexMask[i] = mask;
        PerIndexMinD2[i] = d2;
    }
}

/// <summary>
/// Reduces PerIndexMinD2 (min) and PerIndexMask (OR).
/// Outputs:
///   SmallestD2[0]
///   ChangeMask[0]
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
public struct BasisDistanceReduceJob : IJob
{
    [ReadOnly] public NativeArray<float> PerIndexMinD2;
    [ReadOnly] public NativeArray<int> PerIndexMask;

    [WriteOnly] public NativeArray<float> SmallestD2; // length 1
    [WriteOnly] public NativeArray<int> ChangeMask;   // length 1

    public void Execute()
    {
        float minD2 = float.PositiveInfinity;
        int mask = 0;

        int len = PerIndexMinD2.Length;
        for (int i = 0; i < len; i++)
        {
            minD2 = math.min(minD2, PerIndexMinD2[i]);
            mask |= PerIndexMask[i];
        }

        SmallestD2[0] = minD2;
        ChangeMask[0] = mask;
    }
}
