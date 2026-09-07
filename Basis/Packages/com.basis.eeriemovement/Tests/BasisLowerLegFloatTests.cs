using System.Text;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public sealed class BasisLowerLegFloatTests
    {
        const float Dt = 1f / 90f;
        const float Thigh = 0.42f, Shin = 0.42f;
        const float LegLen = Thigh + Shin;
        static readonly Vector3 Hips = new Vector3(0f, 0.95f, 0f), Chest = new Vector3(0f, 1.25f, 0f);
        static readonly Vector3 LeftHip = new Vector3(-0.09f, 0.90f, 0f), RightHip = new Vector3(0.09f, 0.90f, 0f);
        static readonly Quaternion HipsRot = Quaternion.identity;
        // Lock-step with the shipping constants.
        const float ButterflyRate = 8f;                                                    // BasisLocalRigDriver.ButterflyKneeSmoothRate
        const float TrackedMinCutoffHz = 1.5f, TrackedBeta = 0.20f, TrackedDerivHz = 1.0f; // k_TrackedKneeSwivel*
        const float MaxOpenDeg = 60f;                                                      // FBIKButterflyKneeMaxOpenDeg
        struct Legs
        {
            public Vector3 HintState;
            public float WeightState;
            public BasisSwivelFilterState Swivel;
            public bool Seeded;
        }
        struct Frame
        {
            public Vector3 KneeRigid;    // the solve's knee, BEFORE the output One-Euro
            public Vector3 KneeFinal;    // after it — what actually renders
            public float RawSwivelDeg, SmoothSwivelDeg, Conditioning, ButterflyWeight, ModelConfidence;
        }
        static Frame Step(Vector3 footPos, Quaternion footRot, ref Legs s, bool butterflyOn, bool swivelSmoothing, bool trustModelBlindly = false)
        {
            Frame f = default;

            // ---- BasisLocalRigDriver: butterfly branch (foot tracker, no knee tracker)
            float hintW = 0f;
            Vector3 hint = Vector3.zero;
            if (butterflyOn)
            {
                BasisButterflyKneeInput bi = default;
                bi.HipPosition = LeftHip;
                bi.FootPosition = footPos;
                bi.FootInstepDir = footRot * Vector3.up;
                bi.OutwardDir = -(HipsRot * Vector3.right);   // left leg
                bi.DefaultBendDir = HipsRot * Vector3.forward;
                bi.PlayerUp = Vector3.up;
                bi.TorsoFacingDir = HipsRot * Vector3.forward;
                bi.UpperLength = Thigh;
                bi.LowerLength = Shin;
                bi.MaxOpenDeg = MaxOpenDeg;
                bi.Strength = 1f;
                bi.SupineFloor = 1f;
                BasisButterflyKneeCore.Solve(bi, out BasisButterflyKneeResult br);

                float a = 1f - Mathf.Exp(-ButterflyRate * Dt);
                if (s.WeightState <= 0.0001f && br.HintWeight <= 0.0001f)
                {
                    s.HintState = br.KneeHint;
                    s.WeightState = 0f;
                }
                else
                {
                    s.HintState = Vector3.Lerp(s.HintState, br.KneeHint, a);
                    s.WeightState = Mathf.Lerp(s.WeightState, br.HintWeight, a);
                }
                if (s.WeightState > 0.001f) { hint = s.HintState; hintW = s.WeightState; }
            }
            f.ButterflyWeight = hintW;

            // ---- BasisFullBodyIK.SolveLegs: no hint => synthesize one from the fitted leg swivel model,
            //      and hand its confidence to the solve as POLE DISTRUST (never as a hintW fade).
            float hintDistrust = 0f;
            if (!(hintW > 0f))
            {
                BasisSwivelFrame frame = BasisSwivelHintCore.BuildFrame(LeftHip, RightHip, Hips, Chest);
                if (BasisSwivelHintCore.LegHint(frame, LeftHip, footPos, LegLen, true, out Vector3 modelHint, out float conf))
                {
                    hint = modelHint;
                    hintW = 1f;
                    hintDistrust = trustModelBlindly ? 0f : 1f - BasisSwivelHintCore.LegModelTrust(conf);
                    f.ModelConfidence = conf;
                }
            }

            // ---- the real two-bone leg solve. Mid is the ANIMATION's knee: the stream is re-posed by the
            // animator every frame, so the filter's output does NOT feed back into the solve input.
            BasisLegSolveInput li = default;
            li.Root = LeftHip;
            li.Mid = LeftHip + Vector3.down * Thigh + Vector3.forward * 0.03f;
            li.Tip = LeftHip + Vector3.down * LegLen;
            li.RootRotation = Quaternion.identity;
            li.MidRotation = Quaternion.identity;
            li.TargetPosition = footPos;
            li.TargetRotation = footRot;
            li.TargetOffset = Quaternion.identity;
            li.HintPosition = hint;
            li.HintWeight = hintW;
            li.HintDistrust = hintDistrust;
            li.BendNormal = HipsRot * Vector3.right;
            BasisLegSolveCore.Solve(li, out BasisLegSolveResult lr);

            f.KneeRigid = lr.KneeSolved;
            f.KneeFinal = lr.KneeSolved;

            // ---- BasisFullBodyIK.SmoothKneeSwivel (tracked cutoffs + pole conditioning)
            BasisSwivelSmootherInput si = default;
            si.Root = LeftHip;
            si.Mid = lr.KneeSolved;
            si.Tip = lr.FootSolved;
            si.BodyRotation = HipsRot;
            si.ReferenceLocal = Vector3.forward;
            si.FallbackLocal = Vector3.right;
            si.Dt = Dt;
            si.MinCutoffHz = TrackedMinCutoffHz;
            si.Beta = TrackedBeta;
            si.DerivCutoffHz = TrackedDerivHz;
            si.ConditionOnPole = true;
            si.SingularMinCutoffHz = BasisSwivelFilterCore.MinCutoffHz;
            si.GuardAnteriorHalfSpace = true;
            si.AnteriorSoftDeg = BasisLegSolveCore.KneeAnteriorSoftDeg;
            si.AnteriorHardDeg = BasisLegSolveCore.KneeAnteriorHardDeg;
            si.State = s.Swivel;
            si.Seeded = s.Seeded;

            BasisSwivelSmootherCore.Solve(si, out BasisSwivelSmootherResult sr);
            if (sr.WriteState) { s.Swivel = sr.State; s.Seeded = sr.Seeded; }

            f.RawSwivelDeg = sr.RawSwivelDeg;
            f.SmoothSwivelDeg = sr.SmoothSwivelDeg;
            f.Conditioning = sr.Conditioning;
            if (swivelSmoothing && sr.Valid) f.KneeFinal = sr.DesiredMid;

            return f;
        }
        static Frame[] Run(System.Func<int, (Vector3 pos, Quaternion rot)> footAt, int frames, bool butterflyOn, bool swivelSmoothing)
        {
            var s = new Legs();
            (Vector3 p0, Quaternion r0) = footAt(0);
            for (int i = 0; i < 120; i++) Step(p0, r0, ref s, butterflyOn, swivelSmoothing);   // settle at rest

            var track = new Frame[frames];
            for (int i = 0; i < frames; i++)
            {
                (Vector3 fp, Quaternion fr) = footAt(i);
                track[i] = Step(fp, fr, ref s, butterflyOn, swivelSmoothing);
            }
            return track;
        }
        // --------------------------------------------------------------- motions
        const float MoveSecs = 0.45f, HoldSecs = 1.5f;
        static int Frames => Mathf.RoundToInt((MoveSecs + HoldSecs) / Dt);
        static int StopFrame => Mathf.RoundToInt(MoveSecs / Dt);
        static float T(int i) => Mathf.Clamp01(i * Dt / MoveSecs);
        // A REACHABLE envelope. The hips do not move in this probe, so the foot must stay inside the leg:
        // sliding a foot 30 cm sideways from a standing 0.82 m drop puts it 0.87 m from the hip, past the
        // 0.84 m leg, and the solver simply pins the leg straight -- conditioning 0, swivel meaningless, and
        // the probe measures nothing. A real person's hips move; here the foot swings on an ARC instead, at a
        // constant leg length, which is the same thing from the leg's point of view.
        const float StandReach = 0.95f;                       // a standing leg carries a slight knee bend
        static float LegDist => StandReach * LegLen;          // 0.80 m hip->ankle
        static Vector3 FootAt(float outDeg, float fwdDeg) => LeftHip + (Quaternion.AngleAxis(outDeg, Vector3.forward) * Quaternion.AngleAxis(fwdDeg, Vector3.right) * Vector3.down) * LegDist;
        static (Vector3, Quaternion) SwingOut(int i) => (FootAt(22f * T(i), 0f), Quaternion.identity);
        static (Vector3, Quaternion) StepAbout(int i) => (FootAt(12f * T(i), 22f * T(i)), Quaternion.identity);
        static (Vector3, Quaternion) LiftKnee(int i)
        {
            float t = T(i);
            Vector3 rest = FootAt(0f, 0f);
            return (rest + (Vector3.up * 0.35f + Vector3.forward * 0.20f) * t, Quaternion.identity);
        }
        // --------------------------------------------------------------- the probe
        [Test]
        public void Abduction_DoesNotFlingTheKnee()
        {
            var s = new Legs();
            BasisSwivelFrame frame = BasisSwivelHintCore.BuildFrame(LeftHip, RightHip, Hips, Chest);
            float restFwd = 0f, worstFwdLoss = 0f, prevLat = float.NaN, worstReversal = 0f, minConf = 99f;
            for (int i = 0; i <= 90; i++)
            {
                Vector3 foot = FootAt(22f * (i / 90f), 0f);
                Frame f = Step(foot, Quaternion.identity, ref s, butterflyOn: true, swivelSmoothing: true);
                BasisSwivelHintCore.LegHint(frame, LeftHip, foot, LegLen, true, out _, out float conf);
                minConf = Mathf.Min(minConf, conf);

                Vector3 rel = f.KneeFinal - LeftHip;
                float fwd = Vector3.Dot(rel, Vector3.forward), lat = Vector3.Dot(rel, Vector3.left);
                if (i == 0) restFwd = fwd;
                worstFwdLoss = Mathf.Max(worstFwdLoss, restFwd - fwd);
                if (!float.IsNaN(prevLat)) worstReversal = Mathf.Max(worstReversal, prevLat - lat);
                prevLat = lat;
            }

            Assert.Less(minConf, BasisSwivelHintCore.LegTrustHi,
                "test wiring: this swing is supposed to walk the model OUT of its fitted domain "
                + $"(worst confidence {minConf:F2}); if it no longer does, the gate proves nothing");

            Assert.Less(worstFwdLoss, 0.03f, $"the knee lost {worstFwdLoss * 100f:F1} cm of its FORWARD offset during a plain leg abduction " + "-- it is being rotated out of the sagittal plane and flung backwards");

            Assert.Less(worstReversal, 0.01f, $"the knee REVERSED direction by {worstReversal * 100f:F1} cm while the foot kept swinging one way. " + "A knee that backs up while the foot goes out is the 'floating' the user sees.");
        }
        [Test]
        public void TrustingTheModelBlindly_Fails_SoTheGateCannotRotIntoATautology()
        {
            var s = new Legs();
            float restFwd = 0f, worstFwdLoss = 0f;
            for (int i = 0; i <= 90; i++)
            {
                Frame f = Step(FootAt(22f * (i / 90f), 0f), Quaternion.identity, ref s, butterflyOn: true, swivelSmoothing: true, trustModelBlindly: true);
                float fwd = Vector3.Dot(f.KneeFinal - LeftHip, Vector3.forward);
                if (i == 0) restFwd = fwd;
                worstFwdLoss = Mathf.Max(worstFwdLoss, restFwd - fwd);
            }

            Assert.Greater(worstFwdLoss, 0.05f, $"trusting BasisLegSwivelModel blindly is supposed to fling the knee (it lost {worstFwdLoss * 100f:F1} cm " + "of forward offset); if it no longer does, the gate above is testing nothing");
        }
        [Test]
        public void InsideTheFittedDomain_TheGateIsExactlyANoOp()
        {
            foreach (float conf in new[] { BasisSwivelHintCore.LegTrustHi, 0.75f, 0.85f, 0.95f, 1.0f, 1.4f })
            {
                Assert.AreEqual(1f, BasisSwivelHintCore.LegModelTrust(conf), 1e-6f, $"a confident model (|s,c| = {conf:F2}) must be trusted completely -- anything less silently " + "re-tunes the knee everywhere the model actually works");
            }
            Assert.AreEqual(0f, BasisSwivelHintCore.LegModelTrust(BasisSwivelHintCore.LegTrustLo), 1e-6f,"at the floor the model has no opinion and the solve must fall back to its own bend pole");

            // ...and it must be CONTINUOUS in between, or the pole kinks and the knee pops.
            float prev = 0f;
            for (float c = 0f; c <= 1.2f; c += 0.005f)
            {
                float t = BasisSwivelHintCore.LegModelTrust(c);
                Assert.LessOrEqual(t - prev, 0.02f, $"LegModelTrust jumps at |s,c| = {c:F3} -- a step in the trust is a pop in the knee");
                Assert.GreaterOrEqual(t, prev - 1e-6f, "LegModelTrust must be monotonic in confidence");
                prev = t;
            }
        }
        static Frame StepNoHint(Vector3 footPos, ref Legs s)
        {
            BasisLegSolveInput li = default;
            li.Root = LeftHip;
            li.Mid = LeftHip + Vector3.down * Thigh + Vector3.forward * 0.03f;
            li.Tip = LeftHip + Vector3.down * LegLen;
            li.RootRotation = Quaternion.identity;
            li.MidRotation = Quaternion.identity;
            li.TargetPosition = footPos;
            li.TargetRotation = Quaternion.identity;
            li.TargetOffset = Quaternion.identity;
            li.HintPosition = Vector3.zero;
            li.HintWeight = 0f;
            li.HintDistrust = 0f;
            li.BendNormal = HipsRot * Vector3.right;
            BasisLegSolveCore.Solve(li, out BasisLegSolveResult lr);

            BasisSwivelSmootherInput si = default;
            si.Root = LeftHip; si.Mid = lr.KneeSolved; si.Tip = lr.FootSolved;
            si.BodyRotation = HipsRot;
            si.ReferenceLocal = Vector3.forward; si.FallbackLocal = Vector3.right;
            si.Dt = Dt;
            si.MinCutoffHz = TrackedMinCutoffHz; si.Beta = TrackedBeta; si.DerivCutoffHz = TrackedDerivHz;
            si.ConditionOnPole = true; si.SingularMinCutoffHz = BasisSwivelFilterCore.MinCutoffHz;
            si.GuardAnteriorHalfSpace = true;
            si.AnteriorSoftDeg = BasisLegSolveCore.KneeAnteriorSoftDeg;
            si.AnteriorHardDeg = BasisLegSolveCore.KneeAnteriorHardDeg;
            si.State = s.Swivel; si.Seeded = s.Seeded;
            BasisSwivelSmootherCore.Solve(si, out BasisSwivelSmootherResult sr);
            if (sr.WriteState) { s.Swivel = sr.State; s.Seeded = sr.Seeded; }

            return new Frame
            {
                KneeRigid = lr.KneeSolved,
                KneeFinal = sr.Valid ? sr.DesiredMid : lr.KneeSolved,
                RawSwivelDeg = sr.RawSwivelDeg,
                Conditioning = sr.Conditioning,
            };
        }
        [Test]
        public void Probe_ButterflyStillSplaysTheKnee()
        {
            // The butterfly gate is dot(instep, outward). For the LEFT leg outward is -X, so the foot's up
            // vector must lean toward -X: a POSITIVE roll about +Z. (Negative rolls it toward +X, the gate
            // reads Max(0, dot) = 0, and the butterfly never engages -- which is how the first version of
            // this test managed to "prove" the feature was off.)
            Quaternion tilted = Quaternion.AngleAxis(55f, Vector3.forward);
            Assert.Greater(Vector3.Dot(tilted * Vector3.up, -(HipsRot * Vector3.right)), 0.5f,"test wiring: the foot's instep must actually lean OUTWARD or the butterfly cannot engage");

            Vector3 pulledIn = LeftHip + Vector3.down * 0.45f + Vector3.forward * 0.25f;   // foot pulled toward the hip

            var s = new Legs();
            Frame f = default;
            for (int i = 0; i < 400; i++) f = Step(pulledIn, tilted, ref s, butterflyOn: true, swivelSmoothing: true);

            // ...and the same pose with butterfly OFF, for the contrast.
            var s2 = new Legs();
            Frame off = default;
            for (int i = 0; i < 400; i++) off = Step(pulledIn, tilted, ref s2, butterflyOn: false, swivelSmoothing: true);

            Vector3 outward = -(HipsRot * Vector3.right);
            float splayOn = Vector3.Dot(f.KneeFinal - LeftHip, outward);
            float splayOff = Vector3.Dot(off.KneeFinal - LeftHip, outward);

            Debug.Log($"[BUTTERFLY] insteps rolled out 55 deg, foot pulled in:\n" + $"   butterfly weight     {f.ButterflyWeight:F2}\n" + $"   knee OUTWARD splay   {splayOn * 100f:F1} cm   (butterfly OFF: {splayOff * 100f:F1} cm)\n" + $"   delta                {(splayOn - splayOff) * 100f:F1} cm of splay attributable to butterfly");

            Assert.Greater(f.ButterflyWeight, 0.5f, "butterfly did not engage on a proper butterfly pose");
            Assert.Greater(splayOn - splayOff, 0.05f,"butterfly is engaged but the knee is not actually splaying outward -- the feature is broken");
        }
    }
}
