using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
/// <summary>
/// Remote Face Management
/// this is mulithreaded blink and eye positions
/// </summary>
public static class BasisRemoteFaceManagement
{
    public static NativeArray<EyeState> eyeStates;
    public static NativeArray<BlinkState> blinkStates;
    public static NativeArray<EyeOutput> eyeIn, eyeOut;
    public static NativeArray<BlinkOutput> blinkOut;

    public static int capacity;

    // You can tune this
    const int BatchSize = 64;

    // Blink defaults (you can expose these)
    public static float MinBlinkInterval = 5f;
    public static float MaxBlinkInterval = 25f;
    public static float BlinkDuration = 0.2f;
    public static float OpenDuration = 0.05f;

    /// <summary>
    /// Minimum time (in seconds) between randomized look-around events.
    /// </summary>
    public const float MinLookAroundInterval = 1f;

    /// <summary>
    /// Maximum time (in seconds) between randomized look-around events.
    /// </summary>
    public const float MaxLookAroundInterval = 6f;

    /// <summary>
    /// Maximum horizontal offset (in normalized degrees) for random look targets.
    /// </summary>
    public const float MaxHorizontalLook = 0.75f;

    /// <summary>
    /// Maximum vertical offset (in normalized degrees) for random look targets.
    /// </summary>
    public const float MaxVerticalLook = 0.75f;

    /// <summary>
    /// Speed multiplier controlling how quickly the eyes interpolate toward their target.
    /// </summary>
    public const float LookSpeed = 15;
    public static JobHandle handle;
    public static void Simulate(double t, float dt,int count, Basis.Scripts.Networking.Receivers.BasisNetworkReceiver[] snapshot)
    {
        if (count <= 0) return;

        EnsureArrays(count);

        // MAIN THREAD: snapshot current eye values
        for (int Index = 0; Index < count; Index++)
        {
            var receiver = snapshot[Index];

            // If this can ever be null/short, guard it:
            var arr = receiver.EyesAndMouth;
            eyeIn[Index] = new EyeOutput
            {
                vL = arr[0],
                hL = arr[1],
                vR = arr[2],
                hR = arr[3]
            };
        }
        float deltaSpeed = LookSpeed * dt;

        var job = new RemoteAnimJob
        {
            time = t,
            deltaSpeed = deltaSpeed,

            minLookInterval = MinLookAroundInterval,
            maxLookInterval = MaxLookAroundInterval,
            maxHoriz = MaxHorizontalLook,
            maxVert = MaxVerticalLook,

            minBlinkInterval = MinBlinkInterval,
            maxBlinkInterval = MaxBlinkInterval,
            blinkDuration = BlinkDuration,
            openDuration = OpenDuration,

            eyeStates = eyeStates,
            blinkStates = blinkStates,
            eyeIn = eyeIn,
            eyeOut = eyeOut,
            blinkOut = blinkOut,
        };

         handle = job.Schedule(count, BatchSize);
    }
    public static void Apply(int count, Basis.Scripts.Networking.Receivers.BasisNetworkReceiver[] snapshot)
    {
        if (count <= 0) return;

        handle.Complete();

        for (int Index = 0; Index < count; Index++)
        {
            var receiver = snapshot[Index];
            var remote = receiver.RemotePlayer;

            var Face = remote.RemoteFaceDriver;

            if (Face.OverrideEye == false)
            {
                var e = eyeOut[Index];
                float[] eyes = receiver.EyesAndMouth;

                eyes[0] = e.vL;
                eyes[1] = e.hL;
                eyes[2] = e.vR;
                eyes[3] = e.hR;
                receiver.EyesAndMouth = eyes;
            }
            if (Face.BlinkingEnabled && !Face.OverrideBlinking && Face.meshRenderer != null)
            {
                float weight100 = blinkOut[Index].weight01 * 100f;
                for (int b = 0; b < Face.blendShapeCount; b++)
                {
                    Face.SafeSetBlendShape(Face.blendShapeIndices[b], weight100);
                }
            }
        }
    }
   static void EnsureArrays(int requiredCount)
    {
        if (requiredCount <= capacity && eyeStates.IsCreated)
            return;

        // Resize strategy: grow to next power of two to reduce realloc churn
        int newCap = math.ceilpow2(math.max(16, requiredCount));

        DisposeArrays();

        capacity = newCap;

        eyeStates = new NativeArray<EyeState>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        blinkStates = new NativeArray<BlinkState>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        eyeIn = new NativeArray<EyeOutput>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        eyeOut = new NativeArray<EyeOutput>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        blinkOut = new NativeArray<BlinkOutput>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        // Seed RNGs + initial timings
        // Use a changing base seed so two instances don't behave identically.
        uint baseSeed = (uint)UnityEngine.Random.Range(1, int.MaxValue);

        for (int i = 0; i < capacity; i++)
        {
            uint eyeSeed = HashToNonZero(baseSeed, (uint)(i * 2 + 1));
            uint blinkSeed = HashToNonZero(baseSeed, (uint)(i * 2 + 2));

            eyeStates[i] = new EyeState
            {
                nextLookAroundTime = 0.0, // job will schedule on first tick
                target = float2.zero,
                isLooking = 0,
                rng = new Unity.Mathematics.Random(eyeSeed)
            };

            blinkStates[i] = new BlinkState
            {
                nextBlinkTime = 0.0, // job will schedule on first tick
                blinkStartTime = 0.0,
                openStartTime = 0.0,
                isClosing = 0,
                isOpening = 0,
                rng = new Unity.Mathematics.Random(blinkSeed)
            };
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
        if (eyeIn.IsCreated) eyeIn.Dispose();
        if (eyeOut.IsCreated) eyeOut.Dispose();
        if (blinkOut.IsCreated) blinkOut.Dispose();
    }
    public static void Dispose()
    {
        DisposeArrays();
        capacity = 0;
    }

    [BurstCompile]
    public struct RemoteAnimJob : IJobParallelFor
    {
        public double time;
        public float deltaSpeed;

        // Eye config
        public float minLookInterval;
        public float maxLookInterval;
        public float maxHoriz;
        public float maxVert;

        // Blink config
        public float minBlinkInterval;
        public float maxBlinkInterval;
        public float blinkDuration;
        public float openDuration;

        public NativeArray<EyeState> eyeStates;
        public NativeArray<BlinkState> blinkStates;

        [ReadOnly] public NativeArray<EyeOutput> eyeIn;
        public NativeArray<EyeOutput> eyeOut;

        public NativeArray<BlinkOutput> blinkOut;

        public void Execute(int Index)
        {
            // --------------------
            // EYES
            // --------------------
            var es = eyeStates[Index];
            var e = eyeIn[Index];

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
                e.vL = math.lerp(e.vL, es.target.y, deltaSpeed);
                e.vR = math.lerp(e.vR, es.target.y, deltaSpeed);

                e.hL = math.lerp(e.hL, es.target.x, deltaSpeed);
                e.hR = math.lerp(e.hR, -es.target.x, deltaSpeed);

                if (math.abs(e.vL - es.target.y) < 0.01f && math.abs(e.hL - es.target.x) < 0.01f)
                    es.isLooking = 0;
            }

            eyeStates[Index] = es;
            eyeOut[Index] = e;

            // --------------------
            // BLINK
            // --------------------
            var bs = blinkStates[Index];
            float w01;

            // schedule next blink if needed
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

            blinkStates[Index] = bs;
            blinkOut[Index] = new BlinkOutput { weight01 = w01 };
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

    public struct BlinkOutput
    {
        public float weight01;
    }
}
