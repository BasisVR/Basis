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
    /// <summary>Multiplier for exit threshold (use > 1 for hysteresis, e.g. 1.10f)</summary>
    public float HysteresisPercent;

    /// <summary>Normalized = d2 * ReductionMultiplier (caller defines scaling)</summary>
    public float ReductionMultiplier;

    /// <summary>When true, the LOD calculation scales squared distance down for players
    /// inside the gaze cone so they get a higher-detail mesh LOD even at distance.</summary>
    public bool UseEyeGaze;

    /// <summary>World-space gaze forward vector (unit length). Only consumed when <see cref="UseEyeGaze"/> is true.</summary>
    public float3 GazeForward;

    /// <summary>Cosine of the half-angle of the gaze cone. Players with dot(gazeForward, dir) above this threshold
    /// get the foveation boost.</summary>
    public float CosHalfGazeCone;

    /// <summary>Multiplier applied to squared distance for players at the cone center; players at the cone edge
    /// receive no boost. Lower values = stronger foveation. Identity boost is 1.0.</summary>
    public float GazeBoostFactor;

    [ReadOnly] public float3 referencePosition;
    [ReadOnly] public NativeArray<float3> targetPositions;

    [ReadOnly] public NativeArray<bool> PrevInMicrophoneRange;
    [ReadOnly] public NativeArray<bool> PrevInHearingRange;
    [ReadOnly] public NativeArray<bool> PrevInAvatarRange;

    [ReadOnly] public NativeArray<short> PrevMeshLodLevel;

    [WriteOnly] public NativeArray<float> distanceSq;
    [WriteOnly] public NativeArray<short> MeshLodLevel;

    /// <summary>Per-index pose LOD (0 = closest, 3 = furthest), banded off avatar range rather than
    /// the mesh LOD percentage so pose skipping is independent of the mesh/skin/shadow LOD slider.</summary>
    [WriteOnly] public NativeArray<short> PoseLodLevel;

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

        float effectiveD2 = d2;
        if (UseEyeGaze && d2 > 1e-6f)
        {
            float3 dir = diff * math.rsqrt(d2);
            float gazeDot = math.dot(GazeForward, dir);
            if (gazeDot >= CosHalfGazeCone)
            {
                float t = (gazeDot - CosHalfGazeCone) / math.max(1f - CosHalfGazeCone, 1e-6f);
                effectiveD2 = d2 * math.lerp(1f, GazeBoostFactor, t);
            }
        }

        float normalized = effectiveD2 * ReductionMultiplier;
        int lod = (int)math.floor(normalized * 4f);
        lod = math.clamp(lod, 0, 3);
        short newLod = (short)lod;

        MeshLodLevel[i] = newLod;

        // Pose LOD bands on the fraction of avatar range travelled, so the thresholds track the
        // distance at which a player stops being readable rather than the mesh LOD quality percent.
        float rangeFrac = SquaredAvatarDistance > 1e-6f ? effectiveD2 / SquaredAvatarDistance : 0f;
        PoseLodLevel[i] = (short)math.clamp((int)math.floor(rangeFrac * 4f), 0, 3);

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
    public int ReceiverCount;

    [ReadOnly] public NativeArray<float> PerIndexMinD2;
    [ReadOnly] public NativeArray<int> PerIndexMask;

    [WriteOnly] public NativeArray<float> SmallestD2; // length 1
    [WriteOnly] public NativeArray<int> ChangeMask;   // length 1

    public void Execute()
    {
        float minD2 = float.PositiveInfinity;
        int mask = 0;

        int len = ReceiverCount;
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
        // The job only runs when the limiter is enabled, so 0 means "show zero real
        // avatars", not unlimited — unlimited is the limiter toggle being off.
        if (MaxVisible < 0 || ReceiverCount <= 0)
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
/// Burst parallel job: computes the per-player spatial voice terms — the
/// listener cone-of-influence attenuation, and the two high-shelf depths that
/// carry the frequency-dependent half of the model (talker mouth directivity,
/// and the listener's own head shadowing whoever is behind them).
///
/// Reads targetPositions/targetForwards (shared [ReadOnly] with the distance
/// job) so it can run fully in parallel with distance, reduce, and cap jobs.
/// Output goes to NativeArrays; the caller copies to the managed
/// AudioReceiverModule in a trivial main-thread loop.
///
/// The cone is split into a broadband term and a shelf term rather than being a
/// single fader: see <c>BasisVoiceAcoustics.ListenerConeTerms</c> for why, and
/// for the guarantee that the two together still attenuate by exactly what the
/// user's dampening slider asks for.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
public struct BasisDirectionalDampenJob : IJobParallelFor
{
    public float3 ListenerPosition;
    public float3 ListenerForward;
    public float CosHalfCone;
    public float HalfConeRad;
    public float MinVolume;

    /// <summary>False when the cone angle is 360 — directivity still runs.</summary>
    public bool ConeEnabled;
    /// <summary>False to leave both shelves flat (tone shaping turned off).</summary>
    public bool ToneEnabled;

    /// <summary>Mirrors BasisVoiceAcoustics.ConeMaxShelfDb.</summary>
    public float ConeMaxShelfDb;
    /// <summary>Mirrors BasisVoiceAcoustics.ConeHighFrequencyShare.</summary>
    public float ConeHighFrequencyShare;
    /// <summary>Mirrors the broadband loss the capped cone shelf delivers.</summary>
    public float ConeShelfBroadbandDb;
    /// <summary>Mirrors BasisVoiceAcoustics.DirectivityShelfMaxDb.</summary>
    public float DirectivityShelfMaxDb;
    /// <summary>Mirrors BasisVoiceAcoustics.DirectivityShapePower.</summary>
    public float DirectivityShapePower;

    [ReadOnly] public NativeArray<float3> TargetPositions;
    [ReadOnly] public NativeArray<float3> TargetForwards;

    [WriteOnly] public NativeArray<float> Multipliers;
    [WriteOnly] public NativeArray<float> ConeShelfDb;
    [WriteOnly] public NativeArray<float> DirectivityShelfDb;

    public void Execute(int i)
    {
        Multipliers[i] = 1f;
        ConeShelfDb[i] = 0f;
        DirectivityShelfDb[i] = 0f;

        float3 toSource = TargetPositions[i] - ListenerPosition;
        float sqrMag = math.lengthsq(toSource);

        // Inside a few centimetres there is no meaningful direction to either the
        // talker or their mouth axis, and both terms would chatter on noise.
        if (sqrMag < 0.001f)
        {
            return;
        }

        float3 dirToSource = toSource * math.rsqrt(sqrMag);

        if (ConeEnabled)
        {
            float dot = math.dot(ListenerForward, dirToSource);
            if (dot < CosHalfCone)
            {
                float theta = math.acos(math.clamp(dot, -1f, 1f));
                float falloff = math.smoothstep(HalfConeRad, math.PI, theta);
                float wantDb = 20f * math.log10(math.max(1e-4f, MinVolume)) * falloff;

                if (ToneEnabled)
                {
                    float shelfDb = math.clamp(wantDb * ConeHighFrequencyShare, ConeMaxShelfDb, 0f);
                    float deliveredDb = ConeShelfBroadbandDb * (shelfDb / ConeMaxShelfDb);
                    ConeShelfDb[i] = shelfDb;
                    Multipliers[i] = math.pow(10f, math.min(0f, wantDb - deliveredDb) / 20f);
                }
                else
                {
                    Multipliers[i] = math.lerp(1f, MinVolume, falloff);
                }
            }
        }

        if (ToneEnabled)
        {
            // Angle between the mouth axis and the ray from the mouth to the
            // listener. TargetForwards is already unit length (it comes out of a
            // quaternion rotate), so no renormalise.
            float cosOffAxis = math.clamp(math.dot(TargetForwards[i], -dirToSource), -1f, 1f);
            float u = math.saturate((1f - cosOffAxis) * 0.5f);
            // pow(0, p) under FloatMode.Fast goes through exp2(p * log2(0)); branch
            // around it rather than trust that to land on 0 rather than NaN. The
            // result feeds a one-pole filter on the audio thread, and a NaN there is
            // unrecoverable for that voice.
            DirectivityShelfDb[i] = u > 0f ? -DirectivityShelfMaxDb * math.pow(u, DirectivityShapePower) : 0f;
        }
    }
}
