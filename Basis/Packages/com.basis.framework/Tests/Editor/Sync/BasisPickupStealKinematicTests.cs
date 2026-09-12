using System.Collections.Generic;
using System.Reflection;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// Issue #1042: a Rigidbody prop passed from one player to another slid out of the second holder's
    /// hand. Grabbing a prop you do not own is a steal, and the ownership round-trip lands
    /// <see cref="BasisPickupSyncNetworking.ControlState"/> a whole RTT AFTER the local grab already put
    /// the prop in the hand, so the steal branch has to leave a live hold alone. It used to re-assert the
    /// authored (dynamic) kinematic state unconditionally, handing the held prop back to gravity while the
    /// constraint was still driving it. The first grab never showed it: the spawner already owns the prop,
    /// so no steal is pending and ControlState is never called for it.
    /// </summary>
    public class BasisPickupStealKinematicTests
    {
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly FieldInfo InputStateF = typeof(BasisInputWrapper).GetField("State", NP);
        static readonly FieldInfo HeldLoopF = typeof(BasisInteractableObject)
            .GetField("<RequiresUpdateLoop>k__BackingField", NP);

        readonly List<GameObject> _cleanup = new List<GameObject>();

        class StubInput : BasisInput
        {
            public override void LateDoPollData() { }
            public override void ShowTrackedVisual() { }
            public override void PlayHaptic(float duration = 0.25f, float amplitude = 0.5f, float frequency = 0.5f) { }
            public override void PlaySoundEffect(string SoundEffectName, float Volume) { }
        }

        [SetUp]
        public void SetUp()
        {
            Assert.IsNotNull(InputStateF, "BasisInputWrapper.State moved");
            Assert.IsNotNull(HeldLoopF, "RequiresUpdateLoop backing field moved");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _cleanup)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            _cleanup.Clear();
        }

        [Test]
        public void Steal_WhileHeld_LeavesTheHeldPropKinematic()
        {
            BasisPickupSyncNetworking sync = MakeRemotelyHeldProp(kinematicWhileInteracting: true, out Rigidbody body,
                out BasisInput input);
            Assert.IsTrue(body.isKinematic, "staging: a remote-owned prop is pose driven, so it is kinematic");

            GrabLocally(sync, input, body);
            sync.ControlState();

            Assert.IsTrue(body.isKinematic, "the steal confirmation dropped the held prop back into physics");
            Assert.IsFalse(sync.BasisPickupInteractable._previousKinematicValue,
                "drop has to restore the authored dynamic state, not the remote-driven kinematic one");
            Assert.IsNull(sync.pendingStealRequest);
        }

        [Test]
        public void Steal_WhileHeldWithoutKinematicWhileInteracting_RestoresAuthoredKinematic()
        {
            BasisPickupSyncNetworking sync = MakeRemotelyHeldProp(kinematicWhileInteracting: false, out Rigidbody body,
                out BasisInput input);

            GrabLocally(sync, input, body);
            sync.ControlState();

            Assert.IsFalse(body.isKinematic,
                "a prop that holds by physics has to leave the remote-driven kinematic state behind");
        }

        [Test]
        public void OwnedAndHeld_WithoutPendingSteal_LeavesTheHeldPropKinematic()
        {
            BasisPickupSyncNetworking sync = MakeRemotelyHeldProp(kinematicWhileInteracting: true, out Rigidbody body,
                out BasisInput input);

            GrabLocally(sync, input, body);
            sync.pendingStealRequest = null;
            sync.ControlState();

            Assert.IsTrue(body.isKinematic);
        }

        /// <summary>
        /// A prop whose authored Rigidbody is dynamic, currently owned and held by someone else: every
        /// observer forces it kinematic and drives it off the stream (the non-owned branch of ControlState).
        /// </summary>
        BasisPickupSyncNetworking MakeRemotelyHeldProp(bool kinematicWhileInteracting, out Rigidbody body,
            out BasisInput input)
        {
            var go = new GameObject("prop");
            _cleanup.Add(go);
            body = go.AddComponent<Rigidbody>();
            body.isKinematic = false;
            BasisPickupInteractable pickup = go.AddComponent<BasisPickupInteractable>();
            pickup.RigidRef = body;
            pickup.KinematicWhileInteracting = kinematicWhileInteracting;
            BasisPickupSyncNetworking sync = go.AddComponent<BasisPickupSyncNetworking>();
            sync.Target = go.transform;
            sync.BasisPickupInteractable = pickup;
            sync.IsOwnedLocallyOnClient = false;

            var inputGo = new GameObject("hand");
            _cleanup.Add(inputGo);
            input = inputGo.AddComponent<StubInput>();
            input.UniqueDeviceIdentifier = "left";

            sync.AuthoredKinematic = false;
            return sync;
        }

        /// <summary>Everything <see cref="BasisPickupInteractable.OnInteractStart"/> leaves behind on the stealing client.</summary>
        void GrabLocally(BasisPickupSyncNetworking sync, BasisInput input, Rigidbody body)
        {
            BasisPickupInteractable pickup = sync.BasisPickupInteractable;
            BasisInputWrapper held = default;
            object boxed = held;
            InputStateF.SetValue(boxed, BasisInteractInputState.Interacting);
            held = (BasisInputWrapper)boxed;
            held.Source = input;
            pickup.Inputs.leftHand = held;
            HeldLoopF.SetValue(pickup, true);
            if (pickup.KinematicWhileInteracting)
            {
                pickup._previousKinematicValue = body.isKinematic;
                body.isKinematic = true;
            }
            sync.IsOwnedLocallyOnClient = true;
            sync.pendingStealRequest = input;
        }
    }
}
