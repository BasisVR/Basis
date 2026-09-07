using Basis.Scripts.Rendering;
using NUnit.Framework;

namespace Basis.Tests.Performance
{
    /// <summary>
    /// The closed loop that trades render resolution for frame time. It is the one graphics control
    /// that reacts to lag while the player is looking at it, so its failure modes are all visible:
    /// oscillating between two scales reads as the image breathing, a step that is too large reads
    /// as a resolution pop, and reacting to a single slow frame drops the whole image because one
    /// shader compiled.
    ///
    /// The guards against that are the settle window, the smoothing, the asymmetric step limits
    /// (drop fast, recover slowly) and the dead band around the target. Every test here pins one of
    /// them by feeding GPU times, since none of it is observable from a screenshot.
    /// </summary>
    public class BasisDynamicResolutionPolicyTests
    {
        const float Sixty = 1000f / 60f;
        const float Ninety = 1000f / 90f;

        static BasisDynamicResolutionSettings Settings() => BasisDynamicResolutionSettings.Default();

        static BasisDynamicResolutionState Fresh(in BasisDynamicResolutionSettings settings)
        {
            return new BasisDynamicResolutionState
            {
                Scale = settings.MaximumScale,
                SmoothedGpuMilliseconds = 0f,
                FramesSinceChange = settings.SettleFrames,
            };
        }

        /// <summary>Feeds the same GPU time repeatedly and reports whether the scale ever moved.</summary>
        static bool Feed(
            in BasisDynamicResolutionSettings settings,
            ref BasisDynamicResolutionState state,
            float gpuMilliseconds,
            float targetMilliseconds,
            int frames)
        {
            bool changed = false;
            for (int frame = 0; frame < frames; frame++)
            {
                changed |= BasisDynamicResolutionPolicy.Evaluate(settings, ref state, gpuMilliseconds, targetMilliseconds);
            }
            return changed;
        }

        // ── smoothing ─────────────────────────────────────────────────────────

        [Test]
        public void TheFirstSampleSeedsTheAverage()
        {
            Assert.That(BasisDynamicResolutionPolicy.SmoothSample(0f, 12f, 0.1f), Is.EqualTo(12f).Within(1e-5f));
        }

        [Test]
        public void LaterSamplesEaseTowardTheNewValue()
        {
            Assert.That(BasisDynamicResolutionPolicy.SmoothSample(10f, 20f, 0.1f), Is.EqualTo(11f).Within(1e-4f));
        }

        [Test]
        public void AZeroSampleIsIgnoredRatherThanTreatedAsAFastFrame()
        {
            // A frame with no GPU timing available reads as 0 ms, which would otherwise look
            // like infinite headroom and drive the scale straight up.
            Assert.That(BasisDynamicResolutionPolicy.SmoothSample(10f, 0f, 0.1f), Is.EqualTo(10f).Within(1e-5f));
            Assert.That(BasisDynamicResolutionPolicy.SmoothSample(10f, -5f, 0.1f), Is.EqualTo(10f).Within(1e-5f));
        }

        [Test]
        public void SmoothingOfZeroHoldsAndSmoothingOfOneSnaps()
        {
            Assert.That(BasisDynamicResolutionPolicy.SmoothSample(10f, 20f, 0f), Is.EqualTo(10f).Within(1e-5f));
            Assert.That(BasisDynamicResolutionPolicy.SmoothSample(10f, 20f, 1f), Is.EqualTo(20f).Within(1e-5f));
            Assert.That(BasisDynamicResolutionPolicy.SmoothSample(10f, 20f, 2f), Is.EqualTo(20f).Within(1e-5f));
        }

        // ── the loop ──────────────────────────────────────────────────────────

        [Test]
        public void AnUninitialisedScaleStartsAtFullResolution()
        {
            BasisDynamicResolutionSettings settings = Settings();
            BasisDynamicResolutionState state = default;

            BasisDynamicResolutionPolicy.Evaluate(settings, ref state, Sixty, Sixty);
            Assert.That(state.Scale, Is.EqualTo(settings.MaximumScale).Within(1e-5f),
                "nothing has been measured yet, so the image must not start degraded.");
        }

        [Test]
        public void MissingTimingsDoNothing()
        {
            BasisDynamicResolutionSettings settings = Settings();
            BasisDynamicResolutionState state = Fresh(settings);

            Assert.That(Feed(settings, ref state, 0f, Sixty, 30), Is.False);
            Assert.That(Feed(settings, ref state, 12f, 0f, 30), Is.False);
            Assert.That(state.Scale, Is.EqualTo(settings.MaximumScale).Within(1e-5f));
        }

        [Test]
        public void NothingMovesUntilTheSettleWindowHasPassed()
        {
            // A change needs time to show up in the measurement it is judged by; reacting
            // sooner is reacting to the previous scale.
            BasisDynamicResolutionSettings settings = Settings();
            BasisDynamicResolutionState state = new BasisDynamicResolutionState
            {
                Scale = settings.MaximumScale,
                SmoothedGpuMilliseconds = 0f,
                FramesSinceChange = 0,
            };

            for (int frame = 0; frame < settings.SettleFrames; frame++)
            {
                Assert.That(BasisDynamicResolutionPolicy.Evaluate(settings, ref state, 40f, Sixty), Is.False,
                    $"frame {frame} is still inside the settle window");
            }
        }

        [Test]
        public void AGpuOverBudgetPullsTheScaleDown()
        {
            BasisDynamicResolutionSettings settings = Settings();
            BasisDynamicResolutionState state = Fresh(settings);

            Assert.That(Feed(settings, ref state, 40f, Sixty, 200), Is.True);
            Assert.That(state.Scale, Is.LessThan(settings.MaximumScale));
        }

        [Test]
        public void HeadroomLetsTheScaleClimbBackToFull()
        {
            BasisDynamicResolutionSettings settings = Settings();
            BasisDynamicResolutionState state = new BasisDynamicResolutionState
            {
                Scale = settings.MinimumScale,
                SmoothedGpuMilliseconds = 0f,
                FramesSinceChange = settings.SettleFrames,
            };

            Feed(settings, ref state, 2f, Sixty, 2000);
            Assert.That(state.Scale, Is.EqualTo(settings.MaximumScale).Within(1e-4f));
        }

        [Test]
        public void TheScaleNeverLeavesItsBounds()
        {
            BasisDynamicResolutionSettings settings = Settings();

            BasisDynamicResolutionState sinking = Fresh(settings);
            Feed(settings, ref sinking, 500f, Sixty, 2000);
            Assert.That(sinking.Scale, Is.EqualTo(settings.MinimumScale).Within(1e-4f),
                "an unplayably slow GPU still must not drop below the floor.");

            BasisDynamicResolutionState climbing = Fresh(settings);
            Feed(settings, ref climbing, 0.01f, Sixty, 2000);
            Assert.That(climbing.Scale, Is.EqualTo(settings.MaximumScale).Within(1e-4f),
                "there is no supersampling above the ceiling here.");
        }

        [Test]
        public void SittingInsideTheDeadBandChangesNothing()
        {
            // Between the raise and lower headroom the frame is close enough to target that
            // moving the scale would only make the image wobble.
            BasisDynamicResolutionSettings settings = Settings();
            BasisDynamicResolutionState state = Fresh(settings);
            float insideBand = Sixty / ((settings.RaiseHeadroom + settings.LowerHeadroom) * 0.5f);

            Assert.That(Feed(settings, ref state, insideBand, Sixty, 500), Is.False);
            Assert.That(state.Scale, Is.EqualTo(settings.MaximumScale).Within(1e-5f));
        }

        [Test]
        public void DroppingIsAllowedToBeMuchFasterThanRecovering()
        {
            // Falling behind is felt immediately; recovering slowly is not, so the two step
            // limits are deliberately asymmetric.
            BasisDynamicResolutionSettings settings = Settings();
            Assert.That(settings.MaximumDownStep, Is.GreaterThan(settings.MaximumUpStep));

            BasisDynamicResolutionState state = Fresh(settings);
            float before = state.Scale;
            BasisDynamicResolutionPolicy.Evaluate(settings, ref state, 100f, Sixty);
            Assert.That(before - state.Scale, Is.LessThanOrEqualTo(settings.MaximumDownStep + 1e-5f),
                "even a catastrophic frame moves at most one down-step.");
        }

        [Test]
        public void RecoveryIsCappedToOneUpStepPerDecision()
        {
            BasisDynamicResolutionSettings settings = Settings();
            BasisDynamicResolutionState state = new BasisDynamicResolutionState
            {
                Scale = settings.MinimumScale,
                SmoothedGpuMilliseconds = 1f,
                FramesSinceChange = settings.SettleFrames,
            };

            float before = state.Scale;
            BasisDynamicResolutionPolicy.Evaluate(settings, ref state, 1f, Sixty);
            Assert.That(state.Scale - before, Is.LessThanOrEqualTo(settings.MaximumUpStep + 1e-5f),
                "a big jump back up is a visible pop, however much headroom there is.");
        }

        [Test]
        public void AChangeResetsTheSettleCounter()
        {
            BasisDynamicResolutionSettings settings = Settings();
            BasisDynamicResolutionState state = Fresh(settings);
            state.SmoothedGpuMilliseconds = 40f;

            Assert.That(BasisDynamicResolutionPolicy.Evaluate(settings, ref state, 40f, Sixty), Is.True);
            Assert.That(state.FramesSinceChange, Is.Zero);
        }

        [Test]
        public void AChangeTooSmallToSeeIsNotAppliedAtAll()
        {
            // Applying a sub-thousandth scale change costs a render-target reallocation for
            // something no one can see.
            BasisDynamicResolutionSettings settings = Settings();
            settings.MaximumUpStep = BasisDynamicResolutionPolicy.MinimumMeaningfulChange * 0.5f;

            BasisDynamicResolutionState state = new BasisDynamicResolutionState
            {
                Scale = 0.75f,
                SmoothedGpuMilliseconds = 1f,
                FramesSinceChange = settings.SettleFrames,
            };

            Assert.That(BasisDynamicResolutionPolicy.Evaluate(settings, ref state, 1f, Sixty), Is.False);
            Assert.That(state.Scale, Is.EqualTo(0.75f).Within(1e-6f));
        }

        [Test]
        public void OneSlowFrameIsAbsorbedByTheSmoothing()
        {
            // A shader compile or an avatar load spikes a single frame. Reacting to one sample
            // would drop the whole image for something that is already over.
            BasisDynamicResolutionSettings settings = Settings();
            BasisDynamicResolutionState state = Fresh(settings);

            Feed(settings, ref state, Sixty, Sixty, 60);
            float steady = state.Scale;

            Assert.That(BasisDynamicResolutionPolicy.Evaluate(settings, ref state, 19f, Sixty), Is.False);
            Assert.That(state.Scale, Is.EqualTo(steady).Within(1e-5f));
        }

        [Test]
        public void TheSameSlowFrameSustainedIsActedOn()
        {
            // Same 19 ms, but now it is what the instance actually costs: smoothing delays
            // the reaction, it does not cancel it.
            BasisDynamicResolutionSettings settings = Settings();
            BasisDynamicResolutionState state = Fresh(settings);

            Feed(settings, ref state, Sixty, Sixty, 60);
            float steady = state.Scale;

            Feed(settings, ref state, 19f, Sixty, 300);
            Assert.That(state.Scale, Is.LessThan(steady));
        }

        [Test]
        public void TheLoopSettlesInsteadOfOscillating()
        {
            // A GPU whose cost scales with pixel count: the loop has to converge somewhere and
            // stay there, not hunt between two scales forever.
            BasisDynamicResolutionSettings settings = Settings();
            BasisDynamicResolutionState state = Fresh(settings);
            const float costAtFullScale = 24f;

            for (int frame = 0; frame < 4000; frame++)
            {
                BasisDynamicResolutionPolicy.Evaluate(settings, ref state,
                    costAtFullScale * state.Scale * state.Scale, Sixty);
            }

            float settledScale = state.Scale;
            int changes = 0;
            for (int frame = 0; frame < 600; frame++)
            {
                if (BasisDynamicResolutionPolicy.Evaluate(settings, ref state,
                        costAtFullScale * state.Scale * state.Scale, Sixty))
                {
                    changes++;
                }
            }

            Assert.That(changes, Is.Zero, "a converged loop must stop moving the resolution.");
            Assert.That(state.Scale, Is.EqualTo(settledScale).Within(1e-5f));
            Assert.That(costAtFullScale * settledScale * settledScale, Is.LessThanOrEqualTo(Sixty * 1.05f),
                "and it must land inside the frame budget it was aiming at.");
        }

        [Test]
        public void ATighterTargetSettlesLower()
        {
            BasisDynamicResolutionSettings settings = Settings();
            const float costAtFullScale = 24f;

            BasisDynamicResolutionState atSixty = Fresh(settings);
            BasisDynamicResolutionState atNinety = Fresh(settings);
            for (int frame = 0; frame < 4000; frame++)
            {
                BasisDynamicResolutionPolicy.Evaluate(settings, ref atSixty,
                    costAtFullScale * atSixty.Scale * atSixty.Scale, Sixty);
                BasisDynamicResolutionPolicy.Evaluate(settings, ref atNinety,
                    costAtFullScale * atNinety.Scale * atNinety.Scale, Ninety);
            }

            Assert.That(atNinety.Scale, Is.LessThan(atSixty.Scale),
                "asking for 90 Hz out of the same GPU has to cost resolution.");
        }

        [Test]
        public void ScaleMovesWithTheSquareRootOfTheHeadroom()
        {
            // Cost scales with pixel count, which scales with the square of the linear scale,
            // so the correction is the square root. Getting this wrong makes every step either
            // overshoot or crawl.
            BasisDynamicResolutionSettings settings = Settings();
            settings.MaximumDownStep = 1f;
            settings.MaximumUpStep = 1f;
            settings.MinimumScale = 0.01f;
            settings.Smoothing = 1f;

            BasisDynamicResolutionState state = new BasisDynamicResolutionState
            {
                Scale = 1f,
                SmoothedGpuMilliseconds = 0f,
                FramesSinceChange = settings.SettleFrames,
            };

            // Four times over budget: the correction is sqrt(1/4) = 0.5.
            BasisDynamicResolutionPolicy.Evaluate(settings, ref state, Sixty * 4f, Sixty);
            Assert.That(state.Scale, Is.EqualTo(0.5f).Within(1e-3f));
        }

        [Test]
        public void DefaultsAreSelfConsistent()
        {
            BasisDynamicResolutionSettings settings = Settings();

            Assert.That(settings.MinimumScale, Is.GreaterThan(0f));
            Assert.That(settings.MinimumScale, Is.LessThan(settings.MaximumScale));
            Assert.That(settings.MaximumScale, Is.LessThanOrEqualTo(1f), "this policy downscales, it does not supersample.");
            Assert.That(settings.LowerHeadroom, Is.LessThan(settings.RaiseHeadroom), "otherwise there is no dead band at all.");
            Assert.That(settings.SettleFrames, Is.GreaterThan(0));
            Assert.That(settings.Smoothing, Is.GreaterThan(0f).And.LessThanOrEqualTo(1f));
        }
    }
}
