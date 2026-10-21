using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk.Interactions;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Interactions
{
    [NoAutoStaticsCleanup]
    public class BasisPickupJointCollisionRestoreTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo IgnoredControllerF = typeof(BasisPickupJointInteractable).GetField("ignoredController", Private);
        private static readonly FieldInfo IgnoredCollidersF = typeof(BasisPickupJointInteractable).GetField("ignoredColliders", Private);
        private static readonly MethodInfo RestoreM = typeof(BasisPickupJointInteractable).GetMethod("RestorePlayerCollisions", Private);
        private readonly List<GameObject> cleanup = new List<GameObject>();
        private SimulationMode previousMode;

        [SetUp]
        public void SetUp()
        {
            Assert.IsNotNull(IgnoredControllerF, "ignoredController moved");
            Assert.IsNotNull(IgnoredCollidersF, "ignoredColliders moved");
            Assert.IsNotNull(RestoreM, "RestorePlayerCollisions moved");
            previousMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            Assert.IsNull(BasisPickupJointInteractable.OnFixedSimulate, "another test left a physics step callback behind");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in cleanup)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            cleanup.Clear();
            BasisPickupJointInteractable.OnFixedSimulate?.Invoke(0d);
            Physics.simulationMode = previousMode;
        }

        private CharacterController MakePlayer()
        {
            GameObject player = new GameObject("player");
            cleanup.Add(player);
            CharacterController controller = player.AddComponent<CharacterController>();
            controller.radius = 0.3f;
            controller.height = 1.8f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            return controller;
        }

        private BasisPickupJointInteractable MakeIgnoredCube(CharacterController controller, Vector3 position, out BoxCollider box)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cleanup.Add(cube);
            cube.transform.position = position;
            Rigidbody body = cube.AddComponent<Rigidbody>();
            body.isKinematic = true;
            BasisPickupJointInteractable pickup = cube.AddComponent<BasisPickupJointInteractable>();
            pickup.RigidRef = body;
            box = cube.GetComponent<BoxCollider>();
            Physics.IgnoreCollision(box, controller, true);
            IgnoredControllerF.SetValue(pickup, controller);
            ((List<Collider>)IgnoredCollidersF.GetValue(pickup)).Add(box);
            Physics.SyncTransforms();
            return pickup;
        }

        [Test]
        public void AnOverlappingObjectStaysIgnoredUntilThePlayerIsClear()
        {
            CharacterController controller = MakePlayer();
            BasisPickupJointInteractable pickup = MakeIgnoredCube(controller, new Vector3(0f, 1f, 0f), out BoxCollider box);

            RestoreM.Invoke(pickup, null);
            Assert.IsTrue(Physics.GetIgnoreCollision(box, controller), "restoring inside the player would shove them out of the object");
            Assert.IsNotNull(BasisPickupJointInteractable.OnFixedSimulate, "the pair has to stay pending, not be forgotten");

            BasisPickupJointInteractable.OnFixedSimulate.Invoke(0d);
            Assert.IsTrue(Physics.GetIgnoreCollision(box, controller), "still overlapping");

            box.transform.position = new Vector3(5f, 1f, 0f);
            Physics.SyncTransforms();
            BasisPickupJointInteractable.OnFixedSimulate.Invoke(0d);
            Assert.IsFalse(Physics.GetIgnoreCollision(box, controller), "once clear the player collides with the object again");
            Assert.IsNull(BasisPickupJointInteractable.OnFixedSimulate, "nothing is left to check each physics step");
        }

        [Test]
        public void AClearObjectIsRestoredOnRelease()
        {
            CharacterController controller = MakePlayer();
            BasisPickupJointInteractable pickup = MakeIgnoredCube(controller, new Vector3(5f, 1f, 0f), out BoxCollider box);

            RestoreM.Invoke(pickup, null);
            Assert.IsFalse(Physics.GetIgnoreCollision(box, controller));
            Assert.IsNull(BasisPickupJointInteractable.OnFixedSimulate);
        }

        [Test]
        public void ADisabledControllerKeepsThePairPending()
        {
            CharacterController controller = MakePlayer();
            BasisPickupJointInteractable pickup = MakeIgnoredCube(controller, new Vector3(5f, 1f, 0f), out BoxCollider box);
            controller.enabled = false;

            RestoreM.Invoke(pickup, null);
            Assert.IsNotNull(BasisPickupJointInteractable.OnFixedSimulate, "a disabled controller cannot be restored against yet");
            BasisPickupJointInteractable.OnFixedSimulate.Invoke(0d);
            Assert.IsNotNull(BasisPickupJointInteractable.OnFixedSimulate);

            controller.enabled = true;
            Physics.SyncTransforms();
            BasisPickupJointInteractable.OnFixedSimulate.Invoke(0d);
            Assert.IsFalse(Physics.GetIgnoreCollision(box, controller));
            Assert.IsNull(BasisPickupJointInteractable.OnFixedSimulate);
        }

        [Test]
        public void ADestroyedObjectStopsTheChecks()
        {
            CharacterController controller = MakePlayer();
            BasisPickupJointInteractable pickup = MakeIgnoredCube(controller, new Vector3(0f, 1f, 0f), out BoxCollider box);

            RestoreM.Invoke(pickup, null);
            Assert.IsNotNull(BasisPickupJointInteractable.OnFixedSimulate);

            Object.DestroyImmediate(pickup.gameObject);
            BasisPickupJointInteractable.OnFixedSimulate.Invoke(0d);
            Assert.IsNull(BasisPickupJointInteractable.OnFixedSimulate, "a destroyed object has no pair left to restore");
        }
    }
}
