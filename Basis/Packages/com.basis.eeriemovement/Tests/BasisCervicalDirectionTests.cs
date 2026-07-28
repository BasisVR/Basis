using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// "Chicken neck" / "gamer neck" regression tests for the cervical-lordosis IK.
    ///
    /// These exercise the pure, stream-free neck math directly -- <see cref="BasisCervicalSolveCore"/>
    /// (the port of BasisFullIKConstraintJob.ApplyCervicalLordosis). It computes the forward neck/upper-
    /// chest curl from head-gaze pitch, the head-pitch clamp, and the extreme-region whole-body
    /// counterbalance. They guard the failure mode the neck has:
    ///
    ///   CHICKEN NECK -- the head poking forward/down with the neck craned, instead of sitting with a
    ///                   gentle resting lordosis that flexes naturally with gaze.
    ///
    /// The headline guards are: the RESTING forward curl at level gaze stays small (a regression that
    /// cranks the base bend is exactly the chicken-neck artifact), the curl is correctly SIGNED (looking
    /// down flexes forward, looking up extends back -- never the reverse), grows MONOTONICALLY with gaze,
    /// is CLAMPED so the gaze can't crane past the anatomical limit, and has NO RATE KINK at level gaze
    /// (the most common pose, where a hard abs() would snap the neck ~4x). The bounds are intentionally
    /// looser than the values the current solver produces (each test notes the measured headroom) so they
    /// fail only on a real regression, not on float noise -- and they bound the curl from ABOVE, so the
    /// neck can be *tuned down* (less chicken neck) without tripping them.
    ///
    /// This is the same math the offline BasisHeadSweep CSV harness scans, asserted here so the "gentle
    /// forward lordosis, never a forward-poked chicken neck" property is checked in CI instead of by
    /// eyeballing a CSV. Sign convention (verified against the sweep): a positive pitch command is looking
    /// DOWN, matching Quaternion.Euler(x,0,0).
    /// </summary>
    public class BasisCervicalDirectionTests
    {
        // Production defaults, matching BasisFullBodyIK.SetDefaults() and BasisHeadSweepConfig.Default().
        const float BaseDeg = 5f;
        const float NeckShare = 0.65f;
        const float MaxHeadPitchDeg = 80f;
        const float ExtremeStartDeg = 50f;
        const float ExtremeFullDeg = 80f;
        const float PitchGainDeg = 8f;
        const float ExtremeRollForwardMaxDeg = 10f;
        const float ExtremeRollBackwardMaxDeg = 4f;
        const float ExtremeHipsHorizontalMax = 0.025f;
        const float ExtremeChestHorizontalMax = 0.04f;
        const float ExtremeHipsDownMax = 0.015f;
        const float ExtremeChestDownMax = 0.025f;
        const float ExtremeHipsDownLookUp = 0.0005f;
        const float ExtremeChestDownLookUp = 0.001f;

        // ----------------------------------------------------------------- sign convention

        [Test]
        public void Convention_LookingDownIsPositivePitch_LookingUpIsNegative()
        {
            // The whole suite rests on this basis: a positive pitch command (head tipped down, as
            // Quaternion.Euler(x,0,0)) must register as look-DOWN, and a negative command as look-UP.
            // If this flips, every "forward when down" assertion below would be reading the wrong gaze.
            var down = Solve(30f);
            Assert.That(down.SignedPitch, Is.GreaterThan(0f), "look-down did not produce a positive signed pitch.");
            Assert.That(down.LookDownFrac, Is.GreaterThan(0f), "look-down did not register a down fraction.");
            Assert.That(down.LookUpFrac, Is.EqualTo(0f), "look-down spuriously registered an up fraction.");

            var up = Solve(-30f);
            Assert.That(up.SignedPitch, Is.LessThan(0f), "look-up did not produce a negative signed pitch.");
            Assert.That(up.LookUpFrac, Is.GreaterThan(0f), "look-up did not register an up fraction.");
            Assert.That(up.LookDownFrac, Is.EqualTo(0f), "look-up spuriously registered a down fraction.");
        }

        // ----------------------------------------------------------------- resting pose (the headline)

        [Test]
        public void RestingNeck_IsGentleForwardCurl_NotAForwardPokedChickenNeck()
        {
            // At level gaze the neck must sit in a gentle FORWARD lordosis -- not a backward kink, and not
            // a big forward poke. This is the pose the avatar holds most of the time, so it is the one a
            // "chicken neck" shows up in. With the default 5 deg base bend the neck curls 3.25 deg; the
            // 12 deg ceiling lets the base bend be tuned UP a little before it reads as a forward poke,
            // while a runaway value (e.g. someone bumping the base to 20+) trips it.
            var r = Solve(0f);
            Assert.That(r.NeckDeg, Is.GreaterThan(0f),
                $"resting neck bend {r.NeckDeg:0.00} deg is backward; the neck should rest in a gentle forward lordosis.");
            Assert.That(r.NeckDeg, Is.LessThan(12f),
                $"resting neck bend {r.NeckDeg:0.00} deg is a forward poke (chicken neck); it should be a gentle curl (~3.25 deg).");
            // Total torso curl (neck + upper-chest) is likewise just the base bend at level gaze.
            Assert.That(r.NeckDeg + r.UpperChestLordosisDeg, Is.LessThan(15f),
                "resting neck + upper-chest curl is excessive; the whole upper torso is poking forward.");
        }

        [Test]
        public void RestingLordosis_EqualsBaseBend_Exactly()
        {
            // Structural invariant: at level gaze the smoothed-abs term is exactly zero
            // (sqrt(0.0225) == 0.15), so the resting lordosis is precisely the configured base bend with
            // no hidden offset. If the smoothing constants drift apart the resting pose silently shifts
            // (a permanent forward/back lean creeping into every avatar) -- this pins them together.
            var r = Solve(0f);
            Assert.That(r.LordosisDeg, Is.EqualTo(BaseDeg).Within(1e-3f),
                $"resting lordosis {r.LordosisDeg:0.0000} deg != base {BaseDeg} (the level-gaze smoothing offset has drifted).");
        }

        // ----------------------------------------------------------------- neck/upper-chest distribution

        [Test]
        public void NeckAndUpperChest_ShareSumsToTotalLordosis()
        {
            // The neck/upper-chest split is a partition of the same lordosis budget: whatever the head
            // pitch, neck + upper-chest must equal the total. A bug that double-counts or drops a share
            // would over/under-bend the torso (the neck leading or lagging the chest -- a kinked neck).
            for (float pitch = -90f; pitch <= 90f; pitch += 5f)
            {
                var r = Solve(pitch);
                Assert.That(r.NeckDeg + r.UpperChestLordosisDeg, Is.EqualTo(r.LordosisDeg).Within(1e-3f),
                    $"pitch {pitch:0}: neck {r.NeckDeg:0.000} + upperChest {r.UpperChestLordosisDeg:0.000} != lordosis {r.LordosisDeg:0.000}.");
            }
        }

        [Test]
        public void WithoutUpperChest_NeckCarriesTheWholeLordosis()
        {
            // On a rig with no upper-chest bone the neck must carry the full lordosis (no share is lost
            // to a bone that isn't there) and nothing is written to a phantom upper-chest.
            for (float pitch = -60f; pitch <= 60f; pitch += 15f)
            {
                var r = Solve(pitch, hasUpperChest: false);
                Assert.That(r.NeckDeg, Is.EqualTo(r.LordosisDeg).Within(1e-3f),
                    $"pitch {pitch:0}: neck {r.NeckDeg:0.000} != full lordosis {r.LordosisDeg:0.000} without an upper-chest.");
                Assert.That(r.UpperChestLordosisDeg, Is.EqualTo(0f),
                    $"pitch {pitch:0}: upper-chest curl {r.UpperChestLordosisDeg:0.000} written with no upper-chest bone.");
            }
        }

        // ----------------------------------------------------------------- gaze response (direction)

        [Test]
        public void ForwardCurl_GrowsLookingDown_ShrinksLookingUp_Monotonically()
        {
            // The neck curl must be a monotonic function of gaze: more look-down -> more forward flexion,
            // more look-up -> extension. A non-monotonic dip would mean the neck un-bends mid-nod (a
            // visible wobble), and a flat/inverted response would mean the base bend or pitch gain has the
            // wrong sign. Swept across the clamped gaze range; the +-80 clamp flattens the ends. (Stops
            // short of +-90 on purpose: a pure-pitch test rotation past vertical folds the *gaze
            // elevation* back down -- an artifact of building the rotation, not the solver, and the live
            // head never gazes past vertical. The full-sweep finite test covers the singularity itself.)
            float prev = float.NegativeInfinity;
            for (float pitch = -85f; pitch <= 85f; pitch += 2f)
            {
                float neck = Solve(pitch).NeckDeg;
                Assert.That(neck, Is.GreaterThanOrEqualTo(prev - 1e-3f),
                    $"neck curl dropped from {prev:0.000} to {neck:0.000} as gaze moved toward down at pitch {pitch:0} (non-monotonic).");
                prev = neck;
            }

            // And the endpoints are clearly ordered: down flexes forward, up extends back, through zero.
            Assert.That(Solve(60f).NeckDeg, Is.GreaterThan(Solve(0f).NeckDeg + 1f),
                "looking down did not flex the neck further forward than level.");
            Assert.That(Solve(0f).NeckDeg, Is.GreaterThan(Solve(-60f).NeckDeg + 1f),
                "looking up did not extend the neck back relative to level.");
        }

        [Test]
        public void LookingUp_PastTheRestingCurl_ExtendsNeckBackward()
        {
            // The opposite of chicken neck: a real up-gaze must extend the neck backward (the chin lifts),
            // not poke forward. Note the resting forward lordosis (base bend) DOMINATES until ~26 deg of
            // up-gaze -- below that the neck is still gently forward-curled (this is the residual "gamer
            // neck" in near-level poses; the lever to reduce it is the base bend). So extension is asserted
            // past the crossover. Measured: -40 deg -> -1.75 deg, -80 deg -> -4.6 deg (extension).
            for (float pitch = -40f; pitch >= -80f; pitch -= 20f)
            {
                var r = Solve(pitch);
                Assert.That(r.NeckDeg, Is.LessThan(0f),
                    $"look-up {pitch:0} deg curled the neck forward ({r.NeckDeg:0.00} deg) instead of extending it back.");
            }
        }

        // ----------------------------------------------------------------- head-pitch clamp

        [Test]
        public void HeadGaze_IsClampedToTheAnatomicalLimit()
        {
            // The gaze (and the neck bend it drives) must never crane past the configured limit, no matter
            // how far the head target pitches -- including past vertical. The clamped pitch the solver
            // reports and the actual pitch of the clamped head rotation both stay inside the limit.
            foreach (float cmd in new[] { 81f, 85f, 90f, 95f, 100f, -85f, -95f })
            {
                var r = Solve(cmd);
                Assert.That(Mathf.Abs(r.HeadPitchClampedDeg), Is.LessThanOrEqualTo(MaxHeadPitchDeg + 1e-3f),
                    $"command {cmd:0}: reported clamped pitch {r.HeadPitchClampedDeg:0.0} exceeds the {MaxHeadPitchDeg} deg limit.");

                Vector3 fwd = r.HeadRotClamped * Vector3.forward;
                float horiz = Mathf.Sqrt(fwd.x * fwd.x + fwd.z * fwd.z);
                float actualPitch = Mathf.Atan2(-fwd.y, horiz) * Mathf.Rad2Deg;
                Assert.That(Mathf.Abs(actualPitch), Is.LessThanOrEqualTo(MaxHeadPitchDeg + 1f),
                    $"command {cmd:0}: the clamped head still gazes {actualPitch:0.0} deg, past the {MaxHeadPitchDeg} deg limit.");
            }
        }

        // ----------------------------------------------------------------- smoothness at the common pose

        [Test]
        public void NeckBend_HasNoRateKink_AtLevelGaze()
        {
            // Level gaze is the most common head pose, so a kink in the bend rate right there reads as the
            // neck "snapping" as the user nods through level. The solver smooths |signedPitch| precisely
            // to avoid this -- a hard abs() would ~4x the bend rate across zero. Measured as the worst
            // centred second difference of the neck angle over a fine sweep through level: about 0.007 deg
            // with the smoothing, versus ~0.11 deg (16x) with a hard abs(). 0.03 is a wide regression guard.
            const float step = 1f;
            float worst = 0f, worstAt = 0f;
            for (float pitch = -19f; pitch <= 19f; pitch += step)
            {
                float a = Solve(pitch - step).NeckDeg;
                float b = Solve(pitch).NeckDeg;
                float c = Solve(pitch + step).NeckDeg;
                float secondDiff = Mathf.Abs(a - 2f * b + c);
                if (secondDiff > worst) { worst = secondDiff; worstAt = pitch; }
            }
            Assert.That(worst, Is.LessThan(0.03f),
                $"neck bend has a {worst:0.000} deg rate kink near level gaze (pitch {worstAt:0}); it would snap as the user nods through level.");
        }

        // ----------------------------------------------------------------- extreme-region counterbalance

        [Test]
        public void ExtremeCounterbalance_StaysDormant_ThroughNormalGaze()
        {
            // The whole-body counterbalance (sliding the hips/chest to follow an extreme look) must NOT
            // engage during ordinary head movement -- only past the extreme onset. A spurious offset in
            // the normal range is the torso drifting/detaching under the head, which itself reads as the
            // body lagging the neck. Below the 50 deg onset there must be no extreme and no body offset.
            for (float pitch = -45f; pitch <= 45f; pitch += 5f)
            {
                var r = Solve(pitch);
                Assert.That(r.HasExtreme, Is.False, $"pitch {pitch:0}: extreme counterbalance engaged inside the normal gaze range.");
                Assert.That(r.ExtremeFrac, Is.EqualTo(0f), $"pitch {pitch:0}: extreme fraction {r.ExtremeFrac:0.00} is non-zero before the onset.");
                Assert.That(r.HipsForwardAmount, Is.EqualTo(0f), $"pitch {pitch:0}: hips slid {r.HipsForwardAmount:0.0000} m in the normal range.");
                Assert.That(r.ChestForwardAmount, Is.EqualTo(0f), $"pitch {pitch:0}: chest slid {r.ChestForwardAmount:0.0000} m in the normal range.");
            }
        }

        [Test]
        public void ExtremeCounterbalance_EngagesPastOnset_AndRampsToFull()
        {
            // Past the onset the extreme fraction must ramp up monotonically and reach full by the
            // extreme-full angle (it saturates with the gaze clamp). This is what fades the counterbalance
            // in smoothly rather than popping it on.
            Assert.That(Solve(ExtremeStartDeg - 5f).ExtremeFrac, Is.EqualTo(0f), "extreme engaged before the onset.");
            Assert.That(Solve(ExtremeFullDeg).ExtremeFrac, Is.EqualTo(1f).Within(1e-3f), "extreme did not reach full by the extreme-full angle.");

            float prev = -1f;
            for (float pitch = ExtremeStartDeg; pitch <= ExtremeFullDeg; pitch += 2f)
            {
                float frac = Solve(pitch).ExtremeFrac;
                Assert.That(frac, Is.GreaterThanOrEqualTo(prev - 1e-4f),
                    $"pitch {pitch:0}: extreme fraction dropped from {prev:0.000} to {frac:0.000} (non-monotonic onset).");
                prev = frac;
            }
        }

        [Test]
        public void ExtremeCounterbalance_PushesTheBodyTheRightWay_AndStaysBounded()
        {
            // Direction: looking far DOWN (at your feet) sits the hips back and down; looking far UP (at
            // the ceiling) pushes the hips forward (the belly leads, the back arches). Magnitude: no
            // offset may ever exceed its configured maximum, so the counterbalance can't fling the body.
            var down = Solve(MaxHeadPitchDeg);     // clamped full look-down
            Assert.That(down.HasExtreme, Is.True, "full look-down did not engage the counterbalance.");
            Assert.That(down.HipsDownAmount, Is.GreaterThan(0f), "full look-down did not drop the hips.");
            Assert.That(down.HipsForwardAmount, Is.LessThanOrEqualTo(0f), "full look-down should sit the hips back, not forward.");

            var up = Solve(-MaxHeadPitchDeg);      // clamped full look-up
            Assert.That(up.HipsForwardAmount, Is.GreaterThanOrEqualTo(0f), "full look-up should push the hips forward.");

            for (float pitch = -100f; pitch <= 100f; pitch += 2f)
            {
                var r = Solve(pitch);
                Assert.That(Mathf.Abs(r.HipsForwardAmount), Is.LessThanOrEqualTo(ExtremeHipsHorizontalMax + 1e-4f),
                    $"pitch {pitch:0}: hips slid {r.HipsForwardAmount:0.0000} m, past the {ExtremeHipsHorizontalMax} m cap.");
                Assert.That(Mathf.Abs(r.ChestForwardAmount), Is.LessThanOrEqualTo(ExtremeChestHorizontalMax + 1e-4f),
                    $"pitch {pitch:0}: chest slid {r.ChestForwardAmount:0.0000} m, past the {ExtremeChestHorizontalMax} m cap.");
                Assert.That(r.HipsDownAmount, Is.InRange(-1e-4f, ExtremeHipsDownMax + 1e-4f),
                    $"pitch {pitch:0}: hips dropped {r.HipsDownAmount:0.0000} m, outside [0, {ExtremeHipsDownMax}].");
                Assert.That(r.ChestDownAmount, Is.InRange(-1e-4f, ExtremeChestDownMax + 1e-4f),
                    $"pitch {pitch:0}: chest dropped {r.ChestDownAmount:0.0000} m, outside [0, {ExtremeChestDownMax}].");
            }
        }

        // ----------------------------------------------------------------- degenerate / safety

        [Test]
        public void NegligibleConfig_EarlyOutsWithoutWritingTheNeck()
        {
            // With no base bend and no pitch gain the lordosis is ~0 at level gaze, so the solver must
            // early-out rather than write a sub-0.01 deg jitter rotation onto the neck every frame.
            var input = DefaultInput(0f);
            input.BaseDeg = 0f;
            input.PitchGainDeg = 0f;
            BasisCervicalSolveCore.Solve(input, out var r);

            Assert.That(r.EarlyOut, Is.True, "a negligible config did not early-out.");
            Assert.That(r.NeckDeg, Is.EqualTo(0f), $"a negligible config still wrote {r.NeckDeg:0.0000} deg to the neck.");
        }

        [Test]
        public void AllOutputs_StayFinite_AcrossTheFullPitchSweep()
        {
            // Sweep the head from past-vertical up to past-vertical down, through the straight-up/down
            // singularities where the horizontal gaze magnitude collapses. Nothing may go NaN/Inf, and the
            // neck bend must stay in a sane range -- a blown-up neck angle is an instant, total contortion.
            for (float pitch = -110f; pitch <= 110f; pitch += 1f)
            {
                var r = Solve(pitch);
                Assert.That(IsFinite(r.NeckDeg) && IsFinite(r.LordosisDeg) && IsFinite(r.UpperChestLordosisDeg)
                            && IsFinite(r.BhDeg) && IsFinite(r.ExtremeFrac)
                            && IsFinite(r.HipsForwardAmount) && IsFinite(r.HipsDownAmount)
                            && IsFinite(r.ChestForwardAmount) && IsFinite(r.ChestDownAmount),
                    Is.True, $"pitch {pitch:0}: a cervical output is non-finite.");
                Assert.That(Mathf.Abs(r.NeckDeg), Is.LessThan(90f),
                    $"pitch {pitch:0}: neck bend {r.NeckDeg:0.0} deg is an inhuman contortion.");
            }
        }

        // ----------------------------------------------------------------- helpers (test scaffolding)

        static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        static BasisCervicalResult Solve(float pitchDeg, float yawDeg = 0f, bool hasUpperChest = true)
        {
            BasisCervicalSolveCore.Solve(DefaultInput(pitchDeg, yawDeg, hasUpperChest), out var r);
            return r;
        }

        // Build the production-default cervical input at a given gaze. Head rotation matches the sweep
        // (and the live job): yaw about up, then pitch about right -- positive pitch is look-down.
        static BasisCervicalInput DefaultInput(float pitchDeg, float yawDeg = 0f, bool hasUpperChest = true)
        {
            BasisCervicalInput i;
            i.BaseDeg = BaseDeg;
            i.NeckShare = NeckShare;
            i.MaxHeadPitchDeg = MaxHeadPitchDeg;
            i.ExtremeStartDeg = ExtremeStartDeg;
            i.ExtremeFullDeg = ExtremeFullDeg;
            i.ExtremeRollForwardMaxDeg = ExtremeRollForwardMaxDeg;
            i.ExtremeRollBackwardMaxDeg = ExtremeRollBackwardMaxDeg;
            i.ExtremeHipsHorizontalMax = ExtremeHipsHorizontalMax;
            i.ExtremeChestHorizontalMax = ExtremeChestHorizontalMax;
            i.ExtremeHipsDownMax = ExtremeHipsDownMax;
            i.ExtremeChestDownMax = ExtremeChestDownMax;
            i.ExtremeHipsDownLookUp = ExtremeHipsDownLookUp;
            i.ExtremeChestDownLookUp = ExtremeChestDownLookUp;
            i.PitchGainDeg = PitchGainDeg;
            i.ReferenceUp = Vector3.up;
            i.HeadTargetRot = Quaternion.AngleAxis(yawDeg, Vector3.up) * Quaternion.AngleAxis(pitchDeg, Vector3.right);
            i.HasUpperChest = hasUpperChest;
            return i;
        }
    }
}
