using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisSpineLookUpTests
    {
        // A 1.7 m humanoid, standing, sagittal. Straight so that every centimetre measured below is the
        // solver's own contribution rather than the avatar's authored curve.
        static readonly Vector3 Hips = new Vector3(0f, 0.950f, 0f), Chest = new Vector3(0f, 1.170f, 0f);
        static readonly Vector3 Neck = new Vector3(0f, 1.400f, 0f), Head = new Vector3(0f, 1.500f, 0f);
        static readonly Vector3 HeadToNeckLocal = Neck - Head;      // head bind is identity on this rig
        const float Damp = BasisNeckCueCore.DefaultExtensionDamp;   // 0.65 shipped
        const float RealLookUpCarry = 0.35f;
        // Unity convention, shared with BasisCervicalDirectionTests and BasisHeadSweep: POSITIVE pitch is
        // looking DOWN.
        static Quaternion Gaze(float pitchDeg) => Quaternion.AngleAxis(pitchDeg, Vector3.right);
        static Vector3 HeadTarget(float pitchDeg, float carry)
        {
            return Vector3.Lerp(Head, Neck + Gaze(pitchDeg) * (Head - Neck), carry);
        }
        static Vector3 Cue(float pitchDeg, float carry, float damp)
        {
            return BasisNeckCueCore.Solve(HeadTarget(pitchDeg, carry), Gaze(pitchDeg), HeadToNeckLocal, Vector3.up, damp);
        }
        static float PhantomForwardCm(float pitchDeg, float carry, float damp)
        {
            Vector3 d = Cue(pitchDeg, carry, damp) - Neck;
            return new Vector3(d.x, 0f, d.z).magnitude * 100f;
        }
        static float PhantomSignedForwardCm(float pitchDeg, float carry, float damp)
        {
            Vector3 d = Cue(pitchDeg, carry, damp) - Neck;
            return d.z * 100f;   // the whole rig and sweep are sagittal, so +z is the gaze azimuth
        }
        // ------------------------------------------------------------------ the artifact, and its removal
        [Test]
        public void TheRigidReattachment_WalksTheNeckForward_OnARealLookUp()
        {
            // The bug, stated as a measurement: with the damping OFF (the shipped behaviour before this
            // fix), a player who stands perfectly still and looks up is reported as having leaned. If this
            // ever measures ~0 the premise has changed and the rest of the file is measuring nothing.
            float phantom = PhantomForwardCm(-60f, RealLookUpCarry, 0f);
            Assert.That(phantom, Is.GreaterThan(4f), $"a 60 deg look-up produced only {phantom:0.00} cm of phantom neck travel undamped; " +"the artifact these tests guard is gone or the rig changed.");
        }
        [Test]
        public void Damping_CutsThePhantomLean_ByMostOfIt()
        {
            // The headline. Same gaze, same standing player, damping on.
            for (float pitch = -80f; pitch <= -30f; pitch += 10f)
            {
                float before = PhantomForwardCm(pitch, RealLookUpCarry, 0f);
                float after = PhantomForwardCm(pitch, RealLookUpCarry, Damp);
                Assert.That(after, Is.LessThan(before * 0.35f), $"look-up {-pitch:0}: phantom neck travel {before:0.00} -> {after:0.00} cm, " +"less than the two thirds of it the damping is supposed to remove.");
            }
        }
        [Test]
        public void Damping_LeavesUnderTwoCentimetres_AtAnyLookUp_AndAlwaysBeats0ff()
        {
            // An absolute bound, so this cannot pass by the artifact merely shrinking in proportion, plus a
            // strict improvement at every angle so it cannot pass by trading one part of the range for
            // another. The worst residual is at the vertical-gaze pole (measured 1.72 cm of 6.4 undamped),
            // where the extension angle saturates at 90 deg and the correction has the least room.
            for (float pitch = -90f; pitch <= 0f; pitch += 5f)
            {
                float before = PhantomForwardCm(pitch, RealLookUpCarry, 0f);
                float after = PhantomForwardCm(pitch, RealLookUpCarry, Damp);
                Assert.That(after, Is.LessThan(2f), $"look-up {-pitch:0}: {after:0.00} cm of neck still walks out in front of the body.");
                Assert.That(after, Is.LessThanOrEqualTo(before + 1e-4f), $"look-up {-pitch:0}: damping made it worse, {before:0.00} -> {after:0.00} cm.");
            }
        }
        // ------------------------------------------------------------------ what must NOT change
        [Test]
        public void LookDown_IsBitIdentical_ToTheRigidReattachment()
        {
            // Flexion is the side where the rigid model holds, and the side every shipped look-down fix was
            // tuned against. The damping must not touch it -- not approximately, exactly.
            for (float pitch = 0f; pitch <= 90f; pitch += 5f)
            {
                Vector3 rigid = HeadTarget(pitch, 1f) + Gaze(pitch) * HeadToNeckLocal, damped = Cue(pitch, 1f, Damp);
                Assert.That(damped.x, Is.EqualTo(rigid.x), $"look-down {pitch:0}: x moved.");
                Assert.That(damped.y, Is.EqualTo(rigid.y), $"look-down {pitch:0}: y moved.");
                Assert.That(damped.z, Is.EqualTo(rigid.z), $"look-down {pitch:0}: z moved.");
            }
        }
        [Test]
        public void PureYaw_IsBitIdentical_ToTheRigidReattachment()
        {
            // Turning on the spot has no extension in it, so the correction angle is zero and the whole
            // look-around range must come through untouched.
            for (float yaw = -180f; yaw <= 180f; yaw += 15f)
            {
                Quaternion rot = Quaternion.AngleAxis(yaw, Vector3.up);
                Vector3 rigid = Head + rot * HeadToNeckLocal;
                Vector3 damped = BasisNeckCueCore.Solve(Head, rot, HeadToNeckLocal, Vector3.up, Damp);
                Assert.That((damped - rigid).magnitude, Is.LessThan(1e-6f), $"yaw {yaw:0}: the cue moved.");
            }
        }
        [Test]
        public void ZeroDamping_IsATrueOffSwitch()
        {
            // Every job struct and fixture that never sets the field gets 0, so 0 has to be the old
            // behaviour exactly -- otherwise adding the field silently re-poses every existing test.
            for (float pitch = -90f; pitch <= 90f; pitch += 5f)
            {
                Vector3 rigid = HeadTarget(pitch, RealLookUpCarry) + Gaze(pitch) * HeadToNeckLocal;
                Vector3 off = Cue(pitch, RealLookUpCarry, 0f);
                Assert.That((off - rigid).magnitude, Is.EqualTo(0f), $"pitch {pitch:0}: damp 0 was not a no-op.");
            }
        }
        [Test]
        public void TheLever_KeepsItsLength()
        {
            // The head->neck span is a link in the spine CCD's chain: shortening it would push the solve
            // toward the full-extension singularity BasisSpineTautBandTests exists to keep it off. The
            // correction is a rotation, so the length is invariant by construction -- pinned here because a
            // "lerp the cue back" implementation would look equivalent and quietly break that.
            float rest = HeadToNeckLocal.magnitude;
            for (float pitch = -90f; pitch <= 90f; pitch += 5f)
            {
                float len = (Cue(pitch, RealLookUpCarry, Damp) - HeadTarget(pitch, RealLookUpCarry)).magnitude;
                Assert.That(len, Is.EqualTo(rest).Within(1e-5f), $"pitch {pitch:0}: lever length {len:0.0000} != {rest:0.0000}.");
            }
        }
        [Test]
        public void TheCorrection_IsContinuous_ThroughLevelGaze()
        {
            // Level gaze is the most common pose in the game, and the correction switches on there. A step
            // in the cue is a step in the pelvis and the pre-bend, i.e. a visible pop every time you glance
            // up. Sweep finely across zero and bound the per-step motion.
            Vector3 prev = Cue(-5f, RealLookUpCarry, Damp);
            for (float pitch = -4.9f; pitch <= 5f; pitch += 0.1f)
            {
                Vector3 cur = Cue(pitch, RealLookUpCarry, Damp);
                Assert.That((cur - prev).magnitude, Is.LessThan(0.0005f), $"pitch {pitch:0.0}: the cue stepped {(cur - prev).magnitude * 1000f:0.00} mm in a 0.1 deg gaze change.");
                prev = cur;
            }
        }
        [Test]
        public void ThePhantom_MovesBackMonotonically_AsDampingRises()
        {
            // The knob has to behave like a knob. Note it is the SIGNED phantom that is monotone: the
            // magnitude is V-shaped, because past the damping that exactly cancels the swing the cue starts
            // sitting BEHIND the true neck instead of in front of it. That is the honest shape of this
            // trade -- the correction is calibrated against a physiology constant, and a user whose neck
            // carries more of a look-up than 0.35 gets over-corrected the other way. Dialling the setting
            // sweeps smoothly through the whole range and passes through zero exactly once.
            float prev = float.MaxValue;
            bool crossedZero = false;
            for (float damp = 0f; damp <= 1.001f; damp += 0.05f)
            {
                float signed = PhantomSignedForwardCm(-60f, RealLookUpCarry, damp);
                Assert.That(signed, Is.LessThan(prev + 1e-4f), $"damp {damp:0.00}: signed phantom rose from {prev:0.00} to {signed:0.00} cm.");
                if (prev > 0f && signed <= 0f) crossedZero = true;
                prev = signed;
            }
            Assert.That(crossedZero, Is.True, "the damping range never reaches a cancelled cue at all.");
        }
        [Test]
        public void TheCue_IsEquivariantUnderYaw()
        {
            // Same look-up, different facing: the phantom must be the same size and rotate with the player,
            // never grow or shrink with which way they happen to be standing. This is the bug class that bit
            // the hips leash ("the offset is changing as we rotate") and it is cheap to exclude here.
            float reference = PhantomForwardCm(-60f, RealLookUpCarry, Damp);
            for (float yaw = 0f; yaw < 360f; yaw += 30f)
            {
                Quaternion yawQ = Quaternion.AngleAxis(yaw, Vector3.up), rot = yawQ * Gaze(-60f);
                Vector3 headPos = Head + yawQ * (HeadTarget(-60f, RealLookUpCarry) - Head);
                Vector3 cue = BasisNeckCueCore.Solve(headPos, rot, HeadToNeckLocal, Vector3.up, Damp), d = cue - Neck;
                float phantom = new Vector3(d.x, 0f, d.z).magnitude * 100f;
                Assert.That(phantom, Is.EqualTo(reference).Within(1e-3f), $"yaw {yaw:0}: phantom {phantom:0.000} cm vs {reference:0.000} cm facing forward.");
            }
        }
        [Test]
        public void Degenerate_InputsAreSafe()
        {
            // A zero lever (rig with no neck offset), a zero player-up, and a gaze straight up the body
            // axis -- where head-forward carries no azimuth at all and the naive axis is undefined.
            Vector3 zeroLever = BasisNeckCueCore.Solve(Head, Gaze(-60f), Vector3.zero, Vector3.up, Damp);
            Assert.That(zeroLever, Is.EqualTo(Head));

            Vector3 zeroUp = BasisNeckCueCore.Solve(Head, Gaze(-60f), HeadToNeckLocal, Vector3.zero, Damp);
            Assert.That(float.IsNaN(zeroUp.x) || float.IsNaN(zeroUp.y) || float.IsNaN(zeroUp.z), Is.False,"a zero player-up produced a NaN cue.");

            // Straight up the body axis is the case worth pinning: head-forward carries no azimuth there,
            // so the naive rotation axis is undefined and the fallback has to hold. Fed the head target a
            // real vertical gaze produces, not a stationary head.
            Vector3 straightUp = Cue(-90f, RealLookUpCarry, Damp);
            Assert.That(float.IsNaN(straightUp.x) || float.IsNaN(straightUp.y) || float.IsNaN(straightUp.z), Is.False,"a vertical gaze produced a NaN cue.");
            Vector3 vertical = straightUp - Neck;
            Assert.That(new Vector3(vertical.x, 0f, vertical.z).magnitude * 100f, Is.LessThan(2f),"a vertical gaze walked the neck out in front of the body.");
        }
        // ------------------------------------------------------------------ the downstream consumers
        [Test]
        public void Damping_StopsTheTrunkCounterbalance_FromSlidingThePelvisBack()
        {
            // The counterbalance is correct code doing its job on a bad input: told the trunk has folded
            // forward, it answers by sliding the pelvis back to keep the centre of mass over the feet. On a
            // pure look-up nothing folded, so any pelvis travel at all is the phantom leaking through.
            float before = PelvisShiftCm(-60f, 0f), after = PelvisShiftCm(-60f, Damp);
            Assert.That(before, Is.GreaterThan(1.5f), $"the undamped cue only moved the pelvis {before:0.00} cm; premise changed.");
            Assert.That(after, Is.LessThan(0.5f), $"the pelvis still slid {after:0.00} cm back on a pure look-up.");
        }
        static float PelvisShiftCm(float pitchDeg, float damp)
        {
            BasisTrunkCounterbalanceCore.Solve(Hips, Cue(pitchDeg, RealLookUpCarry, damp), Vector3.up, BasisTrunkCounterbalanceCore.DerivedGain, 0.45f * (Head - Hips).magnitude, out Vector3 newHips, out _, out _);
            return (newHips - Hips).magnitude * 100f;
        }
        [Test]
        public void Damping_KeepsTheVirtualSpineChestTarget_OnTheBody()
        {
            // The virtual spine strings the chest and spine bones along the neck->hips chord
            // (ComputeChainPlacement: chestPos = lerp(neckPos, hipsPos, tChest)), so a neck that has walked
            // forward drags the chest TARGET forward with it -- and FBIKChestIKTarget is on by default, so
            // SolveChestTarget then drags the real chest bone out after it. This is the single biggest term
            // in the reported artifact, and it is why the fix has to land on the virtual spine's neck too
            // and not only on the FBIK cue.
            float tChest = (Neck - Chest).magnitude / (Neck - Hips).magnitude;
            float before = ChordChestForwardCm(-60f, 0f, tChest), after = ChordChestForwardCm(-60f, Damp, tChest);
            Assert.That(before, Is.GreaterThan(2f), $"the undamped chord only moved the chest {before:0.00} cm; premise changed.");
            Assert.That(after, Is.LessThan(before * 0.35f), $"the chest target still slides {after:0.00} cm forward on a pure look-up (was {before:0.00}).");
        }
        static float ChordChestForwardCm(float pitchDeg, float damp, float tChest)
        {
            Vector3 chest = Vector3.Lerp(Cue(pitchDeg, RealLookUpCarry, damp), Hips, tChest);
            Vector3 d = chest - Vector3.Lerp(Neck, Hips, tChest);
            return new Vector3(d.x, 0f, d.z).magnitude * 100f;
        }
        // ------------------------------------------------------------------ the cervical extreme block
        [Test]
        public void OnALookUp_TheChestDoesNotLeadThePelvis()
        {
            // The other half of the report, and the one that fires even under the idealised orbit model:
            // past 50 deg the cervical solve TRANSLATES the chest and hips bodily. Looking far down sits the
            // whole body back, and mirroring those numbers for a look-up sent the chest 4 cm forward against
            // the hips' 2.5 cm. But a look-up is an ARCH, and in an arch the pelvis leads -- the belly comes
            // forward and the sternum stays over or behind it. A chest that out-runs its own pelvis is the
            // "chest comes super forwards" the report describes.
            for (float pitch = -50f; pitch >= -90f; pitch -= 5f)
            {
                BasisCervicalResult r = Extreme(pitch);
                Assert.That(r.ChestForwardAmount, Is.LessThanOrEqualTo(r.HipsForwardAmount + 1e-5f), $"look-up {-pitch:0}: chest slid {r.ChestForwardAmount * 100f:0.00} cm forward, ahead of the " + $"pelvis' {r.HipsForwardAmount * 100f:0.00} cm.");
            }
        }
        [Test]
        public void OnALookDown_TheChestStillTravelsFurtherThanThePelvis()
        {
            // The flip side, pinned so the look-up split cannot be "fixed" by flattening both directions:
            // a deep look-down still sits the chest back further than the hips, exactly as before.
            BasisCervicalResult r = Extreme(80f);
            Assert.That(r.ChestForwardAmount, Is.LessThan(0f), "look-down did not sit the chest back.");
            Assert.That(Mathf.Abs(r.ChestForwardAmount), Is.GreaterThan(Mathf.Abs(r.HipsForwardAmount)),"look-down no longer moves the chest further than the hips.");
        }
        [Test]
        public void TheExtremeBlock_StaysDormant_ThroughOrdinaryGaze()
        {
            // Unchanged contract, re-pinned from the look-up side: nothing translates below the 50 deg onset.
            for (float pitch = -45f; pitch <= 45f; pitch += 5f)
            {
                BasisCervicalResult r = Extreme(pitch);
                Assert.That(r.ChestForwardAmount, Is.EqualTo(0f), $"pitch {pitch:0}: chest translated inside the normal range.");
                Assert.That(r.HipsForwardAmount, Is.EqualTo(0f), $"pitch {pitch:0}: hips translated inside the normal range.");
            }
        }
        // ------------------------------------------------------------------ the pelvis stance leash
        //
        // SECOND MECHANISM, same family. BasisVirtualSpineCore leashes the pelvis to the EYE, because the eye
        // is the only YAW-invariant point -- every derived bone orbits it when the view turns. But the eye is
        // not PITCH-invariant, and the leash only ever needed the first property: a head pitches about the base
        // of the skull, so the HMD rides that lever and a look-up carries it ~17 cm BACKWARD with the player's
        // feet welded to the floor. The follow law is deliberately fast (rate 2 + 250*dist/radius adopts about
        // two thirds of that in a single frame at 90 Hz), so the pelvis walked out from under the player, the
        // trunk was left leaning forward to reach a head that had not moved, and once hips->head passed the
        // chain's reach the CCD projected the head onto its reach sphere and it came off the HMD.
        //
        // The fix subtracts the swing the Eye->Head lock itself attributes to the gaze. That lock declares
        // head = eyePos + eyeRot * (tposeHead - tposeEye), i.e. the eye rigidly orbits the HEAD BONE -- so the
        // cancellation below is exact by construction rather than by tuning.
        static readonly Vector3 EyeRest = new Vector3(0f, 1.600f, 0.090f), EyeFromHead = EyeRest - Head;
        static Vector3 EyePos(float pitchDeg, float yawDeg, Vector3 bodyTranslation)
        {
            Quaternion rot = Quaternion.AngleAxis(yawDeg, Vector3.up) * Gaze(pitchDeg);
            return Head + bodyTranslation + rot * EyeFromHead;
        }
        static Vector3 StanceReference(float pitchDeg, float yawDeg, Vector3 bodyTranslation, float removal)
        {
            Vector3 eye = EyePos(pitchDeg, yawDeg, bodyTranslation);
            if (removal <= 0f) return eye;

            BasisHeadPitchSwingCore.Solve(pitchDeg, yawDeg, EyeFromHead, removal, 1f, out Vector3 offset, out _);
            return eye - offset;
        }
        [Test]
        public void TheEye_WalksBackwards_OnALookUp()
        {
            // The premise, measured: this is what the leash was being fed and reading as a step.
            float travel = (EyePos(-60f, 0f, Vector3.zero) - EyeRest).z * 100f;
            Assert.That(travel, Is.LessThan(-10f), $"a 60 deg look-up only carried the eye {travel:0.0} cm; the artifact these tests guard is gone.");
        }
        [Test]
        public void TheStanceReference_IsUnmovedByAPureGaze()
        {
            // The whole point. Standing still, any gaze, any facing: the pelvis's idea of where the player is
            // standing must not move by a millimetre.
            for (float yaw = 0f; yaw < 360f; yaw += 45f)
            {
                Vector3 rest = StanceReference(0f, yaw, Vector3.zero, 1f);
                for (float pitch = -90f; pitch <= 90f; pitch += 5f)
                {
                    Vector3 stance = StanceReference(pitch, yaw, Vector3.zero, 1f), d = stance - rest;
                    Assert.That(new Vector3(d.x, 0f, d.z).magnitude, Is.LessThan(1e-4f), $"yaw {yaw:0}, pitch {pitch:0}: the standing spot moved {new Vector3(d.x, 0f, d.z).magnitude * 100f:0.00} cm on a pure gaze.");
                }
            }
        }
        [Test]
        public void TheStanceReference_StillSeesARealStep()
        {
            // The removal must not deafen the leash: it depends only on gaze pitch, so genuine travel has to
            // pass through it exactly, at any gaze. A fix that pinned the pelvis outright would pass the test
            // above and be far worse than the bug.
            Vector3 step = new Vector3(0.13f, 0f, -0.20f);
            for (float pitch = -80f; pitch <= 80f; pitch += 20f)
            {
                Vector3 still = StanceReference(pitch, 0f, Vector3.zero, 1f);
                Vector3 stepped = StanceReference(pitch, 0f, step, 1f);
                Assert.That((stepped - still - step).magnitude, Is.LessThan(1e-5f), $"pitch {pitch:0}: a real {step.magnitude * 100f:0} cm step did not pass through the leash intact.");
            }
        }
        [Test]
        public void ZeroRemoval_LeavesTheLeashOnTheRawEye()
        {
            // Same off-switch contract as the neck damping: an unset field is the shipped-before behaviour.
            for (float pitch = -90f; pitch <= 90f; pitch += 10f)
            {
                Assert.That((StanceReference(pitch, 0f, Vector3.zero, 0f) - EyePos(pitch, 0f, Vector3.zero)).magnitude, Is.EqualTo(0f), $"pitch {pitch:0}: removal 0 was not a no-op.");
            }
        }
        static BasisCervicalResult Extreme(float pitchDeg)
        {
            BasisCervicalInput i = default;
            i.BaseDeg = 5f;
            i.NeckShare = 0.65f;
            i.MaxHeadPitchDeg = 80f;
            i.ExtremeStartDeg = 50f;
            i.ExtremeFullDeg = 80f;
            i.ExtremeRollForwardMaxDeg = 10f;
            i.ExtremeRollBackwardMaxDeg = 4f;
            i.ExtremeHipsHorizontalMax = 0.025f;
            i.ExtremeChestHorizontalMax = 0.04f;
            i.ExtremeHipsHorizontalLookUp = 0.025f;
            i.ExtremeChestHorizontalLookUp = 0.010f;
            i.ExtremeHipsDownMax = 0.015f;
            i.ExtremeChestDownMax = 0.025f;
            i.ExtremeHipsDownLookUp = 0.0005f;
            i.ExtremeChestDownLookUp = 0.001f;
            i.PitchGainDeg = 8f;
            i.ReferenceUp = Vector3.up;
            i.HeadTargetRot = Gaze(pitchDeg);
            i.HasUpperChest = true;
            BasisCervicalSolveCore.Solve(i, out BasisCervicalResult r);
            return r;
        }
    }
}
