using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Common;
using Basis.Scripts.Device_Management.Devices.Desktop;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Basis.Tests.Interactions
{
    /// <summary>
    /// Desktop drag-rotate (middle mouse while holding a pickup) pauses the player's mouse look through
    /// <see cref="BasisLocks.LookRotation"/>. The owner it registers under has to belong to the pickup that
    /// took the lock: another pickup starting mid-drag (a spawned prop, a spawn-anchor handle, a media
    /// player) must not be able to leave the look lock behind under an owner nothing will ever release.
    /// </summary>
    public class BasisPickupHeadLockTests
    {
        private static readonly MethodInfo PollDesktopControl = typeof(BasisPickupInteractable).GetMethod("PollDesktopControl", BindingFlags.NonPublic | BindingFlags.Instance);
        private static BasisLocks.LockContext Look => BasisLocks.GetContext(BasisLocks.LookRotation);

        private readonly List<GameObject> _cleanup = new();
        private Mouse _addedMouse;

        [SetUp]
        public void SetUp()
        {
            Assert.IsNotNull(PollDesktopControl, "BasisPickupInteractable.PollDesktopControl moved");
            Look.Clear();
            if (Mouse.current == null)
            {
                _addedMouse = InputSystem.AddDevice<Mouse>();
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _cleanup)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            _cleanup.Clear();
            Look.Clear();
            if (_addedMouse != null)
            {
                InputSystem.RemoveDevice(_addedMouse);
                _addedMouse = null;
            }
        }

        private BasisPickupInteractable StartPickup(string name)
        {
            var go = new GameObject(name);
            _cleanup.Add(go);
            BasisPickupInteractable pickup = go.AddComponent<BasisPickupInteractable>();
            pickup.Start();
            return pickup;
        }

        private BasisDesktopEye MakeDesktopEye()
        {
            var go = new GameObject("desktop-eye");
            _cleanup.Add(go);
            return go.AddComponent<BasisDesktopEye>();
        }

        private static void Poll(BasisPickupInteractable pickup, BasisDesktopEye eye, bool middleMouseDown)
        {
            eye.CurrentInputState.Secondary2DAxisClick = middleMouseDown;
            PollDesktopControl.Invoke(pickup, new object[] { eye });
        }

        [Test]
        public void ReleasingDragRotate_HandsTheLookLockBack()
        {
            BasisPickupInteractable held = StartPickup("held");
            BasisDesktopEye eye = MakeDesktopEye();

            Poll(held, eye, true);
            Assert.That(Look.Count, Is.EqualTo(1), "middle mouse on a held pickup pauses mouse look");

            Poll(held, eye, false);
            Assert.That(Look.Count, Is.EqualTo(0), Look.ToString());
        }

        [Test]
        public void AnotherPickupStartingMidDrag_CannotStrandTheLookLock()
        {
            BasisPickupInteractable held = StartPickup("held");
            BasisDesktopEye eye = MakeDesktopEye();
            Poll(held, eye, true);

            StartPickup("spawned-while-rotating");

            Poll(held, eye, false);
            Assert.That(Look.Count, Is.EqualTo(0), Look.ToString());
        }

        [Test]
        public void EachPickupRegistersItsOwnLookLockOwner()
        {
            BasisPickupInteractable first = StartPickup("first");
            BasisPickupInteractable second = StartPickup("second");
            BasisDesktopEye eye = MakeDesktopEye();

            Poll(first, eye, true);
            Poll(second, eye, true);
            Assert.That(Look.Count, Is.EqualTo(2), Look.ToString());

            Poll(first, eye, false);
            Assert.That(Look.Count, Is.EqualTo(1), "the second pickup is still being rotated");
            Poll(second, eye, false);
            Assert.That(Look.Count, Is.EqualTo(0), Look.ToString());
        }
    }
}
