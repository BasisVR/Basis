using NUnit.Framework;
using Unity.Collections;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisBodyEvidenceCoreTests
    {
        const float Eps = 1e-4f, MinPlausible = 0.8f, MaxPlausible = 2.8f, SettledStep = 1f;
        static void FoldEye(ref BasisBodyEvidenceState state, float eyeHeight, float deltaSeconds = SettledStep)
        {
            var sample = new BasisBodyEvidenceSample
            {
                HeadY = eyeHeight,
                HeadValid = true,
                DeltaSeconds = deltaSeconds,
            };
            BasisBodyEvidenceCore.Fold(ref state, sample, hasFloor: false, floorY: 0f, minPlausible: MinPlausible, maxPlausible: MaxPlausible);
        }
        static void FoldSpan(ref BasisBodyEvidenceState state, float span, float deltaSeconds = SettledStep)
        {
            var sample = new BasisBodyEvidenceSample
            {
                HandSpan = span,
                HandsValid = true,
                DeltaSeconds = deltaSeconds,
            };
            BasisBodyEvidenceCore.Fold(ref state, sample, hasFloor: false, floorY: 0f, minPlausible: MinPlausible, maxPlausible: MaxPlausible);
        }
        static void FoldEyeRepeated(ref BasisBodyEvidenceState state, float eyeHeight, int count)
        {
            for (int i = 0; i < count; i++)
            {
                FoldEye(ref state, eyeHeight);
            }
        }
        [Test]
        public void BeforeEnoughSamples_NoEstimateIsOffered()
        {
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.60f, BasisBodyEvidenceCore.MinSamplesForConfidence - 1);

            Assert.IsFalse(BasisBodyEvidenceCore.TryGetEstimate(state.Eye, out _, out _),"a handful of frames is not evidence");
        }
        [Test]
        public void SteadySamples_SettleOnTheObservedHeight()
        {
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.60f, BasisBodyEvidenceCore.SamplesForFullConfidence);

            Assert.IsTrue(BasisBodyEvidenceCore.TryGetEstimate(state.Eye, out float estimate, out float confidence));
            Assert.AreEqual(1.60f, estimate, Eps);
            Assert.AreEqual(1f, confidence, Eps);
        }
        [Test]
        public void CrouchingDoesNotShrinkTheEstimate()
        {
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.60f, BasisBodyEvidenceCore.SamplesForFullConfidence);
            // Then spend a long time low — the single-capture failure mode this replaces.
            FoldEyeRepeated(ref state, 1.10f, BasisBodyEvidenceCore.SamplesForFullConfidence);

            Assert.IsTrue(BasisBodyEvidenceCore.TryGetEstimate(state.Eye, out float estimate, out _));
            Assert.AreEqual(1.60f, estimate, Eps, "a stance can only read short, so a short one proves nothing");
        }
        [Test]
        public void AHandfulOfGlitchedFramesCannotPinTheEstimate()
        {
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.60f, BasisBodyEvidenceCore.SamplesForFullConfidence);
            for (int i = 0; i < BasisBodyEvidenceCore.OutlierRejection; i++)
            {
                // Slow enough to clear the quasi-static gate, so this exercises the outlier rejection
                // itself rather than being turned away at the door.
                FoldEye(ref state, 2.10f, deltaSeconds: 4f); // plausible on its own, but not this player
            }

            Assert.Greater(state.Eye.Top.Length, 0);
            Assert.AreEqual(2.10f, state.Eye.Top[0], Eps, "the glitch is retained — and then discarded as the highest");
            Assert.IsTrue(BasisBodyEvidenceCore.TryGetEstimate(state.Eye, out float estimate, out _));
            Assert.AreEqual(1.60f, estimate, Eps, "the highest readings are discarded exactly so this cannot happen");
        }
        [Test]
        public void ARealChangeSustainedAcrossFramesIsAdopted()
        {
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.60f, BasisBodyEvidenceCore.SamplesForFullConfidence);
            // Enough to outlast the rejection depth: the player was slouching before, not now.
            FoldEyeRepeated(ref state, 1.72f, BasisBodyEvidenceCore.OutlierRejection + 1);

            Assert.IsTrue(BasisBodyEvidenceCore.TryGetEstimate(state.Eye, out float estimate, out _));
            Assert.AreEqual(1.72f, estimate, Eps);
        }
        [Test]
        public void ImplausiblyTallSamplesNeverEnterAtAll()
        {
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.60f, BasisBodyEvidenceCore.SamplesForFullConfidence);
            for (int i = 0; i < 20; i++)
            {
                FoldEye(ref state, 4.0f); // teleport, tracking loss, play-space reset
            }

            Assert.IsTrue(BasisBodyEvidenceCore.TryGetEstimate(state.Eye, out float estimate, out _));
            Assert.AreEqual(1.60f, estimate, Eps);
        }
        [Test]
        public void JumpingIsRejectedByTheQuasiStaticGate()
        {
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.60f, BasisBodyEvidenceCore.SamplesForFullConfidence);
            int before = state.Eye.SampleCount;

            // A jump covers a lot of height between two frames — the one thing that reads an eye height
            // longer than the body, and the reason a bare maximum would not do.
            for (int i = 0; i < 10; i++)
            {
                FoldEye(ref state, 1.95f, deltaSeconds: 0.083f);
                FoldEye(ref state, 1.60f, deltaSeconds: 0.083f);
            }

            Assert.IsTrue(BasisBodyEvidenceCore.TryGetEstimate(state.Eye, out float estimate, out _));
            Assert.AreEqual(1.60f, estimate, Eps);
            Assert.AreEqual(before, state.Eye.SampleCount, "moving fast is not a stance and should not count");
        }
        [Test]
        public void ArmsAtTheirSides_YieldNoSpanEstimateAtAll()
        {
            // Exactly what an avatar load catches, and the reading that used to be taken at face value:
            // hands resting at your sides are barely a shoulder-width apart. Offering that as a body
            // measurement is what shrank the avatar's arms on every join, so it must not be offered.
            var state = new BasisBodyEvidenceState();
            for (int i = 0; i < BasisBodyEvidenceCore.SamplesForFullConfidence; i++)
            {
                FoldSpan(ref state, 0.42f);
            }

            Assert.IsFalse(BasisBodyEvidenceCore.TryGetEstimate(state.ArmSpan, out _, out _),"a hand-to-hand distance that small is not anybody's reach");
        }
        [Test]
        public void ArmSpanConvergesOnceThePlayerReachesOut()
        {
            var state = new BasisBodyEvidenceState();
            for (int i = 0; i < BasisBodyEvidenceCore.SamplesForFullConfidence; i++)
            {
                FoldSpan(ref state, 0.42f);
            }

            // Then they gesture wide and hold it for a moment, which is all it takes.
            for (int i = 0; i < BasisBodyEvidenceCore.OutlierRejection + 1; i++)
            {
                FoldSpan(ref state, 1.68f, deltaSeconds: 4f);
            }

            Assert.IsTrue(BasisBodyEvidenceCore.TryGetEstimate(state.ArmSpan, out float span, out _));
            Assert.AreEqual(1.68f, span, Eps, "reach is only measurable when the player actually reaches");
        }
        [Test]
        public void MeasuringAgainstATrackedFloorCancelsAPlayspaceShift()
        {
            var shifted = new BasisBodyEvidenceState();
            var level = new BasisBodyEvidenceState();
            const float Shift = 0.65f;

            for (int i = 0; i < BasisBodyEvidenceCore.SamplesForFullConfidence; i++)
            {
                // Head and floor both ride the shift, so the measurement between them is unchanged.
                var lifted = new BasisBodyEvidenceSample { HeadY = 1.60f + Shift, HeadValid = true, DeltaSeconds = SettledStep };
                BasisBodyEvidenceCore.Fold(ref shifted, lifted, hasFloor: true, floorY: Shift, MinPlausible, MaxPlausible);

                var flat = new BasisBodyEvidenceSample { HeadY = 1.60f, HeadValid = true, DeltaSeconds = SettledStep };
                BasisBodyEvidenceCore.Fold(ref level, flat, hasFloor: true, floorY: 0f, MinPlausible, MaxPlausible);
            }

            Assert.IsTrue(BasisBodyEvidenceCore.TryGetEstimate(shifted.Eye, out float shiftedEye, out _));
            Assert.IsTrue(BasisBodyEvidenceCore.TryGetEstimate(level.Eye, out float levelEye, out _));
            Assert.AreEqual(levelEye, shiftedEye, Eps);
        }
        [Test]
        public void OneGoodSampleAmongLowOnesStillRaisesNothingItShouldNot()
        {
            // Guards the interaction between the two mechanisms: the different-person streak reads the
            // estimate, and folding must not let that bookkeeping disturb the estimate itself.
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.60f, BasisBodyEvidenceCore.SamplesForFullConfidence);
            FoldEyeRepeated(ref state, 1.20f, 50);

            Assert.IsTrue(BasisBodyEvidenceCore.TryGetEstimate(state.Eye, out float estimate, out _));
            Assert.AreEqual(1.60f, estimate, Eps);
        }
        [Test]
        public void ResetDropsEverythingObserved()
        {
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.60f, BasisBodyEvidenceCore.SamplesForFullConfidence);
            Assert.IsTrue(BasisBodyEvidenceCore.TryGetEstimate(state.Eye, out _, out _));

            BasisBodyEvidenceCore.Reset(ref state);

            Assert.IsFalse(BasisBodyEvidenceCore.TryGetEstimate(state.Eye, out _, out _),"recalibrating has to be able to escape a poisoned session");
        }
        [Test]
        public void SlouchingIsNeverMistakenForADifferentPerson()
        {
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.75f, BasisBodyEvidenceCore.SamplesForFullConfidence);

            // A long stretch of relaxed, slightly-low standing — exactly what a real session looks like.
            FoldEyeRepeated(ref state, 1.70f, BasisBodyEvidenceCore.DifferentPersonStreak * 2);

            Assert.IsFalse(BasisBodyEvidenceCore.LooksLikeADifferentPerson(state.Eye),"posture must never trigger the prompt, or it would fire constantly");
        }
        [Test]
        public void APersistentlyShorterBodyIsFlagged()
        {
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.85f, BasisBodyEvidenceCore.SamplesForFullConfidence);
            Assert.IsFalse(BasisBodyEvidenceCore.LooksLikeADifferentPerson(state.Eye));

            // Someone a head shorter picks up the headset. The high-water mark cannot come down on its
            // own, so without this they would wear the previous player's size for the whole session.
            FoldEyeRepeated(ref state, 1.50f, BasisBodyEvidenceCore.DifferentPersonStreak + 1);

            Assert.IsTrue(BasisBodyEvidenceCore.LooksLikeADifferentPerson(state.Eye));
        }
        [Test]
        public void StandingBackUpClearsTheSuspicion()
        {
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.85f, BasisBodyEvidenceCore.SamplesForFullConfidence);
            FoldEyeRepeated(ref state, 1.50f, BasisBodyEvidenceCore.DifferentPersonStreak / 2);
            FoldEyeRepeated(ref state, 1.85f, 1);
            FoldEyeRepeated(ref state, 1.50f, BasisBodyEvidenceCore.DifferentPersonStreak / 2);

            Assert.IsFalse(BasisBodyEvidenceCore.LooksLikeADifferentPerson(state.Eye),"one sample at full height proves the body is still there; the streak has to restart");
        }
        [Test]
        public void ResetAlsoClearsTheDifferentPersonStreak()
        {
            var state = new BasisBodyEvidenceState();
            FoldEyeRepeated(ref state, 1.85f, BasisBodyEvidenceCore.SamplesForFullConfidence);
            FoldEyeRepeated(ref state, 1.50f, BasisBodyEvidenceCore.DifferentPersonStreak + 1);
            Assert.IsTrue(BasisBodyEvidenceCore.LooksLikeADifferentPerson(state.Eye));

            BasisBodyEvidenceCore.Reset(ref state);

            Assert.IsFalse(BasisBodyEvidenceCore.LooksLikeADifferentPerson(state.Eye),"re-measuring is the answer to the prompt, so it must also silence it");
        }
        [Test]
        public void FloorComesFromAPairOfLowTrackers()
        {
            var heights = new FixedList128Bytes<float> { 0.09f, 0.11f, 0.95f };

            bool found = BasisBodyEvidenceCore.TryEstimateFloor(heights, headY: 1.68f, footMountAllowance: 0.07f, footBand: 0.22f, minFootBandTrackers: 2, minPlausible: MinPlausible, maxPlausible: MaxPlausible, out float floorY);

            Assert.IsTrue(found);
            Assert.AreEqual(0.09f - 0.07f, floorY, Eps);
        }
        [Test]
        public void ALoneTrackerIsNeverTreatedAsTheFloor()
        {
            var heights = new FixedList128Bytes<float> { 0.95f };

            bool found = BasisBodyEvidenceCore.TryEstimateFloor(heights, headY: 1.68f, footMountAllowance: 0.07f, footBand: 0.22f, minFootBandTrackers: 2, minPlausible: MinPlausible, maxPlausible: MaxPlausible, out _);

            Assert.IsFalse(found, "a single hip puck must not masquerade as the floor");
        }
    }
}
