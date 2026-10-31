using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Common;
using NUnit.Framework;
using System.Reflection;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisSeatBodyControlTests
    {
        private GameObject _go;
        private BasisSeat _seat;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject(nameof(BasisSeatBodyControlTests));
            _seat = _go.AddComponent<BasisSeat>();
            _seat.SetPoints(new Vector3(0f, 0f, -0.25f), new Vector3(0f, -0.5f, 0.25f), new Vector3(0f, 0f, 0.25f), 90.0);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        private static BasisTransformMapping Rig(Quaternion hipsRest, Vector3 forward)
        {
            var mapping = new BasisTransformMapping();
            mapping.TposeFromRoot[HumanBodyBones.Hips] = new BasisCalibratedCoords(new Vector3(0f, 0.95f, 0f), hipsRest);
            mapping.TposeFromRoot[HumanBodyBones.LeftUpperLeg] = new BasisCalibratedCoords(new Vector3(-0.1f, 0.9f, 0f), Quaternion.identity);
            mapping.TposeFromRoot[HumanBodyBones.LeftLowerLeg] = new BasisCalibratedCoords(new Vector3(-0.1f, 0.48f, 0f), Quaternion.identity);
            mapping.TposeFromRoot[HumanBodyBones.LeftFoot] = new BasisCalibratedCoords(new Vector3(-0.1f, 0.08f, 0f), Quaternion.identity);
            mapping.TposeFromRoot[HumanBodyBones.LeftToes] = new BasisCalibratedCoords(new Vector3(-0.1f, 0.02f, 0.12f), Quaternion.identity);
            mapping.AvatarForwards = forward;
            mapping.AvatarUpwards = Vector3.up;
            mapping.AvatarRightwards = Vector3.Cross(Vector3.up, forward);
            return mapping;
        }

        private static void AssertSameRotation(Quaternion expected, Quaternion actual, string message)
        {
            Assert.Less(Quaternion.Angle(expected, actual), 0.05f, message);
        }

        private MeshFilter AttachHighlightFilter()
        {
            MeshFilter filter = _go.AddComponent<MeshFilter>();
            typeof(BasisSeat)
                .GetField("_seatHighlightMeshFilter", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_seat, filter);
            return filter;
        }

        private void DestroyHighlightMesh()
        {
            typeof(BasisSeat)
                .GetMethod("DestroyHighlightMesh", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_seat, null);
        }

        private void RegenerateHighlight()
        {
            _seat.Back = _seat.Back;
        }

        [Test]
        public void EditMode_RegeneratingTheHighlightReusesOneMesh()
        {
            MeshFilter filter = AttachHighlightFilter();
            RegenerateHighlight();
            Mesh mesh = filter.sharedMesh;
            Assert.IsNotNull(mesh);
            Vector3 footVertex = mesh.vertices[0];

            _seat.Foot = new Vector3(0f, -0.8f, 0.25f);

            Assert.AreSame(mesh, filter.sharedMesh, "an inspector edit must not make a new mesh");
            Assert.AreNotEqual(footVertex, mesh.vertices[0], "the mesh must follow the new points");
            Assert.AreEqual(18, mesh.vertexCount);
        }

        [Test]
        public void EditMode_CleanupDestroysTheHighlightMesh()
        {
            MeshFilter filter = AttachHighlightFilter();
            RegenerateHighlight();
            Mesh mesh = filter.sharedMesh;
            Assert.IsNotNull(mesh);

            DestroyHighlightMesh();

            Assert.IsTrue(mesh == null, "The highlight mesh must not outlive its seat.");
        }

        [Test]
        public void EditMode_ChangingAPointWithoutAHighlightMakesNoMesh()
        {
            _seat.Back = new Vector3(0f, 0f, -0.3f);
            _seat.Foot = new Vector3(0f, -0.6f, 0.25f);
            _seat.Knee = new Vector3(0f, 0f, 0.3f);
            _seat.SpineAngleDegrees = 100.0;
            Assert.IsFalse(_go.TryGetComponent(out MeshFilter _));
        }

        [Test]
        public void EditMode_CleanupWithoutAHighlightIsSafeTwice()
        {
            Assert.DoesNotThrow(DestroyHighlightMesh);
            Assert.DoesNotThrow(DestroyHighlightMesh);
        }

        [Test]
        public void RemotePin_LandsTheHipsBoneWhereTheLocalOverrideDoes()
        {
            _go.transform.SetPositionAndRotation(new Vector3(2f, 0.5f, -3f), Quaternion.Euler(4f, 37f, -6f));
            _seat.OccupantRotationRangeDegrees = 360f;
            _seat.SetOccupantYaw(50f);

            foreach (Quaternion hipsRest in new[] { Quaternion.identity, Quaternion.Euler(-90f, 0f, 0f), Quaternion.Euler(0f, 90f, 90f) })
            {
                foreach (Vector3 forward in new[] { Vector3.forward, Vector3.back, Vector3.right })
                {
                    BasisTransformMapping rig = Rig(hipsRest, forward);
                    Assert.IsTrue(_seat.TryCalculateOccupantPose(rig, Vector3.one, _seat.OccupantYawDegrees, out Vector3 remotePos, out Quaternion remoteRoot, out Quaternion remoteRest));

                    Assert.IsTrue(BasisSeatFitLegs.TryFromMapping(rig.TposeFromRoot, Vector3.one, 0f, out BasisSeatFitLegs legs));
                    BasisSeatFitResult fit = _seat.SolveFit(legs);
                    BasisSeatFit.ComposeHipsWorld(_go.transform.localToWorldMatrix, _go.transform.rotation, _seat.SpineRotation, fit.Back, _seat.OccupantYawDegrees, out Vector3 localPos, out Quaternion seated, out _);
                    Quaternion basis = Quaternion.LookRotation(forward, Vector3.up);
                    BasisSeatFit.ComposeSeatedRoot(localPos, seated, basis, rig.TposeFromRoot[HumanBodyBones.Hips].position, out _, out Quaternion localRoot);

                    Assert.Less((remotePos - localPos).magnitude, 1e-4f);
                    AssertSameRotation(localRoot * hipsRest, remoteRoot * remoteRest, $"rest {hipsRest.eulerAngles} forward {forward}");
                }
            }
        }

        [Test]
        public void TheOldRawSeatRotationPin_WasWrongOnRigsWithARotatedHipsRest()
        {
            Quaternion hipsRest = Quaternion.Euler(-90f, 0f, 0f);
            BasisTransformMapping rig = Rig(hipsRest, Vector3.forward);
            Assert.IsTrue(_seat.TryCalculateOccupantPose(rig, Vector3.one, 0f, out _, out Quaternion root, out Quaternion rest));
            Assert.IsTrue(BasisSeatFitLegs.TryFromMapping(rig.TposeFromRoot, Vector3.one, 0f, out BasisSeatFitLegs legs));
            _seat.CalculateSeatPositionRotation(legs, out Quaternion oldPin, out _);

            Assert.Greater(Quaternion.Angle(oldPin, root * rest), 80f, "this rig must tell the old pin apart from the new one or the parity test above proves nothing");
        }

        [Test]
        public void MissingTposeData_ReportsFailureInsteadOfThrowing()
        {
            Assert.IsFalse(_seat.TryCalculateOccupantPose(null, Vector3.one, 0f, out _, out _, out _));
            Assert.IsFalse(_seat.TryCalculateOccupantPose(new BasisTransformMapping(), Vector3.one, 0f, out _, out _, out _));
        }

        [Test]
        public void TheShinFollowsTheSeatOnReclinedSeats()
        {
            Vector3 restDown = Vector3.down;
            foreach (float spineAngle in new[] { 80f, 90f, 110f, 130f, 150f, 170f })
            {
                Assert.IsTrue(BasisSeatFit.BuildFrame(new Vector3(0f, 0f, -0.25f), new Vector3(0f, -0.5f, 0.25f), new Vector3(0f, 0f, 0.25f), spineAngle, out BasisSeatFitFrame frame));
                Vector3 shinInSpineFrame = Quaternion.Inverse(frame.SpineRotation) * (frame.Foot - frame.Knee);
                Quaternion delta = BasisSeatFit.LegDelta(restDown, Vector3.forward, shinInSpineFrame, Vector3.right);

                Vector3 worldShin = frame.SpineRotation * (delta * restDown);
                Assert.Less(Vector3.Angle(worldShin, frame.Foot - frame.Knee), 0.05f, $"spine angle {spineAngle} lost the authored shin");
                if (spineAngle > 90f)
                {
                    Assert.Less(shinInSpineFrame.z, 0f, $"spine angle {spineAngle}: the old forward hemisphere flip mirrored this shin, so it must stay a case the test covers");
                }
            }
        }

        [Test]
        public void LegDelta_NeverTwistsTheKneeHinge()
        {
            for (float angle = 0f; angle < 360f; angle += 7f)
            {
                Vector3 desired = Quaternion.AngleAxis(angle, Vector3.right) * Vector3.down;
                Quaternion delta = BasisSeatFit.LegDelta(Vector3.down, Vector3.forward, desired, Vector3.right);
                Assert.Less(Vector3.Angle(delta * Vector3.down, desired), 0.05f, $"aim lost at {angle}");
                Assert.Less(Vector3.Angle(delta * Vector3.right, Vector3.right), 0.05f, $"the knee hinge twisted at {angle}");
            }
        }

        [Test]
        public void LegDelta_MatchesTheSquareChair()
        {
            AssertSameRotation(Quaternion.identity, BasisSeatFit.LegDelta(Vector3.down, Vector3.forward, Vector3.down, Vector3.right), "a hanging shin should not move");
            Quaternion thigh = BasisSeatFit.LegDelta(Vector3.down, Vector3.forward, Vector3.forward, Vector3.right);
            Assert.Less(Vector3.Angle(thigh * Vector3.forward, Vector3.up), 0.05f, "a level thigh should point the kneecap up");
        }

        [Test]
        public void AScaledSeat_FitsTheBodyInWorldUnits()
        {
            var legs = new BasisSeatFitLegs { UpperLegLength = 0.42f, LowerLegLength = 0.40f, FootThickness = 0.08f };
            BasisSeatFitResult unscaled = _seat.SolveFit(legs);
            _go.transform.localScale = Vector3.one * 2f;
            BasisSeatFitResult scaled = _seat.SolveFit(new BasisSeatFitLegs { UpperLegLength = 0.84f, LowerLegLength = 0.80f, FootThickness = 0.16f });
            Assert.Less((unscaled.Back - scaled.Back).magnitude, 1e-5f);
        }

        [Test]
        public void TheSoleIsSubtractedFromTheFootThickness()
        {
            BasisTransformMapping rig = Rig(Quaternion.identity, Vector3.forward);
            Assert.IsTrue(BasisSeatFitLegs.TryFromMapping(rig.TposeFromRoot, Vector3.one * 2f, 0f, out BasisSeatFitLegs flat));
            Assert.IsTrue(BasisSeatFitLegs.TryFromMapping(rig.TposeFromRoot, Vector3.one * 2f, 0.03f, out BasisSeatFitLegs raised));
            Assert.AreEqual(0.06f, flat.FootThickness - raised.FootThickness, 1e-5f);
        }

        [Test]
        public void DesktopLook_TurnsTheBodyWithinTheRangeAndLeavesTheRestToTheHead()
        {
            Assert.AreEqual(0f, _seat.TakeOccupantLookYaw(30f), 1e-4f, "a held seat must leave all of the look on the head");

            _seat.OccupantRotationRangeDegrees = 90f;
            Assert.AreEqual(30f, _seat.TakeOccupantLookYaw(30f), 1e-3f);
            Assert.AreEqual(30f, _seat.OccupantYawDegrees, 1e-3f);
            Assert.AreEqual(15f, _seat.TakeOccupantLookYaw(40f), 1e-3f, "the limit must stop the body and leave the remainder on the head");
            Assert.AreEqual(45f, _seat.OccupantYawDegrees, 1e-3f);
            Assert.AreEqual(0f, _seat.TakeOccupantLookYaw(25f), 1e-3f, "a body at its limit must stay there");

            _seat.ResetOccupantYaw();
            _seat.OccupantRotationRangeDegrees = 360f;
            _seat.OccupantRotationSnapDegrees = 45f;
            Assert.AreEqual(0f, _seat.TakeOccupantLookYaw(20f), 1e-3f, "under half a step must stay on the head");
            Assert.AreEqual(45f, _seat.TakeOccupantLookYaw(30f), 1e-3f, "past half a step must turn one whole step");
            Assert.AreEqual(0f, _seat.TakeOccupantLookYaw(-15f), 1e-3f, "the remainder from that step must not turn the body back");
        }

        [Test]
        public void TheLockMode_ResolvesFromTheTracker()
        {
            Assert.IsTrue(BasisSeatFit.IsLocked(BasisSeatLockMode.Locked, false));
            Assert.IsTrue(BasisSeatFit.IsLocked(BasisSeatLockMode.Locked, true), "Locked must hold a tracked player too");
            Assert.IsTrue(BasisSeatFit.IsLocked(BasisSeatLockMode.UnlessTracked, false), "no tracker means the seat keeps the body part");
            Assert.IsFalse(BasisSeatFit.IsLocked(BasisSeatLockMode.UnlessTracked, true), "a tracker takes the body part back");
            Assert.IsFalse(BasisSeatFit.IsLocked(BasisSeatLockMode.Free, false));
            Assert.IsFalse(BasisSeatFit.IsLocked(BasisSeatLockMode.Free, true));
        }

        [Test]
        public void AFreshSeat_KeepsTheChairBehaviour()
        {
            Assert.AreEqual(BasisSeatLockMode.Locked, _seat.HipsLock, "existing seats must keep pinning the hips, trackers or not");
            Assert.AreEqual(BasisSeatLockMode.Locked, _seat.LeftFootLock, "existing seats must keep posing the left leg, trackers or not");
            Assert.AreEqual(BasisSeatLockMode.Locked, _seat.RightFootLock, "existing seats must keep posing the right leg, trackers or not");
            Assert.AreEqual(0f, _seat.OccupantOffsetLimit);
            Assert.IsFalse(_seat.SetOccupantOffset(Vector3.up), "a limit of 0 must hold the occupant on the fit point");
            Assert.AreEqual(Vector3.zero, _seat.OccupantOffset);
        }

        [Test]
        public void TheOffset_ClampsToItsLimitAndMovesTheFit()
        {
            var legs = new BasisSeatFitLegs { UpperLegLength = 0.42f, LowerLegLength = 0.40f, FootThickness = 0.08f };
            _seat.CalculateSeatPositionRotation(legs, out _, out Vector3 before);
            _seat.OccupantOffsetLimit = 0.5f;
            Assert.IsTrue(_seat.SetOccupantOffset(Vector3.up * 3f));
            Assert.AreEqual(0.5f, _seat.OccupantOffset.magnitude, 1e-5f);
            _seat.CalculateSeatPositionRotation(legs, out _, out Vector3 after);
            Assert.Less((after - before - Vector3.up * 0.5f).magnitude, 1e-5f);

            Assert.IsFalse(_seat.SetOccupantOffset(new Vector3(float.NaN, 0f, 0f)));
            _seat.OccupantOffsetLimit = 0.1f;
            Assert.AreEqual(0.1f, _seat.OccupantOffset.magnitude, 1e-5f, "lowering the limit must pull the occupant in");
            _seat.ResetOccupantOffset();
            Assert.AreEqual(Vector3.zero, _seat.OccupantOffset);
        }

        [Test]
        public void ThePacket_CarriesTheBodyAndKeepsTheOldBytesInPlace()
        {
            var sender = _go.AddComponent<BasisSeatSync>();
            sender.Seat = _seat;
            _seat.OccupantOffsetLimit = 1f;
            _seat.SetOccupantOffset(new Vector3(0.1f, 0.2f, -0.3f));
            _seat.HipsLock = BasisSeatLockMode.Free;
            byte[] packet = sender.CreateSeatPacket(true);
            Assert.AreEqual(BasisSeatSync.BodyPacketLength, packet.Length);
            Assert.AreEqual(1, packet[7] & 1, "the free hips flag belongs to the tail");
            Assert.AreEqual(7, sender.CreateSeatPacket(false).Length, "a release has no body to describe");

            var receiverObject = new GameObject("receiver");
            try
            {
                var receiverSeat = receiverObject.AddComponent<BasisSeat>();
                var receiver = receiverObject.AddComponent<BasisSeatSync>();
                receiver.Seat = receiverSeat;
                packet[1] = 1;
                receiver.OnNetworkMessage(5, packet, DeliveryMethod.ReliableOrdered);
                Assert.IsTrue(receiver.HasUser(out ushort occupant));
                Assert.AreEqual(5, occupant);
                Assert.Less((receiverSeat.OccupantOffset - new Vector3(0.1f, 0.2f, -0.3f)).magnitude, 1e-3f);

                byte[] shortPacket = new byte[7];
                System.Array.Copy(packet, shortPacket, 7);
                receiver.OnNetworkMessage(5, shortPacket, DeliveryMethod.ReliableOrdered);
                Assert.Less((receiverSeat.OccupantOffset - new Vector3(0.1f, 0.2f, -0.3f)).magnitude, 1e-3f, "an old sender's short packet must not clear the tail");
            }
            finally
            {
                Object.DestroyImmediate(receiverObject);
            }
        }
    }
}
