using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using Movement = BasisAuthoredMotion.Movement;
using Kind = BasisAuthoredMotion.Movement.Kind;
using Channel = BasisAuthoredMotion.Movement.Channel;
using Waveform = BasisAuthoredMotion.Movement.Waveform;

/// <summary>
/// Blittable, flattened <see cref="BasisAuthoredMotion.Movement"/> plus the captured rest pose.
/// One slot per driven transform — a chain element is its own slot, with its element phase /
/// falloff baked in at registration. Parallel to the system's <c>TransformAccessArray</c>.
/// </summary>
public struct AuthoredMovementData
{
    public int kind;       // (int)Movement.Kind
    public int channel;    // (int)Movement.Channel
    public int waveform;   // (int)Movement.Waveform

    public float3 axis;
    public float amplitude;
    public float frequencyHz;
    public float phase;
    public float pulseWidth;

    public float speedDeg;       // Rotate
    public float radius;         // Orbit
    public float orbitSpeedDeg;  // Orbit
    public float3 pivotLocal;    // Orbit pivot, in the target's parent space
    public float noiseSpeed;     // Noise
    public uint seed;            // Noise / RandomSelect

    // RandomSelect — selection is deterministic per cycle, so each slot evaluates independently.
    public float period;         // seconds per pick cycle
    public float totalWeight;    // sum of option weights + idle weight
    public float attack;         // ease-in seconds
    public float release;        // ease-out seconds
    public int optionStart;      // first option index in the shared options buffer
    public int optionCount;      // options targeting this slot

    // Sequence — a playhead over a shared baked-sample buffer; rotation written absolutely.
    public int sampleStart;      // first frame index in the shared rotation-sample buffer
    public int frameCount;       // frames for this slot's transform
    public float frameRate;      // samples per second
    public int loop;             // 0 = one-shot, 1 = loop
    public float sequenceSpeed;  // playback rate multiplier (negative reverses)

    // Captured rest pose (local space); motion composes as a delta from this.
    public quaternion restRotation;
    public float3 restPosition;
    public float3 restScale;
}

/// <summary>
/// One weighted <c>RandomSelect</c> option. The slot's target eases to this rotation when a
/// cycle's deterministic roll lands in <c>[bandLo, bandHi)</c> of the movement's total weight;
/// the gap up to <c>totalWeight</c> is the idle band (pose nothing). Held in a buffer shared by
/// every slot so the per-transform job stays allocation-free.
/// </summary>
public struct AuthoredOptionData
{
    public float bandLo;
    public float bandHi;
    public float3 axis;
    public float angleDeg;
}

/// <summary>
/// Single compute-and-write pass for all avatars' authored movements. One movement touches only
/// a few transforms, so a single <see cref="IJobParallelForTransform"/> parallelises cleanly
/// across avatars (no compute/apply split like the 51-bone skeleton needs). Every kind is a delta
/// from the captured rest pose. All math is <c>Unity.Mathematics</c>, so the routine is Burst-legal.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Low)]
public struct AuthoredMotionJob : IJobParallelForTransform
{
    [ReadOnly] public NativeArray<AuthoredMovementData> Movements;
    [ReadOnly] public NativeArray<AuthoredOptionData> Options;
    [ReadOnly] public NativeArray<float4> RotationSamples;
    [ReadOnly] public NativeArray<byte> ValidMask;
    public float Time;

    public void Execute(int index, TransformAccess transform)
    {
        if (ValidMask[index] == 0) return;

        AuthoredMovementData m = Movements[index];
        float t = Time;

        switch ((Kind)m.kind)
        {
            case Kind.Oscillate:
            {
                float w = Wave(m.waveform, t * m.frequencyHz * 2f * math.PI + m.phase, m.pulseWidth);
                ApplyChannel(transform, m, m.amplitude * w);
                break;
            }
            case Kind.Noise:
            {
                float n = noise.snoise(new float2(t * m.noiseSpeed, m.seed * 0.7283f));
                ApplyChannel(transform, m, m.amplitude * n);
                break;
            }
            case Kind.Rotate:
            {
                float angle = math.radians((t * m.speedDeg) % 360f);
                transform.localRotation = math.mul(m.restRotation, quaternion.AxisAngle(math.normalizesafe(m.axis), angle));
                break;
            }
            case Kind.Orbit:
            {
                float theta = math.radians(t * m.orbitSpeedDeg);
                float3 n = math.normalizesafe(m.axis, new float3(0f, 1f, 0f));
                float3 u = math.cross(n, new float3(0f, 1f, 0f));
                if (math.lengthsq(u) < 1e-6f) u = math.cross(n, new float3(1f, 0f, 0f));
                u = math.normalizesafe(u);
                float3 v = math.cross(n, u);
                transform.localPosition = m.pivotLocal + m.radius * (math.cos(theta) * u + math.sin(theta) * v);
                break;
            }

            case Kind.RandomSelect:
            {
                if (m.optionCount == 0) break;
                float period = m.period > 1e-4f ? m.period : 1f;
                float cyclesF = t / period;
                int cycle = (int)math.floor(cyclesF);
                float intoCycle = (cyclesF - cycle) * period; // seconds since this cycle began

                quaternion now = PickDelta(m, cycle, out bool posedNow);
                quaternion prev = PickDelta(m, cycle - 1, out bool posedPrev);

                // Ease out (release) when this cycle picks rest after being posed; ease in (attack) otherwise.
                float ease = (!posedNow && posedPrev) ? m.release : m.attack;
                float blend = ease > 1e-4f ? math.saturate(intoCycle / ease) : 1f;
                transform.localRotation = math.mul(m.restRotation, math.slerp(prev, now, blend));
                break;
            }

            case Kind.Sequence:
            {
                if (m.frameCount <= 0) break;
                float fr = m.frameRate > 1e-4f ? m.frameRate : 30f;
                float length = m.frameCount / fr;
                float st = t * m.sequenceSpeed;
                float pt;
                if (m.loop != 0)
                {
                    pt = math.fmod(st, length);
                    if (pt < 0f) pt += length; // fmod can return negative for negative t
                }
                else
                {
                    // Reverse one-shot walks the playhead down from the end; forward holds at 0..length.
                    pt = math.clamp(m.sequenceSpeed < 0f ? length + st : st, 0f, length);
                }

                float frameF = pt * fr;
                int f0 = (int)math.floor(frameF);
                float frac = frameF - f0;
                int f1;
                if (m.loop != 0)
                {
                    f0 = ((f0 % m.frameCount) + m.frameCount) % m.frameCount;
                    f1 = (f0 + 1) % m.frameCount;
                }
                else
                {
                    f0 = math.clamp(f0, 0, m.frameCount - 1);
                    f1 = math.min(f0 + 1, m.frameCount - 1);
                }

                quaternion q0 = new quaternion(RotationSamples[m.sampleStart + f0]);
                quaternion q1 = new quaternion(RotationSamples[m.sampleStart + f1]);
                transform.localRotation = math.slerp(q0, q1, frac);
                break;
            }

            default:
                break;
        }
    }

    // Deterministic weighted pick for one cycle: this slot's winning rotation delta, or identity if idle / another target won.
    quaternion PickDelta(in AuthoredMovementData m, int cycle, out bool posed)
    {
        uint h = math.hash(new int2((int)m.seed, cycle)) | 1u;
        float r = new Unity.Mathematics.Random(h).NextFloat() * m.totalWeight;

        for (int o = 0; o < m.optionCount; o++)
        {
            AuthoredOptionData opt = Options[m.optionStart + o];
            if (r >= opt.bandLo && r < opt.bandHi)
            {
                posed = true;
                return quaternion.AxisAngle(math.normalizesafe(opt.axis, new float3(0f, 1f, 0f)), math.radians(opt.angleDeg));
            }
        }
        posed = false;
        return quaternion.identity;
    }

    static void ApplyChannel(TransformAccess transform, in AuthoredMovementData m, float value)
    {
        float3 axis = math.normalizesafe(m.axis, new float3(0f, 1f, 0f));
        switch ((Channel)m.channel)
        {
            case Channel.Rotation:
                transform.localRotation = math.mul(m.restRotation, quaternion.AxisAngle(axis, math.radians(value)));
                break;
            case Channel.Position:
                transform.localPosition = m.restPosition + axis * value;
                break;
            case Channel.Scale:
                transform.localScale = m.restScale + axis * value;
                break;
        }
    }

    // phase is in radians; pulseWidth is the square/pulse duty cycle (0–1).
    static float Wave(int waveform, float phase, float pulseWidth)
    {
        switch ((Waveform)waveform)
        {
            case Waveform.Sine: return math.sin(phase);
            case Waveform.Triangle: { float x = math.frac(phase / (2f * math.PI)); return 4f * math.abs(x - 0.5f) - 1f; }
            case Waveform.Square: { float x = math.frac(phase / (2f * math.PI)); return x < pulseWidth ? 1f : -1f; }
            case Waveform.Pulse: { float x = math.frac(phase / (2f * math.PI)); return x < pulseWidth ? 1f : 0f; }
            default: return math.sin(phase);
        }
    }
}

/// <summary>
/// Static orchestrator for authored-motion evaluation — a sibling to <c>RemoteBoneJobSystem</c>.
/// Holds the persistent SoA + <see cref="TransformAccessArray"/> for every registered avatar's
/// driven transforms and schedules one batched Burst pass per frame. Drives transforms outside the
/// networked humanoid skeleton, so there's no write contention with the bone pipeline.
///
/// <para>Registration is calibration-driven (no scene discovery): the local/remote avatar drivers
/// call <see cref="Register"/> for each <see cref="BasisAuthoredMotion"/> on the avatar at calibration;
/// recalibration re-registers (dedup drops the stale record) and a torn-down avatar's slots are
/// reclaimed by the next <see cref="Schedule"/> rebuild. Authoritative state lives in managed
/// <c>Registration</c> records (rest poses captured once at registration); a structural change
/// rebuilds the native containers from them.</para>
/// </summary>
public static class BasisAuthoredMotionSystem
{
    sealed class Registration
    {
        public BasisAuthoredMotion Component;
        public Transform[] Targets;          // one per slot
        public AuthoredMovementData[] Data;  // parallel to Targets
        public AuthoredOptionData[] Options; // RandomSelect options, rebased into sOptions on rebuild
        public float4[] RotationSamples;     // Sequence rotation frames, rebased into sRotationSamples on rebuild
        public bool[] MovementEnabled;       // per-slot author default (Movement.enabled)
        public bool ComponentEnabled;        // mirrors the component's runtime enabled state
        public int Offset;                   // current start index in the native containers
    }

    // Persistent SoA, parallel to sTargets.
    static NativeList<AuthoredMovementData> sMovements;
    static NativeList<AuthoredOptionData> sOptions;   // shared RandomSelect option bands
    static NativeList<float4> sRotationSamples;       // shared Sequence rotation frames (absolute, xyzw)
    static NativeList<byte> sValidMask;
    static TransformAccessArray sTargets;

    static readonly List<Registration> sRegistrations = new List<Registration>();
    static readonly Dictionary<BasisAuthoredMotion, Registration> sLookup = new Dictionary<BasisAuthoredMotion, Registration>();

    static JobHandle sPending;
    static bool sInitialized;

    public static int SlotCount => sInitialized ? sMovements.Length : 0;

    public static void Initialize(int initialCapacity = 0)
    {
        if (sInitialized) return;
        sMovements = new NativeList<AuthoredMovementData>(initialCapacity, Allocator.Persistent);
        sOptions = new NativeList<AuthoredOptionData>(0, Allocator.Persistent);
        sRotationSamples = new NativeList<float4>(0, Allocator.Persistent);
        sValidMask = new NativeList<byte>(initialCapacity, Allocator.Persistent);
        sTargets = new TransformAccessArray(math.max(1, initialCapacity));
        sRegistrations.Clear();
        sLookup.Clear();
        sInitialized = true;
    }

    public static void Dispose()
    {
        if (!sInitialized) return;
        CompletePending();
        if (sMovements.IsCreated) sMovements.Dispose();
        if (sOptions.IsCreated) sOptions.Dispose();
        if (sRotationSamples.IsCreated) sRotationSamples.Dispose();
        if (sValidMask.IsCreated) sValidMask.Dispose();
        if (sTargets.isCreated) sTargets.Dispose();
        sRegistrations.Clear();
        sLookup.Clear();
        sInitialized = false;
    }

    /// <summary>
    /// Registers every movement on <paramref name="component"/>, capturing rest poses from the
    /// avatar's current (calibration TPose) state. Re-registering an already-known component
    /// refreshes it. Safe to call with a null/empty component.
    /// </summary>
    public static void Register(BasisAuthoredMotion component)
    {
        if (component == null) return;
        if (!sInitialized) Initialize();
        if (sLookup.ContainsKey(component)) Unregister(component);

        Registration reg = Build(component);
        if (reg == null || reg.Data.Length == 0) return;

        sRegistrations.Add(reg);
        sLookup[component] = reg;
        component.EnabledStateChanged += OnEnabledStateChanged;
        Rebuild();
    }

    public static void Unregister(BasisAuthoredMotion component)
    {
        if (!sInitialized || component == null) return;
        if (!sLookup.TryGetValue(component, out Registration reg)) return;
        component.EnabledStateChanged -= OnEnabledStateChanged;
        sRegistrations.Remove(reg);
        sLookup.Remove(component);
        Rebuild();
    }

    /// <summary>
    /// Schedules the single compute-and-write pass for all registered movements. Call once per
    /// frame, before the jiggle updater's LateUpdate samples the bones (so authored motion is the
    /// animated base and jiggle layers on top). Returns the handle for dependency chaining;
    /// the caller (or the next <see cref="Schedule"/>) completes it via <see cref="Complete"/>.
    /// </summary>
    public static JobHandle Schedule()
    {
        if (!sInitialized || sMovements.Length == 0) return default;

        // Complete the previous frame's writes before rescheduling over the same containers.
        CompletePending();

        // A destroyed transform auto-drops from the TransformAccessArray, desyncing the parallel SoA — rebuild to resync.
        if (sTargets.length != sMovements.Length)
        {
            Rebuild();
            if (sMovements.Length == 0) return default;
        }

        // TODO: a shared/networked clock for bit-identical remote copies; Time.timeAsDouble is fine for local validation.
        float time = (float)Time.timeAsDouble;

        sPending = new AuthoredMotionJob
        {
            Movements = sMovements.AsDeferredJobArray(),
            Options = sOptions.AsDeferredJobArray(),
            RotationSamples = sRotationSamples.AsDeferredJobArray(),
            ValidMask = sValidMask.AsDeferredJobArray(),
            Time = time,
        }.Schedule(sTargets);

        return sPending;
    }

    public static void Complete(JobHandle handle)
    {
        handle.Complete();
        CompletePending();
    }

    static void CompletePending()
    {
        sPending.Complete();
        sPending = default;
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    static Registration Build(BasisAuthoredMotion component)
    {
        var data = new List<AuthoredMovementData>();
        var targets = new List<Transform>();
        var movementEnabled = new List<bool>();
        var options = new List<AuthoredOptionData>();
        var rotSamples = new List<float4>();

        Movement[] movements = component.movements;
        for (int i = 0; i < movements.Length; i++)
        {
            Movement mv = movements[i];
            uint seed = mv.seed != 0 ? mv.seed : (uint)(i + 1);

            switch (mv.kind)
            {
                case Kind.Oscillate:
                case Kind.Noise:
                    // Chain kinds: one slot per element, element phase/falloff baked in.
                    if (mv.chain != null)
                    {
                        for (int n = 0; n < mv.chain.Length; n++)
                        {
                            Transform tf = mv.chain[n];
                            if (tf == null) continue;
                            AuthoredMovementData d = Base(mv, seed, tf);
                            d.phase = mv.phase + n * mv.chainPhaseStep;
                            d.amplitude = mv.amplitude * Mathf.Pow(mv.chainFalloff, n);
                            AddSlot(data, targets, movementEnabled, d, tf, mv.enabled);
                        }
                    }
                    break;

                case Kind.Rotate:
                    if (mv.target != null)
                        AddSlot(data, targets, movementEnabled, Base(mv, seed, mv.target), mv.target, mv.enabled);
                    break;

                case Kind.Orbit:
                    if (mv.target != null)
                    {
                        AuthoredMovementData d = Base(mv, seed, mv.target);
                        Vector3 pivotWorld = mv.pivot != null ? mv.pivot.position : mv.target.position;
                        d.pivotLocal = mv.target.parent != null
                            ? (float3)mv.target.parent.InverseTransformPoint(pivotWorld)
                            : (float3)pivotWorld;
                        AddSlot(data, targets, movementEnabled, d, mv.target, mv.enabled);
                    }
                    break;

                case Kind.RandomSelect:
                {
                    if (mv.options == null || mv.options.Length == 0) break;

                    // Cumulative weight bands in declaration order; the idle weight takes the remainder.
                    float running = 0f;
                    var resolved = new List<(Transform tf, float3 axis, float angle, float lo, float hi)>();
                    for (int o = 0; o < mv.options.Length; o++)
                    {
                        var opt = mv.options[o];
                        float w = Mathf.Max(0f, opt.weight);
                        Transform tf = opt.target != null ? opt.target : mv.selectTarget;
                        if (tf == null || w <= 0f) continue;
                        float lo = running; running += w;
                        resolved.Add((tf, opt.axis, opt.angleDeg, lo, running));
                    }
                    float total = running + Mathf.Max(0f, mv.idleWeight);
                    if (resolved.Count == 0 || total <= 0f) break;

                    // One slot per distinct target; lay its options out contiguously in the buffer.
                    var done = new HashSet<Transform>();
                    for (int o = 0; o < resolved.Count; o++)
                    {
                        Transform tf = resolved[o].tf;
                        if (!done.Add(tf)) continue;

                        int start = options.Count;
                        int count = 0;
                        for (int p = 0; p < resolved.Count; p++)
                        {
                            if (resolved[p].tf != tf) continue;
                            options.Add(new AuthoredOptionData
                            {
                                bandLo = resolved[p].lo,
                                bandHi = resolved[p].hi,
                                axis = resolved[p].axis,
                                angleDeg = resolved[p].angle,
                            });
                            count++;
                        }

                        AuthoredMovementData d = Base(mv, seed, tf);
                        d.period = Mathf.Max(1e-4f, mv.intervalRange.x);
                        d.totalWeight = total;
                        d.attack = mv.attack;
                        d.release = mv.release;
                        d.optionStart = start;
                        d.optionCount = count;
                        AddSlot(data, targets, movementEnabled, d, tf, mv.enabled);
                    }
                    break;
                }

                case Kind.Sequence:
                {
                    BasisMotionClip clip = mv.bakedClip;
                    if (clip == null || clip.transformCount <= 0 || clip.frameCount <= 0 || clip.rotationSamples == null)
                        break;

                    Transform root = mv.sequenceRoot != null ? mv.sequenceRoot : component.transform;
                    int fc = clip.frameCount;
                    for (int ti = 0; ti < clip.transformCount; ti++)
                    {
                        string path = (clip.paths != null && ti < clip.paths.Length) ? clip.paths[ti] : null;
                        Transform tf = ResolvePath(root, path);
                        if (tf == null) continue;

                        int start = rotSamples.Count;
                        for (int f = 0; f < fc; f++)
                        {
                            Vector4 q = clip.rotationSamples[ti * fc + f];
                            rotSamples.Add(new float4(q.x, q.y, q.z, q.w));
                        }

                        AuthoredMovementData d = Base(mv, seed, tf);
                        d.sampleStart = start;
                        d.frameCount = fc;
                        d.frameRate = clip.frameRate;
                        d.loop = mv.loop ? 1 : 0;
                        d.sequenceSpeed = mv.sequenceSpeed;
                        AddSlot(data, targets, movementEnabled, d, tf, mv.enabled);
                    }
                    break;
                }
            }
        }

        if (data.Count == 0) return null;

        return new Registration
        {
            Component = component,
            Targets = targets.ToArray(),
            Data = data.ToArray(),
            Options = options.ToArray(),
            RotationSamples = rotSamples.ToArray(),
            MovementEnabled = movementEnabled.ToArray(),
            ComponentEnabled = component.isActiveAndEnabled,
            Offset = 0,
        };
    }

    // Builds the common fields and captures the rest pose from the driven transform.
    static AuthoredMovementData Base(Movement mv, uint seed, Transform tf)
    {
        tf.GetLocalPositionAndRotation(out Vector3 restPos, out Quaternion restRot);
        return new AuthoredMovementData
        {
            kind = (int)mv.kind,
            channel = (int)mv.channel,
            waveform = (int)mv.waveform,
            axis = mv.axis,
            amplitude = mv.amplitude,
            frequencyHz = mv.frequencyHz,
            phase = mv.phase,
            pulseWidth = mv.pulseWidth,
            speedDeg = mv.speedDeg,
            radius = mv.radius,
            orbitSpeedDeg = mv.orbitSpeedDeg,
            noiseSpeed = mv.noiseSpeed,
            seed = seed,
            restRotation = restRot,
            restPosition = restPos,
            restScale = tf.localScale,
        };
    }

    static void AddSlot(List<AuthoredMovementData> data, List<Transform> targets, List<bool> enabledList,
        AuthoredMovementData d, Transform tf, bool movementEnabled)
    {
        data.Add(d);
        targets.Add(tf);
        enabledList.Add(movementEnabled);
    }

    // Resolves a baked-clip path under root (empty = root itself), matching how an AnimationClip binds curves.
    static Transform ResolvePath(Transform root, string path)
    {
        if (root == null) return null;
        return string.IsNullOrEmpty(path) ? root : root.Find(path);
    }

    // Rebuilds the native containers from the managed registrations on a structural change — never per-frame.
    static void Rebuild()
    {
        CompletePending();

        // Prune registrations whose component was destroyed without an explicit Unregister (Unity fake-null).
        for (int i = sRegistrations.Count - 1; i >= 0; i--)
        {
            if (sRegistrations[i].Component == null)
            {
                sLookup.Remove(sRegistrations[i].Component);
                sRegistrations.RemoveAt(i);
            }
        }

        int total = 0;
        for (int i = 0; i < sRegistrations.Count; i++) total += sRegistrations[i].Data.Length;

        sMovements.Clear();
        sOptions.Clear();
        sRotationSamples.Clear();
        sValidMask.Clear();
        if (sTargets.isCreated) sTargets.Dispose();
        sTargets = new TransformAccessArray(math.max(1, total));

        for (int r = 0; r < sRegistrations.Count; r++)
        {
            Registration reg = sRegistrations[r];
            reg.Offset = sMovements.Length;

            // Concatenate this registration's side buffers; slots reference them via a rebased offset.
            int optionBase = sOptions.Length;
            if (reg.Options != null)
                for (int o = 0; o < reg.Options.Length; o++) sOptions.Add(reg.Options[o]);

            int sampleBase = sRotationSamples.Length;
            if (reg.RotationSamples != null)
                for (int s = 0; s < reg.RotationSamples.Length; s++) sRotationSamples.Add(reg.RotationSamples[s]);

            for (int i = 0; i < reg.Data.Length; i++)
            {
                Transform tf = reg.Targets[i];
                if (tf == null) continue; // UGC: a target may have been destroyed
                AuthoredMovementData d = reg.Data[i];
                if ((Kind)d.kind == Kind.RandomSelect) d.optionStart += optionBase;
                else if ((Kind)d.kind == Kind.Sequence) d.sampleStart += sampleBase;
                sMovements.Add(d);
                sTargets.Add(tf);
                sValidMask.Add((byte)(reg.ComponentEnabled && reg.MovementEnabled[i] ? 1 : 0));
            }
        }
    }

    // Toggle-system-agnostic: any actuator flipping the component's enabled lands here; patch just its mask slice.
    static void OnEnabledStateChanged(BasisAuthoredMotion component, bool enabled)
    {
        if (!sLookup.TryGetValue(component, out Registration reg)) return;
        reg.ComponentEnabled = enabled;

        CompletePending(); // the job reads ValidMask
        for (int i = 0; i < reg.Data.Length; i++)
        {
            int slot = reg.Offset + i;
            if (slot < sValidMask.Length)
                sValidMask[slot] = (byte)(enabled && reg.MovementEnabled[i] ? 1 : 0);
        }
    }
}
