using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Transmitters;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisCilboxOwnerGateTests
    {
        const ushort LocalId = 7, RemoteId = 8;
        const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
        static readonly Type Consent = typeof(CilboxBasisCommon).Assembly.GetType("Cilbox.CilboxBasisConsent", true);
        static readonly PropertyInfo LocalPlayerInstance = typeof(BasisLocalPlayer).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        readonly List<GameObject> hosts = new List<GameObject>();
        BasisNetworkTransmitter transmitterBefore;
        BasisLocalPlayer localBefore;
        bool hapticsBefore;

        [SetUp]
        public void SetUp()
        {
            transmitterBefore = BasisNetworkManagement.Transmitter;
            localBefore = BasisLocalPlayer.Instance;
            hapticsBefore = BasisSettingsDefaults.UIHaptics.RawValue;
            BasisNetworkManagement.Transmitter = new BasisNetworkTransmitter(LocalId);
            LocalPlayerInstance.SetValue(null, null);
        }

        [TearDown]
        public void TearDown()
        {
            BasisNetworkManagement.Transmitter = transmitterBefore;
            LocalPlayerInstance.SetValue(null, localBefore);
            BasisSettingsDefaults.UIHaptics.RawValue = hapticsBefore;
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        GameObject Content<T>() where T : BasisContentBase
        {
            GameObject root = new GameObject(typeof(T).Name + " gate test");
            hosts.Add(root);
            root.AddComponent<T>();
            return root;
        }

        GameObject Prop(bool spawnedHere)
        {
            GameObject root = Content<BasisProp>();
            root.GetComponent<BasisProp>().AssignContentIdentifier(new BasisNetworkContentBase.BasisContentInformation { LoadedNetID = "gate-test", SpawnedByLocalPlayer = spawnedHere });
            return root;
        }

        GameObject Avatar(ushort? linkedPlayer)
        {
            GameObject root = Content<BasisAvatar>();
            if (linkedPlayer.HasValue) root.GetComponent<BasisAvatar>().LinkedPlayerID = linkedPlayer.Value;
            return root;
        }

        CilboxProxy Script(Type boxType, GameObject content)
        {
            GameObject host = new GameObject(boxType.Name + " gate test");
            if (content != null) host.transform.SetParent(content.transform);
            else hosts.Add(host);
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            CilboxProxy proxy = new GameObject("Script").AddComponent<CilboxProxy>();
            proxy.transform.SetParent(host.transform);
            proxy.box = box;
            return proxy;
        }

        static bool ForLocalPlayer(CilboxProxy script)
        {
            MethodInfo gate = Consent.GetMethod("ForLocalPlayer", Hidden);
            Assert.IsNotNull(gate, "CilboxBasisConsent.ForLocalPlayer moved; this test cannot ask the gate.");
            return (bool)gate.Invoke(null, new object[] { script.box, script.gameObject });
        }

        static int Pulses(CilboxProxy script)
        {
            script.box.interpreterAccountingDepth = 1;
            try
            {
                BasisHapticsShim.Play(script, BasisHapticHand.Both);
            }
            finally
            {
                script.box.interpreterAccountingDepth = 0;
            }
            return (int)typeof(CilboxBasisCommon).GetField("hapticWindowPulses", Internal).GetValue(script.box);
        }

        [Test]
        public void Worlds_RunForThisPlayer()
        {
            Assert.IsTrue(ForLocalPlayer(Script(typeof(CilboxSceneBasis), null)));
            Assert.IsTrue(ForLocalPlayer(Script(typeof(CilboxSceneBasis), Content<BasisScene>())));
        }

        [Test]
        public void Props_OnlyWhenThisPlayerSpawnedThem_OrTheyArePartOfTheWorld()
        {
            Assert.IsFalse(ForLocalPlayer(Script(typeof(CilboxPropBasis), Prop(false))), "another player's prop runs its scripts on every client");
            Assert.IsTrue(ForLocalPlayer(Script(typeof(CilboxPropBasis), Prop(true))));
            Assert.IsTrue(ForLocalPlayer(Script(typeof(CilboxPropBasis), Content<BasisScene>())), "a prop box that belongs to the world");
        }

        [Test]
        public void APropWithNoContent_IsNobodys()
        {
            Assume.That(UnityEngine.Object.FindObjectsByType<BasisScene>(FindObjectsInactive.Include), Is.Empty, "the open scene has a BasisScene, which a loose prop box would resolve to");
            Assert.IsFalse(ForLocalPlayer(Script(typeof(CilboxPropBasis), null)));
            Assert.IsFalse(ForLocalPlayer(Script(typeof(CilboxAvatarBasis), null)));
        }

        [Test]
        public void Avatars_OnlyTheOneThisPlayerWears()
        {
            Assert.IsFalse(ForLocalPlayer(Script(typeof(CilboxAvatarBasis), Avatar(RemoteId))), "another player's copy of an approved avatar");
            Assert.IsTrue(ForLocalPlayer(Script(typeof(CilboxAvatarBasis), Avatar(LocalId))));
            Assert.IsFalse(ForLocalPlayer(Script(typeof(CilboxAvatarBasis), Avatar(null))), "linked to nobody and worn by nobody");
            Assert.IsFalse(ForLocalPlayer(Script(typeof(CilboxAvatarBasis), Prop(true))), "an avatar box needs an avatar to belong to");
            BasisNetworkManagement.Transmitter = null;
            Assert.IsFalse(ForLocalPlayer(Script(typeof(CilboxAvatarBasis), Avatar(LocalId))), "with no local network player there is no id to match");
        }

        [Test]
        public void TheAvatarThisPlayerWears_CountsBeforeItIsLinked()
        {
            GameObject worn = Avatar(null);
            BasisLocalPlayer player = (BasisLocalPlayer)FormatterServices.GetUninitializedObject(typeof(BasisLocalPlayer));
            player.BasisAvatar = worn.GetComponent<BasisAvatar>();
            LocalPlayerInstance.SetValue(null, player);
            Assert.IsTrue(ForLocalPlayer(Script(typeof(CilboxAvatarBasis), worn)), "OnAvatarReady fires before the network link is made, and offline it is never made");
            BasisNetworkManagement.Transmitter = null;
            Assert.IsTrue(ForLocalPlayer(Script(typeof(CilboxAvatarBasis), worn)));
            Assert.IsFalse(ForLocalPlayer(Script(typeof(CilboxAvatarBasis), Avatar(null))));
        }

        [Test]
        public void ClaimingToBeOwnedLocally_DoesNotMakeARemoteAvatarLocal()
        {
            GameObject theirs = Avatar(RemoteId);
            theirs.GetComponent<BasisAvatar>().IsOwnedLocally = true;
            Assert.IsTrue(theirs.GetComponent<BasisAvatar>().SpawnedByLocalPlayer, "the flag this gate must not trust");
            Assert.IsFalse(ForLocalPlayer(Script(typeof(CilboxAvatarBasis), theirs)));
            Assert.IsFalse(ForLocalPlayer(Script(typeof(CilboxPropBasis), theirs)), "a prop box inside an avatar is judged by the avatar rule");
        }

        [Test]
        public void TheOwnerRecordedAtTheGate_Wins_OverWhereTheScriptMovesItself()
        {
            GameObject theirs = Prop(false), mine = Prop(true);
            CilboxProxy script = Script(typeof(CilboxPropBasis), theirs);
            MethodInfo gate = Consent.GetMethod("GateContent", Hidden);
            Assert.IsNotNull(gate, "CilboxBasisConsent.GateContent moved; this test cannot record an owner.");
            gate.Invoke(null, new object[] { theirs });
            Assert.IsTrue((bool)typeof(CilboxBasisCommon).GetField("ownerRecorded", Internal).GetValue(script.box), "the gate recorded no owner, so the move below would prove nothing");
            script.box.transform.SetParent(mine.transform);
            Assert.IsFalse(ForLocalPlayer(script), "moving under this player's prop must not borrow its ownership");

            CilboxProxy local = Script(typeof(CilboxPropBasis), mine);
            gate.Invoke(null, new object[] { mine });
            local.box.transform.SetParent(theirs.transform);
            Assert.IsTrue(ForLocalPlayer(local));
        }

        [Test]
        public void Haptics_OnlyForContentThatRunsForThisPlayer()
        {
            BasisSettingsDefaults.UIHaptics.RawValue = true;
            Assert.AreEqual(0, Pulses(Script(typeof(CilboxPropBasis), Prop(false))), "another player's prop must not buzz this player's controllers");
            Assert.AreEqual(0, Pulses(Script(typeof(CilboxAvatarBasis), Avatar(RemoteId))));
            Assert.AreEqual(1, Pulses(Script(typeof(CilboxPropBasis), Prop(true))));
            Assert.AreEqual(1, Pulses(Script(typeof(CilboxPropBasis), Content<BasisScene>())));
            Assert.AreEqual(1, Pulses(Script(typeof(CilboxAvatarBasis), Avatar(LocalId))));
            Assert.AreEqual(1, Pulses(Script(typeof(CilboxSceneBasis), null)));
        }
    }
}
