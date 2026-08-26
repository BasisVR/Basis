using Basis.IK.Motion;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
namespace Basis.Tests.IK
{
    public sealed class BasisBoneChainLagTests
    {
        // The real left-leg chain, as wired by BasisLocalAvatarDriver.SetAndCreateLock.
        const int Hips = 0, UpperLeg = 1, LowerLeg = 2, Foot = 3, Toes = 4;
        const int BoneCount = 5;
        static float3 OffsetOf(int bone) => bone switch
        {
            UpperLeg => new float3(0.09f, -0.06f, 0f),
            LowerLeg => new float3(0f, -0.42f, 0f),
            Foot => new float3(0f, -0.42f, 0f),
            Toes => new float3(0f, -0.08f, 0.12f),
            _ => float3.zero,
        };
        static float3[] RunChain(float dt, int frames, System.Func<int, (float3 pos, quaternion rot)> hipsAt, bool footHasTracker = false)
        {
            var chain = new NativeArray<int>(new[] { UpperLeg, LowerLeg, Foot, Toes }, Allocator.Temp);
            var inputs = new NativeArray<BasisBoneSimInput>(BoneCount, Allocator.Temp);
            var states = new NativeArray<BasisBoneSimState>(BoneCount, Allocator.Temp);
            try
            {
                for (int b = UpperLeg; b <= Toes; b++)
                {
                    inputs[b] = new BasisBoneSimInput
                    {
                        TargetIndex = b - 1,
                        HasTarget = 1,
                        HasTracker = (byte)(footHasTracker && b == Foot ? 1 : 0),
                        HasVirtualOverride = 0,
                        UseInverseOffset = 0,
                        ScaledOffset = OffsetOf(b),
                        IncomingPosition = float3.zero,
                        IncomingRotation = quaternion.identity,
                        InverseOffsetPosition = float3.zero,
                        InverseOffsetRotation = quaternion.identity,
                    };
                }

                (float3 p0, quaternion r0) = hipsAt(0);
                SeedRest(states, p0, r0);

                var job = new BasisBoneSimChainJob
                {
                    ChainIndices = chain,
                    Inputs = inputs,
                    States = states,
                    ParentMatrix = float4x4.identity,
                    ParentRotation = quaternion.identity,
                    DeltaTime = dt,
                    InstantSnap = 0,
                };

                var footTrack = new float3[frames];
                for (int i = 0; i < frames; i++)
                {
                    (float3 hp, quaternion hr) = hipsAt(i);

                    BasisBoneSimState h = states[Hips];
                    h.OutgoingPosition = hp;
                    h.OutgoingRotation = hr;
                    h.LastRunPosition = hp;
                    h.LastRunRotation = hr;
                    states[Hips] = h;

                    if (footHasTracker)
                    {
                        BasisBoneSimInput fi = inputs[Foot];
                        fi.IncomingPosition = RigidPose(hp, hr, Foot);
                        fi.IncomingRotation = hr;
                        inputs[Foot] = fi;
                        job.Inputs = inputs;
                    }

                    job.States = states;
                    job.Execute();

                    footTrack[i] = states[Foot].OutgoingWorldPosition;
                }
                return footTrack;
            }
            finally
            {
                chain.Dispose();
                inputs.Dispose();
                states.Dispose();
            }
        }
        static float3 RigidPose(float3 hipsPos, quaternion hipsRot, int upTo)
        {
            float3 p = hipsPos;
            for (int b = UpperLeg; b <= upTo; b++) p += math.mul(hipsRot, OffsetOf(b));
            return p;
        }
        static void SeedRest(NativeArray<BasisBoneSimState> states, float3 hipsPos, quaternion hipsRot)
        {
            for (int b = Hips; b <= Toes; b++)
            {
                float3 p = RigidPose(hipsPos, hipsRot, b);
                states[b] = new BasisBoneSimState
                {
                    OutgoingPosition = p,
                    OutgoingRotation = hipsRot,
                    LastRunPosition = p,
                    LastRunRotation = hipsRot,
                    OutgoingWorldPosition = p,
                    OutgoingWorldRotation = hipsRot,
                };
            }
        }
        const float TurnSecs = 0.25f, HoldSecs = 1.0f, YawDeg = 60f;
        static readonly float3 k_Hips = new float3(0f, 0.95f, 0f);
        static System.Func<int, (float3, quaternion)> Turn(float dt) => i =>
        {
            float t = Mathf.Clamp01(i * dt / TurnSecs);
            return (k_Hips, quaternion.AxisAngle(math.up(), math.radians(YawDeg * t)));
        };
        static float SlideAfterStopMs(int fps)
        {
            float dt = 1f / fps;
            int frames = Mathf.RoundToInt((TurnSecs + HoldSecs) * fps), stopFrame = Mathf.RoundToInt(TurnSecs * fps);
            float3[] track = RunChain(dt, frames, Turn(dt));
            float3 final = RigidPose(k_Hips, quaternion.AxisAngle(math.up(), math.radians(YawDeg)), Foot);

            for (int i = track.Length - 1; i >= stopFrame; i--)
                if (math.distance(track[i], final) > 0.002f) return (i + 1 - stopFrame) * dt * 1000f;
            return 0f;
        }
        // ------------------------------------------------------------------ the gates
        [Test]
        public void TheLegChain_SettlesAtTheSameSpeed_OnEveryHeadset()
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (int fps in BasisMotionResponse.VrFrameRates)
            {
                float ms = SlideAfterStopMs(fps);
                lo = Mathf.Min(lo, ms);
                hi = Mathf.Max(hi, ms);
            }

            Assert.Less(hi - lo, 25f, $"a roleless leg settles in {lo:F0} ms on one headset and {hi:F0} ms on another. The bone chain's " + "smoothing time constant is a function of the framerate -- the same saturate(dt*speed) bug " + "BasisBlendSpeedTests pins in the virtual spine and the foot swing.");
        }
        [Test]
        public void TheLegacyForm_Fails_SoTheGateCannotRotIntoATautology()
        {
            // Scalarised cascade of the three untracked follows, with the OLD alpha. Reproduced ONLY so this
            // negative can bite -- never call this from shipping code, it is the bug.
            float SettleMs(int fps)
            {
                float dt = 1f / fps, alpha = Mathf.Clamp01(BasisLocalBoneControl.QuaternionLerp * dt);
                int frames = Mathf.RoundToInt((TurnSecs + HoldSecs) * fps);
                int stopFrame = Mathf.RoundToInt(TurnSecs * fps);
                float a = 0f, b = 0f, c = 0f;
                var y = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float drive = Mathf.Clamp01(i * dt / TurnSecs);
                    a += alpha * (drive - a);
                    b += alpha * (a - b);
                    c += alpha * (b - c);
                    y[i] = c;
                }
                for (int i = frames - 1; i >= stopFrame; i--)
                    if (Mathf.Abs(y[i] - 1f) > 0.02f) return (i + 1 - stopFrame) * dt * 1000f;
                return 0f;
            }

            float lo = float.MaxValue, hi = float.MinValue;
            foreach (int fps in new[] { 30, 72, 144, 240 })
            {
                float ms = SettleMs(fps);
                lo = Mathf.Min(lo, ms);
                hi = Mathf.Max(hi, ms);
            }

            Assert.Greater(hi - lo, 25f, $"the legacy clamp(rate*dt) cascade is supposed to settle at wildly different speeds across " + $"framerates ({lo:F0}..{hi:F0} ms); if it no longer does, the gate above is testing nothing");
        }
        [Test]
        public void TheLegChain_StillSmooths_AtLowFramerate_InsteadOfSnapping()
        {
            foreach (int fps in new[] { 20, 30, 40 })
            {
                float dt = 1f / fps, legacy = Mathf.Clamp01(BasisLocalBoneControl.PositionLerpAmount * dt);
                float now = BasisSmoothingProfiles.FramerateIndependentAlpha(BasisLocalBoneControl.PositionLerpAmount, dt);

                Assert.AreEqual(1f, legacy, 1e-6f, $"sanity: the old form should have been snapping at {fps} fps");
                Assert.Less(now, 0.995f, $"at {fps} fps the leg's position alpha is {now:F4} -- a blend that completes in one frame is " + "not a blend");
            }
        }
        [Test]
        public void TheFix_IsABitForBitNoOp_AtTheReferenceFramerate()
        {
            const float refDt = 1f / 90f;
            foreach (float rate in new[]
                     {
                         BasisLocalBoneControl.PositionLerpAmount,
                         BasisLocalBoneControl.QuaternionLerp,
                         BasisLocalBoneControl.QuaternionLerpFastMovement,
                     })
            {
                float legacy = Mathf.Clamp01(rate * refDt);
                float now = BasisSmoothingProfiles.FramerateIndependentAlpha(rate, refDt);
                Assert.AreEqual(legacy, now, 1e-4f, $"at the reference 90 Hz the fix must reproduce the old behaviour exactly, but rate={rate} " + $"gives {now:F5} where the old code gave {legacy:F5}");
            }
        }
        [Test]
        public void ATrackedFoot_IsDramaticallyMoreResponsive_ThanARolelessOne()
        {
            const int fps = 90;
            const float dt = 1f / fps;
            int frames = Mathf.RoundToInt((TurnSecs + HoldSecs) * fps), stopFrame = Mathf.RoundToInt(TurnSecs * fps);
            float3 final = RigidPose(k_Hips, quaternion.AxisAngle(math.up(), math.radians(YawDeg)), Foot);

            float BehindAtStop(bool tracked) => math.distance(RunChain(dt, frames, Turn(dt), tracked)[stopFrame], final);

            float tracked = BehindAtStop(true), roleless = BehindAtStop(false);

            Assert.Less(tracked, 0.005f, $"a TRACKED foot was {tracked * 100f:F2} cm behind when the body stopped -- it is supposed to be a " + "passthrough (trackersmooth saturates to 1)");
            Assert.Less(roleless, 0.030f, $"a roleless foot was {roleless * 100f:F2} cm behind when the body stopped. It is a smoothed " + "follower and some lag is expected, but this much reads as the leg sliding after the motion.");
        }
    }
}
