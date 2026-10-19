using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Interactions;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Interactions
{
    public class BasisPickupJointHoldTests
    {
        private const float Step = 0.02f;
        private readonly List<GameObject> _cleanup = new List<GameObject>();
        private readonly List<BasisPickupJointInteractable> _pickups = new List<BasisPickupJointInteractable>();
        private SimulationMode _previousMode;
        private double _time;

        [SetUp]
        public void SetUp()
        {
            _previousMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            _time = 0d;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (BasisPickupJointInteractable pickup in _pickups)
            {
                if (pickup == null) continue;
                pickup.EndJointHold();
                if (pickup.HoldAnchor != null) Object.DestroyImmediate(pickup.HoldAnchor.gameObject);
            }
            _pickups.Clear();
            foreach (GameObject go in _cleanup)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            _cleanup.Clear();
            BasisPickupJointInteractable.OnFixedSimulate?.Invoke(0d);
            Physics.simulationMode = _previousMode;
        }

        [Test]
        public void FollowsTheHandAndSettlesOnIt()
        {
            BasisPickupJointInteractable pickup = MakeCube(Vector3.up, Vector3.one * 0.2f, 2f, out Rigidbody body);
            Hold(pickup, Vector3.zero);

            float worst = 0f, worstTurn = 0f;
            for (int i = 1; i <= 50; i++)
            {
                StepTowards(pickup, Path(i), Turn(i));
                if (i >= 5 && i < 50)
                {
                    worst = Mathf.Max(worst, Vector3.Distance(body.position, Path(i + 1)));
                    worstTurn = Mathf.Max(worstTurn, Quaternion.Angle(body.rotation, Turn(i + 1)));
                }
            }
            for (int i = 0; i < 25; i++)
            {
                StepTowards(pickup, Path(50), Turn(50));
            }

            TestContext.WriteLine($"worst tracking error {worst * 1000f:F2} mm / {worstTurn:F2} deg, settled {Vector3.Distance(body.position, Path(50)) * 1000f:F3} mm / {Quaternion.Angle(body.rotation, Turn(50)):F3} deg");
            Assert.Less(worst, 0.01f, "the held object fell behind the hand while it moved");
            Assert.Less(worstTurn, 2f, "the held object's turn fell behind the hand's");
            Assert.Less(Vector3.Distance(body.position, Path(50)), 0.002f, "the held object did not settle on the hand");
            Assert.Less(Quaternion.Angle(body.rotation, Turn(50)), 0.5f, "the held object did not turn with the hand");
            Assert.IsFalse(body.useGravity, "gravity stays off while held, so a held object does not sag out of the hand");

            static Vector3 Path(int step) => Vector3.up + new Vector3(0.02f, 0.005f, -0.01f) * Mathf.Min(step, 50);
            static Quaternion Turn(int step) => Quaternion.Euler(0f, 1.8f * Mathf.Min(step, 50), 0.5f * Mathf.Min(step, 50));
        }

        [Test]
        public void SolidGeometryStopsTheHeldObjectInsteadOfLettingItThrough()
        {
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cleanup.Add(wall);
            wall.transform.SetPositionAndRotation(new Vector3(0.5f, 1f, 0f), Quaternion.identity);
            wall.transform.localScale = new Vector3(0.1f, 2f, 2f);
            BasisPickupJointInteractable pickup = MakeCube(Vector3.up, Vector3.one * 0.2f, 2f, out Rigidbody body);
            Hold(pickup, Vector3.zero);

            for (int i = 1; i <= 100; i++)
            {
                StepTowards(pickup, new Vector3(Mathf.Min(1.5f, i * 0.06f), 1f, 0f), Quaternion.identity);
            }

            TestContext.WriteLine($"resting x {body.position.x:F4}, speed {body.linearVelocity.magnitude:F4}");
            Assert.Less(body.position.x, 0.36f, "the hand dragged the held object into the wall");
            Assert.Greater(body.position.x, 0.3f, "the held object should be pressed against the wall, not left behind");
            Assert.Less(body.linearVelocity.magnitude, 0.05f, "the held object kept fighting the wall");
        }

        [Test]
        public void HeavyObjectsLagWhileLightOnesKeepUp()
        {
            BasisPickupJointInteractable light = MakeCube(Vector3.up, Vector3.one * 0.2f, 1f, out Rigidbody lightBody);
            BasisPickupJointInteractable heavy = MakeCube(new Vector3(0f, 1f, 5f), Vector3.one * 0.2f, 100f, out Rigidbody heavyBody);
            Hold(light, Vector3.zero);
            Hold(heavy, Vector3.zero);

            for (int i = 0; i < 5; i++)
            {
                light.SetHoldTarget(new Vector3(1f, 1f, 0f), Quaternion.identity, new Vector3(1f, 1f, 0f), _time);
                heavy.SetHoldTarget(new Vector3(1f, 1f, 5f), Quaternion.identity, new Vector3(1f, 1f, 5f), _time);
                Simulate();
            }

            TestContext.WriteLine($"after 0.1 s: light moved {lightBody.position.x:F3} m, heavy moved {heavyBody.position.x:F3} m");
            Assert.Greater(lightBody.position.x, 0.9f, "a 1 kg object should reach the hand within a tenth of a second");
            Assert.Less(heavyBody.position.x, 0.2f, "a 100 kg object accelerated faster than Maximum Force allows");
        }

        [Test]
        public void ScaledObjectsArriveExactlyOnTheHeldPose()
        {
            BasisPickupJointInteractable pickup = MakeCube(Vector3.up, new Vector3(0.5f, 0.2f, 0.3f), 2f, out Rigidbody body);
            Hold(pickup, new Vector3(0.25f, 0.05f, 0f));

            Vector3 target = new Vector3(0.3f, 1.2f, 0.2f);
            Quaternion turn = Quaternion.Euler(20f, 45f, 0f);
            for (int i = 0; i < 100; i++)
            {
                StepTowards(pickup, target, turn);
            }

            TestContext.WriteLine($"scaled object settled {Vector3.Distance(body.position, target) * 1000f:F3} mm / {Quaternion.Angle(body.rotation, turn):F3} deg from the held pose");
            Assert.Less(Vector3.Distance(body.position, target), 0.002f, "the grab point was anchored in the wrong space for a scaled object");
            Assert.Less(Quaternion.Angle(body.rotation, turn), 0.5f);
        }

        [Test]
        public void ReleasingRestoresTheAuthoredBody()
        {
            BasisPickupJointInteractable pickup = MakeCube(Vector3.up, Vector3.one * 0.2f, 2f, out Rigidbody body);
            body.isKinematic = true;
            body.useGravity = true;
            body.interpolation = RigidbodyInterpolation.None;
            pickup.InterpolateWhileHeld = true;
            Hold(pickup, Vector3.zero);

            Assert.IsTrue(pickup.JointHolding);
            Assert.IsFalse(body.isKinematic, "a joint can only move a dynamic body");
            Assert.IsFalse(body.useGravity);
            Assert.AreEqual(RigidbodyInterpolation.Interpolate, body.interpolation);
            Assert.AreEqual(CollisionDetectionMode.ContinuousSpeculative, body.collisionDetectionMode, "a swung object must not tunnel through thin walls");
            Assert.IsTrue(pickup.HoldAnchor.gameObject.activeSelf);

            pickup.EndJointHold();

            Assert.IsFalse(pickup.JointHolding);
            Assert.IsTrue(body.isKinematic, "an authored kinematic object goes back to kinematic where it was let go");
            Assert.IsTrue(body.useGravity);
            Assert.AreEqual(RigidbodyInterpolation.None, body.interpolation);
            Assert.AreEqual(CollisionDetectionMode.Discrete, body.collisionDetectionMode);
            Assert.IsFalse(pickup.HoldAnchor.gameObject.activeSelf, "the anchor is parked while nothing is held");

            Vector3 rest = body.position;
            BasisPickupJointInteractable.OnFixedSimulate?.Invoke(_time + Step);
            Physics.Simulate(Step);
            Assert.AreEqual(rest, body.position, "a released hold still moved the object");
        }

        [Test]
        public void NothingRunsEachPhysicsStepUnlessSomethingIsHeld()
        {
            BasisPickupJointInteractable first = MakeCube(Vector3.up, Vector3.one * 0.2f, 1f, out _);
            BasisPickupJointInteractable second = MakeCube(new Vector3(0f, 1f, 5f), Vector3.one * 0.2f, 1f, out _);
            Assert.IsNull(BasisPickupJointInteractable.OnFixedSimulate, "nothing is held, so nothing should run each physics step");

            Hold(first, Vector3.zero);
            Hold(second, Vector3.zero);
            Assert.IsNotNull(BasisPickupJointInteractable.OnFixedSimulate);
            Assert.AreEqual(1, BasisPickupJointInteractable.OnFixedSimulate.GetInvocationList().Length, "two holds share one physics step callback");

            first.EndJointHold();
            Assert.IsNotNull(BasisPickupJointInteractable.OnFixedSimulate, "the second object is still held");
            second.EndJointHold();
            Assert.IsNull(BasisPickupJointInteractable.OnFixedSimulate, "the last release has to unhook the physics step callback");

            Hold(first, Vector3.zero);
            _cleanup.Add(first.HoldAnchor.gameObject);
            Object.DestroyImmediate(first.gameObject);
            BasisPickupJointInteractable.OnFixedSimulate?.Invoke(_time);
            Assert.IsNull(BasisPickupJointInteractable.OnFixedSimulate, "an object destroyed while held must not keep the callback alive");
        }

        [Test]
        public void HingedObjectsSwingAboutTheirHinge()
        {
            GameObject door = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cleanup.Add(door);
            door.transform.SetPositionAndRotation(new Vector3(0.5f, 1f, 0f), Quaternion.identity);
            door.transform.localScale = new Vector3(1f, 2f, 0.05f);
            Rigidbody body = door.AddComponent<Rigidbody>();
            body.mass = 10f;
            HingeJoint hinge = door.AddComponent<HingeJoint>();
            hinge.anchor = new Vector3(-0.5f, 0f, 0f);
            hinge.axis = Vector3.up;
            BasisPickupJointInteractable pickup = door.AddComponent<BasisPickupJointInteractable>();
            pickup.RigidRef = body;
            pickup.InterpolateWhileHeld = false;
            pickup.DriveRotation = false;
            pickup.Start();
            _pickups.Add(pickup);

            Assert.IsFalse(pickup.HoldFollowsHand, "a hinged object is placed by its hinge, so it has to stream its world pose");
            Assert.IsFalse(pickup.LerpToHandOnPickup, "a hinged object cannot be pulled into the hand");
            Hold(pickup, new Vector3(0.5f, 0f, 0f));

            Vector3 hingePoint = new Vector3(0f, 1f, 0f);
            for (int i = 1; i <= 100; i++)
            {
                Quaternion swing = Quaternion.Euler(0f, -45f * Mathf.Min(i, 50) / 50f, 0f);
                StepTowards(pickup, hingePoint + swing * new Vector3(0.5f, 0f, 0f), swing);
            }

            float yaw = Vector3.SignedAngle(Vector3.right, body.rotation * Vector3.right, Vector3.up);
            Vector3 fromHinge = body.position - hingePoint;
            TestContext.WriteLine($"door yaw {yaw:F2} deg, distance from hinge {fromHinge.magnitude:F4} m");
            Assert.AreEqual(-45f, yaw, 2f, "pulling the handle did not swing the door to the hand");
            Assert.AreEqual(0.5f, new Vector2(fromHinge.x, fromHinge.z).magnitude, 0.01f, "the hold pulled the door off its hinge");
        }

        [Test]
        public void OnlyJointsThatConstrainSomethingCountAsAHinge()
        {
            GameObject go = new GameObject("constraint-probe");
            _cleanup.Add(go);
            Rigidbody body = go.AddComponent<Rigidbody>();
            ConfigurableJoint joint = go.AddComponent<ConfigurableJoint>();
            Assert.IsFalse(BasisPickupJointInteractable.IsConstrainedByJoint(body), "a ConfigurableJoint left at its defaults constrains nothing");

            joint.yMotion = ConfigurableJointMotion.Limited;
            Assert.IsTrue(BasisPickupJointInteractable.IsConstrainedByJoint(body));

            joint.yMotion = ConfigurableJointMotion.Free;
            joint.slerpDrive = new JointDrive { positionSpring = 10f, maximumForce = float.MaxValue };
            Assert.IsTrue(BasisPickupJointInteractable.IsConstrainedByJoint(body), "a drive pulls the object somewhere even with every axis free");

            Object.DestroyImmediate(joint);
            go.AddComponent<HingeJoint>();
            Assert.IsTrue(BasisPickupJointInteractable.IsConstrainedByJoint(body));
        }

        [Test]
        public void TheAnchorLeadsTheLastHandSampleByAtMostOneSample()
        {
            Quaternion ten = Quaternion.Euler(0f, 10f, 0f);
            Quaternion twenty = Quaternion.Euler(0f, 20f, 0f);

            BasisPickupJointInteractable.Extrapolate(Vector3.zero, ten, 0d, Vector3.right, twenty, 1d, 1.5d, out Vector3 half, out Quaternion halfTurn);
            Assert.That(Vector3.Distance(half, new Vector3(1.5f, 0f, 0f)), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(halfTurn, Quaternion.Euler(0f, 25f, 0f)), Is.LessThan(0.01f));

            BasisPickupJointInteractable.Extrapolate(Vector3.zero, ten, 0d, Vector3.right, twenty, 1d, 9d, out Vector3 capped, out Quaternion cappedTurn);
            Assert.That(Vector3.Distance(capped, new Vector3(2f, 0f, 0f)), Is.LessThan(1e-5f), "a stalled hand must not fling the anchor ahead");
            Assert.That(Quaternion.Angle(cappedTurn, Quaternion.Euler(0f, 30f, 0f)), Is.LessThan(0.01f));

            BasisPickupJointInteractable.Extrapolate(Vector3.zero, ten, 0d, Vector3.right, twenty, 1d, 0.5d, out Vector3 behind, out Quaternion behindTurn);
            Assert.AreEqual(Vector3.right, behind);
            Assert.AreEqual(twenty, behindTurn);

            Quaternion negatedTen = new Quaternion(-ten.x, -ten.y, -ten.z, -ten.w);
            BasisPickupJointInteractable.Extrapolate(Vector3.zero, negatedTen, 0d, Vector3.zero, twenty, 1d, 1.5d, out _, out Quaternion flipped);
            Assert.That(Quaternion.Angle(flipped, Quaternion.Euler(0f, 25f, 0f)), Is.LessThan(0.01f), "the same rotation written with the opposite sign took the long way round");
        }

        [Test]
        public void ATeleportedHandBringsTheHeldObjectWithIt()
        {
            BasisPickupJointInteractable pickup = MakeCube(Vector3.up, Vector3.one * 0.2f, 2f, out Rigidbody body);
            Hold(pickup, Vector3.zero);
            for (int i = 0; i < 5; i++)
            {
                StepTowards(pickup, Vector3.up, Quaternion.identity);
            }

            Vector3 far = new Vector3(400f, 3f, -250f);
            Quaternion facing = Quaternion.Euler(0f, 90f, 0f);
            pickup.SetHoldTarget(far, facing, far, _time);

            Assert.That(Vector3.Distance(body.position, far), Is.LessThan(1e-4f), "the held object was left behind to be dragged across the world");
            Assert.That(Quaternion.Angle(body.rotation, facing), Is.LessThan(0.01f));
            for (int i = 0; i < 10; i++)
            {
                StepTowards(pickup, far, facing);
            }
            Assert.That(Vector3.Distance(body.position, far), Is.LessThan(0.002f));
        }

        [Test]
        public void AnObjectCaughtAwayFromTheHandIsLetGo()
        {
            BasisPickupJointInteractable pickup = MakeCube(Vector3.up, Vector3.one * 0.2f, 2f, out Rigidbody body);
            Hold(pickup, Vector3.zero);

            Assert.IsFalse(pickup.PulledAway(0.05f), "an object in the hand is not pulled away");
            Vector3 away = Vector3.up + new Vector3(1f, 0f, 0f);
            pickup.SetHoldTarget(away, Quaternion.identity, away, _time);
            Assert.IsFalse(pickup.PulledAway(0.1f), "one bad frame must not drop the object");
            Assert.IsTrue(pickup.PulledAway(0.15f), "an object held a metre from the hand should be let go");

            BasisPickupJointInteractable fresh = MakeCube(new Vector3(0f, 1f, 5f), Vector3.one * 0.2f, 2f, out _);
            Hold(fresh, Vector3.zero);
            Vector3 reach = new Vector3(3f, 1f, 5f);
            fresh.SetHoldTarget(reach, Quaternion.identity, reach, _time);
            Assert.IsFalse(fresh.PulledAway(0.5f), "an object still on its way to the hand is not dropped");
            Assert.IsFalse(fresh.PulledAway(0.45f));
            Assert.IsTrue(fresh.PulledAway(0.25f), "an object that never reaches the hand is let go once it has had time to");
        }

        private BasisPickupJointInteractable MakeCube(Vector3 position, Vector3 scale, float mass, out Rigidbody body)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cleanup.Add(go);
            go.transform.SetPositionAndRotation(position, Quaternion.identity);
            go.transform.localScale = scale;
            body = go.AddComponent<Rigidbody>();
            body.mass = mass;
            BasisPickupJointInteractable pickup = go.AddComponent<BasisPickupJointInteractable>();
            pickup.RigidRef = body;
            pickup.InterpolateWhileHeld = false;
            pickup.Start();
            _pickups.Add(pickup);
            return pickup;
        }

        private void Hold(BasisPickupJointInteractable pickup, Vector3 handOnObject)
        {
            pickup.InputConstraint.SetOffsetPositionAndRotation(0, -handOnObject, Quaternion.identity);
            Physics.SyncTransforms();
            Assert.IsTrue(pickup.BeginJointHold(), "the joint hold did not start");
        }

        private void StepTowards(BasisPickupJointInteractable pickup, Vector3 position, Quaternion rotation)
        {
            pickup.SetHoldTarget(position, rotation, position, _time);
            Simulate();
        }

        private void Simulate()
        {
            _time += Step;
            BasisPickupJointInteractable.OnFixedSimulate?.Invoke(_time);
            Physics.Simulate(Step);
        }
    }
}
