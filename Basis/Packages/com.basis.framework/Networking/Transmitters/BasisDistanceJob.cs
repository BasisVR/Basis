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

/// <summary>
/// Burst job that enforces the MaxVisibleAvatars cap.
/// Scheduled after the distance job (reads its outputs), runs in parallel with the reduce job.
/// Uses quickselect (O(n) average) to partition the closest N candidates from the rest,
/// then flips AvatarRange to false for everyone beyond the cap.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
public struct BasisAvatarCapJob : IJob
{
    public int MaxVisible;
    public int ReceiverCount;
    public float StickinessBonus;

    [ReadOnly] public NativeArray<float> DistanceSq;
    [ReadOnly] public NativeArray<bool> HasRealAvatarLoaded;

    public NativeArray<bool> AvatarRange;
    public NativeArray<AvatarCapEntry> Entries;

    public void Execute()
    {
        if (MaxVisible <= 0 || ReceiverCount <= 0)
        {
            return;
        }

        // Build candidate list with effective distances (stickiness baked in).
        int count = 0;
        for (int i = 0; i < ReceiverCount; i++)
        {
            if (!AvatarRange[i])
            {
                continue;
            }

            float d2 = DistanceSq[i];
            Entries[count++] = new AvatarCapEntry
            {
                Index = i,
                EffectiveDistSq = HasRealAvatarLoaded[i] ? d2 * StickinessBonus : d2,
            };
        }

        if (count <= MaxVisible)
        {
            return;
        }

        // Quickselect: partition so the MaxVisible closest are in [0..MaxVisible-1].
        // O(n) average — no full sort needed since we only care about the boundary.
        NthElement(0, count - 1, MaxVisible);

        // Everything from MaxVisible onward loses avatar range.
        for (int i = MaxVisible; i < count; i++)
        {
            AvatarRange[Entries[i].Index] = false;
        }
    }

    private void NthElement(int left, int right, int n)
    {
        while (left < right)
        {
            int pivot = Partition(left, right);
            if (pivot == n)
            {
                return;
            }
            if (pivot < n)
            {
                left = pivot + 1;
            }
            else
            {
                right = pivot - 1;
            }
        }
    }

    private int Partition(int left, int right)
    {
        // Median-of-three pivot for better average performance.
        int mid = left + (right - left) / 2;
        if (Entries[mid].EffectiveDistSq < Entries[left].EffectiveDistSq)
        {
            SwapEntries(left, mid);
        }
        if (Entries[right].EffectiveDistSq < Entries[left].EffectiveDistSq)
        {
            SwapEntries(left, right);
        }
        if (Entries[mid].EffectiveDistSq < Entries[right].EffectiveDistSq)
        {
            SwapEntries(mid, right);
        }

        float pivotVal = Entries[right].EffectiveDistSq;
        int store = left;
        for (int j = left; j < right; j++)
        {
            if (Entries[j].EffectiveDistSq <= pivotVal)
            {
                SwapEntries(store, j);
                store++;
            }
        }
        SwapEntries(store, right);
        return store;
    }

    private void SwapEntries(int a, int b)
    {
        AvatarCapEntry tmp = Entries[a];
        Entries[a] = Entries[b];
        Entries[b] = tmp;
    }
}

/// <summary>
/// Burst job: filters AvatarRange based on view-cone direction.
/// Only players within the specified cone angle (relative to the local
/// camera forward) keep AvatarRange = true. Players outside the cone
/// have AvatarRange set to false, causing a fallback avatar.
/// Scheduled after distance + cap jobs so it acts as a final filter.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
public struct BasisViewConeAvatarJob : IJobParallelFor
{
    public float3 ListenerPosition;
    public float3 ListenerForward;
    public float CosHalfCone;

    [ReadOnly] public NativeArray<float3> TargetPositions;

    [NativeDisableParallelForRestriction]
    public NativeArray<bool> AvatarRange;

    public void Execute(int i)
    {
        if (!AvatarRange[i])
        {
            return;
        }

        float3 toTarget = TargetPositions[i] - ListenerPosition;
        float sqrMag = math.lengthsq(toTarget);

        if (sqrMag < 0.001f)
        {
            return;
        }

        float3 dir = toTarget * math.rsqrt(sqrMag);
        float dot = math.dot(ListenerForward, dir);

        if (dot < CosHalfCone)
        {
            AvatarRange[i] = false;
        }
    }
}

/// <summary>
/// Sortable entry for the avatar visibility cap.
/// </summary>
public struct AvatarCapEntry
{
    public int Index;
    public float EffectiveDistSq;
}

/// <summary>
/// Sortable entry for the audio source cap.
/// </summary>
public struct AudioCapEntry
{
    public int Index;
    public float EffectiveDistSq;
}

/// <summary>
/// Burst job that enforces the MaxAudioSources cap.
/// Mirrors BasisAvatarCapJob but operates on hearingRange instead of AvatarRange.
/// Uses quickselect O(n) average to partition the closest N candidates,
/// then flips hearingRange to false for everyone beyond the cap.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
public struct BasisAudioCapJob : IJob
{
    public int MaxAudio;
    public int ReceiverCount;
    public float StickinessBonus;

    [ReadOnly] public NativeArray<float> DistanceSq;
    [ReadOnly] public NativeArray<bool> HasActiveAudioSource;

    public NativeArray<bool> HearingRange;
    public NativeArray<AudioCapEntry> Entries;

    public void Execute()
    {
        if (MaxAudio <= 0 || ReceiverCount <= 0)
        {
            return;
        }

        int count = 0;
        for (int i = 0; i < ReceiverCount; i++)
        {
            if (!HearingRange[i])
            {
                continue;
            }

            float d2 = DistanceSq[i];
            Entries[count++] = new AudioCapEntry
            {
                Index = i,
                EffectiveDistSq = HasActiveAudioSource[i] ? d2 * StickinessBonus : d2,
            };
        }

        if (count <= MaxAudio)
        {
            return;
        }

        NthElement(0, count - 1, MaxAudio);

        for (int i = MaxAudio; i < count; i++)
        {
            HearingRange[Entries[i].Index] = false;
        }
    }

    private void NthElement(int left, int right, int n)
    {
        while (left < right)
        {
            int pivot = Partition(left, right);
            if (pivot == n)
            {
                return;
            }
            if (pivot < n)
            {
                left = pivot + 1;
            }
            else
            {
                right = pivot - 1;
            }
        }
    }

    private int Partition(int left, int right)
    {
        int mid = left + (right - left) / 2;
        if (Entries[mid].EffectiveDistSq < Entries[left].EffectiveDistSq)
        {
            SwapEntries(left, mid);
        }
        if (Entries[right].EffectiveDistSq < Entries[left].EffectiveDistSq)
        {
            SwapEntries(left, right);
        }
        if (Entries[mid].EffectiveDistSq < Entries[right].EffectiveDistSq)
        {
            SwapEntries(mid, right);
        }

        float pivotVal = Entries[right].EffectiveDistSq;
        int store = left;
        for (int j = left; j < right; j++)
        {
            if (Entries[j].EffectiveDistSq <= pivotVal)
            {
                SwapEntries(store, j);
                store++;
            }
        }
        SwapEntries(store, right);
        return store;
    }

    private void SwapEntries(int a, int b)
    {
        AudioCapEntry tmp = Entries[a];
        Entries[a] = Entries[b];
        Entries[b] = tmp;
    }
}

/// <summary>
/// Burst parallel job: computes per-player directional dampening multipliers.
/// Reads targetPositions (shared [ReadOnly] with the distance job) so it can
/// run fully in parallel with distance, reduce, and cap jobs.
/// Output is written to a NativeArray; the caller copies to managed AudioReceiverModule
/// in a trivial main-thread loop.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
public struct BasisDirectionalDampenJob : IJobParallelFor
{
    public float3 ListenerPosition;
    public float3 ListenerForward;
    public float CosHalfCone;
    public float CosRange;
    public float MinVolume;

    [ReadOnly] public NativeArray<float3> TargetPositions;
    [WriteOnly] public NativeArray<float> Multipliers;

    public void Execute(int i)
    {
        float3 toSource = TargetPositions[i] - ListenerPosition;
        float sqrMag = math.lengthsq(toSource);

        if (sqrMag < 0.001f)
        {
            Multipliers[i] = 1f;
            return;
        }

        float3 dir = toSource * math.rsqrt(sqrMag);
        float dot = math.dot(ListenerForward, dir);

        if (dot >= CosHalfCone)
        {
            Multipliers[i] = 1f;
        }
        else
        {
            float t = (CosHalfCone - dot) / CosRange;
            Multipliers[i] = math.lerp(1f, MinVolume, t);
        }
    }
}
