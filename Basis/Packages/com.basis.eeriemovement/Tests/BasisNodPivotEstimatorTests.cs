using NUnit.Framework;
using Unity.Mathematics;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisNodPivotEstimatorTests
    {
        const float k_Dt = 1f / 90f;
        // A ~1.7 m user: HMD centre-eye 8 cm above and 16 cm in front of where their head actually pivots.
        static readonly float3 TrueArm = new float3(0f, 0.08f, 0.16f);
        // What the avatar authors, which is the estimator's prior and its fallback.
        static readonly float3 PriorArm = new float3(0f, 0.05f, 0.07f);
        static void Hmd(float3 arm, float3 pivot, float yawDeg, float pitchDeg, out float3 pos, out quaternion rot)
        {
            rot = math.mul(quaternion.AxisAngle(new float3(0f, 1f, 0f), math.radians(yawDeg)), quaternion.AxisAngle(new float3(1f, 0f, 0f), math.radians(-pitchDeg)));
            pos = pivot + math.mul(rot, arm);
        }
        static float3 Drive(BasisNodPivotSampler sampler, float seconds, System.Func<float, (float3 pivot, float yaw, float pitch)> motion)
        {
            BasisNodPivotSettings settings = BasisNodPivotEstimatorCore.Defaults();
            float3 arm = PriorArm;
            int steps = (int)(seconds / k_Dt);
            for (int i = 0; i < steps; i++)
            {
                float t = i * k_Dt;
                var m = motion(t);
                Hmd(TrueArm, m.pivot, m.yaw, m.pitch, out float3 pos, out quaternion rot);
                arm = sampler.Update(pos, rot, k_Dt, PriorArm, in settings);
            }
            return arm;
        }
        static (float3, float, float) LookingAround(float t) => (float3.zero, 25f * math.sin(2f * math.PI * 0.23f * t), 35f * math.sin(2f * math.PI * 0.5f * t));
        [Test]
        public void ACleanNodRecoversTheUsersOwnArm_NotTheAvatarsAuthoredOne()
        {
            float3 arm = Drive(new BasisNodPivotSampler(30), 6f, LookingAround);

            Assert.That(math.length(arm - TrueArm), Is.LessThan(0.01f),"six seconds of ordinary looking around should place the pivot within a centimetre");
            Assert.That(math.length(arm - PriorArm), Is.GreaterThan(0.05f),"and it must actually have moved off the prior, or the test is measuring nothing");
        }
        [Test]
        public void TheArmIsRecoveredAcrossTheWholePlausibleRangeOfNecks()
        {
            float3[] necks =
            {
                new float3(0f, 0.02f, 0.06f),
                new float3(0f, 0.04f, 0.10f),
                new float3(0f, 0.06f, 0.13f),
                new float3(0f, 0.08f, 0.16f),
            };

            foreach (float3 neck in necks)
            {
                BasisNodPivotSettings settings = BasisNodPivotEstimatorCore.Defaults();
                var sampler = new BasisNodPivotSampler(30);
                float3 arm = PriorArm;
                for (int i = 0; i < (int)(6f / k_Dt); i++)
                {
                    float t = i * k_Dt;
                    Hmd(neck, float3.zero, 25f * math.sin(2f * math.PI * 0.23f * t), 35f * math.sin(2f * math.PI * 0.5f * t), out float3 pos, out quaternion rot);
                    arm = sampler.Update(pos, rot, k_Dt, PriorArm, in settings);
                }

                Assert.That(math.length(arm - neck), Is.LessThan(0.015f), $"neck {neck} should be recovered, got {arm}");
            }
        }
        [Test]
        public void AHeadThatNeverNodsIsRefused_AndTheArmStaysOnThePrior()
        {
            var sampler = new BasisNodPivotSampler(30);
            float3 arm = Drive(sampler, 4f, t => (float3.zero, 0f, 0f));

            Assert.That(sampler.LastResult.Accepted, Is.False);
            Assert.That(arm, Is.EqualTo(PriorArm), "with nothing to fit, the prior must pass through untouched");
        }
        [Test]
        public void PureYawIsRefused_ItCarriesNoPitchToFitTheArmAgainst()
        {
            var sampler = new BasisNodPivotSampler(30);
            float3 arm = Drive(sampler, 4f, t => (float3.zero, 60f * math.sin(2f * math.PI * 0.4f * t), 0f));

            Assert.That(sampler.LastResult.Accepted, Is.False);
            Assert.That(sampler.LastResult.PitchRangeDeg, Is.LessThan(1f));
            Assert.That(arm, Is.EqualTo(PriorArm));
        }
        [Test]
        public void NoddingWhileWalkingIsRefused_TheStepIsMotionTheArcCannotExplain()
        {
            var sampler = new BasisNodPivotSampler(30);
            int accepted = CountAcceptances(sampler, 8f, t => (new float3(0f, 0f, 1.4f * t), 0f, 35f * math.sin(2f * math.PI * 0.5f * t)));

            Assert.That(accepted, Is.LessThanOrEqualTo(2),"a walk leaves residual the arc cannot account for and must be thrown out");
        }
        [Test]
        public void NoddingWhileSquattingIsRefused_ThoughItLooksLikeAPerfectFit()
        {
            // The trap: a squat taken in step with a look-down is genuinely ambiguous. Dropping the body and
            // lengthening the arm predict the SAME HMD path, so the fit comes out near perfect and the arm
            // absorbs the squat. Only bounding the vertical excursion separates them.
            var sampler = new BasisNodPivotSampler(30);
            BasisNodPivotSettings settings = BasisNodPivotEstimatorCore.Defaults();
            float3 arm = PriorArm;
            for (int i = 0; i < (int)(3f / k_Dt); i++)
            {
                float t = i * k_Dt, squat = -0.40f * math.saturate(t / 1.0f);
                Hmd(TrueArm, new float3(0f, squat, 0f), 0f, -55f * math.saturate(t / 1.0f), out float3 pos, out quaternion rot);
                arm = sampler.Update(pos, rot, k_Dt, PriorArm, in settings);
            }

            Assert.That(arm, Is.EqualTo(PriorArm),"a correlated squat must never reach the arm; it is the one window a residual test cannot catch");
        }
        [Test]
        public void TheArmCannotLeaveTheAnatomicalBox()
        {
            BasisNodPivotSettings settings = BasisNodPivotEstimatorCore.Defaults();
            // A pivot two metres behind the head is not a neck, however well it fits.
            float3 absurd = new float3(0f, 1.5f, 2f);
            var positions = new float3[16];
            var rotations = new quaternion[16];
            for (int i = 0; i < 16; i++)
            {
                Hmd(absurd, float3.zero, 0f, -40f + i * 5f, out positions[i], out rotations[i]);
            }

            BasisNodPivotEstimatorCore.Solve(positions, rotations, 16, PriorArm, in settings, out var r);

            Assert.That(r.Arm.y, Is.InRange(0f, settings.MaxArm.y + 1e-4f));
            Assert.That(r.Arm.z, Is.InRange(0f, settings.MaxArm.z + 1e-4f));
            Assert.That(math.abs(r.Arm.x), Is.LessThanOrEqualTo(settings.MaxArm.x + 1e-4f));
        }
        [Test]
        public void TheBoxScalesWithTheAvatar_ItIsABodyDimension()
        {
            // A slightly-too-long arm, kept modest so the vertical-range gate lets the window through and
            // the clamp is what the assertion is actually measuring.
            float3 overlong = new float3(0f, 0.15f, 0.25f);
            var positions = new float3[16];
            var rotations = new quaternion[16];
            for (int i = 0; i < 16; i++)
            {
                Hmd(overlong, float3.zero, 0f, -20f + i * 2.5f, out positions[i], out rotations[i]);
            }

            BasisNodPivotSettings unit = BasisNodPivotEstimatorCore.Defaults();
            BasisNodPivotEstimatorCore.Solve(positions, rotations, 16, PriorArm, in unit, out var atUnitScale);

            BasisNodPivotSettings giant = BasisNodPivotEstimatorCore.Defaults();
            giant.Scale = 2f;
            BasisNodPivotEstimatorCore.Solve(positions, rotations, 16, PriorArm, in giant, out var atDoubleScale);

            Assert.That(atUnitScale.Accepted, Is.True);
            Assert.That(atUnitScale.Arm.z, Is.EqualTo(unit.MaxArm.z).Within(1e-4f),"at scale 1 this arm is past the box and must be clamped to it");

            Assert.That(atDoubleScale.Accepted, Is.True);
            Assert.That(atDoubleScale.Arm.z, Is.GreaterThan(unit.MaxArm.z + 1e-3f),"a body twice the size has twice the box, so the same arm is no longer out of range");
        }
        [Test]
        public void ASingleAcceptedWindowCanOnlyMoveTheArmByTheBlendFraction()
        {
            // The blend lives inside the solve on purpose: per-frame slewing would let one window that
            // slipped past the gates keep pulling for the whole solve interval.
            var sampler = new BasisNodPivotSampler(30) { BlendPerAcceptance = 0.15f };
            BasisNodPivotSettings settings = BasisNodPivotEstimatorCore.Defaults();
            float3 arm = PriorArm;
            int acceptances = 0;
            for (int i = 0; i < (int)(1.6f / k_Dt); i++)
            {
                float t = i * k_Dt;
                Hmd(TrueArm, float3.zero, 0f, 35f * math.sin(2f * math.PI * 0.5f * t), out float3 p, out quaternion q);
                bool before = sampler.LastResult.Accepted;
                arm = sampler.Update(p, q, k_Dt, PriorArm, in settings);
                if (sampler.LastResult.Accepted && !before) acceptances++;
            }

            Assert.That(acceptances, Is.GreaterThan(0), "the window has to be accepted at all for this to mean anything");
            float travelled = math.length(arm - PriorArm) / math.length(TrueArm - PriorArm);
            Assert.That(travelled, Is.LessThan(0.6f),"a handful of acceptances must not slam the arm onto the fit");
        }
        [Test]
        public void DegenerateInputIsRefusedRatherThanCrashing()
        {
            BasisNodPivotSettings settings = BasisNodPivotEstimatorCore.Defaults();

            BasisNodPivotEstimatorCore.Solve(null, null, 8, PriorArm, in settings, out var rNull);
            Assert.That(rNull.Accepted, Is.False);
            Assert.That(rNull.Arm, Is.EqualTo(PriorArm));

            var positions = new float3[8];
            var rotations = new quaternion[8];
            for (int i = 0; i < 8; i++) rotations[i] = quaternion.identity;

            BasisNodPivotEstimatorCore.Solve(positions, rotations, 8, PriorArm, in settings, out var rStill);
            Assert.That(rStill.Accepted, Is.False);

            BasisNodPivotEstimatorCore.Solve(positions, rotations, 2, PriorArm, in settings, out var rShort);
            Assert.That(rShort.Accepted, Is.False);

            BasisNodPivotEstimatorCore.Solve(positions, rotations, 99, PriorArm, in settings, out var rOver);
            Assert.That(rOver.Accepted, Is.False, "a count past the end of the buffer must be refused, not read");
        }
        [Test]
        public void AResetReturnsTheSamplerToThePrior()
        {
            var sampler = new BasisNodPivotSampler(30);
            float3 learned = Drive(sampler, 6f, LookingAround);
            Assert.That(sampler.HasEstimate, Is.True);
            Assert.That(math.length(learned - PriorArm), Is.GreaterThan(0.05f));

            sampler.Reset();

            Assert.That(sampler.HasEstimate, Is.False);
            BasisNodPivotSettings settings = BasisNodPivotEstimatorCore.Defaults();
            Hmd(TrueArm, float3.zero, 0f, 0f, out float3 p, out quaternion q);
            Assert.That(sampler.Update(p, q, k_Dt, PriorArm, in settings), Is.EqualTo(PriorArm));
        }
        static int CountAcceptances(BasisNodPivotSampler sampler, float seconds, System.Func<float, (float3 pivot, float yaw, float pitch)> motion)
        {
            BasisNodPivotSettings settings = BasisNodPivotEstimatorCore.Defaults();
            int acceptances = 0;
            bool before = false;
            for (int i = 0; i < (int)(seconds / k_Dt); i++)
            {
                float t = i * k_Dt;
                var m = motion(t);
                Hmd(TrueArm, m.pivot, m.yaw, m.pitch, out float3 pos, out quaternion rot);
                sampler.Update(pos, rot, k_Dt, PriorArm, in settings);
                if (sampler.LastResult.Accepted && !before) acceptances++;
                before = sampler.LastResult.Accepted;
            }
            return acceptances;
        }
    }
}
