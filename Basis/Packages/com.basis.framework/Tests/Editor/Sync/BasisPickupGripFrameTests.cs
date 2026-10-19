using Unity.Scripting.LifecycleManagement;
using System.Reflection;
using System.Runtime.Serialization;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Sync;
using Basis.Scripts.TransformBinders.BoneControl;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// The canonical-hand-frame grip path, driven owner-to-observer the way two clients actually work:
    /// the real OnBeforeTransmit measures on one rig and the real receive path decodes on a SEPARATE rig
    /// with its OWN bone layout.
    ///
    /// BasisPickupWristFrameTests covers the legacy wrist-bone encoding, which only reconstructs when both
    /// ends have the same avatar. These give the two ends genuinely different rigs — different wrist bind
    /// rotations, different hand sizes, and in one case no fingers at all — because that is the case the
    /// frame exists for: an observer drawing a fallback or substitute avatar must still get the prop into
    /// the hand it is drawing.
    /// </summary>
    [NoAutoStaticsCleanup]
    public class BasisPickupGripFrameTests
    {
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

        static readonly FieldInfo ReceiverF = typeof(BasisSyncedObject).GetField("_receiver", NP);
        static readonly FieldInfo SchemaF = typeof(BasisSyncedObject).GetField("_schema", NP);
        static readonly FieldInfo LocalF = typeof(BasisSyncedObject).GetField("_local", NP);
        static readonly FieldInfo PlayerF = typeof(BasisNetworkPlayer).GetField("_player", NP);
        static readonly FieldInfo InputStateF = typeof(BasisInputWrapper).GetField("State", NP);
        static readonly FieldInfo GripAlignedF = typeof(BasisPickupInteractable).GetField("_gripAlignedHold", NP);
        static readonly FieldInfo CachedAnimF = typeof(BasisPickupSyncNetworking).GetField("_cachedHandAnimator", NP);
        static readonly FieldInfo CachedIdF = typeof(BasisPickupSyncNetworking).GetField("_cachedHandId", NP);
        static readonly FieldInfo CachedHandF = typeof(BasisPickupSyncNetworking).GetField("_cachedHand", NP);
        const byte HandLeft = 1;
        static readonly MethodInfo AwakeM = typeof(BasisPickupSyncNetworking).GetMethod("Awake", NP);
        static readonly MethodInfo EnsureBuffersM = typeof(BasisSyncedObject).GetMethod("EnsureBuffers", NP);
        static readonly MethodInfo BeforeTransmitM = typeof(BasisPickupSyncNetworking).GetMethod("OnBeforeTransmit", NP);

        readonly System.Collections.Generic.List<GameObject> _cleanup = new System.Collections.Generic.List<GameObject>();
        BasisPickupSyncNetworking _sender;
        BasisPickupSyncNetworking _remote;
        Transform _ownerWrist;
        Transform _observerWrist;
        byte _seq;

        [SetUp]
        public void SetUp()
        {
            Assert.IsNotNull(BeforeTransmitM, "OnBeforeTransmit moved");
            BasisSyncDriver.Initialize();
            _sender = CreatePickup("owner-prop");
            _remote = CreatePickup("observer-prop");
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

        [Test]
        public void DifferentBindRotations_ObserverHoldsTheSameGrip()
        {
            // Same hand, rigged with a completely different wrist bind on the observer's copy. The wrist-bone
            // encoding cannot survive this; a frame built from joint positions has to.
            BuildPair(ownerBind: Quaternion.identity, observerBind: Quaternion.Euler(40f, -110f, 75f));
            _ownerWrist.position = new Vector3(1f, 1.4f, -2f);
            _observerWrist.position = new Vector3(-4f, 0.9f, 7f);

            Vector3 grip = new Vector3(0.03f, -0.015f, 0.11f);
            Quaternion gripRot = Quaternion.Euler(0f, 25f, 90f);
            HoldAtGrip(grip, gripRot);

            AssertGripPreserved(grip, gripRot, 1f,
                "a grip measured in the canonical hand frame must decode into a differently rigged hand");
        }

        [Test]
        public void LargerObserverHand_HoldsTheGripProportionally()
        {
            BuildPair(Quaternion.identity, Quaternion.Euler(0f, 90f, 0f), observerHandScale: 2f);
            _ownerWrist.position = new Vector3(0f, 1.3f, 0f);
            _observerWrist.position = new Vector3(3f, 1.1f, 1f);

            Vector3 grip = new Vector3(0.02f, 0f, 0.06f);
            HoldAtGrip(grip, Quaternion.identity);

            // Offsets travel as multiples of hand length, so a hand twice the size holds it twice as far out
            // rather than pulling it back into a palm it no longer fits.
            AssertGripPreserved(grip, Quaternion.identity, 2f,
                "a bigger hand must hold the grip proportionally, not at the holder's absolute reach");
        }

        [Test]
        public void AuthoredGrip_ObserverPutsTheGripPointOnItsOwnPalm()
        {
            BuildPair(Quaternion.identity, Quaternion.Euler(-25f, 160f, 40f), observerHandScale: 1.4f);
            Transform senderGrip = AddGripPoint(_sender, new Vector3(0f, 0.02f, -0.3f), Quaternion.Euler(0f, 0f, 30f));
            Transform observerGrip = AddGripPoint(_remote, new Vector3(0f, 0.02f, -0.3f), Quaternion.Euler(0f, 0f, 30f));

            _ownerWrist.position = new Vector3(0f, 1.3f, 0f);
            _observerWrist.position = new Vector3(-2f, 1.5f, 4f);

            // The owner holds it exactly where its own grip solve puts it.
            Assert.IsTrue(BasisHandGrip.TryGetPlayerFrame(Player(_sender), true, out BasisHandFrame ownerFrame));
            _sender.Target.GetPositionAndRotation(out Vector3 op, out Quaternion orot);
            Assert.IsTrue(_sender.BasisPickupInteractable.TryGetGripOffsets(op, orot, out Vector3 offPos, out Quaternion offRot));
            _sender.Target.SetPositionAndRotation(ownerFrame.Position + ownerFrame.Rotation * offPos, ownerFrame.Rotation * offRot);
            Assert.LessOrEqual(Vector3.Distance(senderGrip.position, ownerFrame.Position), 0.001f, "owner-side sanity");

            BeforeTransmitM.Invoke(_sender, null);
            Feed();
            Tick();

            Assert.IsTrue(BasisHandGrip.TryGetPlayerFrame(Player(_remote), true, out BasisHandFrame observerFrame));
            Assert.LessOrEqual(Vector3.Distance(observerGrip.position, observerFrame.Position), 0.005f,
                "an authored grip is solved from the prefab on both ends, so it must land exactly on the palm");
            Assert.LessOrEqual(Quaternion.Angle(observerGrip.rotation, observerFrame.Rotation), 1.5f,
                "and take the hand's orientation, with nothing about the holder's rig on the wire");
        }

        [Test]
        public void NoFingerBones_FallsBackToTheWristFrameAndStillHolds()
        {
            BuildPair(Quaternion.Euler(10f, 30f, 0f), Quaternion.Euler(10f, 30f, 0f), fingers: false);
            _ownerWrist.position = new Vector3(0.5f, 1.2f, 0.3f);
            _observerWrist.position = new Vector3(-3f, 1.2f, 2f);
            // The legacy frame resolves through Animator.GetBoneTransform, which needs a humanoid Avatar the
            // test rigs do not have; seed the bone cache the same way BasisPickupWristFrameTests does.
            SeedHandCache(_sender, _ownerWrist);
            SeedHandCache(_remote, _observerWrist);

            Vector3 grip = new Vector3(0.05f, -0.02f, 0.1f);
            Quaternion gripRot = Quaternion.Euler(15f, 0f, 45f);

            // With no fingers the frame degrades to the wrist bone in metres — the id says so, and both ends
            // must agree on that reading rather than one of them scaling by a hand length it never measured.
            _ownerWrist.GetPositionAndRotation(out Vector3 hp, out Quaternion hr);
            _sender.Target.SetPositionAndRotation(hp + hr * grip, hr * gripRot);
            BeforeTransmitM.Invoke(_sender, null);
            Feed();
            Tick();

            _observerWrist.GetPositionAndRotation(out Vector3 ohp, out Quaternion ohr);
            Assert.LessOrEqual(Vector3.Distance(_remote.Target.position, ohp + ohr * grip), 0.005f,
                "a fingerless avatar must still hold the prop, in metres against the wrist");
        }

        // ── rig ──

        BasisPickupSyncNetworking CreatePickup(string name)
        {
            var go = new GameObject(name);
            _cleanup.Add(go);
            BasisPickupSyncNetworking p = go.AddComponent<BasisPickupSyncNetworking>();
            p.Target = go.transform;
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

        void BuildPair(Quaternion ownerBind, Quaternion observerBind, float observerHandScale = 1f, bool fingers = true)
        {
            _ownerWrist = MakeHolderRig("holder-on-owner", ownerBind, 1f, fingers, out BasisNetworkPlayer ownerView);
            _observerWrist = MakeHolderRig("holder-on-observer", observerBind, observerHandScale, fingers, out BasisNetworkPlayer observerView);

            _sender.currentOwnedPlayer = ownerView;
            _sender.IsOwnedLocallyOnClient = true;
            _sender.BasisPickupInteractable = MakeHeldInteractable(_sender.gameObject);

            _remote.currentOwnedPlayer = observerView;
            _remote.IsOwnedLocallyOnClient = false;
            _remote.BasisPickupInteractable = MakeHeldInteractable(_remote.gameObject);
        }

        /// <summary>One client's reconstruction of the holder: avatar, wrist, and the knuckles the frame is built from.</summary>
        Transform MakeHolderRig(string name, Quaternion bind, float handScale, bool fingers, out BasisNetworkPlayer netPlayer)
        {
            var avatarGo = new GameObject(name);
            _cleanup.Add(avatarGo);
            var animator = avatarGo.AddComponent<Animator>();
            var avatar = avatarGo.AddComponent<BasisAvatar>();
            avatar.Animator = animator;

            var wrist = new GameObject("wrist").transform;
            wrist.SetParent(avatarGo.transform, false);
            wrist.rotation = bind;

            var mapping = new BasisTransformMapping();
            mapping.leftHand = wrist;
            mapping.HasleftHand = true;

            if (fingers)
            {
                mapping.LeftMiddle[0] = Knuckle(wrist, "middle", new Vector3(0f, 0f, 0.09f) * handScale);
                mapping.HasLeftMiddle[0] = true;
                mapping.LeftIndex[0] = Knuckle(wrist, "index", new Vector3(0.02f, 0f, 0.085f) * handScale);
                mapping.HasLeftIndex[0] = true;
                mapping.LeftLittle[0] = Knuckle(wrist, "little", new Vector3(-0.03f, 0f, 0.075f) * handScale);
                mapping.HasLeftLittle[0] = true;
            }

            var player = (BasisRemotePlayer)FormatterServices.GetUninitializedObject(typeof(BasisRemotePlayer));
            player.BasisAvatar = avatar;
            player.RemoteAvatarDriver = new BasisRemoteAvatarDriver { References = mapping };
            netPlayer = (BasisNetworkPlayer)FormatterServices.GetUninitializedObject(typeof(BasisUnInitializedPlayer));
            PlayerF.SetValue(netPlayer, player);
            return wrist;
        }

        static void SeedHandCache(BasisPickupSyncNetworking p, Transform wrist)
        {
            p.currentOwnedPlayer.TryGetPlayer(out IBasisPlayer player);
            CachedAnimF.SetValue(p, player.BasisAvatar.Animator);
            CachedIdF.SetValue(p, (int)HandLeft);
            CachedHandF.SetValue(p, wrist);
        }

        static Transform Knuckle(Transform wrist, string name, Vector3 fromWrist)
        {
            var go = new GameObject(name);
            go.transform.SetParent(wrist, false);
            go.transform.position = wrist.position + fromWrist;
            return go.transform;
        }

        static BasisPickupInteractable MakeHeldInteractable(GameObject on)
        {
            var pickup = on.AddComponent<BasisPickupInteractable>();
            BasisInputWrapper held = default;
            object boxed = held;
            InputStateF.SetValue(boxed, BasisInteractInputState.Interacting);
            pickup.Inputs.leftHand = (BasisInputWrapper)boxed;
            return pickup;
        }

        static Transform AddGripPoint(BasisPickupSyncNetworking p, Vector3 localPos, Quaternion localRot)
        {
            var go = new GameObject("Grip");
            go.transform.SetParent(p.Target, false);
            go.transform.SetLocalPositionAndRotation(localPos, localRot);
            p.BasisPickupInteractable.GripPoint = go.transform;
            // A GripPoint alone no longer flags the authored-grip id: the owner has to actually be holding
            // by it (see BasisPickupInteractable.HoldIsGripAligned). OnInteractStart sets that; this fixture
            // stages the held state directly, so seed the same latch.
            GripAlignedF.SetValue(p.BasisPickupInteractable, true);
            return go.transform;
        }

        static IBasisPlayer Player(BasisPickupSyncNetworking p)
        {
            p.currentOwnedPlayer.TryGetPlayer(out IBasisPlayer player);
            return player;
        }

        /// <summary>Owner holds the prop at <paramref name="grip"/> in its canonical hand frame, then transmits.</summary>
        void HoldAtGrip(Vector3 grip, Quaternion gripRot)
        {
            Assert.IsTrue(BasisHandGrip.TryGetPlayerFrame(Player(_sender), true, out BasisHandFrame frame));
            Assert.IsTrue(frame.Canonical, "the owner rig must produce a canonical frame for this test to mean anything");
            _sender.Target.SetPositionAndRotation(frame.Position + frame.Rotation * grip, frame.Rotation * gripRot);
            BeforeTransmitM.Invoke(_sender, null);
            Feed();
            Tick();
        }

        void Feed(int count = 2)
        {
            var recv = (BasisSyncReceiver)ReceiverF.GetValue(_remote);
            var schema = (BasisSyncSchema)SchemaF.GetValue(_sender);
            var vals = (BasisSyncValues)LocalF.GetValue(_sender);
            for (int i = 0; i < count; i++) BasisSyncTestSupport.FeedKeyframe(recv, schema, vals, ++_seq);
        }

        static void Tick(float dt = BasisSyncTestSupport.Dt)
        {
            BasisSyncDriver.ScheduleRemote(dt);
            BasisSyncDriver.CompleteRemote();
            BasisSyncDriver.ReweldAttachedPickups();
        }

        void AssertGripPreserved(Vector3 grip, Quaternion gripRot, float handRatio, string why)
        {
            Assert.IsTrue(BasisHandGrip.TryGetPlayerFrame(Player(_remote), true, out BasisHandFrame frame));
            Vector3 expected = frame.Position + frame.Rotation * (grip * handRatio);
            Assert.LessOrEqual(Vector3.Distance(_remote.Target.position, expected), 0.005f, why + " (position)");
            Assert.LessOrEqual(Quaternion.Angle(_remote.Target.rotation, frame.Rotation * gripRot), 1.5f, why + " (rotation)");
        }
    }
}
