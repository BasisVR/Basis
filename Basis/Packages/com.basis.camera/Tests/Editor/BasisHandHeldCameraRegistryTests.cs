using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Camera
{
    /// <summary>
    /// The registry is what makes the Camera Settings menu button appear and disappear, and its
    /// OnChanged event is what closes an orphaned panel. A miscounted registry therefore shows up
    /// as a menu button for a camera that no longer exists, so add/remove is asserted directly.
    /// </summary>
    public class BasisHandHeldCameraRegistryTests
    {
        private readonly System.Collections.Generic.List<GameObject> _spawned = new System.Collections.Generic.List<GameObject>();

        private BasisHandHeldCamera NewCamera(string name)
        {
            GameObject go = new GameObject(name);
            _spawned.Add(go);
            return go.AddComponent<BasisHandHeldCamera>();
        }

        [SetUp]
        public void SetUp()
        {
            DrainRegistry();
        }

        [TearDown]
        public void TearDown()
        {
            DrainRegistry();
            for (int Index = 0; Index < _spawned.Count; Index++)
            {
                if (_spawned[Index] != null) Object.DestroyImmediate(_spawned[Index]);
            }
            _spawned.Clear();
        }

        // The registry is process-wide static state; a leftover entry from another test would
        // silently change every count assertion here. Drained from a snapshot rather than by
        // looping on Count: Remove early-returns for a destroyed camera (it reads as null), so a
        // count-driven loop would spin forever instead of failing.
        private static void DrainRegistry()
        {
            var snapshot = new System.Collections.Generic.List<BasisHandHeldCamera>(
                BasisHandHeldCameraRegistry.Cameras);

            for (int Index = 0; Index < snapshot.Count; Index++)
            {
                BasisHandHeldCameraRegistry.Remove(snapshot[Index]);
            }
        }

        [Test]
        public void Add_IncrementsCountAndRaisesChanged()
        {
            int raised = 0;
            System.Action handler = () => raised++;
            BasisHandHeldCameraRegistry.OnChanged += handler;

            try
            {
                BasisHandHeldCameraRegistry.Add(NewCamera("A"));

                Assert.That(BasisHandHeldCameraRegistry.Count, Is.EqualTo(1));
                Assert.That(raised, Is.EqualTo(1));
            }
            finally
            {
                BasisHandHeldCameraRegistry.OnChanged -= handler;
            }
        }

        [Test]
        public void Add_IsIdempotentForTheSameCamera()
        {
            BasisHandHeldCamera camera = NewCamera("A");

            BasisHandHeldCameraRegistry.Add(camera);
            BasisHandHeldCameraRegistry.Add(camera);

            Assert.That(BasisHandHeldCameraRegistry.Count, Is.EqualTo(1),
                "A double Add would leave a phantom entry keeping the menu button alive.");
        }

        [Test]
        public void Add_IgnoresNull()
        {
            BasisHandHeldCameraRegistry.Add(null);

            Assert.That(BasisHandHeldCameraRegistry.Count, Is.Zero);
        }

        [Test]
        public void Remove_DecrementsCountAndRaisesChanged()
        {
            BasisHandHeldCamera camera = NewCamera("A");
            BasisHandHeldCameraRegistry.Add(camera);

            int raised = 0;
            System.Action handler = () => raised++;
            BasisHandHeldCameraRegistry.OnChanged += handler;

            try
            {
                BasisHandHeldCameraRegistry.Remove(camera);

                Assert.That(BasisHandHeldCameraRegistry.Count, Is.Zero);
                Assert.That(raised, Is.EqualTo(1));
            }
            finally
            {
                BasisHandHeldCameraRegistry.OnChanged -= handler;
            }
        }

        [Test]
        public void Remove_UnknownCamera_DoesNotRaiseChanged()
        {
            BasisHandHeldCamera tracked = NewCamera("A");
            BasisHandHeldCameraRegistry.Add(tracked);

            int raised = 0;
            System.Action handler = () => raised++;
            BasisHandHeldCameraRegistry.OnChanged += handler;

            try
            {
                BasisHandHeldCameraRegistry.Remove(NewCamera("NeverAdded"));

                Assert.That(raised, Is.Zero,
                    "Spurious change events rebuild the whole menu button row for nothing.");
                Assert.That(BasisHandHeldCameraRegistry.Count, Is.EqualTo(1));
            }
            finally
            {
                BasisHandHeldCameraRegistry.OnChanged -= handler;
            }
        }

        [Test]
        public void MultipleCameras_AreTrackedIndependently()
        {
            BasisHandHeldCamera first = NewCamera("A");
            BasisHandHeldCamera second = NewCamera("B");

            BasisHandHeldCameraRegistry.Add(first);
            BasisHandHeldCameraRegistry.Add(second);
            Assert.That(BasisHandHeldCameraRegistry.Count, Is.EqualTo(2));

            BasisHandHeldCameraRegistry.Remove(first);

            Assert.That(BasisHandHeldCameraRegistry.Count, Is.EqualTo(1));
            Assert.That(BasisHandHeldCameraRegistry.Cameras[0], Is.SameAs(second),
                "Removing one camera must not disturb the other's entry.");
        }
    }
}
