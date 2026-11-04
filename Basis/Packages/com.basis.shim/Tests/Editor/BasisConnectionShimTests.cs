using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Networking;
using Basis.BasisUI;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisConnectionShimTests
    {
        const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly Type[] Boxes = { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) };
        static readonly string[] Reads = { "get_" + nameof(BasisNetworkConnection.LocalPlayerIsConnected), nameof(BasisNetworkConnection.TryGetLocalPlayerID) };
        static readonly Type[] ConnectionControl =
        {
            typeof(BasisNetworkLifeCycle),
            typeof(BasisNetworkManagement),
            typeof(BasisConnectionService),
            typeof(BasisServerRequests),
            typeof(BasisDeepLinkProvider),
        };
        readonly List<GameObject> spawned = new List<GameObject>();
        bool untrustedBefore;

        [SetUp]
        public void SetUp()
        {
            untrustedBefore = BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue;
            BasisUserActivation.Clear();
            ResetRequests();
        }

        [TearDown]
        public void TearDown()
        {
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = untrustedBefore;
            BasisUserActivation.Clear();
            ResetRequests();
            foreach (GameObject go in spawned)
            {
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            }
            spawned.Clear();
        }

        static void ResetRequests()
        {
            FieldInfo asking = typeof(BasisServerRequests).GetField("asking", BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo strikes = typeof(BasisServerRequests).GetField("strikes", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(asking, "BasisServerRequests.asking moved; this test cannot reset a prompt.");
            Assert.IsNotNull(strikes, "BasisServerRequests.strikes moved; this test cannot reset declines.");
            asking.SetValue(null, false);
            ((IDictionary)strikes.GetValue(null)).Clear();
        }

        GameObject Spawn(string name, Transform parent = null)
        {
            GameObject go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent);
            spawned.Add(go);
            return go;
        }

        GameObject ButtonOn<T>(out T content) where T : BasisContentBase
        {
            GameObject root = Spawn(typeof(T).Name);
            content = root.AddComponent<T>();
            return Spawn("Portal Button", root.transform);
        }

        CilboxBasisCommon Box(Type boxType)
        {
            GameObject host = Spawn(boxType.Name + " connection test");
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            return box;
        }

        static bool Method(CilboxBasisCommon box, Type type, string name, out MethodInfo mi) =>
            box.CheckMethodAllowed(out mi, type, name, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), name + "()");

        static bool Method(CilboxBasisCommon box, Type type, string name) => box.CheckTypeAllowed(type) && Method(box, type, name, out MethodInfo _);

        static void Grant(CilboxBasisCommon box, params string[] items)
        {
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = true;
            FieldInfo state = typeof(CilboxBasisCommon).GetField("consentState", Internal);
            state.SetValue(box, Enum.Parse(state.FieldType, "Allowed"));
            typeof(CilboxBasisCommon).GetField("consentGranted", Internal).SetValue(box, new HashSet<string>(items, StringComparer.Ordinal));
        }

        static BasisServerRequests.Result Refusal(GameObject source)
        {
            Assert.IsFalse(BasisServerRequests.Admit(source, out _, out BasisServerRequests.Result refusal), "The request was admitted.");
            return refusal;
        }

        [Test]
        public void Addresses_AreCleanedBeforeTheyAreShownOrUsed()
        {
            Assert.IsTrue(BasisServerRequests.TryBuildEntry("Play.Example.com", 4296, "secret", out ServerDirectoryEntry entry, out string server));
            Assert.AreEqual("play.example.com:4296", server);
            Assert.AreEqual("play.example.com", entry.Target.Get(ConnectionTarget.Keys.Address));
            Assert.AreEqual("4296", entry.Target.Get(ConnectionTarget.Keys.Port));
            Assert.AreEqual(BasisNetworkStackRegistry.DefaultId, entry.Target.StackId);
            Assert.IsTrue(entry.HasPassword);
            Assert.AreEqual("secret", entry.Password);
            Assert.IsFalse(entry.CanAutoConnect, "a server a world sent you to must not become your auto-connect server.");
            Assert.AreEqual(BasisServerRequests.EntryId, entry.Id);

            Assert.IsTrue(BasisServerRequests.TryBuildEntry("::1", 4296, null, out entry, out server));
            Assert.AreEqual("[::1]:4296", server);
            Assert.IsFalse(entry.HasPassword);

            Assert.IsTrue(BasisServerRequests.TryBuildEntry("2130706433", 80, null, out _, out server));
            Assert.AreEqual("127.0.0.1:80", server, "numeric forms are shown the way they connect.");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("exa mple.com")]
        [TestCase("<b>example</b>.com")]
        [TestCase("example.com#password")]
        [TestCase("example.com/path")]
        [TestCase("example.com:4296")]
        [TestCase("basis://example.com")]
        [TestCase("http://example.com")]
        [TestCase("exa‮mple.com")]
        public void BadAddresses_AreRefused(string address)
        {
            Assert.IsFalse(BasisServerRequests.TryBuildEntry(address, 4296, null, out ServerDirectoryEntry entry, out string server), address);
            Assert.IsNull(entry);
            Assert.IsNull(server);
        }

        [Test]
        public void ZeroPortsAndLongNamesAreRefused()
        {
            Assert.IsFalse(BasisServerRequests.TryBuildEntry("example.com", 0, null, out _, out _));
            Assert.IsFalse(BasisServerRequests.TryBuildEntry(new string('a', 250) + ".com", 4296, null, out _, out _));
        }

        [Test]
        public void ContentThatWasNotJustUsedCannotAsk()
        {
            GameObject button = ButtonOn(out BasisProp _);
            Assert.AreEqual(BasisServerRequests.Result.NoInteraction, Refusal(button));
        }

        [Test]
        public void UsingTheContentLetsItAsk()
        {
            GameObject button = ButtonOn(out BasisProp prop);
            BasisUserActivation.Note(button);
            Assert.IsTrue(BasisServerRequests.Admit(button, out BasisContentBase content, out _));
            Assert.AreSame(prop, content);
        }

        [Test]
        public void AvatarsAndObjectsOutsideContentCanNeverAsk()
        {
            GameObject badge = ButtonOn(out BasisAvatar _);
            BasisUserActivation.Note(badge);
            Assert.AreEqual(BasisServerRequests.Result.Blocked, Refusal(badge));
            Assert.AreEqual(BasisServerRequests.Result.Blocked, Refusal(null));
        }

        [Test]
        public void OnlyOnePromptIsShownAtATime()
        {
            GameObject button = ButtonOn(out BasisProp _);
            BasisUserActivation.Note(button);
            typeof(BasisServerRequests).GetField("asking", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, true);
            Assert.IsTrue(BasisServerRequests.IsAsking);
            Assert.AreEqual(BasisServerRequests.Result.Busy, Refusal(button));
        }

        [Test]
        public void DeclinesSilenceTheContent()
        {
            GameObject button = ButtonOn(out BasisProp prop);
            BasisUserActivation.Note(button);
            BasisServerRequests.StrikeOut(prop);
            Assert.IsTrue(BasisServerRequests.IsMuted(prop), "a decline must cool the content down.");
            Assert.AreEqual(BasisServerRequests.Result.Blocked, Refusal(button));
            BasisServerRequests.Forgive(prop);
            Assert.IsTrue(BasisServerRequests.Admit(button, out _, out _));
            for (int i = 0; i < BasisServerRequests.DeclinesBeforeMute; i++) BasisServerRequests.StrikeOut(prop);
            Assert.IsTrue(BasisServerRequests.IsMuted(prop));
        }

        [Test]
        public void BadRequestsAnswerWithoutAPrompt()
        {
            Assume.That(BasisNetworkConnection.LocalPlayerIsConnected, Is.False);
            GameObject button = ButtonOn(out BasisProp _);
            BasisUserActivation.Note(button);
            var results = new List<BasisServerRequests.Result>();
            BasisServerRequests.RequestConnect("exa mple.com", 4296, null, button, results.Add);
            BasisServerRequests.RequestDisconnect(button, results.Add);
            BasisServerRequests.RequestDisconnect(null, results.Add);
            CollectionAssert.AreEqual(new[] { BasisServerRequests.Result.InvalidAddress, BasisServerRequests.Result.NotConnected, BasisServerRequests.Result.Blocked }, results);
            Assert.IsFalse(BasisServerRequests.IsAsking);
        }

        [Test]
        public void EveryBox_ReadsTheConnectionAndNothingElse()
        {
            string[] others = typeof(BasisNetworkConnection).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name).Where(n => !Reads.Contains(n)).Distinct().ToArray();
            Assert.IsNotEmpty(others);
            string[] fields = typeof(BasisNetworkConnection).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name).ToArray();
            foreach (Type boxType in Boxes)
            {
                CilboxBasisCommon box = Box(boxType);
                foreach (string read in Reads) Assert.IsTrue(Method(box, typeof(BasisNetworkConnection), read), boxType.Name + " " + read);
                foreach (string name in others) Assert.IsFalse(Method(box, typeof(BasisNetworkConnection), name), boxType.Name + " " + name);
                foreach (string field in fields) Assert.IsFalse(box.CheckFieldAllowed(typeof(BasisNetworkConnection), field), boxType.Name + " " + field);
            }
        }

        [Test]
        public void Connect_IsBlockedInEveryBox()
        {
            foreach (Type boxType in Boxes)
            {
                Assert.IsFalse(Method(Box(boxType), typeof(BasisNetworkConnection), nameof(BasisNetworkConnection.Connect), out MethodInfo mi), boxType.Name);
                Assert.IsNull(mi, boxType.Name + ": Connect must not be bound or rerouted; scripts ask through BasisConnectionShim.");
            }
        }

        [Test]
        public void TheShim_WorldsAndPropsOnly_EntryPointsOnly()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis) })
            {
                CilboxBasisCommon box = Box(boxType);
                Assert.IsTrue(Method(box, typeof(BasisConnectionShim), nameof(BasisConnectionShim.RequestConnect)), boxType.Name);
                Assert.IsTrue(Method(box, typeof(BasisConnectionShim), nameof(BasisConnectionShim.RequestDisconnect)), boxType.Name);
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisConnectionResult)), boxType.Name);
                Assert.IsFalse(Method(box, typeof(BasisConnectionShim), "Reply"), boxType.Name);
                Assert.IsFalse(Method(box, typeof(BasisConnectionShim), "MemberwiseClone"), boxType.Name);
            }
            CilboxBasisCommon avatar = Box(typeof(CilboxAvatarBasis));
            Assert.IsFalse(avatar.CheckTypeAllowed(typeof(BasisConnectionShim)));
            Assert.IsFalse(Method(avatar, typeof(BasisConnectionShim), nameof(BasisConnectionShim.RequestConnect), out _));
        }

        [Test]
        public void ConsentCannotUnlockAnyOfIt()
        {
            foreach (Type boxType in Boxes)
            {
                CilboxBasisCommon box = Box(boxType);
                var items = new List<string>
                {
                    "Method:Basis.Scripts.Networking.BasisNetworkConnection.OnDestroy",
                    "Method:Basis.Scripts.Networking.BasisNetworkConnection.set_LocalPlayerIsConnected",
                    "Field:Basis.Scripts.Networking.BasisNetworkConnection.OnRebootComplete",
                    "Method:Basis.Scripts.Networking.BasisNetworkConnection.Connect",
                };
                items.AddRange(ConnectionControl.Select(t => "Type:" + t.FullName));
                items.Add("Type:UnityEngine.Networking.UnityWebRequest");
                Grant(box, items.ToArray());
                Assert.IsTrue(box.CheckTypeAllowed("UnityEngine.Networking.UnityWebRequest"), boxType.Name + ": the grant itself did not take, so the refusals below would prove nothing.");
                Assert.IsFalse(Method(box, typeof(BasisNetworkConnection), "OnDestroy"), boxType.Name);
                Assert.IsFalse(Method(box, typeof(BasisNetworkConnection), "set_LocalPlayerIsConnected"), boxType.Name);
                Assert.IsFalse(box.CheckFieldAllowed(typeof(BasisNetworkConnection), nameof(BasisNetworkConnection.OnRebootComplete)), boxType.Name);
                Assert.IsFalse(Method(box, typeof(BasisNetworkConnection), nameof(BasisNetworkConnection.Connect)), boxType.Name);
                foreach (Type type in ConnectionControl) Assert.IsFalse(box.CheckTypeAllowed(type), boxType.Name + " " + type.FullName);
            }
        }

        [Test]
        public void ResultsMatchTheFramework()
        {
            CollectionAssert.AreEqual(Enum.GetNames(typeof(BasisServerRequests.Result)), Enum.GetNames(typeof(BasisConnectionResult)));
            foreach (BasisServerRequests.Result result in Enum.GetValues(typeof(BasisServerRequests.Result)))
            {
                Assert.AreEqual(result.ToString(), ((BasisConnectionResult)(int)result).ToString());
            }
        }

        [Test]
        public void TheShim_HasNoNonPublicMembers()
        {
            Assert.IsEmpty(typeof(BasisConnectionShim).GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly),
                "Cilbox resolves non-public members of a whitelisted type, so helpers belong on CilboxConnection.");
        }
    }
}
