using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
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
    /// Two clients of one held prop, wired the way production wires them: the holder resolves its hand
    /// frame through the LOCAL branch of <see cref="BasisHandGrip.TryGetPlayerFrame"/> (the local rig
    /// driver's mapping) and the observer through the REMOTE branch (that remote's avatar driver
    /// references), each with its own rig in its own place.
    ///
    /// That split is the whole point. The reconstruction is a change of coordinates, so it is exact for
    /// ANY frame as long as both ends build the same one — which means a networked hold can only be wrong
    /// if the two ends disagree, and the two ends can only disagree where they resolve differently. Suites
    /// that make both ends a <see cref="BasisRemotePlayer"/> exercise one branch twice and cannot see it.
    ///
    /// The real transmit (<c>OnBeforeTransmit</c>) and the real receive path (driver schedule → complete →
    /// reweld) run; only the input stack and the transport are staged.
    /// </summary>
    [NoAutoStaticsCleanup]
    public abstract class BasisPickupWeldFixture
    {
        protected const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        protected const BindingFlags NPS = BindingFlags.NonPublic | BindingFlags.Static;

        static readonly FieldInfo ReceiverF = typeof(BasisSyncedObject).GetField("_receiver", NP);
        static readonly FieldInfo SchemaF = typeof(BasisSyncedObject).GetField("_schema", NP);
        static readonly FieldInfo LocalF = typeof(BasisSyncedObject).GetField("_local", NP);
        static readonly FieldInfo PlayerF = typeof(BasisNetworkPlayer).GetField("_player", NP);
        static readonly FieldInfo InputStateF = typeof(BasisInputWrapper).GetField("State", NP);
        static readonly FieldInfo InstanceF = typeof(BasisLocalPlayer).GetField("<Instance>k__BackingField", NPS);
        protected static readonly FieldInfo GripAlignedF = typeof(BasisPickupInteractable).GetField("_gripAlignedHold", NP);
        protected static readonly MethodInfo AwakeM = typeof(BasisPickupSyncNetworking).GetMethod("Awake", NP);
        protected static readonly MethodInfo EnsureBuffersM = typeof(BasisSyncedObject).GetMethod("EnsureBuffers", NP);
        protected static readonly MethodInfo BeforeTransmitM = typeof(BasisPickupSyncNetworking).GetMethod("OnBeforeTransmit", NP);

        protected readonly List<GameObject> _cleanup = new List<GameObject>();
        protected BasisPickupSyncNetworking _sender;
        protected BasisPickupSyncNetworking _remote;
        protected Transform _ownerWrist;
        protected Transform _observerWrist;
        protected BasisTransformMapping _ownerMapping;
        protected BasisTransformMapping _observerMapping;
        protected BasisLocalPlayer _localPlayer;
        BasisTransformMapping _savedAvatarMapping;
        BasisLocalPlayer _savedInstance;
        BasisLocalBoneControl _savedLeftHandControl;
        BasisLocalBoneControl _savedRightHandControl;
        BasisLocalBoneDriver _boneDriver;
        byte _seq;

        [SetUp]
        public void FixtureSetUp()
        {
            Assert.IsNotNull(BeforeTransmitM, "OnBeforeTransmit moved");
            Assert.IsNotNull(GripAlignedF, "_gripAlignedHold moved");
            _savedAvatarMapping = BasisLocalAvatarDriver.Mapping;
            _savedInstance = BasisLocalPlayer.Instance;
            _savedLeftHandControl = BasisLocalBoneDriver.LeftHandControl;
            _savedRightHandControl = BasisLocalBoneDriver.RightHandControl;
            BasisSyncDriver.Initialize();
            _sender = CreatePickup("owner-prop");
            _remote = CreatePickup("observer-prop");
            BasisSyncDriver.RegisterRemote(_remote);
            _seq = 0;
        }

        [TearDown]
        public void FixtureTearDown()
        {
            if (_remote != null) BasisSyncDriver.UnregisterRemote(_remote);
            foreach (GameObject go in _cleanup)
            {
                if (go != null) Object.DestroyImmediate(go);
            }
            _cleanup.Clear();
            BasisSyncDriver.ReweldAttachedPickups();
            BasisSyncDriver.OnDestroy();
            BasisPickupWeldDiagnostics.Enabled = false;
            BasisPickupWeldDiagnostics.Clear();
            BasisLocalAvatarDriver.Mapping = _savedAvatarMapping;
            InstanceF.SetValue(null, _savedInstance);
            if (_boneDriver != null)
            {
                typeof(BasisLocalBoneDriver).GetMethod("DisposeNative", NP).Invoke(_boneDriver, null);
                _boneDriver = null;
            }
            BasisLocalBoneDriver.LeftHandControl = _savedLeftHandControl;
            BasisLocalBoneDriver.RightHandControl = _savedRightHandControl;
        }

        /// <summary>
        /// Gives the local hand bone controls a real native store and publishes a post-IK pose into it, so a
        /// test can make <see cref="BasisLocalBoneControl.IKWorldData"/> differ from the live bone transform —
        /// which is exactly what the engine-driven animator stage does to the real client every frame.
        /// </summary>
        protected void PublishPostIKHandPose(Vector3 position, Quaternion rotation)
        {
            if (_boneDriver == null)
            {
                _boneDriver = new BasisLocalBoneDriver();
                var leftHand = new BasisLocalBoneControl();
                var rightHand = new BasisLocalBoneControl();
                _boneDriver.Controls = new[] { leftHand, rightHand };
                _boneDriver.ControlsLength = 2;
                typeof(BasisLocalBoneDriver).GetMethod("EnsureNativeAllocated", NP).Invoke(_boneDriver, null);
                BasisLocalBoneDriver.LeftHandControl = leftHand;
                BasisLocalBoneDriver.RightHandControl = rightHand;
            }
            BasisLocalBoneDriver.LeftHandControl.SetIKWorldData(position, rotation);
            BasisLocalBoneDriver.RightHandControl.SetIKWorldData(position, rotation);
        }

        // ── rig ──

        /// <summary>
        /// Owner side resolves as the local player, observer side as a remote. The observer's rig can be
        /// given a different bind orientation, a different hand size, or no fingers at all — the cases a
        /// substitute avatar puts in front of the reconstruction.
        /// </summary>
        protected void BuildProductionPair(
            bool observerFingers = true,
            Quaternion? observerBind = null,
            float observerHandScale = 1f,
            bool ownerFingers = true)
        {
            _ownerWrist = MakeRig("holder-on-owner", ownerFingers, Quaternion.identity, 1f, out _ownerMapping, out BasisAvatar ownerAvatar);
            _observerWrist = MakeRig("holder-on-observer", observerFingers, observerBind ?? Quaternion.identity,
                observerHandScale, out _observerMapping, out BasisAvatar observerAvatar);

            // Production points BasisLocalAvatarDriver.Mapping (what the local weld builds from) and the rig
            // driver's mapping (what the transmit measures against) at ONE object; the fixture does the same,
            // so a divergence there would be a real code change rather than a fixture artefact.
            _localPlayer = (BasisLocalPlayer)FormatterServices.GetUninitializedObject(typeof(BasisLocalPlayer));
            _localPlayer.LocalRigDriver = new BasisLocalRigDriver { basisTransformMapping = _ownerMapping };
            // The legacy wrist id resolves through the avatar's Animator, so the holder needs one on both ends.
            _localPlayer.BasisAvatar = ownerAvatar;
            InstanceF.SetValue(null, _localPlayer);
            BasisLocalAvatarDriver.Mapping = _ownerMapping;

            var ownerView = (BasisNetworkPlayer)FormatterServices.GetUninitializedObject(typeof(BasisUnInitializedPlayer));
            PlayerF.SetValue(ownerView, _localPlayer);
            _sender.currentOwnedPlayer = ownerView;
            _sender.IsOwnedLocallyOnClient = true;
            _sender.BasisPickupInteractable = MakeHeldInteractable(_sender.gameObject);

            _remote.currentOwnedPlayer = MakeRemoteView(observerAvatar, _observerMapping);
            _remote.IsOwnedLocallyOnClient = false;
            _remote.BasisPickupInteractable = MakeHeldInteractable(_remote.gameObject);
        }

        /// <summary>Replaces the player behind a pickup's owner entry — used to stage a holder that no longer resolves.</summary>
        protected static void SetHolderPlayer(BasisPickupSyncNetworking pickup, IBasisPlayer player) =>
            PlayerF.SetValue(pickup.currentOwnedPlayer, player);

        /// <summary>A BasisNetworkPlayer whose player is a typed remote holding the given rig.</summary>
        protected BasisNetworkPlayer MakeRemoteView(BasisAvatar avatar, BasisTransformMapping mapping)
        {
            var remotePlayer = (BasisRemotePlayer)FormatterServices.GetUninitializedObject(typeof(BasisRemotePlayer));
            remotePlayer.BasisAvatar = avatar;
            remotePlayer.RemoteAvatarDriver = new BasisRemoteAvatarDriver { References = mapping };
            var view = (BasisNetworkPlayer)FormatterServices.GetUninitializedObject(typeof(BasisUnInitializedPlayer));
            PlayerF.SetValue(view, remotePlayer);
            return view;
        }

        protected Transform MakeRig(string name, bool fingers, Quaternion bind, float handScale,
            out BasisTransformMapping mapping, out BasisAvatar avatar)
        {
            var avatarGo = new GameObject(name);
            _cleanup.Add(avatarGo);
            var animator = avatarGo.AddComponent<Animator>();
            avatar = avatarGo.AddComponent<BasisAvatar>();
            avatar.Animator = animator;

            var wrist = new GameObject("wrist").transform;
            wrist.SetParent(avatarGo.transform, false);
            wrist.localRotation = bind;

            mapping = new BasisTransformMapping();
            mapping.leftHand = wrist;
            mapping.HasleftHand = true;
            mapping.rightHand = wrist;
            mapping.HasrightHand = true;

            if (fingers)
            {
                mapping.LeftMiddle[0] = Knuckle(wrist, "middle", new Vector3(0f, 0f, 0.09f) * handScale);
                mapping.HasLeftMiddle[0] = true;
                mapping.LeftIndex[0] = Knuckle(wrist, "index", new Vector3(0.02f, 0f, 0.085f) * handScale);
                mapping.HasLeftIndex[0] = true;
                mapping.LeftLittle[0] = Knuckle(wrist, "little", new Vector3(-0.03f, 0f, 0.075f) * handScale);
                mapping.HasLeftLittle[0] = true;

                mapping.RightMiddle[0] = mapping.LeftMiddle[0];
                mapping.HasRightMiddle[0] = true;
                mapping.RightIndex[0] = mapping.LeftIndex[0];
                mapping.HasRightIndex[0] = true;
                mapping.RightLittle[0] = mapping.LeftLittle[0];
                mapping.HasRightLittle[0] = true;
            }
            return wrist;
        }

        static Transform Knuckle(Transform wrist, string name, Vector3 fromWrist)
        {
            var go = new GameObject(name);
            go.transform.SetParent(wrist, false);
            go.transform.localPosition = fromWrist;
            return go.transform;
        }

        protected static BasisPickupInteractable MakeHeldInteractable(GameObject on, bool left = true)
        {
            var pickup = on.AddComponent<BasisPickupInteractable>();
            BasisInputWrapper held = default;
            object boxed = held;
            InputStateF.SetValue(boxed, BasisInteractInputState.Interacting);
            if (left) pickup.Inputs.leftHand = (BasisInputWrapper)boxed;
            else pickup.Inputs.rightHand = (BasisInputWrapper)boxed;
            return pickup;
        }

        /// <summary>Switches the staged hold to the other hand, which changes the streamed id.</summary>
        protected static void HoldWith(BasisPickupInteractable pickup, bool left)
        {
            BasisInputWrapper held = default;
            object boxed = held;
            InputStateF.SetValue(boxed, BasisInteractInputState.Interacting);
            BasisInputWrapper interacting = (BasisInputWrapper)boxed;
            BasisInputWrapper idle = default;
            pickup.Inputs.leftHand = left ? interacting : idle;
            pickup.Inputs.rightHand = left ? idle : interacting;
        }

        protected Transform AddGripPoint(BasisPickupSyncNetworking p, Vector3 localPos, Quaternion localRot, bool aligned)
        {
            var go = new GameObject("Grip");
            go.transform.SetParent(p.Target, false);
            go.transform.SetLocalPositionAndRotation(localPos, localRot);
            p.BasisPickupInteractable.GripPoint = go.transform;
            // A GripPoint alone does not flag the authored-grip id — the owner has to actually be holding by
            // it (BasisPickupInteractable.HoldIsGripAligned, latched in OnInteractStart). Staged holds set it.
            if (aligned) GripAlignedF.SetValue(p.BasisPickupInteractable, true);
            return go.transform;
        }

        protected BasisPickupSyncNetworking CreatePickup(string name, bool syncPositionY = true)
        {
            var go = new GameObject(name);
            _cleanup.Add(go);
            BasisPickupSyncNetworking p = go.AddComponent<BasisPickupSyncNetworking>();
            p.Target = go.transform;
            p.AttachToHandOnGrab = true;
            p.SyncPosition = true; p.PositionX = true; p.PositionY = syncPositionY; p.PositionZ = true;
            p.SyncRotation = true; p.RotationX = true; p.RotationY = true; p.RotationZ = true;
            p.SyncScale = true; p.ScaleX = true; p.ScaleY = true; p.ScaleZ = true;
            p.UseChecksum = false;
            AwakeM.Invoke(p, null);
            EnsureBuffersM.Invoke(p, null);
            p.ApplySyncConfig();
            return p;
        }

        // ── drive ──

        protected BasisHandFrame OwnerFrame(bool left = true)
        {
            Assert.IsTrue(BasisHandGrip.TryGetFrame(BasisLocalAvatarDriver.Mapping, left,
                _ownerWrist.position, _ownerWrist.rotation, out BasisHandFrame frame), "owner frame");
            return frame;
        }

        protected BasisHandFrame ObserverFrame(bool left = true)
        {
            Assert.IsTrue(BasisHandGrip.TryGetFrame(_observerMapping, left,
                _observerWrist.position, _observerWrist.rotation, out BasisHandFrame frame), "observer frame");
            return frame;
        }

        /// <summary>Owner welds the prop into its own hand frame at <paramref name="grip"/>, then transmits.</summary>
        protected void HoldWelded(Vector3 grip, Quaternion gripRot, bool left = true)
        {
            BasisHandFrame frame = OwnerFrame(left);
            Assert.IsTrue(frame.Canonical, "the owner rig must be canonical for this to mean anything");
            _sender.Target.SetPositionAndRotation(frame.Position + frame.Rotation * grip, frame.Rotation * gripRot);
            Transmit();
        }

        /// <summary>Runs the real transmit, delivers it, and advances the receiver a frame.</summary>
        protected void Transmit()
        {
            BeforeTransmitM.Invoke(_sender, null);
            Feed();
            Tick();
        }

        /// <summary>
        /// Runs the receiver forward on the value already sent until interpolation has nothing left to
        /// converge on, so an assertion is about the weld rather than about the jitter buffer.
        /// </summary>
        protected void Settle()
        {
            for (int i = 0; i < 3; i++)
            {
                Feed();
                Tick();
            }
        }

        protected void Feed(int count = 2)
        {
            var recv = (BasisSyncReceiver)ReceiverF.GetValue(_remote);
            var schema = (BasisSyncSchema)SchemaF.GetValue(_sender);
            var vals = (BasisSyncValues)LocalF.GetValue(_sender);
            for (int i = 0; i < count; i++) BasisSyncTestSupport.FeedKeyframe(recv, schema, vals, ++_seq);
        }

        protected static void Tick(float dt = BasisSyncTestSupport.Dt)
        {
            BasisSyncDriver.ScheduleRemote(dt);
            BasisSyncDriver.CompleteRemote();
            BasisSyncDriver.ReweldAttachedPickups();
        }

        /// <summary>The observer's prop must sit where the owner's does relative to its own copy of the hand.</summary>
        protected void AssertObserverMatchesOwner(Vector3 grip, Quaternion gripRot, string why, bool left = true, float handRatio = 1f)
        {
            BasisHandFrame frame = ObserverFrame(left);
            Assert.LessOrEqual(Vector3.Distance(_remote.Target.position, frame.Position + frame.Rotation * (grip * handRatio)), 0.005f,
                why + " (position)");
            Assert.LessOrEqual(Quaternion.Angle(_remote.Target.rotation, frame.Rotation * gripRot), 1.5f,
                why + " (rotation)");
        }
    }
}
