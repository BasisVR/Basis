using Basis.Scripts.BasisCharacterController;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Framework.Tests
{
    public class BasisExternalVelocityTests
    {
        private const float Tolerance = 1e-4f;
        private const float Frame = 1f / 90f;

        private static void AssertNear(Vector3 expected, Vector3 actual)
        {
            Assert.That(Vector3.Distance(expected, actual), Is.LessThan(Tolerance), $"expected {expected:F4}, got {actual:F4}");
        }

        [Test]
        public void SettingVelocityWhileWalking_HandsTheVerticalPartToGravity()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver();
            driver.SetVelocity(new Vector3(3f, 5f, 4f));
            Assert.That(driver.currentVerticalSpeed, Is.EqualTo(5f));
            AssertNear(new Vector3(3f, 0f, 4f), driver.ExternalVelocity);
        }

        [Test]
        public void SettingVelocityWhileFlying_KeepsAllThreeAxes()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver { CurrentModeKind = BasisLocalCharacterDriver.Mode.Fly };
            driver.SetVelocity(new Vector3(3f, 5f, 4f));
            AssertNear(new Vector3(3f, 5f, 4f), driver.ExternalVelocity);
            Assert.That(driver.currentVerticalSpeed, Is.Zero);
        }

        [Test]
        public void NonFiniteVelocity_IsIgnored()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver();
            driver.SetVelocity(new Vector3(1f, 2f, 3f));
            driver.SetVelocity(new Vector3(float.NaN, 0f, 0f));
            driver.SetVelocity(new Vector3(0f, float.PositiveInfinity, 0f));
            driver.AddVelocity(new Vector3(0f, 0f, float.NegativeInfinity));
            AssertNear(new Vector3(1f, 0f, 3f), driver.ExternalVelocity);
            Assert.That(driver.currentVerticalSpeed, Is.EqualTo(2f));
        }

        [Test]
        public void Velocity_IsCapped()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver();
            driver.SetVelocity(new Vector3(1000f, 0f, 0f));
            Assert.That(driver.ExternalVelocity.magnitude, Is.EqualTo(BasisLocalCharacterDriver.MaxExternalSpeed).Within(Tolerance));
            driver.AddVelocity(new Vector3(1000f, 0f, 0f));
            Assert.That(driver.ExternalVelocity.magnitude, Is.EqualTo(BasisLocalCharacterDriver.MaxExternalSpeed).Within(Tolerance));
        }

        [Test]
        public void AddingUpwardSpeedOnTheGround_IgnoresThePushThatKeepsYouGrounded()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver { groundedPlayer = true, currentVerticalSpeed = -9.81f };
            driver.AddVelocity(new Vector3(0f, 5f, 0f));
            Assert.That(driver.currentVerticalSpeed, Is.EqualTo(5f).Within(Tolerance));
        }

        [Test]
        public void AddingUpwardSpeedWhileFalling_SlowsTheFall()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver { groundedPlayer = false, currentVerticalSpeed = -6f };
            driver.AddVelocity(new Vector3(2f, 5f, 0f));
            driver.AddVelocity(new Vector3(2f, 0f, 0f));
            Assert.That(driver.currentVerticalSpeed, Is.EqualTo(-1f).Within(Tolerance));
            AssertNear(new Vector3(4f, 0f, 0f), driver.ExternalVelocity);
        }

        [Test]
        public void WithoutMomentum_YourOwnMoveIsUnchanged()
        {
            Vector3 own = new Vector3(1f, 0f, 2f);
            Assert.That(BasisLocalCharacterDriver.CombineMotion(own, Vector3.zero), Is.EqualTo(own));
        }

        [Test]
        public void StandingStill_MomentumMovesYou()
        {
            Vector3 momentum = new Vector3(0f, 0f, 7f);
            Assert.That(BasisLocalCharacterDriver.CombineMotion(Vector3.zero, momentum), Is.EqualTo(momentum));
        }

        [Test]
        public void WalkingWithMomentum_DoesNotStackOnIt()
        {
            AssertNear(new Vector3(0f, 0f, 10f), BasisLocalCharacterDriver.CombineMotion(new Vector3(0f, 0f, 4f), new Vector3(0f, 0f, 10f)));
        }

        [Test]
        public void WalkingAgainstMomentum_Brakes()
        {
            AssertNear(new Vector3(0f, 0f, 6f), BasisLocalCharacterDriver.CombineMotion(new Vector3(0f, 0f, -4f), new Vector3(0f, 0f, 10f)));
        }

        [Test]
        public void SlowMomentum_StillLetsYouWalkAtFullSpeed()
        {
            AssertNear(new Vector3(0f, 0f, 4f), BasisLocalCharacterDriver.CombineMotion(new Vector3(0f, 0f, 4f), new Vector3(0f, 0f, 1.5f)));
        }

        [Test]
        public void WalkingAcrossMomentum_Steers()
        {
            AssertNear(new Vector3(4f, 0f, 10f), BasisLocalCharacterDriver.CombineMotion(new Vector3(4f, 0f, 0f), new Vector3(0f, 0f, 10f)));
        }

        [Test]
        public void WritingBackTheVelocityYouAlreadyHave_DoesNotSpeedYouUp()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver();
            Vector3 walking = new Vector3(0f, 0f, 4f);
            Vector3 measured = BasisLocalCharacterDriver.CombineMotion(walking, driver.ExternalVelocity);
            for (int bounce = 0; bounce < 5; bounce++)
            {
                driver.SetVelocity(new Vector3(measured.x, 9f, measured.z));
                measured = BasisLocalCharacterDriver.CombineMotion(walking, driver.ExternalVelocity);
            }
            AssertNear(walking, measured);
        }

        [Test]
        public void HittingAWall_DropsOnlyThePartGoingIntoIt()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver();
            driver.SetVelocity(new Vector3(-7f, 0f, 7f));
            driver.ClipExternalVelocity(driver.ExternalVelocity * Frame, new Vector3(0f, 0f, 7f) * Frame, Frame);
            AssertNear(new Vector3(0f, 0f, 7f), driver.ExternalVelocity);
        }

        [Test]
        public void PushingIntoAWallYourself_LeavesMomentumAlongItAlone()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver();
            driver.SetVelocity(new Vector3(0f, 0f, 10f));
            Vector3 intended = BasisLocalCharacterDriver.CombineMotion(new Vector3(-4f, 0f, 0f) * Frame, driver.ExternalVelocity * Frame);
            driver.ClipExternalVelocity(intended, new Vector3(0f, 0f, 10f) * Frame, Frame);
            AssertNear(new Vector3(0f, 0f, 10f), driver.ExternalVelocity);
        }

        [Test]
        public void ACornerThatStopsYouDead_StopsTheMomentum()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver();
            driver.SetVelocity(new Vector3(6f, 0f, 8f));
            driver.ClipExternalVelocity(driver.ExternalVelocity * Frame, Vector3.zero, Frame);
            AssertNear(Vector3.zero, driver.ExternalVelocity);
        }

        [Test]
        public void ARoundingErrorAgainstAWall_KeepsTheMomentum()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver();
            driver.SetVelocity(new Vector3(0f, 0f, 10f));
            Vector3 intended = driver.ExternalVelocity * Frame;
            driver.ClipExternalVelocity(intended, intended + new Vector3(1e-6f, 0f, -1e-6f), Frame);
            AssertNear(new Vector3(0f, 0f, 10f), driver.ExternalVelocity);
        }

        [Test]
        public void OnTheGround_MomentumFadesAndSettlesAtExactlyZero()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver();
            driver.SetVelocity(new Vector3(10f, 0f, 0f));
            float previous = driver.ExternalVelocity.x;
            for (int frame = 0; frame < 90 && driver.HasExternalVelocity; frame++)
            {
                driver.DecayExternalVelocity(Frame);
                Assert.That(driver.ExternalVelocity.x, Is.LessThan(previous));
                previous = driver.ExternalVelocity.x;
            }
            Assert.IsFalse(driver.HasExternalVelocity, "a second of ground friction should have stopped a 10 m/s shove");
            Assert.That(driver.ExternalVelocity, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void FadingMomentum_DoesNotDependOnFrameRate()
        {
            BasisLocalCharacterDriver slow = new BasisLocalCharacterDriver(), fast = new BasisLocalCharacterDriver();
            slow.SetVelocity(new Vector3(20f, 0f, 0f));
            fast.SetVelocity(new Vector3(20f, 0f, 0f));
            for (int frame = 0; frame < 6; frame++) slow.DecayExternalVelocity(1f / 30f);
            for (int frame = 0; frame < 24; frame++) fast.DecayExternalVelocity(1f / 120f);
            Assert.That(slow.ExternalVelocity.x, Is.GreaterThan(1f));
            Assert.That(fast.ExternalVelocity.x, Is.EqualTo(slow.ExternalVelocity.x).Within(1e-3f));
        }
    }
}
