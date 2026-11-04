using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk.Interactions;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public class BasisPickupSyncGatingTests
    {
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly MethodInfo AwakeM = typeof(BasisPickupSyncNetworking).GetMethod("Awake", NP);

        readonly List<GameObject> cleanup = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in cleanup)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            cleanup.Clear();
        }

        BasisPickupSyncNetworking MakeProp(out BasisPickupInteractable pickup)
        {
            Assert.IsNotNull(AwakeM, "Awake moved");
            var go = new GameObject("gated prop");
            cleanup.Add(go);
            pickup = go.AddComponent<BasisPickupInteractable>();
            BasisPickupSyncNetworking sync = go.AddComponent<BasisPickupSyncNetworking>();
            sync.Target = go.transform;
            sync.BasisPickupInteractable = pickup;
            sync.SyncPosition = true; sync.PositionX = true; sync.PositionY = true; sync.PositionZ = true;
            sync.SyncRotation = true; sync.RotationX = true; sync.RotationY = true; sync.RotationZ = true;
            sync.SyncScale = true; sync.ScaleX = true; sync.ScaleY = true; sync.ScaleZ = true;
            sync.UseChecksum = false;
            AwakeM.Invoke(sync, null);
            return sync;
        }

        [Test]
        public void GatingComesBackAfterADisableAndEnable()
        {
            BasisPickupSyncNetworking sync = MakeProp(out BasisPickupInteractable pickup);
            sync.OnEnable();
            Assert.AreEqual(1, pickup.CanHoverInjected.Count, "Awake and the first OnEnable register once between them");
            Assert.AreEqual(1, pickup.CanInteractInjected.Count);

            sync.OnDisable();
            Assert.AreEqual(0, pickup.CanHoverInjected.Count);
            Assert.AreEqual(0, pickup.CanInteractInjected.Count);

            sync.OnEnable();
            sync.OnEnable();
            Assert.AreEqual(1, pickup.CanHoverInjected.Count, "a re-enabled prop is gated again, exactly once");
            Assert.AreEqual(1, pickup.CanInteractInjected.Count);
        }

        [Test]
        public void ReenabledGatingStillRefusesAStaticProp()
        {
            BasisPickupSyncNetworking sync = MakeProp(out BasisPickupInteractable pickup);
            sync.OnDisable();
            sync.OnEnable();
            sync.IsStatic = true;

            Assert.IsFalse(pickup.CanHoverInjected[0](null));
            Assert.IsFalse(pickup.CanInteractInjected[0](null));
        }

        [Test]
        public void GatingLeavesThePickupItWasAddedTo()
        {
            BasisPickupSyncNetworking sync = MakeProp(out BasisPickupInteractable first);
            var other = new GameObject("other pickup");
            cleanup.Add(other);
            BasisPickupInteractable second = other.AddComponent<BasisPickupInteractable>();
            sync.BasisPickupInteractable = second;

            sync.OnDisable();
            Assert.AreEqual(0, first.CanHoverInjected.Count);
            Assert.AreEqual(0, first.CanInteractInjected.Count);

            sync.OnEnable();
            Assert.AreEqual(1, second.CanHoverInjected.Count);
            Assert.AreEqual(0, first.CanHoverInjected.Count);
        }
    }
}
