using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Remote Face Management
/// Multithreaded blink + eye look-around.
/// Key changes:
/// - NaN/Inf sanitization happens INSIDE the Burst job.
/// - Removes per-frame main-thread copy into eyeIn (job uses eyeOut as its running state).
/// - Safer lerp factor (saturate) to avoid overshoot explosions.
/// </summary>
public static class BasisRemoteFaceManagement
{
    public static NativeArray<EyeState> eyeStates;
    public static NativeArray<BlinkState> blinkStates;

    // eyeOut is now the authoritative per-remote eye state that the job updates.
    public static NativeArray<EyeOutput> eyeOut;
    public static NativeArray<float> blinkOut;

    public static int capacity;

    // You can tune this
    const int BatchSize = 64;

    // Blink defaults (you can expose these)
    public static float MinBlinkInterval = 5f;
    public static float MaxBlinkInterval = 25f;
    public static float BlinkDuration = 0.2f;
    public static float OpenDuration = 0.05f;

    /// <summary>Minimum time (in seconds) between randomized look-around events.</summary>
    public const float MinLookAroundInterval = 1f;

    /// <summary>Maximum time (in seconds) between randomized look-around events.</summary>
    public const float MaxLookAroundInterval = 6f;

    /// <summary>Maximum horizontal offset (in normalized degrees) for random look targets.</summary>
    public const float MaxHorizontalLook = 0.75f;

    /// <summary>Maximum vertical offset (in normalized degrees) for random look targets.</summary>
    public const float MaxVerticalLook = 0.75f;

    /// <summary>Speed multiplier controlling how quickly the eyes interpolate toward their target.</summary>
    public const float LookSpeed = 15;

    public static JobHandle handle;
    public static BasisNetworkReceiver[] snapshot;
    public static  int count;
    public static void Simulate(double t,float dt)
    {
        snapshot = BasisNetworkPlayers.ReceiversSnapshot;
        count = BasisNetworkPlayers.ReceiverCount;
        if (count <= 0)
        {
            return;
        }

        EnsureArrays(count, t, snapshot);

        // No per-frame main-thread copy into a NativeArray.
        // The job will update eyeOut in-place (as state), and Apply() will write eyeOut back to receivers.

        var job = new RemoteAnimJob
        {
            time = t,
            dt = dt,

            // Eye config
            minLookInterval = MinLookAroundInterval,
            maxLookInterval = MaxLookAroundInterval,
            maxHoriz = MaxHorizontalLook,
            maxVert = MaxVerticalLook,
            lookSpeed = LookSpeed,

            // Blink config
            minBlinkInterval = MinBlinkInterval,
            maxBlinkInterval = MaxBlinkInterval,
            blinkDuration = BlinkDuration,
            openDuration = OpenDuration,

            eyeStates = eyeStates,
            blinkStates = blinkStates,

            eyeOut = eyeOut,
            blinkOut = blinkOut,
        };

        handle = job.Schedule(count, BatchSize);
    }

    public static void Apply()
    {
        if (count <= 0) return;

        handle.Complete();

        for (int Index = 0; Index < count; Index++)
        {
            var receiver = snapshot[Index];
            var remote = receiver.RemotePlayer;
            var Face = remote.RemoteFaceDriver;

            if (!Face.OverrideEye)
            {
                var e = eyeOut[Index];

                float[] eyes = receiver.EyesAndMouth;
                eyes[0] = e.vL;
                eyes[1] = e.hL;
                eyes[2] = e.vR;
                eyes[3] = e.hR;
            }

            if (Face.BlinkingEnabled && !Face.OverrideBlinking && Face.meshRenderer != null)
            {
                float w = blinkOut[Index];
                if (!float.IsFinite(w)) w = 0f;

                float weight100 = w * 100f;

                // If all blendshapes get the same blink weight, this loop is unavoidable
                // unless your driver has a "set all blink shapes" bulk method.
                for (int b = 0; b < Face.blendShapeCount; b++)
                {
                    Face.SafeSetBlendShape(Face.blendShapeIndices[b], weight100);
                }
            }
        }
    }

    static void EnsureArrays(int requiredCount,double nowTime,BasisNetworkReceiver[] snapshot)
    {
        // Already sufficient
        if (requiredCount <= capacity && eyeStates.IsCreated)
        {
            return;
        }

        // Never dispose/reallocate while a job might be using them.
        handle.Complete();

        int oldCap = capacity;
        int newCap = math.ceilpow2(math.max(16, requiredCount));

        var newEyeStates = new NativeArray<EyeState>(newCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        var newBlinkStates = new NativeArray<BlinkState>(newCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        // ClearMemory prevents “uninitialized NaN” surprises.
        var newEyeOut = new NativeArray<EyeOutput>(newCap, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        var newBlinkOut = new NativeArray<float>(newCap, Allocator.Persistent, NativeArrayOptions.ClearMemory);

        // Copy old live data across if present
        if (eyeStates.IsCreated)
        {
            int copyCount = math.min(oldCap, newCap);

            NativeArray<EyeState>.Copy(eyeStates, newEyeStates, copyCount);
            NativeArray<BlinkState>.Copy(blinkStates, newBlinkStates, copyCount);
            NativeArray<EyeOutput>.Copy(eyeOut, newEyeOut, copyCount);
            NativeArray<float>.Copy(blinkOut, newBlinkOut, copyCount);

            DisposeArrays();
        }

        capacity = newCap;
        eyeStates = newEyeStates;
        blinkStates = newBlinkStates;
        eyeOut = newEyeOut;
        blinkOut = newBlinkOut;

        // Seed RNGs + initialize ONLY the new slots
        uint baseSeed = (uint)UnityEngine.Random.Range(1, int.MaxValue);

        for (int i = oldCap; i < newCap; i++)
        {
            uint eyeSeed = HashToNonZero(baseSeed, (uint)(i * 2 + 1));
            uint blinkSeed = HashToNonZero(baseSeed, (uint)(i * 2 + 2));

            // Start pose from snapshot if it exists
            float2 startTarget = float2.zero;
            EyeOutput startEye = default;

            if (i < requiredCount && snapshot != null)
            {
                var arr = snapshot[i].EyesAndMouth;
                if (arr != null && arr.Length >= 4)
                {
                    // Canonical mapping:
                    // arr[0]=vL, arr[1]=hL, arr[2]=vR, arr[3]=hR
                    float vL = float.IsFinite(arr[0]) ? arr[0] : 0f;
                    float hL = float.IsFinite(arr[1]) ? arr[1] : 0f;
                    float vR = float.IsFinite(arr[2]) ? arr[2] : 0f;
                    float hR = float.IsFinite(arr[3]) ? arr[3] : 0f;

                    // Job uses target.x = horiz, target.y = vert (use left eye as representative)
                    startTarget = new float2(hL, vL);
                    startEye = new EyeOutput { vL = vL, hL = hL, vR = vR, hR = hR };
                }
            }

            eyeStates[i] = new EyeState
            {
                nextLookAroundTime = nowTime + Unity.Mathematics.Random.CreateFromIndex(eyeSeed)
                    .NextFloat(MinLookAroundInterval, MaxLookAroundInterval),
                target = startTarget,
                isLooking = 0,
                rng = new Unity.Mathematics.Random(eyeSeed),
            };

            blinkStates[i] = new BlinkState
            {
                nextBlinkTime = nowTime + Unity.Mathematics.Random.CreateFromIndex(blinkSeed)
                    .NextFloat(MinBlinkInterval, MaxBlinkInterval),
                blinkStartTime = 0.0,
                openStartTime = 0.0,
                isClosing = 0,
                isOpening = 0,
                rng = new Unity.Mathematics.Random(blinkSeed),
            };

            // Seed output so Apply has something sensible immediately
            eyeOut[i] = startEye;
            blinkOut[i] = 0f;
        }
    }

    static uint HashToNonZero(uint a, uint b)
    {
        // Simple mix; must not return 0 for Unity.Mathematics.Random
        uint x = a ^ (b * 0x9E3779B9u);
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x == 0 ? 1u : x;
    }

    static void DisposeArrays()
    {
        if (eyeStates.IsCreated) eyeStates.Dispose();
        if (blinkStates.IsCreated) blinkStates.Dispose();
        if (eyeOut.IsCreated) eyeOut.Dispose();
        if (blinkOut.IsCreated) blinkOut.Dispose();
    }

    public static void Dispose()
    {
        handle.Complete();
        DisposeArrays();
        capacity = 0;
    }

    [BurstCompile]
    public struct RemoteAnimJob : IJobParallelFor
    {
        public double time;
        public float dt;

        // Eye config
        public float minLookInterval;
        public float maxLookInterval;
        public float maxHoriz;
        public float maxVert;
        public float lookSpeed;

        // Blink config
        public float minBlinkInterval;
        public float maxBlinkInterval;
        public float blinkDuration;
        public float openDuration;

        public NativeArray<EyeState> eyeStates;
        public NativeArray<BlinkState> blinkStates;

        // eyeOut is BOTH input and output state for eyes
        public NativeArray<EyeOutput> eyeOut;

        public NativeArray<float> blinkOut;

        public void Execute(int Index)
        {
            var es = eyeStates[Index];
            var e = eyeOut[Index];

            // Sanitize current state so NaNs don’t propagate forever
            e.vL = Sanitize(e.vL);
            e.hL = Sanitize(e.hL);
            e.vR = Sanitize(e.vR);
            e.hR = Sanitize(e.hR);

            if (time >= es.nextLookAroundTime)
            {
                double interval = es.rng.NextFloat(minLookInterval, maxLookInterval);
                es.nextLookAroundTime = time + interval;

                es.target = new float2(
                    es.rng.NextFloat(-maxHoriz, maxHoriz),
                    es.rng.NextFloat(-maxVert, maxVert)
                );

                es.isLooking = 1;
            }

            if (es.isLooking != 0)
            {
                // Use a stable 0..1 factor; avoids overshoot.
                float t01 = math.saturate(lookSpeed * dt);

                e.vL = math.lerp(e.vL, es.target.y, t01);
                e.vR = math.lerp(e.vR, es.target.y, t01);

                e.hL = math.lerp(e.hL, es.target.x, t01);
                e.hR = math.lerp(e.hR, -es.target.x, t01);

                if (math.abs(e.vL - es.target.y) < 0.01f && math.abs(e.hL - es.target.x) < 0.01f)
                    es.isLooking = 0;
            }

            // Clamp to your expected operating range (optional but robust)
            e.vL = math.clamp(e.vL, -1f, 1f);
            e.vR = math.clamp(e.vR, -1f, 1f);
            e.hL = math.clamp(e.hL, -1f, 1f);
            e.hR = math.clamp(e.hR, -1f, 1f);

            eyeStates[Index] = es;
            eyeOut[Index] = e;

            // --------------------
            // BLINK
            // --------------------
            var bs = blinkStates[Index];
            float w01;

            if (bs.nextBlinkTime <= 0.0)
            {
                bs.nextBlinkTime = time + bs.rng.NextFloat(minBlinkInterval, maxBlinkInterval);
            }

            if (bs.isClosing == 0 && bs.isOpening == 0 && time >= bs.nextBlinkTime)
            {
                bs.isClosing = 1;
                bs.blinkStartTime = time;

                bs.isOpening = 1;
                bs.openStartTime = time;
            }

            if (bs.isClosing != 0)
            {
                float t = (float)((time - bs.blinkStartTime) / blinkDuration);
                t = math.saturate(t);
                w01 = t;

                if (t >= 1f)
                {
                    bs.isClosing = 0;
                    bs.nextBlinkTime = time + bs.rng.NextFloat(minBlinkInterval, maxBlinkInterval);
                }
            }
            else if (bs.isOpening != 0)
            {
                float t = (float)((time - bs.openStartTime) / openDuration);
                t = math.saturate(t);
                w01 = 1f - t;

                if (t >= 1f)
                    bs.isOpening = 0;
            }
            else
            {
                w01 = 0f;
            }

            // Sanitize blink output too
            w01 = Sanitize01(w01);

            blinkStates[Index] = bs;
            blinkOut[Index] = w01;
        }

        static float Sanitize(float x)
        {
            // Burst-friendly finite check
            return math.isfinite(x) ? x : 0f;
        }

        static float Sanitize01(float x)
        {
            if (!math.isfinite(x)) return 0f;
            return math.saturate(x);
        }
    }

    public struct EyeState
    {
        public double nextLookAroundTime;
        public float2 target;
        public byte isLooking;
        public Unity.Mathematics.Random rng;
    }

    public struct BlinkState
    {
        public double nextBlinkTime;
        public double blinkStartTime;
        public double openStartTime;
        public byte isClosing;
        public byte isOpening;
        public Unity.Mathematics.Random rng;
    }

    public struct EyeOutput
    {
        public float vL, hL, vR, hR;
    }
}
