using Unity.Scripting.LifecycleManagement;
using System.Reflection;
using System.Runtime.Serialization;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.NetworkedAvatar;
using Basis.Scripts.Networking.Sync;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// Drives the real BasisPickupSyncNetworking attach-to-hand path end to end (component -> codec ->
    /// receiver -> BasisSyncDriver -> ApplyInterpolated) in edit mode: held reconstruction, the
    /// post-remote-bones re-weld, release edges, unresolvable owners, and ownership churn.
    /// </summary>
    [NoAutoStaticsCleanup]
    public class BasisPickupAttachTests
    {
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

        static readonly FieldInfo ReceiverF = typeof(BasisSyncedObject).GetField("_receiver", NP);
        static readonly FieldInfo SchemaF = typeof(BasisSyncedObject).GetField("_schema", NP);
        static readonly FieldInfo LocalF = typeof(BasisSyncedObject).GetField("_local", NP);
        static readonly FieldInfo PlayerF = typeof(BasisNetworkPlayer).GetField("_player", NP);
        static readonly FieldInfo CachedAnimF = typeof(BasisPickupSyncNetworking).GetField("_cachedHandAnimator", NP);
        static readonly FieldInfo CachedIdF = typeof(BasisPickupSyncNetworking).GetField("_cachedHandId", NP);
        static readonly FieldInfo CachedHandF = typeof(BasisPickupSyncNetworking).GetField("_cachedHand", NP);
        static readonly FieldInfo AttachIdF = typeof(BasisPickupSyncNetworking).GetField("_attachId", NP);
        static readonly FieldInfo LastHandF = typeof(BasisPickupSyncNetworking).GetField("_lastAppliedHandId", NP);
        static readonly MethodInfo AwakeM = typeof(BasisPickupSyncNetworking).GetMethod("Awake", NP);
        static readonly MethodInfo EnsureBuffersM = typeof(BasisSyncedObject).GetMethod("EnsureBuffers", NP);
        static readonly MethodInfo EncodePoseM = typeof(BasisSyncedTransform).GetMethod("EncodePose", NP);
        static readonly MethodInfo BeforeTransmitM = typeof(BasisPickupSyncNetworking).GetMethod("OnBeforeTransmit", NP);

        readonly System.Collections.Generic.List<GameObject> _cleanup = new System.Collections.Generic.List<GameObject>();
        BasisPickupSyncNetworking _sender;
        BasisPickupSyncNetworking _remote;
        Transform _hand;
        Animator _holderAnimator;
        BasisNetworkPlayer _holder;
        byte _seq;

        [SetUp]
        public void SetUp()
        {
            Assert.IsNotNull(ReceiverF, "_receiver field moved");
            Assert.IsNotNull(PlayerF, "_player field moved");
            Assert.IsNotNull(AwakeM, "Awake moved");
            BasisSyncDriver.Initialize();

            _sender = CreatePickup("sender");
            _remote = CreatePickup("remote");
            _hand = MakeHolder(out _holderAnimator, out _holder);
            _remote.currentOwnedPlayer = _holder;
            SeedHandCache(_remote, 1);

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
        public void HeldProp_ReconstructsAtHandOffset()
        {
            Vector3 off = new Vector3(0.1f, 0.2f, 0.3f);
            Quaternion offRot = Quaternion.Euler(0f, 45f, 0f);
            _hand.SetPositionAndRotation(new Vector3(1f, 1.5f, -2f), Quaternion.Euler(10f, 60f, 0f));

            FeedHeld(off, offRot);
            Frame();

            AssertGlued(off, offRot, "held prop should reconstruct at hand * offset");
        }

        [Test]
        public void ApplyInterpolated_WeldsAgainstStaleHand_ReweldCatchesUp()
        {
            Vector3 off = new Vector3(0.1f, 0f, 0.25f);
            Quaternion offRot = Quaternion.identity;
            _hand.SetPositionAndRotation(new Vector3(0f, 1f, 0f), Quaternion.identity);

            FeedHeld(off, offRot);
            Frame();
            Vector3 stalePos = _remote.Target.position;

            // The remote bone jobs pose the holder's skeleton AFTER CompleteRemote ran ApplyInterpolated.
            _hand.SetPositionAndRotation(new Vector3(0.5f, 1f, 0.4f), Quaternion.Euler(0f, 30f, 0f));

            Assert.AreEqual(stalePos, _remote.Target.position,
                "before the re-weld pass the prop is welded to LAST frame's hand pose - the original lag bug");

            BasisSyncDriver.ReweldAttachedPickups();

            AssertGlued(off, offRot, "ReweldAttachedPickups should re-glue the prop to the freshly posed hand");
        }

        [Test]
        public void UnresolvableOwner_HoldsLastPose_AndReweldIsSafe()
        {
            Vector3 off = new Vector3(0f, 0.1f, 0.2f);
            _hand.SetPositionAndRotation(new Vector3(2f, 1f, 2f), Quaternion.identity);

            FeedHeld(off, Quaternion.identity);
            Frame();
            Vector3 glued = _remote.Target.position;

            _remote.currentOwnedPlayer = null;
            _hand.position += new Vector3(5f, 0f, 0f);
            Frame();

            Assert.AreEqual(glued, _remote.Target.position, "owner gone: prop must hold its last pose");
            Assert.DoesNotThrow(BasisSyncDriver.ReweldAttachedPickups, "re-weld must tolerate an unresolvable owner");
            Assert.AreEqual(glued, _remote.Target.position, "re-weld with no owner must not move the prop");
        }

        [Test]
        public void Release_HoldsInHandPoseOneFrame_ThenSnapsToWorld()
        {
            Vector3 off = new Vector3(0.2f, 0f, 0f);
            _hand.SetPositionAndRotation(new Vector3(1f, 1f, 1f), Quaternion.identity);
            FeedHeld(off, Quaternion.identity);
            Frame();
            Vector3 inHand = _remote.Target.position;

            Vector3 drop = new Vector3(4f, 0.5f, -3f);
            FeedFree(drop, Quaternion.identity);
            Frame();
            Assert.LessOrEqual(Vector3.Distance(_remote.Target.position, inHand), 0.02f,
                "release edge should hold the last in-hand pose for its single transition frame");

            for (int i = 0; i < 4; i++) Frame();
            Assert.LessOrEqual(Vector3.Distance(_remote.Target.position, drop), 0.02f,
                "after the snap lands the prop must sit at the streamed drop pose");
        }

        [Test]
        public void OwnershipChurn_ReleasePacket_DoesNotFlashStaleAttachPose()
        {
            Vector3 off = new Vector3(0.15f, 0.05f, 0.3f);
            _hand.SetPositionAndRotation(new Vector3(-1f, 1.2f, 3f), Quaternion.Euler(0f, 90f, 0f));
            FeedHeld(off, Quaternion.identity);
            Frame();
            Vector3 tenureOneInHand = _remote.Target.position;

            // Local player steals the prop mid-hold (real notification path), carries it elsewhere,
            // then ownership goes back to a remote who leaves it free at a third location.
            _remote.IsOwnedLocallyOnClient = true;
            _remote.OnOwnershipTransfer(_holder);
            _remote.Target.position = new Vector3(9f, 0f, -4f);
            _remote.IsOwnedLocallyOnClient = false;
            _remote.OnOwnershipTransfer(_holder);
            BasisSyncDriver.RegisterRemote(_remote);

            Vector3 free = new Vector3(6f, 0.5f, 6f);
            FeedFree(free, Quaternion.identity);
            Frame();
            Assert.Greater(Vector3.Distance(_remote.Target.position, tenureOneInHand), 0.05f,
                "the first frame after ownership churn must not flash the prop back to the previous " +
                "tenure's stale in-hand pose");

            for (int i = 0; i < 3; i++) Frame();
            Assert.LessOrEqual(Vector3.Distance(_remote.Target.position, free), 0.02f,
                "after the transition the prop must sit at the streamed free-state world pose");
        }

        // -- rig --

        BasisPickupSyncNetworking CreatePickup(string name)
        {
            var go = new GameObject(name);
            _cleanup.Add(go);
            var p = go.AddComponent<BasisPickupSyncNetworking>();
            p.Target = go.transform;
            p.SyncPosition = true; p.PositionX = true; p.PositionY = true; p.PositionZ = true;
            p.SyncRotation = true; p.RotationX = true; p.RotationY = true; p.RotationZ = true;
            p.SyncScale = true; p.ScaleX = true; p.ScaleY = true; p.ScaleZ = true;
            p.UseChecksum = false;
            AwakeM.Invoke(p, null);
            EnsureBuffersM.Invoke(p, null);
            p.ApplySyncConfig();
            return p;
        }

        Transform MakeHolder(out Animator animator, out BasisNetworkPlayer netPlayer)
        {
            var avatarGo = new GameObject("holder-avatar");
            _cleanup.Add(avatarGo);
            animator = avatarGo.AddComponent<Animator>();
            var avatar = avatarGo.AddComponent<BasisAvatar>();
            avatar.Animator = animator;

            var hand = new GameObject("hand").transform;
            hand.SetParent(avatarGo.transform, false);

            var remotePlayer = (BasisRemotePlayer)FormatterServices.GetUninitializedObject(typeof(BasisRemotePlayer));
            remotePlayer.BasisAvatar = avatar;
            netPlayer = (BasisNetworkPlayer)FormatterServices.GetUninitializedObject(typeof(BasisUnInitializedPlayer));
            PlayerF.SetValue(netPlayer, remotePlayer);
            return hand;
        }

        void SeedHandCache(BasisPickupSyncNetworking p, int handId)
        {
            CachedAnimF.SetValue(p, _holderAnimator);
            CachedIdF.SetValue(p, handId);
            CachedHandF.SetValue(p, _hand);
        }

        void FeedHeld(Vector3 offPos, Quaternion offRot)
        {
            EncodePoseM.Invoke(_sender, new object[] { offPos, offRot, Vector3.one });
            var handle = (BasisSyncHandle)AttachIdF.GetValue(_sender);
            _sender.LocalSet(handle, (byte)1);
            FeedTwice();
        }

        void FeedFree(Vector3 worldPos, Quaternion worldRot)
        {
            _sender.transform.SetPositionAndRotation(worldPos, worldRot);
            BeforeTransmitM.Invoke(_sender, null);
            FeedTwice();
        }

        void FeedTwice()
        {
            var recv = (BasisSyncReceiver)ReceiverF.GetValue(_remote);
            var schema = (BasisSyncSchema)SchemaF.GetValue(_sender);
            var vals = (BasisSyncValues)LocalF.GetValue(_sender);
            BasisSyncTestSupport.FeedKeyframe(recv, schema, vals, ++_seq);
            BasisSyncTestSupport.FeedKeyframe(recv, schema, vals, ++_seq);
        }

        static void Frame(float dt = BasisSyncTestSupport.Dt)
        {
            BasisSyncDriver.ScheduleRemote(dt);
            BasisSyncDriver.CompleteRemote();
        }

        void AssertGlued(Vector3 offPos, Quaternion offRot, string why)
        {
            _hand.GetPositionAndRotation(out Vector3 hp, out Quaternion hr);
            Vector3 expectedPos = hp + hr * offPos;
            Quaternion expectedRot = hr * offRot;
            Assert.LessOrEqual(Vector3.Distance(_remote.Target.position, expectedPos), 0.01f, why + " (position)");
            Assert.LessOrEqual(Quaternion.Angle(_remote.Target.rotation, expectedRot), 1.5f, why + " (rotation)");
        }
    }
}
