using System.Globalization;
using System.IO;
using System.Text;
using Basis.Scripts.Drivers;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.IK.Debugging
{
    // Temporal sweep of the runtime virtual-spine solve (BasisVirtualSpineCore.BasisVirtualSpineSolveJob).
    // The existing Spine / SpineBend / SpineClamp / SpineTwist sweeps test the STATIC helpers (chain placement,
    // hips offset, yaw extraction) on independent stateless inputs. They cannot see the per-frame dynamics the
    // job carries in SpineSolveState: the torso yaw DEADZONE (hold the heading until the head leaves a cone, ease
    // a 0..1 follow weight in to catch up, then re-center the anchor once the head stops) and the head-XZ
    // counterbalance low-pass. This drives the real job frame-by-frame over a synthetic 5-bone chain and watches
    // for the ways those dynamics go wrong: a yaw SNAP as the deadzone relocks, per-frame STEPPING on smooth head
    // motion, the hips driven ABOVE the head (the torso flip), or the torso failing to CATCH UP after the head
    // stops. Two scenarios: a yaw ramp (slow->fast->hold) that must break the deadzone and relock cleanly, and a
    // smooth yaw sinusoid that must stay step-free.
    //
    // THREAD-SAFETY: MAIN-THREAD-ONLY. It allocates NativeArrays and ticks BasisVirtualSpineSolveJob (via
    // Execute(), the managed path -- no Schedule), so it runs serially on the main thread like BasisFootIKSweep,
    // not in the worker fan-out.
    public struct BasisVirtualSpineTemporalSweepConfig
    {
        public float Fps;
        public float Seconds;
        public float RampYawDeg;             // peak head yaw the ramp scenario sweeps to (must exceed the deadzone)
        public float SineYawDeg;             // amplitude of the smooth yaw sinusoid scenario
        public float SineHz;                 // frequency of the yaw sinusoid

        // Solver tunables (mirror the BasisSettingsDefaults.VSpine* / driver-field values the live solve reads).
        public float TorsoYawDeadzoneDeg;
        public float TorsoYawBlendSpeed;
        public float ChestPitchFrac;
        public float ChestRollFrac;
        public float SpinePitchFrac;
        public float SpineRollFrac;
        public float NeckRotationSpeed;
        public float ChestRotationSpeed;
        public float SpineRotationSpeed;
        public float HipsRotationSpeed;
        public float HipsForwardBias;

        public static BasisVirtualSpineTemporalSweepConfig Default()
        {
            return new BasisVirtualSpineTemporalSweepConfig
            {
                Fps = 90f,
                Seconds = 6f,
                RampYawDeg = 120f,
                SineYawDeg = 50f,
                SineHz = 0.3f,
                TorsoYawDeadzoneDeg = 20f,
                TorsoYawBlendSpeed = 6f,
                ChestPitchFrac = 0.3f,
                ChestRollFrac = 0.3f,
                SpinePitchFrac = 0.2f,
                SpineRollFrac = 0.2f,
                NeckRotationSpeed = 40f,
                ChestRotationSpeed = 25f,
                SpineRotationSpeed = 30f,
                HipsRotationSpeed = 20f,
                HipsForwardBias = 0.02f,
            };
        }
    }

    public struct BasisVirtualSpineTemporalSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Steps;
        public int NaNCount;
        public float MaxOutputPopDeg;     // worst single-frame torso (hips) yaw jump -- a deadzone relock SNAP
        public float MaxHipsYawStepDeg;   // worst hips-yaw 2nd-difference on the smooth sinusoid (stepping)
        public float MaxChestYawStepDeg;  // worst chest-yaw 2nd-difference on the smooth sinusoid (stepping)
        public float MaxHipsAboveHeadM;   // worst (hips.y - head.y); >0 = hips driven above the head (flip)
        public float FinalCatchupErrDeg;  // |headYaw - hipsYaw| after the ramp head stops (relock must complete)
        public float MaxTorsoFollow;      // peak follow weight reached in the ramp (repro guard: must engage)
        public string Error;
    }

    public static class BasisVirtualSpineTemporalSweep
    {
        public const string DefaultFileName = "BasisVirtualSpineTemporalSweep.csv";

        public static string DefaultPath() => System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);

        // Synthetic chain geometry (local-space bone heights). Indices below; lengths/fractions derive from these.
        const float HeadY = 1.6f, NeckY = 1.5f, ChestY = 1.3f, SpineY = 1.1f, HipsY0 = 1.0f;
        const int IdxHead = 0, IdxNeck = 1, IdxChest = 2, IdxSpine = 3, IdxHips = 4;
        const float LenTotal = 0.5f;  // (NeckY-ChestY)+(ChestY-SpineY)+(SpineY-HipsY0)
        const float TChest = 0.4f;    // (NeckY-ChestY)/LenTotal
        const float TSpine = 0.8f;    // (NeckY-ChestY + ChestY-SpineY)/LenTotal

        public static BasisVirtualSpineTemporalSweepSummary Run(BasisVirtualSpineTemporalSweepConfig cfg, string path)
        {
            var summary = new BasisVirtualSpineTemporalSweepSummary { Ok = false, Path = path };
            float fps = Mathf.Max(5f, cfg.Fps);
            float dt = 1f / fps;
            int n = Mathf.Max(16, Mathf.RoundToInt(cfg.Seconds * fps));

            int nan = 0;
            float maxPop = 0f, maxHipsStep = 0f, maxChestStep = 0f, maxAbove = 0f, finalCatchup = 0f, maxFollow = 0f;

            NativeArray<BasisBoneSimState> states = default;
            NativeArray<BasisVirtualSpineCore.SpineSolveState> state = default;

            try
            {
                string d = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);

                states = new NativeArray<BasisBoneSimState>(5, Allocator.Persistent);
                state = new NativeArray<BasisVirtualSpineCore.SpineSolveState>(1, Allocator.Persistent);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisVirtualSpineTemporalSweep " + System.DateTime.UtcNow.ToString("o") +
                                " fps=" + F(fps) + " deadzone=" + F(cfg.TorsoYawDeadzoneDeg) + " blendSpeed=" + F(cfg.TorsoYawBlendSpeed) +
                                " ramp=" + F(cfg.RampYawDeg) + " sine=" + F(cfg.SineYawDeg) + "@" + F(cfg.SineHz) + "Hz");
                    w.WriteLine("scenario,step,headYaw,hipsYaw,chestYaw,follow,hipsAboveHead");
                    var sb = new StringBuilder(96);

                    // Scenario 1 -- yaw ramp: head eases 0 -> RampYawDeg over the first 60% (slow->fast->slow via
                    // smoothstep), then HOLDS. The hold must let the deadzone relock so the torso catches the head.
                    {
                        Seed(states, state);
                        int rampN = Mathf.Max(2, Mathf.RoundToInt(n * 0.6f));
                        float prevHips = 0f, prevRawHips = 0f; bool have = false;
                        float lastHeadYaw = 0f, lastHipsYaw = 0f;
                        for (int i = 0; i < n; i++)
                        {
                            float p = Mathf.Clamp01((float)i / rampN);
                            float headYaw = cfg.RampYawDeg * (p * p * (3f - 2f * p)); // smoothstep
                            var o = Step(states, state, headYaw, dt, cfg);
                            if (!o.Finite) { nan++; break; }

                            float above = o.HipsY - o.HeadY;
                            if (above > maxAbove) maxAbove = above;
                            if (o.Follow > maxFollow) maxFollow = o.Follow;

                            // pop = single-frame hips-yaw jump (raw delta, wrap-safe)
                            if (have)
                            {
                                float pop = Mathf.Abs(DeltaAngle(prevRawHips, o.HipsYaw));
                                if (pop > maxPop) maxPop = pop;
                            }
                            prevRawHips = o.HipsYaw; have = true; prevHips = o.HipsYaw;
                            lastHeadYaw = headYaw; lastHipsYaw = o.HipsYaw;
                            WriteRow(w, sb, "ramp", i, headYaw, o.HipsYaw, o.ChestYaw, o.Follow, above);
                        }
                        finalCatchup = Mathf.Abs(DeltaAngle(lastHipsYaw, lastHeadYaw));
                    }

                    // Scenario 2 -- smooth yaw sinusoid: the torso slew must track it without per-frame stepping.
                    // Amplitude exceeds the deadzone so the follow engages and reverses each half-cycle.
                    {
                        Seed(states, state);
                        float prevHips = 0f, prevPrevHips = 0f, prevChest = 0f, prevPrevChest = 0f, prevRawHips = 0f;
                        float unwrapHips = 0f, unwrapChest = 0f, prevRawChest = 0f;
                        for (int i = 0; i < n; i++)
                        {
                            float headYaw = cfg.SineYawDeg * Mathf.Sin(2f * Mathf.PI * cfg.SineHz * (i * dt));
                            var o = Step(states, state, headYaw, dt, cfg);
                            if (!o.Finite) { nan++; break; }

                            float above = o.HipsY - o.HeadY;
                            if (above > maxAbove) maxAbove = above;

                            // unwrap output yaws so the 2nd-difference (stepping) is wrap-free
                            if (i == 0) { unwrapHips = o.HipsYaw; unwrapChest = o.ChestYaw; }
                            else { unwrapHips += DeltaAngle(prevRawHips, o.HipsYaw); unwrapChest += DeltaAngle(prevRawChest, o.ChestYaw); }
                            prevRawHips = o.HipsYaw; prevRawChest = o.ChestYaw;

                            if (i >= 2)
                            {
                                float hStep = Mathf.Abs(unwrapHips - 2f * prevHips + prevPrevHips);
                                float cStep = Mathf.Abs(unwrapChest - 2f * prevChest + prevPrevChest);
                                if (hStep > maxHipsStep) maxHipsStep = hStep;
                                if (cStep > maxChestStep) maxChestStep = cStep;
                            }
                            prevPrevHips = prevHips; prevHips = unwrapHips;
                            prevPrevChest = prevChest; prevChest = unwrapChest;
                            WriteRow(w, sb, "sine", i, headYaw, o.HipsYaw, o.ChestYaw, o.Follow, above);
                        }
                    }
                }

                summary.Ok = true;
                summary.Steps = n;
                summary.NaNCount = nan;
                summary.MaxOutputPopDeg = maxPop;
                summary.MaxHipsYawStepDeg = maxHipsStep;
                summary.MaxChestYawStepDeg = maxChestStep;
                summary.MaxHipsAboveHeadM = maxAbove;
                summary.FinalCatchupErrDeg = finalCatchup;
                summary.MaxTorsoFollow = maxFollow;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }
            finally
            {
                if (states.IsCreated) states.Dispose();
                if (state.IsCreated) state.Dispose();
            }

            return summary;
        }

        struct StepOut { public float HipsYaw, ChestYaw, HipsY, HeadY, Follow; public bool Finite; }

        // Builds the per-frame params for a pure-yaw head pose, ticks the real solve job once, reads back the
        // synthesized torso yaws + hips/head Y + follow weight.
        static StepOut Step(NativeArray<BasisBoneSimState> states, NativeArray<BasisVirtualSpineCore.SpineSolveState> state, float headYawDeg, float dt, in BasisVirtualSpineTemporalSweepConfig cfg)
        {
            quaternion yaw = quaternion.AxisAngle(new float3(0f, 1f, 0f), math.radians(headYawDeg));

            var p = new BasisVirtualSpineCore.SpineSolveParams
            {
                Dt = dt,
                Scale = 1f,
                ParentMatrix = float4x4.identity,
                ParentRotation = quaternion.identity,
                EyeRot = yaw,

                HeadTargetPos = new float3(0f, HeadY, 0f),
                HeadTargetRot = yaw,
                NeckTargetPos = new float3(0f, NeckY, 0f),
                NeckTargetRot = yaw,
                ChestTargetPos = new float3(0f, ChestY, 0f),
                ChestTargetRot = yaw,
                SpineTargetPos = new float3(0f, SpineY, 0f),
                SpineTargetRot = yaw,

                HeadScaledOffset = float3.zero,
                NeckScaledOffset = float3.zero,
                ChestScaledOffset = float3.zero,
                SpineScaledOffset = float3.zero,

                ChestTposeY = ChestY,
                SpineTposeY = SpineY,
                TposeHips = new float3(0f, HipsY0, 0f),

                LeftFootPos = float3.zero,
                RightFootPos = float3.zero,
                LeftFootTracked = 0,
                RightFootTracked = 0,

                ChestPitchFrac = cfg.ChestPitchFrac,
                ChestRollFrac = cfg.ChestRollFrac,
                SpinePitchFrac = cfg.SpinePitchFrac,
                SpineRollFrac = cfg.SpineRollFrac,
                NeckRotationSpeed = cfg.NeckRotationSpeed,
                ChestRotationSpeed = cfg.ChestRotationSpeed,
                SpineRotationSpeed = cfg.SpineRotationSpeed,
                HipsRotationSpeed = cfg.HipsRotationSpeed,
                HipsForwardBias = cfg.HipsForwardBias,
                TorsoYawDeadzoneDeg = cfg.TorsoYawDeadzoneDeg,
                TorsoYawBlendSpeed = cfg.TorsoYawBlendSpeed,

                HipsFreeze = 0,
                IsLocomoting = 0,

                LenTotal = LenTotal,
                TChest = TChest,
                TSpine = TSpine,
            };

            new BasisVirtualSpineCore.BasisVirtualSpineSolveJob
            {
                States = states,
                State = state,
                P = p,
                IdxHead = IdxHead,
                IdxNeck = IdxNeck,
                IdxChest = IdxChest,
                IdxSpine = IdxSpine,
                IdxHips = IdxHips,
            }.Execute();

            BasisBoneSimState hips = states[IdxHips];
            BasisBoneSimState chest = states[IdxChest];
            BasisBoneSimState head = states[IdxHead];
            var o = new StepOut
            {
                HipsYaw = YawOf(hips.OutgoingRotation),
                ChestYaw = YawOf(chest.OutgoingRotation),
                HipsY = hips.OutgoingPosition.y,
                HeadY = head.OutgoingPosition.y,
                Follow = state[0].TorsoFollow,
            };
            o.Finite = Finite(hips.OutgoingRotation) && Finite(chest.OutgoingRotation) &&
                       Finite(hips.OutgoingPosition) && Finite(head.OutgoingPosition) && Finite(o.Follow);
            return o;
        }

        static void Seed(NativeArray<BasisBoneSimState> states, NativeArray<BasisVirtualSpineCore.SpineSolveState> state)
        {
            SeedBone(states, IdxHead, HeadY);
            SeedBone(states, IdxNeck, NeckY);
            SeedBone(states, IdxChest, ChestY);
            SeedBone(states, IdxSpine, SpineY);
            SeedBone(states, IdxHips, HipsY0);
            state[0] = default;
        }

        static void SeedBone(NativeArray<BasisBoneSimState> states, int idx, float y)
        {
            float3 pos = new float3(0f, y, 0f);
            states[idx] = new BasisBoneSimState
            {
                OutgoingPosition = pos,
                OutgoingRotation = quaternion.identity,
                LastRunPosition = pos,
                LastRunRotation = quaternion.identity,
                OutgoingWorldPosition = pos,
                OutgoingWorldRotation = quaternion.identity,
            };
        }

        static float YawOf(quaternion q)
        {
            float3 f = math.mul(q, new float3(0f, 0f, 1f));
            return math.degrees(math.atan2(f.x, f.z));
        }

        static float DeltaAngle(float a, float b)
        {
            float d = Mathf.Repeat(b - a + 180f, 360f) - 180f;
            return d;
        }

        static void WriteRow(StreamWriter w, StringBuilder sb, string scenario, int step, float headYaw, float hipsYaw, float chestYaw, float follow, float aboveHead)
        {
            sb.Clear();
            sb.Append(scenario).Append(',').Append(step).Append(',');
            sb.Append(F(headYaw)).Append(',').Append(F(hipsYaw)).Append(',').Append(F(chestYaw)).Append(',');
            sb.Append(F(follow)).Append(',').Append(F(aboveHead));
            w.WriteLine(sb.ToString());
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(float3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        static bool Finite(quaternion q) => Finite(q.value.x) && Finite(q.value.y) && Finite(q.value.z) && Finite(q.value.w);

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
