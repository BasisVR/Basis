using Unity.Scripting.LifecycleManagement;
using System.Reflection;
using System.Runtime.Serialization;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Sync;
using Basis.Scripts.TransformBinders.BoneControl;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// Reference-frame tests for the attach-to-hand pickup path.
    ///
    /// BasisPickupAttachTests injects a synthetic offset with EncodePose and decodes it against the SAME
    /// hand transform, so it validates the algebra but cannot see a mismatch between the wrist the owner
    /// measured against and the wrist the observer reconstructs. These tests drive the real
    /// OnBeforeTransmit measurement on an owner rig and decode on a SEPARATE observer rig - the way two
    /// clients actually work - to find which link puts a held prop out of the hand on remotes.
    /// </summary>
    [NoAutoStaticsCleanup]
    public class BasisPickupWristFrameTests
    {
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

        static readonly FieldInfo ReceiverF = typeof(BasisSyncedObject).GetField("_receiver", NP);
        static readonly FieldInfo SchemaF = typeof(BasisSyncedObject).GetField("_schema", NP);
        static readonly FieldInfo LocalF = typeof(BasisSyncedObject).GetField("_local", NP);
        static readonly FieldInfo PlayerF = typeof(BasisNetworkPlayer).GetField("_player", NP);
        static readonly FieldInfo CachedAnimF = typeof(BasisPickupSyncNetworking).GetField("_cachedHandAnimator", NP);
        static readonly FieldInfo CachedIdF = typeof(BasisPickupSyncNetworking).GetField("_cachedHandId", NP);
        static readonly FieldInfo CachedHandF = typeof(BasisPickupSyncNetworking).GetField("_cachedHand", NP);
        static readonly FieldInfo InputStateF = typeof(BasisInputWrapper).GetField("State", NP);
        static readonly MethodInfo AwakeM = typeof(BasisPickupSyncNetworking).GetMethod("Awake", NP);
        static readonly MethodInfo EnsureBuffersM = typeof(BasisSyncedObject).GetMethod("EnsureBuffers", NP);
        static readonly MethodInfo BeforeTransmitM = typeof(BasisPickupSyncNetworking).GetMethod("OnBeforeTransmit", NP);

        const byte HandLeft = 1;

        readonly System.Collections.Generic.List<GameObject> _cleanup = new System.Collections.Generic.List<GameObject>();
        BasisPickupSyncNetworking _sender;
        BasisPickupSyncNetworking _remote;
        Transform _ownerWrist;      // the holder's wrist as the OWNER's client has it posed
        Transform _observerWrist;   // the same wrist as the OBSERVER's client reconstructs it
        byte _seq;

        [SetUp]
        public void SetUp()
        {
            Assert.IsNotNull(InputStateF, "BasisInputWrapper.State field moved");
            Assert.IsNotNull(BeforeTransmitM, "OnBeforeTransmit moved");
            BasisSyncDriver.Initialize();

            _sender = CreatePickup("owner-prop");
            _remote = CreatePickup("observer-prop");

            _ownerWrist = MakeHolderRig("holder-on-owner", out BasisNetworkPlayer ownerView);
            _observerWrist = MakeHolderRig("holder-on-observer", out BasisNetworkPlayer observerView);

            _sender.currentOwnedPlayer = ownerView;
            _sender.IsOwnedLocallyOnClient = true;
            SeedHandCache(_sender, ownerView, _ownerWrist);
            AttachInteractable(_sender, BasisBoneTrackedRole.LeftHand);

            _remote.currentOwnedPlayer = observerView;
            _remote.IsOwnedLocallyOnClient = false;
            SeedHandCache(_remote, observerView, _observerWrist);

            BasisSyncDriver.RegisterRemote(_remote);
            _seq = 0;
        }

        [TearDown]
        public void TearDown()
        {
            if (_remote != null) BasisSyncDriver.UnregisterRemote(_remote);
            foreach (GameObject go in _cleanup)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            _cleanup.Clear();
            BasisSyncDriver.ReweldAttachedPickups();
            BasisSyncDriver.OnDestroy();
        }

        // ── The eliminating tests: does the observer's wrist have to match the owner's? ──

        [Test]
        public void RealTransmitPath_PropKeepsItsGripInTheObserverWristFrame()
        {
            _ownerWrist.SetPositionAndRotation(new Vector3(1f, 1.4f, -2f), Quaternion.Euler(12f, 60f, -8f));
            // The observer has the same player posed somewhere completely different (own interpolation
            // timeline, own root) - the case the design is supposed to be immune to.
            _observerWrist.SetPositionAndRotation(new Vector3(-4f, 0.9f, 7f), Quaternion.Euler(-30f, 200f, 15f));

            Vector3 grip = new Vector3(0.04f, -0.02f, 0.12f);
            Quaternion gripRot = Quaternion.Euler(0f, 25f, 90f);
            HoldAtGrip(grip, gripRot);

            AssertGripPreserved(grip, gripRot,
                "a held prop must land at the same grip in the observer's wrist frame as in the owner's");
        }

        [TestCase(1f)]
        [TestCase(5f)]
        [TestCase(15f)]
        [TestCase(45f)]
        public void ObserverWristRotationDrift_DoesNotMovePropOutOfTheHand(float driftDegrees)
        {
            _ownerWrist.SetPositionAndRotation(new Vector3(0.5f, 1.3f, 0.2f), Quaternion.Euler(5f, 40f, 0f));
            // Whatever the avatar stream got wrong about the wrist - FK chain drift with the end-effector
            // anchor off, quantisation, a fallback avatar - shows up as the observer's wrist being rotated
            // relative to the owner's.
            _observerWrist.SetPositionAndRotation(_ownerWrist.position,
                _ownerWrist.rotation * Quaternion.Euler(driftDegrees, driftDegrees * 0.5f, -driftDegrees));

            Vector3 grip = new Vector3(0.05f, 0f, 0.18f);
            HoldAtGrip(grip, Quaternion.identity);

            AssertGripPreserved(grip, Quaternion.identity,
                $"the prop is welded to the observer's own wrist, so {driftDegrees} deg of wrist drift " +
                "must carry the prop with the hand rather than out of it");
        }

        [Test]
        public void ObserverWristPositionDrift_DoesNotMovePropOutOfTheHand()
        {
            _ownerWrist.SetPositionAndRotation(new Vector3(0f, 1.2f, 0f), Quaternion.Euler(0f, 90f, 0f));
            _observerWrist.SetPositionAndRotation(_ownerWrist.position + new Vector3(0.35f, -0.2f, 0.15f),
                _ownerWrist.rotation);

            Vector3 grip = new Vector3(0f, 0.03f, 0.2f);
            HoldAtGrip(grip, Quaternion.identity);

            AssertGripPreserved(grip, Quaternion.identity,
                "wrist position error moves hand and prop together - it cannot separate them");
        }

        // ── Candidates that CAN separate the prop from the hand ──

        [Test]
        public void HeldOffset_IsExtrapolatedPastTheNewestSample()
        {
            // The grab offset is a constant of the hold, but Awake registers the transform channels with
            // interpolation on and sets Extrapolate = true / JitterBufferDepth = 1, so the receiver keeps
            // predicting it forward. A grip that shifts once should not keep sliding afterwards.
            _ownerWrist.SetPositionAndRotation(Vector3.up, Quaternion.identity);
            _observerWrist.SetPositionAndRotation(Vector3.up, Quaternion.identity);

            HoldAtGrip(new Vector3(0f, 0f, 0.20f), Quaternion.identity);
            Vector3 settled = new Vector3(0f, 0f, 0.26f);
            HoldAtGrip(settled, Quaternion.identity, feeds: 1);

            // No further packets - the grip settled, so nothing changes and the owner stops sending.
            for (int i = 0; i < 6; i++) Tick();

            Vector3 decoded = DecodedGrip();
            Assert.LessOrEqual(Vector3.Distance(decoded, settled), 0.005f,
                $"with no new packets the grip must hold at the last received value {settled}, " +
                $"but the receiver extrapolated it to {decoded}");
        }

        [Test]
        public void ObserverResolvingAnotherPlayersWrist_PutsPropInTheWrongHand()
        {
            // TakeOwnershipAsync races the attach packets: currentOwnedPlayer can still name the previous
            // owner when the first held frames apply, and the weld silently follows whoever that is.
            _ownerWrist.SetPositionAndRotation(new Vector3(1f, 1.3f, 0f), Quaternion.identity);
            _observerWrist.SetPositionAndRotation(new Vector3(1f, 1.3f, 0f), Quaternion.identity);

            Vector3 grip = new Vector3(0f, 0f, 0.15f);
            HoldAtGrip(grip, Quaternion.identity);
            Vector3 correct = _remote.Target.position;

            Transform wrongWrist = MakeHolderRig("bystander", out BasisNetworkPlayer bystander);
            wrongWrist.SetPositionAndRotation(new Vector3(-3f, 1.3f, 4f), Quaternion.identity);
            _remote.currentOwnedPlayer = bystander;
            SeedHandCache(_remote, bystander, wrongWrist);
            Tick();

            Assert.Greater(Vector3.Distance(_remote.Target.position, correct), 1f,
                "a stale currentOwnedPlayer welds the prop to the wrong player's hand with no fallback");
        }

        [Test]
        public void InteractableOnChildOfSyncTarget_RetargetsToTheDrivenTransform()
        {
            // BasisPickupSyncNetworking.Awake resolves its interactable with GetComponentInChildren, so the
            // interactable may sit on a child. The pickup moves ITS OWN transform into the hand, so Target
            // has to follow it or every remote inherits a fixed miss equal to the child's offset.
            var root = new GameObject("prop-root");
            _cleanup.Add(root);
            var visual = new GameObject("prop-visual").transform;
            visual.SetParent(root.transform, false);
            visual.localPosition = new Vector3(0f, 0.4f, 0f);

            BasisPickupInteractable pickup = MakeHeldInteractable(visual.gameObject, BasisBoneTrackedRole.LeftHand);
            BasisPickupSyncNetworking sync = ConfigurePickup(root.AddComponent<BasisPickupSyncNetworking>(), root.transform);
            Assert.AreSame(pickup.transform, sync.Target, "Awake must retarget onto the transform the pickup drives");

            sync.currentOwnedPlayer = _sender.currentOwnedPlayer;
            sync.IsOwnedLocallyOnClient = true;
            SeedHandCache(sync, _sender.currentOwnedPlayer, _ownerWrist);

            _ownerWrist.SetPositionAndRotation(new Vector3(0f, 1.2f, 0f), Quaternion.identity);
            // What the pickup actually does while held: move its own transform onto the hand.
            Vector3 grip = new Vector3(0f, 0f, 0.15f);
            visual.SetPositionAndRotation(_ownerWrist.position + _ownerWrist.rotation * grip, _ownerWrist.rotation);

            BeforeTransmitM.Invoke(sync, null);
            Vector3 streamed = StreamedGrip(sync);

            Assert.LessOrEqual(Vector3.Distance(streamed, grip), 0.01f,
                $"the streamed grip must describe where the held object is ({grip}), not where the sync " +
                $"root happens to sit ({streamed}) - the difference is a fixed miss on every remote");
        }

        [Test]
        public void HeldWithUnresolvableWrist_FallsBackToWorldStreaming()
        {
            // Both ends freeze while the holder's hand cannot be resolved, which rides out an avatar swap.
            // For an avatar that never resolves a hand bone that condition never clears, so the freeze has
            // to be bounded or the prop is stranded for the whole hold.
            _ownerWrist.SetPositionAndRotation(new Vector3(2f, 1.2f, 0f), Quaternion.identity);
            _observerWrist.SetPositionAndRotation(new Vector3(2f, 1.2f, 0f), Quaternion.identity);
            HoldAtGrip(new Vector3(0f, 0f, 0.15f), Quaternion.identity);
            Vector3 stranded = _remote.Target.position;

            // Holder's avatar stops resolving on both ends (loading, swapping, or simply not humanoid).
            _sender.currentOwnedPlayer = null;
            _remote.currentOwnedPlayer = null;

            // The owner keeps holding it and walks away, well past the freeze grace window.
            for (int i = 0; i < 20; i++)
            {
                _sender.Target.position += new Vector3(0.5f, 0f, 0f);
                BeforeTransmitM.Invoke(_sender, null);
                Feed();
                Tick();
            }

            Assert.Greater(Vector3.Distance(_remote.Target.position, stranded), 0.5f,
                "an unresolvable hand must not strand the prop - it should fall back to world streaming " +
                "instead of freezing for the rest of the hold");
        }

        // ── rig ──

        BasisPickupSyncNetworking CreatePickup(string name)
        {
            var go = new GameObject(name);
            _cleanup.Add(go);
            return ConfigurePickup(go.AddComponent<BasisPickupSyncNetworking>(), go.transform);
        }

        static BasisPickupSyncNetworking ConfigurePickup(BasisPickupSyncNetworking p, Transform target)
        {
            p.Target = target;
            p.AttachToHandOnGrab = true;
            p.SyncPosition = true; p.PositionX = true; p.PositionY = true; p.PositionZ = true;
            p.SyncRotation = true; p.RotationX = true; p.RotationY = true; p.RotationZ = true;
            p.SyncScale = true; p.ScaleX = true; p.ScaleY = true; p.ScaleZ = true;
            p.UseChecksum = false;
            AwakeM.Invoke(p, null);
            EnsureBuffersM.Invoke(p, null);
            p.ApplySyncConfig();
            return p;
        }

        /// <summary>One client's reconstruction of the holder: avatar + animator + a wrist transform.</summary>
        Transform MakeHolderRig(string name, out BasisNetworkPlayer netPlayer)
        {
            var avatarGo = new GameObject(name);
            _cleanup.Add(avatarGo);
            var animator = avatarGo.AddComponent<Animator>();
            var avatar = avatarGo.AddComponent<BasisAvatar>();
            avatar.Animator = animator;

            var wrist = new GameObject("wrist").transform;
            wrist.SetParent(avatarGo.transform, false);

            var player = (BasisRemotePlayer)FormatterServices.GetUninitializedObject(typeof(BasisRemotePlayer));
            player.BasisAvatar = avatar;
            netPlayer = (BasisNetworkPlayer)FormatterServices.GetUninitializedObject(typeof(BasisUnInitializedPlayer));
            PlayerF.SetValue(netPlayer, player);
            return wrist;
        }

        static void SeedHandCache(BasisPickupSyncNetworking p, BasisNetworkPlayer holder, Transform wrist)
        {
            holder.TryGetPlayer(out IBasisPlayer player);
            CachedAnimF.SetValue(p, player.BasisAvatar.Animator);
            CachedIdF.SetValue(p, (int)HandLeft);
            CachedHandF.SetValue(p, wrist);
        }

        /// <summary>An interactable reporting a live grab in <paramref name="role"/>.</summary>
        static BasisPickupInteractable MakeHeldInteractable(GameObject on, BasisBoneTrackedRole role)
        {
            var pickup = on.AddComponent<BasisPickupInteractable>();
            BasisInputWrapper held = default;
            object boxed = held;
            InputStateF.SetValue(boxed, BasisInteractInputState.Interacting);
            held = (BasisInputWrapper)boxed;

            if (role == BasisBoneTrackedRole.LeftHand) pickup.Inputs.leftHand = held;
            else pickup.Inputs.rightHand = held;
            return pickup;
        }

        void AttachInteractable(BasisPickupSyncNetworking p, BasisBoneTrackedRole role)
            => p.BasisPickupInteractable = MakeHeldInteractable(p.gameObject, role);

        /// <summary>Owner holds the prop at <paramref name="grip"/> in its wrist frame, then transmits.</summary>
        void HoldAtGrip(Vector3 grip, Quaternion gripRot, int feeds = 2)
        {
            _ownerWrist.GetPositionAndRotation(out Vector3 hp, out Quaternion hr);
            _sender.Target.SetPositionAndRotation(hp + hr * grip, hr * gripRot);
            BeforeTransmitM.Invoke(_sender, null);
            Feed(feeds);
            Tick();
        }

        /// <summary>Two feeds prime the receiver's current/next pair; one leaves the newest pair differing.</summary>
        void Feed(int count = 2)
        {
            var recv = (BasisSyncReceiver)ReceiverF.GetValue(_remote);
            var schema = (BasisSyncSchema)SchemaF.GetValue(_sender);
            var vals = (BasisSyncValues)LocalF.GetValue(_sender);
            for (int i = 0; i < count; i++) BasisSyncTestSupport.FeedKeyframe(recv, schema, vals, ++_seq);
        }

        /// <summary>One full remote frame: interpolate + apply, then the post-remote-bones re-weld.</summary>
        static void Tick(float dt = BasisSyncTestSupport.Dt)
        {
            BasisSyncDriver.ScheduleRemote(dt);
            BasisSyncDriver.CompleteRemote();
            BasisSyncDriver.ReweldAttachedPickups();
        }

        /// <summary>Where the observer's prop sits in the observer's own wrist frame.</summary>
        Vector3 DecodedGrip()
        {
            _observerWrist.GetPositionAndRotation(out Vector3 hp, out Quaternion hr);
            return Quaternion.Inverse(hr) * (_remote.Target.position - hp);
        }

        /// <summary>The grip the owner just wrote into the transform channels, read back from the wire values.</summary>
        Vector3 StreamedGrip(BasisPickupSyncNetworking p)
        {
            var schema = (BasisSyncSchema)SchemaF.GetValue(p);
            var vals = (BasisSyncValues)LocalF.GetValue(p);
            BasisSyncValues round = BasisSyncTestSupport.RoundTripKeyframe(schema, vals);
            return new Vector3(round.Cont[0], round.Cont[1], round.Cont[2]);
        }

        void AssertGripPreserved(Vector3 grip, Quaternion gripRot, string why)
        {
            _observerWrist.GetPositionAndRotation(out Vector3 hp, out Quaternion hr);
            Vector3 expected = hp + hr * grip;
            Quaternion expectedRot = hr * gripRot;
            Assert.LessOrEqual(Vector3.Distance(_remote.Target.position, expected), 0.005f, why + " (position)");
            Assert.LessOrEqual(Quaternion.Angle(_remote.Target.rotation, expectedRot), 1.5f, why + " (rotation)");
        }
    }
}
