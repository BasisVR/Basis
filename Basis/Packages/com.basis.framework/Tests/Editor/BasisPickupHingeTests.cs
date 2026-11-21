using System.Reflection;
using Basis.Scripts.BasisSdk.Interactions;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Interactions
{
    public class BasisPickupHingeTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _parent;
        private GameObject _door;

        [TearDown]
        public void TearDown()
        {
            if (_door != null) Object.DestroyImmediate(_door);
            if (_parent != null) Object.DestroyImmediate(_parent);
        }

        [Test]
        public void DefaultPickup_IsNotLocked()
        {
            _door = new GameObject("pickup");
            BasisPickupInteractable pickup = _door.AddComponent<BasisPickupInteractable>();
            Assert.AreEqual(BasisAxisType.None, pickup.hingeAxis);
            Assert.IsFalse(pickup.IsMotionLocked);
            Assert.IsTrue(pickup.HoldFollowsHand);
        }

        [Test]
        public void HandSwing_TurnsTheDoorAroundItsHinge()
        {
            BasisPickupInteractable door = MakeDoor(null, Vector3.zero, Quaternion.identity, -120f, 0f);

            door.SolveHingePose(new Vector3(0f, 0f, 1f), 0f, out Vector3 position, out Quaternion rotation);

            Assert.That(Vector3.Distance(position, Vector3.zero), Is.LessThan(0.0001f), "the hinge stays put");
            Assert.That(Quaternion.Angle(rotation, Quaternion.Euler(0f, -90f, 0f)), Is.LessThan(0.01f));
        }

        [Test]
        public void HandOffTheAxis_OnlyTheTurnAroundTheAxisCounts()
        {
            BasisPickupInteractable door = MakeDoor(null, Vector3.zero, Quaternion.identity, -120f, 0f);

            door.SolveHingePose(new Vector3(0f, 0.7f, 2f), 0f, out _, out Quaternion rotation);

            Assert.That(Quaternion.Angle(rotation, Quaternion.Euler(0f, -90f, 0f)), Is.LessThan(0.01f));
        }

        [Test]
        public void PastTheLimit_StopsAtTheLimit()
        {
            BasisPickupInteractable door = MakeDoor(null, Vector3.zero, Quaternion.identity, 0f, 90f);

            door.SolveHingePose(new Vector3(0f, 0f, 1f), 0f, out _, out Quaternion rotation);
            Assert.That(Quaternion.Angle(rotation, Quaternion.identity), Is.LessThan(0.01f), "pushed against the closed side");

            door.SolveHingePose(new Vector3(0f, 0f, -1f), 0f, out _, out rotation);
            Assert.That(Quaternion.Angle(rotation, Quaternion.Euler(0f, 90f, 0f)), Is.LessThan(0.01f), "fully open");
        }

        [Test]
        public void SwingingFarPastTheLimit_DoesNotFlipToTheOtherLimit()
        {
            BasisPickupInteractable door = MakeDoor(null, Vector3.zero, Quaternion.identity, 0f, 90f);
            Quaternion rotation = Quaternion.identity;
            for (float hand = 10f; hand <= 230f; hand += 10f)
            {
                door.SolveHingePose(Quaternion.Euler(0f, hand, 0f) * Vector3.right, 0f, out _, out rotation);
            }
            Assert.That(Quaternion.Angle(rotation, Quaternion.Euler(0f, 90f, 0f)), Is.LessThan(0.01f));
        }

        [Test]
        public void RotationSnap_StepsTheHingeAngle()
        {
            BasisPickupInteractable door = MakeDoor(null, Vector3.zero, Quaternion.identity, 0f, 90f);

            door.SolveHingePose(Quaternion.Euler(0f, 37f, 0f) * Vector3.right, 15f, out _, out Quaternion rotation);

            Assert.That(Quaternion.Angle(rotation, Quaternion.Euler(0f, 30f, 0f)), Is.LessThan(0.01f));
        }

        [Test]
        public void RotatedParent_TurnsAroundTheLocalAxis()
        {
            _parent = new GameObject("hatch-frame");
            _parent.transform.SetPositionAndRotation(new Vector3(2f, 1f, -3f), Quaternion.Euler(0f, 180f, 30f));
            Quaternion localStart = Quaternion.Euler(0f, 0f, 90f);
            BasisPickupInteractable hatch = MakeDoor(_parent.transform, new Vector3(0.5f, 0f, 0f), localStart, 0f, 90f, BasisAxisType.X);
            Transform t = hatch.transform;
            Vector3 hinge = t.position;
            Vector3 axis = t.rotation * Vector3.right;
            Vector3 grab = t.position + t.rotation * new Vector3(0f, 0f, 1f);
            Quaternion opened = Quaternion.AngleAxis(60f, axis);

            hatch.SolveHingePose(hinge + opened * (grab - hinge), 0f, out Vector3 position, out Quaternion rotation);

            Assert.That(Vector3.Distance(position, hinge), Is.LessThan(0.0001f));
            Assert.That(Quaternion.Angle(rotation, opened * t.rotation), Is.LessThan(0.01f));
        }

        [Test]
        public void RemoteClamp_PutsTheDoorBackOnItsHinge()
        {
            BasisPickupInteractable door = MakeDoor(null, Vector3.zero, Quaternion.identity, 0f, 90f);
            door.transform.SetLocalPositionAndRotation(new Vector3(0.3f, 0f, 0.1f), Quaternion.Euler(0f, 130f, 0f));

            door.ClampToTravelLimits();

            Assert.That(Vector3.Distance(door.transform.localPosition, Vector3.zero), Is.LessThan(0.0001f));
            Assert.That(Quaternion.Angle(door.transform.localRotation, Quaternion.Euler(0f, 90f, 0f)), Is.LessThan(0.01f));
        }

        private BasisPickupInteractable MakeDoor(Transform parent, Vector3 localPosition, Quaternion localRotation, float min, float max, BasisAxisType axis = BasisAxisType.Y)
        {
            _door = new GameObject("door");
            _door.transform.SetParent(parent, false);
            _door.transform.SetLocalPositionAndRotation(localPosition, localRotation);
            BasisPickupInteractable door = _door.AddComponent<BasisPickupInteractable>();
            door.hingeAxis = axis;
            door.hingeMinAngle = min;
            door.hingeMaxAngle = max;
            Set(door, "_positionAtStart", localPosition);
            Set(door, "_rotationAtStart", localRotation);
            Set(door, "_travelOriginSet", true);
            Set(door, "_grabPoint", axis == BasisAxisType.Y ? Vector3.right : Vector3.forward);
            Set(door, "_hingeHandAngle", door.CurrentHingeAngle());
            return door;
        }

        private static void Set(BasisPickupInteractable pickup, string field, object value)
        {
            FieldInfo info = typeof(BasisPickupInteractable).GetField(field, Private);
            Assert.IsNotNull(info, $"BasisPickupInteractable.{field} moved");
            info.SetValue(pickup, value);
        }
    }
}
