using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisShoulderBlendTests
    {
        const float dt = 1f / 90f;
        [Test]
        public void Step_RampsAtBlendTimeAndArrivesExactly()
        {
            float blend = 0f;
            int frames = 0;
            while (blend < 1f && frames < 1000)
            {
                blend = BasisShoulderBlendCore.Step(blend, 1f, dt, 0.25f);
                frames++;
            }
            Assert.That(blend, Is.EqualTo(1f));
            Assert.That(frames, Is.EqualTo(Mathf.CeilToInt(0.25f / dt)).Within(1));
            Assert.That(BasisShoulderBlendCore.Step(1f, 0f, dt, 0.25f), Is.EqualTo(1f - dt / 0.25f).Within(1e-5f));
            Assert.That(BasisShoulderBlendCore.Step(0.3f, 0.35f, dt, 0.25f), Is.EqualTo(0.3f + dt / 0.25f).Within(1e-5f), "the ramp is a slew limit toward a partial weight");
            Assert.That(BasisShoulderBlendCore.Step(0.3f, 0.32f, dt, 0.25f), Is.EqualTo(0.32f).Within(1e-6f));
        }
        [Test]
        public void Step_ZeroBlendTimeSnaps_AndZeroDtHolds()
        {
            Assert.That(BasisShoulderBlendCore.Step(0f, 1f, dt, 0f), Is.EqualTo(1f));
            Assert.That(BasisShoulderBlendCore.Step(0.5f, 1f, 0f, 0.25f), Is.EqualTo(0.5f));
            Assert.That(BasisShoulderBlendCore.Step(0.5f, 1f, -dt, 0.25f), Is.EqualTo(0.5f));
        }
        [Test]
        public void Blend_ReturnsEndpointsExactly_AndSweepsEvenly()
        {
            Quaternion a = Quaternion.Euler(0f, 0f, 5f), b = Quaternion.Euler(10f, 0f, 25f);
            Assert.That(BasisShoulderBlendCore.Blend(a, b, 0f), Is.EqualTo(a));
            Assert.That(BasisShoulderBlendCore.Blend(a, b, 1f), Is.EqualTo(b));
            Assert.That(BasisShoulderBlendCore.Blend(a, b, -0.5f), Is.EqualTo(a));
            Assert.That(BasisShoulderBlendCore.Blend(a, b, 1.5f), Is.EqualTo(b));
            float last = 0f, total = Quaternion.Angle(a, b);
            for (int i = 1; i <= 10; i++)
            {
                float angle = Quaternion.Angle(a, BasisShoulderBlendCore.Blend(a, b, i / 10f));
                Assert.That(angle, Is.GreaterThan(last));
                Assert.That(angle, Is.EqualTo(total * i / 10f).Within(0.05f));
                last = angle;
            }
        }
    }
}
