using Unity.Scripting.LifecycleManagement;
using System;
using System.Linq;
using System.Reflection;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Transmitters;
using Basis.Shims;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisPlayersShimTests
    {
        const ushort LocalId = 60001, RemoteId = 60002;
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
        BasisRemotePlayer local, remote;

        [SetUp]
        public void SetUp()
        {
            Assume.That(BasisNetworkPlayers.Players.ContainsKey(LocalId) || BasisNetworkPlayers.Players.ContainsKey(RemoteId), Is.False, "the test player ids are already taken");
            local = new BasisRemotePlayer { IsLocal = true, DisplayName = "Local Person", SafeDisplayName = "Local Person", UUID = "local-uuid", PlayerPlatform = "Windows" };
            remote = new BasisRemotePlayer { IsLocal = false, DisplayName = "Remote Person", SafeDisplayName = "Remote Person", UUID = "remote-uuid", PlayerPlatform = "Android" };
            Assert.IsTrue(BasisNetworkPlayers.Players.TryAdd(LocalId, new BasisNetworkTransmitter(LocalId) { Player = local }));
            Assert.IsTrue(BasisNetworkPlayers.Players.TryAdd(RemoteId, new BasisNetworkTransmitter(RemoteId) { Player = remote }));
            Dirty();
        }

        [TearDown]
        public void TearDown()
        {
            BasisNetworkPlayers.Players.TryRemove(LocalId, out _);
            BasisNetworkPlayers.Players.TryRemove(RemoteId, out _);
            Dirty();
            _ = BasisPlayersShim.All;
        }

        static void Dirty()
        {
            FieldInfo dirty = typeof(BasisPlayersShim).GetField("dirty", Hidden);
            Assert.IsNotNull(dirty, "BasisPlayersShim.dirty moved; this test cannot force a rebuild.");
            dirty.SetValue(null, true);
        }

        [Test]
        public void TheRoster_IsHandedOutAsACopy()
        {
            IBasisPlayer[] all = BasisPlayersShim.All;
            CollectionAssert.AreEquivalent(new IBasisPlayer[] { local, remote }, all);
            Assert.AreNotSame(all, BasisPlayersShim.All, "every read gets its own array");
            Array.Reverse(all);
            for (int i = 0; i < all.Length; i++) all[i] = null;
            Assert.AreSame(local, BasisPlayersShim.Local, "blanking one script's copy must not blank the roster for every other script");
            Assert.AreSame(remote, BasisPlayersShim.GetByUUID("remote-uuid"));
            Assert.AreSame(remote, BasisPlayersShim.GetById(RemoteId));
            CollectionAssert.AreEquivalent(new IBasisPlayer[] { local, remote }, BasisPlayersShim.All);
        }

        [Test]
        public void NamesAndPlatforms_AreCopies_SoNobodyCanBeRenamed()
        {
            string[] names = BasisPlayersShim.DisplayNames;
            Assert.AreNotSame(names, BasisPlayersShim.DisplayNames);
            for (int i = 0; i < names.Length; i++) names[i] = "Local Person";
            Assert.AreSame(remote, BasisPlayersShim.GetByName("Remote Person"));
            Assert.AreEqual("Remote Person", BasisPlayersShim.DisplayNames[Array.IndexOf(BasisPlayersShim.All, remote)], "names stay index-matched with All");

            string[] platforms = BasisPlayersShim.Platforms;
            Assert.AreNotSame(platforms, BasisPlayersShim.Platforms);
            for (int i = 0; i < platforms.Length; i++) platforms[i] = "Android";
            Assert.AreEqual(1, BasisPlayersShim.CountOnPlatform("Windows"));
            Assert.AreEqual(1, BasisPlayersShim.CountOnPlatform("Android"));
        }

        [Test]
        public void NoMember_HandsOutTheShimsOwnArrays()
        {
            object[] handedOut = { BasisPlayersShim.All, BasisPlayersShim.DisplayNames, BasisPlayersShim.Platforms, BasisPlayersShim.Within(Vector3.zero, 1000f) };
            object[] owned = typeof(BasisPlayersShim).GetFields(Hidden).Where(f => f.FieldType.IsArray).Select(f => f.GetValue(null)).ToArray();
            Assert.AreEqual(4, owned.Length, "BasisPlayersShim's cache arrays changed; check that none of the new ones is handed out");
            foreach (object array in handedOut)
            {
                foreach (object field in owned) Assert.AreNotSame(field, array);
            }
            foreach (MethodInfo method in typeof(BasisPlayersShim).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => m.ReturnType.IsArray))
            {
                Assert.IsTrue(method.Name == "get_" + nameof(BasisPlayersShim.All) || method.Name == "get_" + nameof(BasisPlayersShim.DisplayNames) || method.Name == "get_" + nameof(BasisPlayersShim.Platforms) || method.Name == nameof(BasisPlayersShim.Within), method.Name + " returns an array this test does not check");
            }
        }

        [Test]
        public void ABlankEntry_DoesNotBreakTheLookups()
        {
            _ = BasisPlayersShim.All;
            FieldInfo cached = typeof(BasisPlayersShim).GetField("cached", Hidden);
            Assert.IsNotNull(cached, "BasisPlayersShim.cached moved; this test cannot plant a blank entry.");
            IBasisPlayer[] inside = (IBasisPlayer[])cached.GetValue(null);
            inside[Array.IndexOf(inside, local)] = null;
            IBasisPlayer found = null;
            Assert.DoesNotThrow(() => found = BasisPlayersShim.Local, "a throw here disables the calling script's proxy");
            Assert.IsNull(found);
            Assert.DoesNotThrow(() => found = BasisPlayersShim.GetByUUID("remote-uuid"));
            Assert.AreSame(remote, found);
            Assert.DoesNotThrow(() => found = BasisPlayersShim.Nearest(Vector3.zero));
        }
    }
}
