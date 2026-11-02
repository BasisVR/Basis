using NUnit.Framework;
using UnityEngine;

namespace Basis.ImagePickup.Tests
{
    public sealed class BasisImagePickupRemoteSettleTests
    {
        private GameObject _go;
        private BasisImagePickupObject _card;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("remote-settle");
            _card = _go.AddComponent<BasisImagePickupObject>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_go);

        private void Step(int frames) { for (int i = 0; i < frames; i++) _card.SimulateRemoteTransform(1f / 60f); }

        [Test]
        public void ConvergesExactlyOnTheTargetAndThenStopsWriting()
        {
            _card.SetRemoteTarget(Vector3.zero, Quaternion.identity, 1f);
            Vector3 target = new Vector3(1f, 2f, 3f);
            Quaternion targetRotation = Quaternion.Euler(10f, 70f, -20f);
            _card.SetRemoteTarget(target, targetRotation, 1.5f);
            Step(2);
            Assert.That(Vector3.Distance(_go.transform.position, target), Is.GreaterThan(0.01f), "still moving after two frames");
            Step(600);
            Assert.That(_go.transform.position, Is.EqualTo(target), "snapped onto the exact target");
            Assert.That(_go.transform.rotation == targetRotation, "snapped onto the exact rotation");
            Assert.That(_go.transform.localScale.x, Is.EqualTo(1.5f));
            _go.transform.hasChanged = false;
            Step(10);
            Assert.That(_go.transform.hasChanged, Is.False, "a settled card does not touch its transform");
        }

        [Test]
        public void ASettledCardMovedFromOutsideIsPulledBack()
        {
            Vector3 target = new Vector3(4f, 0f, -1f);
            _card.SetRemoteTarget(target, Quaternion.identity, 1f);
            Step(3);
            _go.transform.position = target + new Vector3(0f, 1f, 0f);
            Step(1);
            Assert.That(_go.transform.position.y, Is.LessThan(1f).And.GreaterThan(0f), "moving back toward the target");
            Step(600);
            Assert.That(_go.transform.position, Is.EqualTo(target));
        }

        [Test]
        public void ANewTargetAfterSettlingMovesTheCardAgain()
        {
            _card.SetRemoteTarget(Vector3.zero, Quaternion.identity, 1f);
            Step(5);
            _card.SetRemoteTarget(new Vector3(0f, 0f, 2f), Quaternion.identity, 1f);
            Step(1);
            Assert.That(_go.transform.position.z, Is.GreaterThan(0f).And.LessThan(2f));
            Step(600);
            Assert.That(_go.transform.position, Is.EqualTo(new Vector3(0f, 0f, 2f)));
        }
    }
}
