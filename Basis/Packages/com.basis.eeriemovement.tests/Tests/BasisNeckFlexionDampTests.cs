using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisNeckFlexionDampTests
    {
        // A ~1.7 m avatar: neck 7 cm below and 2 cm behind the head bone.
        static readonly Vector3 Lever = new Vector3(0f, -0.07f, -0.02f);
        // Built through Unity.Mathematics rather than Quaternion.AngleAxis so the fixture is pure managed
        // maths and stays runnable in the standalone core harness as well as in the editor.
        static Quaternion Axis(float deg, float3 axis) => (Quaternion)quaternion.AxisAngle(axis, math.radians(deg));
        static Quaternion Pitch(float deg) => Axis(deg, new float3(1f, 0f, 0f));
        static Quaternion Yaw(float deg) => Axis(deg, new float3(0f, 1f, 0f));
        static Vector3 Neck(Quaternion headRot, float extensionDamp, float flexionDamp) => BasisNeckCueCore.Solve(Vector3.zero, headRot, Lever, Vector3.up, extensionDamp, flexionDamp);
        [Test]
        public void FlexionDampZeroIsTheOldBehaviour_ByteForByte()
        {
            for (float deg = -90f; deg <= 90f; deg += 7.5f)
            {
                Quaternion rot = Pitch(deg);
                Vector3 old = BasisNeckCueCore.Solve(Vector3.zero, rot, Lever, Vector3.up, 0.65f);
                Vector3 now = Neck(rot, 0.65f, 0f);
                Assert.That((old - now).magnitude, Is.EqualTo(0f), $"the five-argument overload must be exactly the six-argument one with flexion 0, at {deg} deg");
            }
        }
        [Test]
        public void FlexionDampNeverTouchesTheLookUpSide()
        {
            // The two halves are SELECTED between, not blended, so enabling one cannot perturb the other.
            for (float deg = 0f; deg <= 85f; deg += 5f)
            {
                Quaternion up = Pitch(-deg);
                Assert.That((Neck(up, 0.65f, 0f) - Neck(up, 0.65f, 1f)).magnitude, Is.EqualTo(0f), $"look-up at {deg} deg must be untouched by the flexion damp");
            }
        }
        [Test]
        public void ExtensionDampNeverTouchesTheLookDownSide()
        {
            for (float deg = 0.5f; deg <= 85f; deg += 5f)
            {
                Quaternion down = Pitch(deg);
                Assert.That((Neck(down, 0f, 0.5f) - Neck(down, 1f, 0.5f)).magnitude, Is.EqualTo(0f), $"look-down at {deg} deg must be untouched by the extension damp");
            }
        }
        [Test]
        public void PureYawIsUntouchedByEitherDamp()
        {
            for (float deg = 0f; deg < 360f; deg += 30f)
            {
                Quaternion rot = Yaw(deg);
                Vector3 undamped = Neck(rot, 0f, 0f);
                Assert.That((Neck(rot, 0.65f, 0.5f) - undamped).magnitude, Is.LessThan(1e-6f), $"a head turn moves no part of the nod lever, at {deg} deg");
            }
        }
        [Test]
        public void DampingCutsTheLookDownNeckRise_AndTheDefaultVirtuallyRemovesIt()
        {
            float restY = (Vector3.zero + Lever).y;

            foreach (float deg in new[] { 30f, 45f, 60f, 75f })
            {
                float undamped = Neck(Pitch(deg), 0.65f, 0f).y - restY;
                float damped = Neck(Pitch(deg), 0.65f, 0.5f).y - restY, full = Neck(Pitch(deg), 0.65f, 1f).y - restY;

                Assert.That(undamped, Is.GreaterThan(0.01f), $"the artefact has to exist at {deg} deg or this file stops measuring anything");
                Assert.That(Mathf.Abs(damped), Is.LessThan(Mathf.Abs(undamped) * 0.6f), $"the default damp must at least halve the rise at {deg} deg");
                Assert.That(Mathf.Abs(full), Is.LessThan(1e-6f), $"full damping means the neck bone does not move at all on a nod, at {deg} deg");
            }
        }
        [Test]
        public void TheLeverKeepsItsLength_ItIsALinkInTheSpineChain()
        {
            // Shortening it would push the CCD solve toward the full-extension singularity that
            // BasisSpineTautBandTests exists to keep it off; the damp is a rotation, never a lerp.
            for (float deg = -85f; deg <= 85f; deg += 5f)
            {
                Vector3 damped = Neck(Pitch(deg), 0.65f, 0.5f);
                Assert.That(damped.magnitude, Is.EqualTo(Lever.magnitude).Within(1e-5f), $"lever length must survive damping at {deg} deg");
            }
        }
        [Test]
        public void TheNeckIsContinuousThroughLevelGaze()
        {
            // Level gaze is where the two damps hand over. A step there would read as a pelvis twitch.
            Vector3 previous = Neck(Pitch(-2f), 0.65f, 0.5f);
            for (float deg = -2f; deg <= 2f; deg += 0.1f)
            {
                Vector3 current = Neck(Pitch(deg), 0.65f, 0.5f);
                Assert.That((current - previous).magnitude, Is.LessThan(2e-3f), $"discontinuity crossing level gaze at {deg} deg");
                previous = current;
            }
        }
        [Test]
        public void TheVerticalPoleIsHandledOnBothSides()
        {
            // Straight up and straight down both leave the gaze with no azimuth of its own; the fallback
            // takes it from the head's up axis, which points opposite the heading in one case and along it
            // in the other. Getting that sign wrong flips the correction.
            Vector3 restLever = Lever, straightDown = Neck(Pitch(90f), 0.65f, 1f);
            Assert.That((straightDown - restLever).magnitude, Is.LessThan(1e-3f),"fully damped, straight down must leave the lever at rest");

            Vector3 straightUp = Neck(Pitch(-90f), 1f, 0.5f);
            Assert.That((straightUp - restLever).magnitude, Is.LessThan(1e-3f),"fully damped, straight up must leave the lever at rest");
        }
        [Test]
        public void ADegenerateLeverIsStillReturnedUntouched()
        {
            Vector3 zero = BasisNeckCueCore.Solve(Vector3.one, Pitch(60f), Vector3.zero, Vector3.up, 0.65f, 0.5f);
            Assert.That(zero, Is.EqualTo(Vector3.one));
        }
        // ---- the guard in BasisHeadPitchSwingCore ----
        static (Vector3 Offset, float ForwardMeters) Swing(Vector3 arm, float pitchDeg)
        {
            BasisHeadPitchSwingCore.Solve(pitchDeg, 0f, arm, 1f, 1f, out Vector3 offset, out float forwardMeters);
            return (offset, forwardMeters);
        }
        [Test]
        public void TheSwingIsStillRemovedWhenTheViewpointSitsAtOrBelowTheHeadBone()
        {
            // The old guard was `lever > 0` on the arm's Y. Any avatar whose authored viewpoint is level
            // with or below its head bone therefore got a silent zero back, which switched the entire
            // gaze-swing removal off and let the pelvis ride the gaze again -- measured at 3.5 cm on a
            // 60 degree look and 5.2 cm at 75.
            foreach (float aboveHead in new[] { 0.05f, 0.005f, 0f, -0.02f, -0.05f })
            {
                Vector3 arm = new Vector3(0f, aboveHead, 0.07f);
                foreach (float pitch in new[] { -75f, -60f, -30f, 30f, 60f })
                {
                    // Closed form: the arc of (0, h, f) about the head bone, along the heading.
                    float p = pitch * Mathf.Deg2Rad, expected = arm.y * Mathf.Sin(p) + arm.z * Mathf.Cos(p) - arm.z;
                    Assert.That(Swing(arm, pitch).ForwardMeters, Is.EqualTo(expected).Within(1e-5f), $"arm y={aboveHead} at {pitch} deg");
                }
            }
        }
        [Test]
        public void AnUnpopulatedArmIsStillRefused()
        {
            // A T-pose that has not been captured yet genuinely has nothing to remove, and that is the only
            // case the guard was ever there for.
            Assert.That(Swing(Vector3.zero, 60f).ForwardMeters, Is.EqualTo(0f));
            Assert.That(Swing(Vector3.zero, 60f).Offset, Is.EqualTo(Vector3.zero));
        }
        [Test]
        public void LevelGazeStillRemovesNothing()
        {
            foreach (float aboveHead in new[] { 0.05f, 0f, -0.05f })
            {
                Assert.That(Swing(new Vector3(0f, aboveHead, 0.07f), 0f).ForwardMeters, Is.EqualTo(0f).Within(1e-7f));
            }
        }
    }
}
