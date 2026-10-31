using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    public sealed class BasisSeatLimbDriveTests
    {
        private static void AssertLandsOn(in Matrix4x4 playerToWorld, Vector3 worldPosition, Quaternion worldRotation, Vector3 offsetPosition, Quaternion offsetRotation)
        {
            BasisSeatFit.ControlIncoming(playerToWorld, worldPosition, worldRotation, offsetPosition, offsetRotation, out Vector3 position, out Quaternion rotation);
            Vector3 outgoing = position + rotation * offsetPosition;
            Quaternion outgoingRotation = rotation * offsetRotation;
            Assert.Less((playerToWorld.MultiplyPoint3x4(outgoing) - worldPosition).magnitude, 1e-4f);
            Assert.Less(Quaternion.Angle(playerToWorld.rotation * outgoingRotation, worldRotation), 0.05f);
        }

        [Test]
        public void ControlIncoming_LandsOnTheTargetWithoutAnOffset()
        {
            Matrix4x4 player = Matrix4x4.TRS(new Vector3(4f, 1f, -2f), Quaternion.Euler(10f, 130f, -5f), Vector3.one);
            AssertLandsOn(player, new Vector3(5f, 2f, -1f), Quaternion.Euler(30f, 40f, 50f), Vector3.zero, Quaternion.identity);
        }

        [Test]
        public void ControlIncoming_UndoesACalibratedTrackerOffset()
        {
            Matrix4x4 player = Matrix4x4.TRS(new Vector3(4f, 1f, -2f), Quaternion.Euler(10f, 130f, -5f), Vector3.one);
            AssertLandsOn(player, new Vector3(5f, 2f, -1f), Quaternion.Euler(30f, 40f, 50f), new Vector3(0.1f, -0.05f, 0.2f), Quaternion.Euler(20f, -70f, 15f));
        }

        [Test]
        public void ControlIncoming_HandlesAScaledPlayer()
        {
            Matrix4x4 player = Matrix4x4.TRS(new Vector3(4f, 1f, -2f), Quaternion.Euler(0f, 90f, 0f), Vector3.one * 1.5f);
            AssertLandsOn(player, new Vector3(5f, 2f, -1f), Quaternion.Euler(30f, 40f, 50f), Vector3.zero, Quaternion.identity);
        }

        [Test]
        public void AnIdleLimb_IsClaimedAndRestored()
        {
            BasisLocalBoneControl control = new BasisLocalBoneControl();
            bool claimed = false;

            BasisLocalSeatDriver.ClaimLimb(control, true, ref claimed);
            Assert.IsTrue(claimed);
            Assert.AreEqual(BasisHasTracked.HasTracker, control.HasTracked);
            Assert.AreEqual(BasisHasRigLayer.HasRigLayer, control.HasRigLayer);

            BasisLocalSeatDriver.ClaimLimb(control, false, ref claimed);
            Assert.IsFalse(claimed);
            Assert.AreEqual(BasisHasTracked.HasNoTracker, control.HasTracked);
            Assert.AreEqual(BasisHasRigLayer.HasNoRigLayer, control.HasRigLayer);
        }

        [Test]
        public void ALimbOwnedByADevice_KeepsItsFlagsAfterATarget()
        {
            BasisLocalBoneControl control = new BasisLocalBoneControl { HasTracked = BasisHasTracked.HasTracker, HasRigLayer = BasisHasRigLayer.HasRigLayer };
            control.DevicesWithRoles.Add("controller");
            bool claimed = false;

            BasisLocalSeatDriver.ClaimLimb(control, true, ref claimed);
            Assert.IsFalse(claimed, "a device limb is written to but never claimed");

            BasisLocalSeatDriver.ClaimLimb(control, false, ref claimed);
            Assert.AreEqual(BasisHasTracked.HasTracker, control.HasTracked);
            Assert.AreEqual(BasisHasRigLayer.HasRigLayer, control.HasRigLayer);
        }

        [Test]
        public void ALimbAnotherSystemHolds_IsLeftAlone()
        {
            BasisLocalBoneControl control = new BasisLocalBoneControl { HasTracked = BasisHasTracked.HasTracker, HasRigLayer = BasisHasRigLayer.HasRigLayer };
            bool claimed = false;

            BasisLocalSeatDriver.ClaimLimb(control, true, ref claimed);
            BasisLocalSeatDriver.ClaimLimb(control, false, ref claimed);

            Assert.IsFalse(claimed);
            Assert.AreEqual(BasisHasTracked.HasTracker, control.HasTracked, "releasing a target must not clear a pickup's hand");
            Assert.AreEqual(BasisHasRigLayer.HasRigLayer, control.HasRigLayer);
        }

        [Test]
        public void ADeviceArrivingMidClaim_TakesTheLimbOver()
        {
            BasisLocalBoneControl control = new BasisLocalBoneControl();
            bool claimed = false;
            BasisLocalSeatDriver.ClaimLimb(control, true, ref claimed);

            control.DevicesWithRoles.Add("tracker");
            BasisLocalSeatDriver.ClaimLimb(control, true, ref claimed);

            Assert.IsFalse(claimed);
            Assert.AreEqual(BasisHasTracked.HasTracker, control.HasTracked, "the device still owns the flags");
        }

        [Test]
        public void AClaimLost_IsReassertedWhileTargeted()
        {
            BasisLocalBoneControl control = new BasisLocalBoneControl();
            bool claimed = false;
            BasisLocalSeatDriver.ClaimLimb(control, true, ref claimed);

            control.HasTracked = BasisHasTracked.HasNoTracker;
            control.HasRigLayer = BasisHasRigLayer.HasNoRigLayer;
            BasisLocalSeatDriver.ClaimLimb(control, true, ref claimed);

            Assert.AreEqual(BasisHasTracked.HasTracker, control.HasTracked);
            Assert.AreEqual(BasisHasRigLayer.HasRigLayer, control.HasRigLayer);
        }

        [Test]
        public void SeatDrives_DoesNotCountItsOwnClaimAsATracker()
        {
            BasisLocalBoneControl control = new BasisLocalBoneControl();
            bool claimed = false;
            BasisLocalSeatDriver.ClaimLimb(control, true, ref claimed);
            Assert.IsTrue(BasisLocalSeatDriver.SeatDrives(control, claimed), "UnlessTracked must not read the seat's own claim as a tracker");

            control.DevicesWithRoles.Add("tracker");
            Assert.IsFalse(BasisLocalSeatDriver.SeatDrives(control, claimed), "a real device counts even during a claim");
        }

        [Test]
        public void ReleasingALimb_ToleratesAMissingControl()
        {
            bool claimed = false;
            Assert.DoesNotThrow(() => BasisLocalSeatDriver.ClaimLimb(null, true, ref claimed));
            Assert.IsFalse(claimed);
        }
    }
}
