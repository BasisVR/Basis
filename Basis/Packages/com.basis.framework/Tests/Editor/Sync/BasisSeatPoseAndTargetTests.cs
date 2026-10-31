using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Common;
using NUnit.Framework;
using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Basis.Tests.Sync
{
    public sealed class BasisSeatPoseAndTargetTests
    {
        private static readonly BasisSeatPose[] AllPoses = (BasisSeatPose[])Enum.GetValues(typeof(BasisSeatPose));
        private static readonly Vector3 Thigh = new Vector3(0f, 0f, 0.5f);
        private static readonly Vector3 Shin = new Vector3(0f, -0.5f, 0f);

        private GameObject _go;
        private BasisSeat _seat;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject(nameof(BasisSeatPoseAndTargetTests));
            _seat = _go.AddComponent<BasisSeat>();
            _seat.SetPoints(new Vector3(0f, 0f, -0.25f), new Vector3(0f, -0.5f, 0.25f), new Vector3(0f, 0f, 0.25f), 90.0);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        private static BasisSeatLegPose Pose(BasisSeatPose pose, bool isLeft)
        {
            return BasisSeatFit.SolveLegPose(pose, Thigh, Shin, Quaternion.identity, isLeft);
        }

        private static BasisTransformMapping Rig()
        {
            var mapping = new BasisTransformMapping();
            mapping.TposeFromRoot[HumanBodyBones.Hips] = new BasisCalibratedCoords(new Vector3(0f, 0.95f, 0f), Quaternion.Euler(0f, 90f, 0f));
            mapping.TposeFromRoot[HumanBodyBones.LeftUpperLeg] = new BasisCalibratedCoords(new Vector3(-0.1f, 0.9f, 0f), Quaternion.identity);
            mapping.TposeFromRoot[HumanBodyBones.LeftLowerLeg] = new BasisCalibratedCoords(new Vector3(-0.1f, 0.48f, 0f), Quaternion.identity);
            mapping.TposeFromRoot[HumanBodyBones.LeftFoot] = new BasisCalibratedCoords(new Vector3(-0.1f, 0.08f, 0f), Quaternion.identity);
            mapping.TposeFromRoot[HumanBodyBones.LeftToes] = new BasisCalibratedCoords(new Vector3(-0.1f, 0.02f, 0.12f), Quaternion.identity);
            mapping.AvatarForwards = Vector3.forward;
            mapping.AvatarUpwards = Vector3.up;
            mapping.AvatarRightwards = Vector3.right;
            return mapping;
        }

        [Test]
        public void Seated_KeepsTheFittedLegsOnBothSides()
        {
            foreach (bool isLeft in new[] { true, false })
            {
                BasisSeatLegPose pose = Pose(BasisSeatPose.Seated, isLeft);
                Assert.Less((pose.UpperDir - Thigh.normalized).magnitude, 1e-5f);
                Assert.Less((pose.LowerDir - Shin.normalized).magnitude, 1e-5f);
                Assert.AreEqual(0f, pose.FootPitchDegrees, "existing seats must not change the foot");
            }
        }

        [Test]
        public void Seated_MatchesTheOldLegDelta()
        {
            Vector3 rest = Vector3.down;
            BasisSeatLegPose pose = Pose(BasisSeatPose.Seated, true);
            Quaternion expected = BasisSeatFit.LegDelta(rest, Vector3.forward, pose.LowerDir, Vector3.right);
            Quaternion actual = BasisSeatFit.LegDeltaWithPole(rest, Vector3.forward, pose.LowerDir, pose.LowerPole);
            Assert.Less(Quaternion.Angle(expected, actual), 0.01f);
        }

        [Test]
        public void EveryPose_GivesUnitDirectionsAndFinitePoles()
        {
            foreach (BasisSeatPose value in AllPoses)
            {
                foreach (bool isLeft in new[] { true, false })
                {
                    BasisSeatLegPose pose = Pose(value, isLeft);
                    Assert.AreEqual(1f, pose.UpperDir.magnitude, 1e-4f, $"{value} thigh");
                    Assert.AreEqual(1f, pose.LowerDir.magnitude, 1e-4f, $"{value} shin");
                    Assert.Greater(pose.UpperPole.sqrMagnitude, 1e-3f, $"{value} thigh pole");
                    Assert.Greater(pose.LowerPole.sqrMagnitude, 1e-3f, $"{value} shin pole");
                }
            }
        }

        [Test]
        public void EveryPose_MirrorsAcrossTheSeat()
        {
            foreach (BasisSeatPose value in AllPoses)
            {
                BasisSeatLegPose left = Pose(value, true);
                BasisSeatLegPose right = Pose(value, false);
                Assert.Less((left.UpperDir - Vector3.Scale(right.UpperDir, new Vector3(-1f, 1f, 1f))).magnitude, 1e-4f, $"{value} thigh");
                Vector3 mirroredShin = Vector3.Scale(right.LowerDir, new Vector3(-1f, 1f, 1f));
                float allowed = value == BasisSeatPose.CrossLegged ? 0.15f : 1e-4f;
                Assert.Less((left.LowerDir - mirroredShin).magnitude, allowed, $"{value} shin");
            }
        }

        [Test]
        public void EveryPose_FollowsTheSeat()
        {
            Quaternion tilt = Quaternion.Euler(20f, 130f, -15f);
            foreach (BasisSeatPose value in AllPoses)
            {
                BasisSeatLegPose flat = Pose(value, true);
                BasisSeatLegPose tilted = BasisSeatFit.SolveLegPose(value, tilt * Thigh, tilt * Shin, tilt, true);
                Assert.Less((tilt * flat.UpperDir - tilted.UpperDir).magnitude, 1e-4f, $"{value} thigh");
                Assert.Less((tilt * flat.LowerDir - tilted.LowerDir).magnitude, 1e-4f, $"{value} shin");
            }
        }

        [Test]
        public void KneesApart_SplaysThighsOutAndKeepsShins()
        {
            Assert.Less(Pose(BasisSeatPose.KneesApart, true).UpperDir.x, -0.3f);
            Assert.Greater(Pose(BasisSeatPose.KneesApart, false).UpperDir.x, 0.3f);
            Assert.Less((Pose(BasisSeatPose.KneesApart, true).LowerDir - Shin.normalized).magnitude, 1e-5f);
        }

        [Test]
        public void CrossLegged_ShinsCrossToTheOtherSide()
        {
            BasisSeatLegPose left = Pose(BasisSeatPose.CrossLegged, true);
            BasisSeatLegPose right = Pose(BasisSeatPose.CrossLegged, false);
            Assert.Less(left.UpperDir.x, -0.5f);
            Assert.Greater(left.LowerDir.x, 0.8f, "the left shin must cross toward the right");
            Assert.Greater(right.UpperDir.x, 0.5f);
            Assert.Less(right.LowerDir.x, -0.8f, "the right shin must cross toward the left");
            Assert.Greater(left.LowerDir.y, right.LowerDir.y, "one shin rests over the other");
        }

        [Test]
        public void LegsOut_StraightensTheKnee()
        {
            BasisSeatLegPose pose = Pose(BasisSeatPose.LegsOut, true);
            Assert.Less(Vector3.Angle(pose.UpperDir, pose.LowerDir), 10f);
        }

        [Test]
        public void Dangling_HangsTheShinAlongTheSpine()
        {
            BasisSeatLegPose pose = BasisSeatFit.SolveLegPose(BasisSeatPose.Dangling, Thigh, new Vector3(0f, -0.2f, 0.4f), Quaternion.identity, true);
            Assert.Less((pose.LowerDir - Vector3.down).magnitude, 1e-5f);
            Assert.Greater(pose.FootPitchDegrees, 0f);
        }

        [Test]
        public void Kneeling_DropsTheThighAndFoldsTheShinBack()
        {
            BasisSeatLegPose pose = Pose(BasisSeatPose.Kneeling, true);
            Assert.Less(pose.UpperDir.y, -0.5f);
            Assert.Less((pose.LowerDir - Vector3.back).magnitude, 1e-5f);
            Assert.Greater(pose.FootPitchDegrees, 0f);
            Quaternion shin = BasisSeatFit.LegDeltaWithPole(Vector3.down, Vector3.forward, pose.LowerDir, pose.LowerPole);
            Assert.Less((shin * Vector3.forward - Vector3.down).magnitude, 1e-4f, "the kneecap must face the seat");
        }

        [Test]
        public void LegDeltaWithPole_LandsTheBoneOnDirectionAndPole()
        {
            Quaternion delta = BasisSeatFit.LegDeltaWithPole(Vector3.down, Vector3.forward, Vector3.right, Vector3.up);
            Assert.Less((delta * Vector3.down - Vector3.right).magnitude, 1e-4f);
            Assert.Less((delta * Vector3.forward - Vector3.up).magnitude, 1e-4f);
        }

        [Test]
        public void LegDeltaWithPole_FallsBackWithoutAPole()
        {
            Quaternion delta = BasisSeatFit.LegDeltaWithPole(Vector3.down, Vector3.forward, Vector3.right, Vector3.zero);
            Assert.Less((delta * Vector3.down - Vector3.right).magnitude, 1e-4f);
            Assert.AreEqual(Quaternion.identity, BasisSeatFit.LegDeltaWithPole(Vector3.zero, Vector3.forward, Vector3.right, Vector3.up));
        }

        [Test]
        public void OnlyKneeling_MovesTheHips()
        {
            foreach (BasisSeatPose value in AllPoses)
            {
                float lift = BasisSeatFit.PosePelvisLift(value, 0.4f);
                if (value == BasisSeatPose.Kneeling)
                {
                    Assert.AreEqual(0.4f * Mathf.Sin(BasisSeatFit.KneelingThighDegrees * Mathf.Deg2Rad), lift, 1e-5f);
                }
                else
                {
                    Assert.AreEqual(0f, lift, value.ToString());
                }
            }
        }

        [Test]
        public void Kneeling_RaisesTheFitAlongTheSpineInSeatUnits()
        {
            var legs = new BasisSeatFitLegs { UpperLegLength = 0.42f, LowerLegLength = 0.40f, FootThickness = 0.08f };
            _go.transform.localScale = Vector3.one * 2f;
            Vector3 before = _seat.SolveFit(legs).Back;
            _seat.Pose = BasisSeatPose.Kneeling;
            Vector3 after = _seat.SolveFit(legs).Back;
            float expected = 0.21f * Mathf.Sin(BasisSeatFit.KneelingThighDegrees * Mathf.Deg2Rad);
            Assert.Less((after - before - Vector3.up * expected).magnitude, 1e-4f);
        }

        [Test]
        public void Kneeling_LandsTheRemotePinWhereTheLocalFitDoes()
        {
            _go.transform.SetPositionAndRotation(new Vector3(2f, 0.5f, -3f), Quaternion.Euler(4f, 37f, -6f));
            _seat.Pose = BasisSeatPose.Kneeling;
            BasisTransformMapping mapping = Rig();
            BasisSeatFitLegs.TryFromMapping(mapping.TposeFromRoot, Vector3.one, _seat.NetworkedSoleHeight, out BasisSeatFitLegs legs);
            _seat.CalculateSeatPositionRotation(legs, out _, out Vector3 local);
            Assert.IsTrue(_seat.TryCalculateOccupantPose(mapping, Vector3.one, 0f, out Vector3 remote, out _, out _));
            Assert.Less((remote - local).magnitude, 1e-4f);
        }

        [Test]
        public void AFreshSeat_ControlsNoExtraLimbs()
        {
            Assert.AreEqual(BasisSeatPose.Seated, _seat.Pose, "existing seats must keep their leg pose");
            Assert.IsNull(_seat.LeftHandTarget);
            Assert.IsNull(_seat.RightHandTarget);
            Assert.IsNull(_seat.LeftFootTarget);
            Assert.IsNull(_seat.RightFootTarget);
            Assert.AreEqual(BasisSeatLockMode.UnlessTracked, _seat.LeftHandLock, "a VR controller must keep its hand by default");
            Assert.AreEqual(BasisSeatLockMode.UnlessTracked, _seat.RightHandLock, "a VR controller must keep its hand by default");
        }

        [Test]
        public void TheTargetApi_SetsAndClearsEveryLimb()
        {
            Transform leftGrip = new GameObject("leftGrip").transform;
            Transform rightGrip = new GameObject("rightGrip").transform;
            Transform leftPedal = new GameObject("leftPedal").transform;
            Transform rightPedal = new GameObject("rightPedal").transform;
            leftGrip.SetParent(_go.transform);
            rightGrip.SetParent(_go.transform);
            leftPedal.SetParent(_go.transform);
            rightPedal.SetParent(_go.transform);

            _seat.SetHandTargets(leftGrip, rightGrip);
            _seat.SetFootTargets(leftPedal, rightPedal);
            Assert.AreSame(leftGrip, _seat.LeftHandTarget);
            Assert.AreSame(rightGrip, _seat.RightHandTarget);
            Assert.AreSame(leftPedal, _seat.LeftFootTarget);
            Assert.AreSame(rightPedal, _seat.RightFootTarget);

            _seat.RightFootTarget = null;
            Assert.AreSame(leftPedal, _seat.LeftFootTarget, "feet must be set independently");
            Assert.IsNull(_seat.RightFootTarget);

            _seat.LeftHandTarget = rightGrip;
            Assert.AreSame(rightGrip, _seat.LeftHandTarget);

            _seat.ClearLimbTargets();
            Assert.IsNull(_seat.LeftHandTarget);
            Assert.IsNull(_seat.RightHandTarget);
            Assert.IsNull(_seat.LeftFootTarget);
            Assert.IsNull(_seat.RightFootTarget);
        }

        [Test]
        public void TheHandLock_FollowsTheController()
        {
            Assert.IsTrue(BasisSeatFit.IsLocked(_seat.LeftHandLock, false), "desktop hands go to the target");
            Assert.IsFalse(BasisSeatFit.IsLocked(_seat.LeftHandLock, true), "a VR controller keeps the hand");
            _seat.LeftHandLock = BasisSeatLockMode.Locked;
            Assert.IsTrue(BasisSeatFit.IsLocked(_seat.LeftHandLock, true), "Locked holds the hand in VR as well");
            _seat.RightHandLock = BasisSeatLockMode.Free;
            Assert.IsFalse(BasisSeatFit.IsLocked(_seat.RightHandLock, false), "Free ignores the target");
        }

        [Test]
        public void ALimbTarget_PutsTheBoneInItsTposeWhenAlignedWithTheAvatar()
        {
            Quaternion basis = Quaternion.Euler(0f, 90f, 0f);
            Quaternion rest = Quaternion.Euler(10f, 20f, 30f);
            Quaternion target = Quaternion.Euler(5f, 140f, -12f);
            Quaternion root = target * Quaternion.Inverse(basis);
            Quaternion bone = BasisSeatFit.LimbBoneRotation(target, basis, rest);
            Assert.Less(Quaternion.Angle(root * rest, bone), 0.01f);
        }

        [Test]
        public void ALimbTarget_TurnsTheBoneWithTheTarget()
        {
            Quaternion rest = Quaternion.Euler(10f, 20f, 30f);
            Quaternion turn = Quaternion.Euler(0f, 45f, 0f);
            Quaternion before = BasisSeatFit.LimbBoneRotation(Quaternion.identity, Quaternion.identity, rest);
            Quaternion after = BasisSeatFit.LimbBoneRotation(turn, Quaternion.identity, rest);
            Assert.Less(Quaternion.Angle(turn * before, after), 0.01f);
        }

        [Test]
        public void AFootTarget_PutsTheSoleOnTheTarget()
        {
            Vector3 pedal = new Vector3(1f, 2f, 3f);
            Assert.Less((BasisSeatFit.FootTargetPosition(pedal, Quaternion.identity, 0.08f) - (pedal + Vector3.up * 0.08f)).magnitude, 1e-5f);
            Quaternion tipped = Quaternion.Euler(90f, 0f, 0f);
            Assert.Less((BasisSeatFit.FootTargetPosition(pedal, tipped, 0.08f) - (pedal + Vector3.forward * 0.08f)).magnitude, 1e-5f, "the ankle sits along the pedal's up axis");
        }

        [Test]
        public void IsFinitePose_RejectsBrokenTargets()
        {
            Assert.IsTrue(BasisSeatFit.IsFinitePose(Vector3.one, Quaternion.identity));
            Assert.IsFalse(BasisSeatFit.IsFinitePose(new Vector3(float.NaN, 0f, 0f), Quaternion.identity));
            Assert.IsFalse(BasisSeatFit.IsFinitePose(new Vector3(0f, float.PositiveInfinity, 0f), Quaternion.identity));
            Assert.IsFalse(BasisSeatFit.IsFinitePose(Vector3.zero, new Quaternion(float.NaN, 0f, 0f, 1f)));
        }

        [Test]
        public void ThePose_RaisesTheBodyEventOnlyOnChange()
        {
            int calls = 0;
            _seat.OnOccupantBodyChanged += () => calls++;
            _seat.Pose = BasisSeatPose.Seated;
            Assert.AreEqual(0, calls);
            _seat.Pose = BasisSeatPose.CrossLegged;
            Assert.AreEqual(1, calls);
            _seat.ApplyNetworkedPose(BasisSeatPose.Dangling);
            Assert.AreEqual(1, calls, "a networked pose must not echo back out");
            Assert.AreEqual(BasisSeatPose.Dangling, _seat.Pose);
        }

        [Test]
        public void ANetworkedPose_RejectsUnknownValues()
        {
            _seat.ApplyNetworkedPose((BasisSeatPose)7);
            Assert.AreEqual(BasisSeatPose.Seated, _seat.Pose);
        }

        [Test]
        public void ThePacket_CarriesThePoseBesideTheHipsFlag()
        {
            var sender = _go.AddComponent<BasisSeatSync>();
            sender.Seat = _seat;
            var receiverObject = new GameObject("receiver");
            try
            {
                var receiverSeat = receiverObject.AddComponent<BasisSeat>();
                var receiver = receiverObject.AddComponent<BasisSeatSync>();
                receiver.Seat = receiverSeat;
                byte generation = 1;
                foreach (BasisSeatPose value in AllPoses)
                {
                    foreach (BasisSeatLockMode hips in new[] { BasisSeatLockMode.Locked, BasisSeatLockMode.Free })
                    {
                        _seat.Pose = value;
                        _seat.HipsLock = hips;
                        byte[] packet = sender.CreateSeatPacket(true);
                        Assert.AreEqual(BasisSeatSync.BodyPacketLength, packet.Length, "the pose must not grow the packet");
                        Assert.AreEqual(hips == BasisSeatLockMode.Free ? 1 : 0, packet[7] & 1, "an older client still reads bit 0 as free hips");
                        packet[1] = generation++;
                        receiver.OnNetworkMessage(5, packet, DeliveryMethod.ReliableOrdered);
                        Assert.AreEqual(value, receiverSeat.Pose);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(receiverObject);
            }
        }

        [Test]
        public void AnOldPacket_LeavesThePoseAlone()
        {
            var receiver = _go.AddComponent<BasisSeatSync>();
            receiver.Seat = _seat;
            _seat.Pose = BasisSeatPose.Dangling;
            byte[] shortPacket = new byte[7];
            shortPacket[0] = 1;
            shortPacket[1] = 1;
            receiver.OnNetworkMessage(5, shortPacket, DeliveryMethod.ReliableOrdered);
            Assert.AreEqual(BasisSeatPose.Dangling, _seat.Pose);
        }

        [Test]
        public void Leaving_KeepsTheSeatsPose()
        {
            var receiver = _go.AddComponent<BasisSeatSync>();
            receiver.Seat = _seat;
            _seat.Pose = BasisSeatPose.KneesApart;
            byte[] seated = receiver.CreateSeatPacket(true);
            seated[1] = 1;
            receiver.OnNetworkMessage(5, seated, DeliveryMethod.ReliableOrdered);
            byte[] left = receiver.CreateSeatPacket(false);
            left[1] = 2;
            receiver.OnNetworkMessage(5, left, DeliveryMethod.ReliableOrdered);
            Assert.IsFalse(receiver.HasUser(out _));
            Assert.AreEqual(BasisSeatPose.KneesApart, _seat.Pose, "the pose belongs to the seat, not to who sat in it");
        }
    }
}
