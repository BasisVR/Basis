using System.Reflection;
using Basis.Scripts.BasisSdk.Interactions;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Interactions
{
    public class BasisPickupAxisConstraintTests
    {
        private readonly MethodInfo _closestBoundsOffset = typeof(BasisPickupInteractable).GetMethod("ComputeClosestBoundsOffset", BindingFlags.NonPublic | BindingFlags.Instance);

        private GameObject _parent;
        private GameObject _pickup;

        [SetUp]
        public void SetUp()
        {
            Assert.IsNotNull(_closestBoundsOffset, "BasisPickupInteractable.ComputeClosestBoundsOffset moved");
        }

        [TearDown]
        public void TearDown()
        {
            if (_pickup != null) Object.DestroyImmediate(_pickup);
            if (_parent != null) Object.DestroyImmediate(_parent);
        }

        [Test]
        public void RotatedParent_KeepsWorldRotationWhenAxisIsLocked()
        {
            _parent = new GameObject("rotated-parent");
            _parent.transform.SetPositionAndRotation(new Vector3(2f, 0f, -3f), Quaternion.Euler(0f, 180f, 0f));

            _pickup = new GameObject("pickup");
            _pickup.transform.SetParent(_parent.transform, false);
            _pickup.transform.SetLocalPositionAndRotation(new Vector3(0f, 1f, 0.5f), Quaternion.Euler(10f, 20f, 30f));

            Vector3 startLocalPosition = _pickup.transform.localPosition;
            Quaternion expectedWorldRotation = _pickup.transform.rotation;
            Vector3 proposedPosition = _parent.transform.TransformPoint(startLocalPosition + new Vector3(0.1f, 2f, 3f));
            Quaternion proposedRotation = Quaternion.identity;

            BasisPickupInteractable.ConstrainPoseToAxis(_pickup.transform, BasisAxisType.X, startLocalPosition,
                0.2f, 0.2f, ref proposedPosition, ref proposedRotation);

            Vector3 constrainedLocalPosition = _parent.transform.InverseTransformPoint(proposedPosition);
            Assert.That(constrainedLocalPosition.x, Is.EqualTo(startLocalPosition.x + 0.1f).Within(0.0001f));
            Assert.That(constrainedLocalPosition.y, Is.EqualTo(startLocalPosition.y).Within(0.0001f));
            Assert.That(constrainedLocalPosition.z, Is.EqualTo(startLocalPosition.z).Within(0.0001f));
            Assert.That(Quaternion.Angle(proposedRotation, expectedWorldRotation), Is.LessThan(0.001f));
        }

        [Test]
        public void FarChildCollider_FollowsTheHandAlongTheAxis()
        {
            BasisPickupInteractable pickup = MakePickup(new Vector3(0f, 1f, 0f), Vector3.zero, new Vector3(0f, 0f, 3f));
            Vector3 start = _pickup.transform.localPosition;
            Vector3 hand = new Vector3(0f, 1f, 2.8f);
            BasisParentConstraint hold = Grab(pickup, hand, Quaternion.identity, true, out Vector3 grabPoint);
            Quaternion turned = Quaternion.Euler(0f, 15f, 0f);

            Vector3 moved = Hold(hold, grabPoint, Quaternion.identity, hand, turned, start);
            Assert.That(moved.x - start.x, Is.EqualTo(0f).Within(0.001f), "turning the hand in place");

            moved = Hold(hold, grabPoint, Quaternion.identity, hand + new Vector3(0.3f, 0f, 0f), turned, start);
            Assert.That(moved.x - start.x, Is.EqualTo(0.3f).Within(0.001f), "moving the hand along the axis");
        }

        [Test]
        public void DistantGrab_FollowsTheAimNotTheOrigin()
        {
            BasisPickupInteractable pickup = MakePickup(new Vector3(0f, 1f, 3f), new Vector3(0f, 0f, -3f));
            Vector3 start = _pickup.transform.localPosition;
            Vector3 eye = new Vector3(0f, 1f, -2f);
            BasisParentConstraint hold = Grab(pickup, eye, Quaternion.identity, false, out Vector3 grabPoint);
            Vector3 grabbed = _pickup.transform.position + _pickup.transform.rotation * grabPoint;
            Quaternion turned = Quaternion.Euler(0f, 10f, 0f);

            Vector3 moved = Hold(hold, grabPoint, Quaternion.identity, eye, turned, start);

            Vector3 aimed = eye + turned * (grabbed - eye);
            Assert.That(moved.x - start.x, Is.EqualTo(aimed.x - grabbed.x).Within(0.001f));
        }

        [Test]
        public void DesktopRotate_DoesNotSlideTheLockedPickup()
        {
            BasisPickupInteractable pickup = MakePickup(new Vector3(0f, 1f, 0f), Vector3.zero, new Vector3(0f, 0f, 3f));
            Vector3 start = _pickup.transform.localPosition;
            Vector3 hand = new Vector3(0f, 1f, 2.8f);
            BasisParentConstraint hold = Grab(pickup, hand, Quaternion.identity, true, out Vector3 grabPoint);

            hold.sources[0].rotationOffset = Quaternion.Euler(0f, 40f, 0f) * hold.sources[0].rotationOffset;
            Vector3 moved = Hold(hold, grabPoint, Quaternion.identity, hand, Quaternion.identity, start);

            Assert.That(moved.x - start.x, Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void TravelPastTheLimit_StopsAtTheLimit()
        {
            _pickup = new GameObject("slider");
            Vector3 start = new Vector3(0f, 1f, 0f);
            _pickup.transform.localPosition = start + new Vector3(0.1f, 0f, 0f);

            Assert.That(ConstrainX(start + new Vector3(0.5f, 0f, 0f), start).x, Is.EqualTo(start.x + 0.2f).Within(0.0001f), "past the positive limit");
            Assert.That(ConstrainX(start - new Vector3(0.3f, 0f, 0f), start).x, Is.EqualTo(start.x).Within(0.0001f), "past the negative limit");
            Assert.That(ConstrainX(start, start).x, Is.EqualTo(start.x).Within(0.0001f), "exactly at the start");
        }

        private BasisPickupInteractable MakePickup(Vector3 position, params Vector3[] cubes)
        {
            _pickup = new GameObject("pickup");
            _pickup.transform.position = position;
            foreach (Vector3 cube in cubes)
            {
                var child = new GameObject("cube");
                child.transform.SetParent(_pickup.transform, false);
                child.transform.localPosition = cube;
                child.AddComponent<BoxCollider>().size = Vector3.one * 0.2f;
            }
            return _pickup.AddComponent<BasisPickupInteractable>();
        }

        private BasisParentConstraint Grab(BasisPickupInteractable pickup, Vector3 handPosition, Quaternion handRotation, bool lerpToHand, out Vector3 grabPoint)
        {
            pickup.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            Vector3 offsetPosition = lerpToHand
                ? (Vector3)_closestBoundsOffset.Invoke(pickup, new object[] { handPosition, handRotation, position })
                : Quaternion.Inverse(handRotation) * (position - handPosition);
            Quaternion offsetRotation = Quaternion.Inverse(handRotation) * rotation;
            grabPoint = lerpToHand
                ? -(Quaternion.Inverse(offsetRotation) * offsetPosition)
                : Quaternion.Inverse(rotation) * (NearestSurfacePoint(pickup, handPosition) - position);

            var hold = new BasisParentConstraint
            {
                sources = new BasisConstraintSourceData[] { new() { weight = 1f } },
                Enabled = true,
                GlobalWeight = 1f,
            };
            hold.SetRestPositionAndRotation(position, rotation);
            hold.SetOffsetPositionAndRotation(0, offsetPosition, offsetRotation);
            return hold;
        }

        private Vector3 Hold(BasisParentConstraint hold, Vector3 grabPoint, Quaternion grabHandRotation, Vector3 handPosition, Quaternion handRotation, Vector3 startLocalPosition)
        {
            hold.UpdateSourcePositionAndRotation(0, handPosition, handRotation);
            Assert.IsTrue(hold.Evaluate(out Vector3 position, out Quaternion rotation), "the hold must evaluate");
            Transform pickup = _pickup.transform;
            position = BasisPickupInteractable.FollowGrabPoint(position, pickup.rotation * grabPoint,
                handRotation * Quaternion.Inverse(grabHandRotation), hold.GlobalWeight);
            BasisPickupInteractable.ConstrainPoseToAxis(pickup, BasisAxisType.X, startLocalPosition, 1f, 1f, ref position, ref rotation);
            pickup.SetPositionAndRotation(position, rotation);
            return position;
        }

        private Vector3 ConstrainX(Vector3 proposed, Vector3 start)
        {
            Quaternion rotation = Quaternion.identity;
            BasisPickupInteractable.ConstrainPoseToAxis(_pickup.transform, BasisAxisType.X, start, 0f, 0.2f, ref proposed, ref rotation);
            return proposed;
        }

        private static Vector3 NearestSurfacePoint(BasisPickupInteractable pickup, Vector3 query)
        {
            Vector3 best = pickup.transform.position;
            float bestSq = float.MaxValue;
            foreach (Collider c in pickup.GetColliders())
            {
                Vector3 p = c.ClosestPoint(query);
                float d = (p - query).sqrMagnitude;
                if (d < bestSq)
                {
                    bestSq = d;
                    best = p;
                }
            }
            return best;
        }
    }
}
