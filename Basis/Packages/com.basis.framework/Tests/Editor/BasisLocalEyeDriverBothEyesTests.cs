using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Basis.Tests.Local
{
    public class BasisLocalEyeDriverBothEyesTests
    {
        public enum Rig { Aligned, YForwardZUp, YForwardZDown, XForwardYUp, NegZForward, Tilted30, Tilted45, Rolled90, MixedConventions, HeadBoneZBack, HeadBoneZUp }

        const float TargetYawDeg = 15f, TargetPitchDeg = 8f;

        GameObject root;
        Transform head, left, right;
        Vector3 leftView, rightView;

        [TearDown]
        public void TearDown()
        {
            BasisLocalEyeDriver.Dispose();
            RemoteBoneJobSystem.Dispose();
            BasisLocalAvatarDriver.Mapping = new BasisTransformMapping();
            BasisLocalEyeDriverData.SetMaxLookAngle(false, BasisAvatar.DefaultEyeMaxLookAngle);
            BasisLocalCameraDriver.HeadPosition = Vector3.zero;
            BasisLocalCameraDriver.HeadRotation = Quaternion.identity;
            if (root != null) Object.DestroyImmediate(root);
        }

        static Quaternion EyeRot(Rig rig, bool isLeft)
        {
            switch (rig)
            {
                case Rig.YForwardZUp: return Quaternion.LookRotation(Vector3.up, Vector3.forward);
                case Rig.YForwardZDown: return Quaternion.LookRotation(Vector3.down, Vector3.forward);
                case Rig.XForwardYUp: return Quaternion.LookRotation(Vector3.left, Vector3.up);
                case Rig.NegZForward: return Quaternion.LookRotation(Vector3.back, Vector3.up);
                case Rig.Tilted30: return Quaternion.AngleAxis(30f, Vector3.right);
                case Rig.Tilted45: return Quaternion.AngleAxis(45f, Vector3.right) * Quaternion.AngleAxis(45f, Vector3.up);
                case Rig.Rolled90: return isLeft ? Quaternion.LookRotation(Vector3.forward, Vector3.right) : Quaternion.LookRotation(Vector3.forward, Vector3.left);
                case Rig.MixedConventions: return isLeft ? Quaternion.LookRotation(Vector3.up, Vector3.forward) : Quaternion.LookRotation(Vector3.down, Vector3.forward);
                default: return Quaternion.identity;
            }
        }

        static Quaternion HeadRot(Rig rig)
        {
            switch (rig)
            {
                case Rig.HeadBoneZBack: return Quaternion.AngleAxis(180f, Vector3.up);
                case Rig.HeadBoneZUp: return Quaternion.LookRotation(Vector3.up, Vector3.back);
                default: return Quaternion.identity;
            }
        }

        void Build(Rig rig)
        {
            root = new GameObject("EyeRigRoot");
            head = new GameObject("Head").transform;
            head.SetParent(root.transform, false);
            head.localPosition = new Vector3(0f, 1.6f, 0f);
            head.localRotation = HeadRot(rig);
            left = new GameObject("LeftEye").transform;
            left.SetParent(head, false);
            left.position = new Vector3(-0.03f, 1.65f, 0.08f);
            left.rotation = EyeRot(rig, true);
            right = new GameObject("RightEye").transform;
            right.SetParent(head, false);
            right.position = new Vector3(0.03f, 1.65f, 0.08f);
            right.rotation = EyeRot(rig, false);
            leftView = Quaternion.Inverse(left.rotation) * Vector3.forward;
            rightView = Quaternion.Inverse(right.rotation) * Vector3.forward;
        }

        static float2 YawPitchDeg(Transform eye, Vector3 viewLocal)
        {
            Vector3 v = eye.rotation * viewLocal;
            return new float2(math.degrees(math.atan2(v.x, v.z)), math.degrees(math.asin(math.clamp(v.y, -1f, 1f))));
        }

        static BasisEyeState Converge(BasisEyeCalibration calL, BasisEyeCalibration calR, float2 targetRad, float maxAngleDeg, int steps)
        {
            BasisEyeState s = BasisEyeState.Create(12345u);
            BasisEyePersonality p = BasisEyePersonality.Compute(0.5f, 0.5f);
            float dt = 1f / 90f;
            for (int i = 0; i < steps; i++) s.Update(dt, float2.zero, math.radians(maxAngleDeg), 0.05f, 0.15f, math.radians(0.4f), p, calL, calR, true, targetRad, targetRad, targetRad, 0f, i == 0);
            return s;
        }

        void ApplyThroughJob(BasisEyeState s, BasisEyeCalibration calL, BasisEyeCalibration calR)
        {
            TransformAccessArray taa = new TransformAccessArray(2);
            taa.Add(left);
            taa.Add(right);
            NativeArray<BasisEyeState> state = new NativeArray<BasisEyeState>(1, Allocator.TempJob);
            state[0] = s;
            try
            {
                new BasisEyeApplyJob { state = state, calLeftInitial = calL.initialRotation, calRightInitial = calR.initialRotation }.Schedule(taa).Complete();
            }
            finally
            {
                state.Dispose();
                taa.Dispose();
            }
        }

        void RunGaze(Rig rig, float maxAngleDeg, float yawDeg, float pitchDeg, out float2 l, out float2 r)
        {
            Build(rig);
            Vector3 facingForward = root.transform.forward, facingUp = root.transform.up;
            BasisEyeCalibration calL = BasisLocalEyeDriver.CalibrateOneEye(left, facingForward, facingUp), calR = BasisLocalEyeDriver.CalibrateOneEye(right, facingForward, facingUp);
            BasisEyeState s = Converge(calL, calR, new float2(math.radians(yawDeg), math.radians(pitchDeg)), maxAngleDeg, 270);
            ApplyThroughJob(s, calL, calR);
            l = YawPitchDeg(left, leftView);
            r = YawPitchDeg(right, rightView);
            Assert.IsTrue(math.all(math.isfinite(l)) && math.all(math.isfinite(r)), $"non-finite eye pose left={l} right={r}");
        }

        [TestCase(Rig.Aligned)]
        [TestCase(Rig.YForwardZUp)]
        [TestCase(Rig.YForwardZDown)]
        [TestCase(Rig.XForwardYUp)]
        [TestCase(Rig.NegZForward)]
        [TestCase(Rig.Tilted30)]
        [TestCase(Rig.Tilted45)]
        [TestCase(Rig.Rolled90)]
        [TestCase(Rig.MixedConventions)]
        [TestCase(Rig.HeadBoneZBack)]
        [TestCase(Rig.HeadBoneZUp)]
        public void BothEyesMoveTogether(Rig rig)
        {
            RunGaze(rig, 25f, TargetYawDeg, TargetPitchDeg, out float2 l, out float2 r);
            Assert.That(math.length(l), Is.GreaterThan(5f), $"left eye barely moved left={l} right={r}");
            Assert.That(math.length(r), Is.GreaterThan(5f), $"right eye barely moved left={l} right={r}");
            Assert.That(math.distance(l, r), Is.LessThan(1.5f), $"eyes diverge left={l} right={r}");
        }

        [TestCase(Rig.Aligned)]
        [TestCase(Rig.YForwardZUp)]
        [TestCase(Rig.YForwardZDown)]
        [TestCase(Rig.XForwardYUp)]
        [TestCase(Rig.NegZForward)]
        [TestCase(Rig.Tilted30)]
        [TestCase(Rig.Tilted45)]
        [TestCase(Rig.Rolled90)]
        [TestCase(Rig.MixedConventions)]
        [TestCase(Rig.HeadBoneZBack)]
        [TestCase(Rig.HeadBoneZUp)]
        public void EyesLookWhereTheTargetIs(Rig rig)
        {
            RunGaze(rig, 25f, TargetYawDeg, TargetPitchDeg, out float2 l, out float2 r);
            float expectedPitch = TargetPitchDeg * BasisEyeState.VerticalDampening;
            Assert.That(l.x, Is.EqualTo(TargetYawDeg).Within(2f), $"left yaw left={l} right={r}");
            Assert.That(r.x, Is.EqualTo(TargetYawDeg).Within(2f), $"right yaw left={l} right={r}");
            Assert.That(l.y, Is.EqualTo(expectedPitch).Within(2f), $"left pitch left={l} right={r}");
            Assert.That(r.y, Is.EqualTo(expectedPitch).Within(2f), $"right pitch left={l} right={r}");
        }

        [Test]
        public void MaxLookAngleCapsBothEyes()
        {
            RunGaze(Rig.YForwardZUp, 10f, 40f, 0f, out float2 l, out float2 r);
            Assert.That(l.x, Is.InRange(8f, 10.6f), $"left yaw not held at the cap left={l} right={r}");
            Assert.That(r.x, Is.InRange(8f, 10.6f), $"right yaw not held at the cap left={l} right={r}");
        }

        [Test]
        public void DriverPipelineWritesBothEyeBones()
        {
            Build(Rig.YForwardZUp);
            BasisLocalCameraDriver.HeadPosition = head.position;
            BasisLocalCameraDriver.HeadRotation = Quaternion.identity;
            BasisLocalAvatarDriver.Mapping = new BasisTransformMapping { AnimatorRoot = root.transform, HasAnimatorRoot = true, head = head, Hashead = true, LeftEye = left, HasLeftEye = true, RightEye = right, HasRightEye = true };
            BasisLocalEyeDriverData.Liveliness = 1f;
            BasisLocalEyeDriverData.Attentiveness = 0.5f;
            BasisLocalEyeDriverData.SetMaxLookAngle(false, 0f);
            BasisLocalEyeDriverData.PersonalityDirty = true;
            BasisLocalEyeDriver.Initialize();
            Assert.IsTrue(BasisLocalEyeDriver.IsEnabled, "driver did not enable with both eye bones mapped");
            BasisLocalEyeDriver driver = new BasisLocalEyeDriver();
            float maxL = 0f, maxR = 0f, maxDiff = 0f;
            for (int i = 0; i < 900; i++)
            {
                driver.Simulate(1f / 90f);
                driver.Apply();
                float2 l = YawPitchDeg(left, leftView), r = YawPitchDeg(right, rightView);
                Assert.IsTrue(math.all(math.isfinite(l)) && math.all(math.isfinite(r)), $"non-finite eye pose at step {i} left={l} right={r}");
                maxL = math.max(maxL, math.length(l));
                maxR = math.max(maxR, math.length(r));
                maxDiff = math.max(maxDiff, math.distance(l, r));
            }
            Assert.That(maxL, Is.GreaterThan(1f), "left eye never left rest");
            Assert.That(maxR, Is.GreaterThan(1f), "right eye never left rest");
            Assert.That(maxDiff, Is.LessThan(1.5f), $"eyes diverged by {maxDiff} deg");
            Assert.That(maxL, Is.LessThanOrEqualTo(25.5f), "left eye exceeded the max look angle");
            Assert.That(maxR, Is.LessThanOrEqualTo(25.5f), "right eye exceeded the max look angle");
        }

        [Test]
        public void DriverPipelineSurvivesCameraPoseBeforeFirstSample()
        {
            Build(Rig.Aligned);
            BasisLocalCameraDriver.HeadPosition = Vector3.zero;
            BasisLocalCameraDriver.HeadRotation = new Quaternion(0f, 0f, 0f, 0f);
            BasisLocalAvatarDriver.Mapping = new BasisTransformMapping
            {
                AnimatorRoot = root.transform,
                HasAnimatorRoot = true,
                head = head,
                Hashead = true,
                LeftEye = left,
                HasLeftEye = true,
                RightEye = right,
                HasRightEye = true
            };

            BasisLocalEyeDriver.Initialize();
            BasisLocalEyeDriver driver = new BasisLocalEyeDriver();

            // The startup frame from issue #1065: the eye stage runs before the camera
            // stage has published a rotation. It must not poison persistent state.
            driver.Simulate(1f / 90f);
            driver.Apply();
            Assert.IsTrue(math.all(math.isfinite(BasisLocalEyeDriver.LastKnownState.leftOffset.value)));
            Assert.IsTrue(math.all(math.isfinite(BasisLocalEyeDriver.LastKnownState.rightOffset.value)));

            // Once the camera publishes a real pose, animation must keep advancing without
            // requiring an avatar change to reinitialize the eye state.
            BasisLocalCameraDriver.HeadPosition = head.position;
            BasisLocalCameraDriver.HeadRotation = Quaternion.identity;
            float maxMovement = 0f;
            for (int i = 0; i < 900; i++)
            {
                driver.Simulate(1f / 90f);
                driver.Apply();
                float2 l = YawPitchDeg(left, leftView);
                float2 r = YawPitchDeg(right, rightView);
                Assert.IsTrue(math.all(math.isfinite(l)) && math.all(math.isfinite(r)), $"non-finite eye pose at step {i} left={l} right={r}");
                maxMovement = math.max(maxMovement, math.max(math.length(l), math.length(r)));
            }

            Assert.That(maxMovement, Is.GreaterThan(1f), "eyes stayed frozen after the first valid camera pose");
        }

        [TestCase(true, 10f, 10f)]
        [TestCase(true, 45f, 45f)]
        [TestCase(true, 200f, 45f)]
        [TestCase(false, 10f, 90f)]
        public void RemoteFaceDriverClampsEyeTrackingToMaxLookAngle(bool enabled, float authoredDeg, float expectedDeg)
        {
            Build(Rig.YForwardZUp);
            BasisRemoteFaceDriver face = new BasisRemoteFaceDriver();
            face.ConfigureEyes(left, right, root.transform.forward, root.transform.up, enabled, authoredDeg);
            face.ApplyEyeRotations(0f, 1f, 0f, 1f);
            float2 l = YawPitchDeg(left, leftView), r = YawPitchDeg(right, rightView);
            Assert.That(l.x, Is.EqualTo(expectedDeg).Within(0.5f), $"left yaw left={l} right={r}");
            Assert.That(r.x, Is.EqualTo(expectedDeg).Within(0.5f), $"right yaw left={l} right={r}");
        }

        [Test]
        public void FacingFramePrefersTposeFacingOverHeadBoneAxes()
        {
            Build(Rig.HeadBoneZUp);
            root.transform.rotation = Quaternion.AngleAxis(90f, Vector3.up);
            BasisTransformMapping refs = new BasisTransformMapping { AnimatorRoot = root.transform, HasAnimatorRoot = true, head = head, Hashead = true, AvatarForwards = Vector3.forward, AvatarUpwards = Vector3.up };
            BasisLocalEyeDriver.FacingFrame(refs, out Vector3 forward, out Vector3 up);
            Assert.That(Vector3.Dot(forward, Vector3.right), Is.GreaterThan(0.999f), $"forward={forward}");
            Assert.That(Vector3.Dot(up, Vector3.up), Is.GreaterThan(0.999f), $"up={up}");
            refs.AvatarForwards = Vector3.zero;
            BasisLocalEyeDriver.FacingFrame(refs, out forward, out up);
            Assert.That(Vector3.Dot(forward, Vector3.right), Is.GreaterThan(0.999f), $"root fallback forward={forward}");
            refs.HasAnimatorRoot = false;
            BasisLocalEyeDriver.FacingFrame(refs, out forward, out up);
            Assert.That(Vector3.Dot(forward, head.forward), Is.GreaterThan(0.999f), $"head fallback forward={forward}");
        }

        [Test]
        public void MaxLookAngleIsOptIn()
        {
            BasisLocalEyeDriverData.SetMaxLookAngle(false, 5f);
            Assert.IsFalse(BasisLocalEyeDriverData.MaxLookAngleEnabled);
            Assert.AreEqual(BasisAvatar.DefaultEyeMaxLookAngle, BasisLocalEyeDriverData.MaxLookAngleDeg, "disabled avatars keep the legacy clamp");
            BasisLocalEyeDriverData.SetMaxLookAngle(true, 5f);
            Assert.IsTrue(BasisLocalEyeDriverData.MaxLookAngleEnabled);
            Assert.AreEqual(5f, BasisLocalEyeDriverData.MaxLookAngleDeg);
            BasisLocalEyeDriverData.SetMaxLookAngle(true, 300f);
            Assert.AreEqual(BasisAvatar.MaxEyeMaxLookAngle, BasisLocalEyeDriverData.MaxLookAngleDeg);
        }

        [Test]
        public void ClampEyeMaxLookAngleFallsBackAndClamps()
        {
            Assert.AreEqual(BasisAvatar.DefaultEyeMaxLookAngle, BasisAvatar.ClampEyeMaxLookAngle(float.NaN));
            Assert.AreEqual(BasisAvatar.DefaultEyeMaxLookAngle, BasisAvatar.ClampEyeMaxLookAngle(0f));
            Assert.AreEqual(BasisAvatar.MaxEyeMaxLookAngle, BasisAvatar.ClampEyeMaxLookAngle(100f));
            Assert.AreEqual(3f, BasisAvatar.ClampEyeMaxLookAngle(3f));
        }
    }
}
