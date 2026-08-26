using NUnit.Framework;
using UnityEngine;
namespace Basis.Tests.IK
{
    public class BasisCalibrationScaleFreshnessTests
    {
        // The bone-sim runtime application (BasisBoneSimJob): destPos = incoming.pos + incoming.rot * invOffPos.
        static Vector3 RuntimeApply(Vector3 incomingPos, Quaternion incomingRot, Vector3 invOffPos) => incomingPos + incomingRot * invOffPos;
        // Unscaled tracker poses (raw playspace metres): hip-ish, foot-ish, chest-ish, plus a rotated one.
        static readonly (Vector3 pos, Quaternion rot)[] Trackers =
        {
            (new Vector3(0.02f, 0.95f, 0.03f), Quaternion.identity),
            (new Vector3(0.12f, 0.08f, 0.05f), Quaternion.Euler(0f, 35f, 10f)),
            (new Vector3(-0.01f, 1.30f, -0.02f), Quaternion.Euler(5f, -20f, 0f)),
        };
        // DeviceScale transitions a single calibration pass can produce: identity (already settled),
        // small player-height re-measure drift, and an avatar swap onto a much smaller/larger body.
        static readonly (float s0, float s1)[] ScaleSteps =
        {
            (1.00f, 1.00f),
            (1.00f, 1.04f),
            (1.00f, 0.63f),  // 1.63 m player into a ~1.02 m eye-height child avatar
            (0.63f, 1.00f),
            (1.00f, 1.45f),  // into a giant
        };
        [Test]
        public void StaleScaleCapture_MissesByScaleDeltaTimesPose_TheMultiCalibrateBug()
        {
            // Capture the offset from coords scaled at s0 while the avatar-bone reference sits at the
            // s1-scaled tracker (T-pose: the bone IS where the correctly-scaled tracker is). At runtime the
            // next poll rescales the tracker to s1, so the bone lands off the avatar bone by exactly
            // (s1 - s0) * |unscaled| — 35 cm for a hip tracker on the 1.0 -> 0.63 avatar swap. This is the
            // misfit users re-calibrated away by hand, pass by pass.
            foreach (var (s0, s1) in ScaleSteps)
            {
                if (Mathf.Approximately(s0, s1)) continue;
                foreach (var t in Trackers)
                {
                    BasisCalibrationMath.ScaleDeviceCoord(t.pos, t.rot, s0, Vector3.zero, Quaternion.identity, out Vector3 stalePos, out Quaternion staleRot);
                    BasisCalibrationMath.ScaleDeviceCoord(t.pos, t.rot, s1, Vector3.zero, Quaternion.identity, out Vector3 freshPos, out Quaternion freshRot);
                    Vector3 reference = freshPos; // avatar bone at the new scale

                    BasisCalibrationMath.ComputeInverseOffset(stalePos, staleRot, reference, freshRot, out Vector3 invOff, out _);
                    Vector3 landed = RuntimeApply(freshPos, freshRot, invOff);
                    float expectedMiss = Mathf.Abs(s1 - s0) * t.pos.magnitude, miss = (landed - reference).magnitude;
                    Assert.That(miss, Is.EqualTo(expectedMiss).Within(1e-5f), $"s0={s0} s1={s1} tracker={t.pos}: stale-scale capture must miss by |s1-s0|*|unscaled| (documents the bug's magnitude).");
                    // The "must be VISIBLE" sanity check only applies to a genuine avatar SWAP -- a large scale
                    // delta on a body-scale tracker. A small re-measure drift (e.g. 1.00 -> 1.04) legitimately
                    // produces a sub-centimetre miss on a low tracker (0.04 * 0.15 m = 6 mm), which is the bug
                    // being correctly small, not the scenario failing to exercise it -- so it is exempt.
                    if (expectedMiss > 0.01f)
                    {
                        Assert.That(miss, Is.GreaterThan(0.01f), $"s0={s0} s1={s1}: this transition was expected to produce a visible misfit; the scenario no longer exercises the bug.");
                    }
                }
            }
        }
        [Test]
        public void FreshScaleCapture_LandsBoneExactlyOnReference_AndFollowsTrackerDeltas()
        {
            // The fix: capture from coords rescaled at the final s1 (RefreshScaledDeviceCoordFromLastPoll)
            // — the offset is zero-residual at the calibration pose, and a subsequently MOVED tracker still
            // carries the bone rigidly with it (the offset encodes only the true tracker->bone relation).
            foreach (var (s0, s1) in ScaleSteps)
            {
                foreach (var t in Trackers)
                {
                    BasisCalibrationMath.ScaleDeviceCoord(t.pos, t.rot, s1, Vector3.zero, Quaternion.identity, out Vector3 freshPos, out Quaternion freshRot);
                    Vector3 reference = freshPos;

                    BasisCalibrationMath.ComputeInverseOffset(freshPos, freshRot, reference, freshRot, out Vector3 invOff, out _);

                    Vector3 landed = RuntimeApply(freshPos, freshRot, invOff);
                    Assert.That((landed - reference).magnitude, Is.LessThan(1e-5f), $"s1={s1} tracker={t.pos}: same-scale capture must land the bone exactly on the avatar bone.");

                    // Move the tracker (player shifts a leg): the bone must follow by the same scaled delta.
                    Vector3 movedUnscaled = t.pos + new Vector3(0.10f, -0.05f, 0.08f);
                    BasisCalibrationMath.ScaleDeviceCoord(movedUnscaled, t.rot, s1, Vector3.zero, Quaternion.identity, out Vector3 movedPos, out Quaternion movedRot);
                    Vector3 landedMoved = RuntimeApply(movedPos, movedRot, invOff);
                    Vector3 expected = reference + (movedPos - freshPos);
                    Assert.That((landedMoved - expected).magnitude, Is.LessThan(1e-5f), $"s1={s1} tracker={t.pos}: after a clean capture the bone must follow tracker deltas rigidly.");
                }
            }
        }
        [Test]
        public void RepeatedCalibration_ConvergedScale_WasTheOldWorkaround()
        {
            // Documents why "calibrate a few times" ever worked: pass N captures offsets at the scale pass
            // N-1 applied. Once the scale stops changing between passes the stale coords equal the fresh
            // ones and the capture is clean — the fix simply makes pass 1 behave like pass 2 always did.
            var t = Trackers[0];
            float s0 = 1.00f, s1 = 0.63f;

            // Pass 1 (old behavior): capture at s0 against s1 references -> misfit.
            BasisCalibrationMath.ScaleDeviceCoord(t.pos, t.rot, s0, Vector3.zero, Quaternion.identity, out Vector3 pass1Pos, out Quaternion pass1Rot);
            BasisCalibrationMath.ScaleDeviceCoord(t.pos, t.rot, s1, Vector3.zero, Quaternion.identity, out Vector3 refPos, out Quaternion refRot);
            BasisCalibrationMath.ComputeInverseOffset(pass1Pos, pass1Rot, refPos, refRot, out Vector3 invOff1, out _);
            float pass1Miss = (RuntimeApply(refPos, refRot, invOff1) - refPos).magnitude;

            // Pass 2 (old behavior): scale already s1, coords polled at s1 -> clean.
            BasisCalibrationMath.ComputeInverseOffset(refPos, refRot, refPos, refRot, out Vector3 invOff2, out _);
            float pass2Miss = (RuntimeApply(refPos, refRot, invOff2) - refPos).magnitude;

            Assert.That(pass1Miss, Is.GreaterThan(0.30f), "pass 1 on a big avatar change should visibly misfit (~35 cm hip offset).");
            Assert.That(pass2Miss, Is.LessThan(1e-5f), "pass 2 was always clean once the scale had settled — the convergence users leaned on.");
        }
    }
}
