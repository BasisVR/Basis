using Basis.Scripts.BasisCharacterController;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Framework.Tests
{
    public class BasisMovementSettleTests
    {
        const float Frame = 1f / 90f, Seconds = 0.35f;
        static Vector3 Walk => new Vector3(0f, 0f, 2.5f) * Frame;
        static BasisLocalCharacterDriver Coasting()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver();
            driver.ApplyMovementSettle(Walk, true, Seconds, Frame);
            return driver;
        }
        [Test]
        public void Moving_IsNotDelayed()
        {
            BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver();
            Assert.That(driver.ApplyMovementSettle(Walk, true, Seconds, Frame), Is.EqualTo(Walk));
            Assert.IsTrue(driver.IsSettling);
        }
        [Test]
        public void LettingGo_SlowsSmoothlyAndStopsAtExactlyTheSettleTime()
        {
            BasisLocalCharacterDriver driver = Coasting();
            float previous = Walk.magnitude, travelled = 0f, time = 0f;
            while (driver.IsSettling && time < 2f)
            {
                float step = driver.ApplyMovementSettle(Vector3.zero, true, Seconds, Frame).magnitude;
                Assert.That(step, Is.LessThanOrEqualTo(previous));
                previous = step;
                travelled += step;
                time += Frame;
            }
            Assert.That(time, Is.EqualTo(Seconds).Within(Frame * 1.5f));
            Assert.That(travelled, Is.EqualTo(2.5f * Seconds / 3f).Within(0.03f));
        }
        [Test]
        public void TheCurveDoesNotDependOnFrameRate()
        {
            float Travel(float deltaTime)
            {
                BasisLocalCharacterDriver driver = new BasisLocalCharacterDriver();
                driver.ApplyMovementSettle(new Vector3(0f, 0f, 2.5f) * deltaTime, true, Seconds, deltaTime);
                float total = 0f;
                while (driver.IsSettling)
                {
                    total += driver.ApplyMovementSettle(Vector3.zero, true, Seconds, deltaTime).magnitude;
                }
                return total;
            }
            Assert.That(Travel(1f / 45f), Is.EqualTo(Travel(1f / 144f)).Within(0.05f));
        }
        [Test]
        public void InputDuringTheCoast_TakesOverAtOnce()
        {
            BasisLocalCharacterDriver driver = Coasting();
            driver.ApplyMovementSettle(Vector3.zero, true, Seconds, Frame);
            Vector3 strafe = new Vector3(1f, 0f, 0f) * Frame;
            Assert.That(driver.ApplyMovementSettle(strafe, true, Seconds, Frame), Is.EqualTo(strafe));
            Assert.That(Vector3.Distance(driver.SettleVelocity, new Vector3(1f, 0f, 0f)), Is.LessThan(1e-4f));
        }
        [Test]
        public void TurnedOff_StopsDead()
        {
            BasisLocalCharacterDriver driver = Coasting();
            Assert.That(driver.ApplyMovementSettle(Vector3.zero, false, Seconds, Frame), Is.EqualTo(Vector3.zero));
            Assert.IsFalse(driver.IsSettling);
        }
        [Test]
        public void TheMoveDoesNotAdvanceWithoutTime()
        {
            BasisLocalCharacterDriver driver = Coasting();
            Vector3 velocity = driver.SettleVelocity;
            Assert.That(driver.ApplyMovementSettle(Vector3.zero, true, Seconds, 0f), Is.EqualTo(Vector3.zero));
            Assert.That(driver.SettleVelocity, Is.EqualTo(velocity));
        }
        [Test]
        public void AWall_EndsTheCoastAndASlideKeepsOnlyWhatMoved()
        {
            BasisLocalCharacterDriver driver = Coasting();
            driver.ApplyMovementSettle(Vector3.zero, true, Seconds, Frame);
            Vector3 slide = new Vector3(0.5f, 0f, 0f);
            driver.ClipMovementSettle(slide * Frame, Frame);
            Assert.That(Vector3.Distance(driver.SettleVelocity, slide), Is.LessThan(1e-3f));
            float next = driver.ApplyMovementSettle(Vector3.zero, true, Seconds, Frame).magnitude / Frame;
            Assert.That(next, Is.LessThan(0.5f));
            driver.ClipMovementSettle(Vector3.zero, Frame);
            Assert.IsFalse(driver.IsSettling);
        }
    }
}
