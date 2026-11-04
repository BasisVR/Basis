using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Basis.BasisUI;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Transmitters;
using Basis.Scripts.Settings;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisSaveShimTests
    {
        const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const ushort LocalId = 7, RemoteId = 8;
        static readonly string[] EntryPoints = { nameof(BasisSaveShim.Save), nameof(BasisSaveShim.Load), nameof(BasisSaveShim.Delete) };
        static readonly string[] Granted = { nameof(BasisSaveShim.Save), nameof(BasisSaveShim.Load), nameof(BasisSaveShim.Delete), nameof(BasisSaveShim.LoadReadOnly) };
        static readonly Type[] Boxes = { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) };
        readonly List<GameObject> hosts = new List<GameObject>();
        readonly List<CilboxBasisCommon> boxes = new List<CilboxBasisCommon>();
        readonly List<string> folders = new List<string>();
        BasisNetworkTransmitter transmitterBefore;
        bool untrustedBefore;

        [SetUp]
        public void SetUp()
        {
            untrustedBefore = BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue;
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = true;
            transmitterBefore = BasisNetworkManagement.Transmitter;
            BasisNetworkManagement.Transmitter = new BasisNetworkTransmitter(LocalId);
        }

        [TearDown]
        public void TearDown()
        {
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = untrustedBefore;
            BasisNetworkManagement.Transmitter = transmitterBefore;
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
            boxes.Clear();
            foreach (string folder in folders)
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            folders.Clear();
        }

        static string Hex(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return string.Concat(digest.Take(16).Select(b => b.ToString("x2")));
            }
        }

        static string Folder(string preimage) => Path.Combine(Application.persistentDataPath, "CilboxSaves", Hex(preimage));

        static string ProtectedDefault(string owner) => Folder(owner.Trim().ToLowerInvariant());

        static string LegacyShared(string store) => Folder(store.Trim().ToLowerInvariant());

        static string SharedOf(string store) => Folder("store:" + store.Trim().ToLowerInvariant());

        static string ProtectedOf(string owner, string store) => Folder("protected:" + owner.Trim().ToLowerInvariant() + "\n" + store.Trim().ToLowerInvariant());

        static string ReadOnlyOf(string owner, string store) => Folder("readonly:" + owner.Trim().ToLowerInvariant() + "\n" + (store == null ? string.Empty : store.Trim().ToLowerInvariant()));

        static string FileIn(string folder, string slot) => Path.Combine(folder, Hex(slot) + ".bsave");

        static string Item(string method) => BasisCilboxPermissionText.MethodPrefix + typeof(BasisSaveShim).FullName + "." + method;

        static FieldInfo Field(string name)
        {
            FieldInfo field = typeof(CilboxBasisCommon).GetField(name, Internal);
            Assert.IsNotNull(field, $"CilboxBasisCommon.{name} moved; this test cannot drive the save shim.");
            return field;
        }

        static void Grant(CilboxBasisCommon box, params string[] methods)
        {
            FieldInfo state = Field("consentState");
            state.SetValue(box, Enum.Parse(state.FieldType, "Allowed"));
            Field("consentGranted").SetValue(box, new HashSet<string>(methods.Select(Item), StringComparer.Ordinal));
        }

        static object BeginScan(CilboxBasisCommon box)
        {
            FieldInfo scanField = Field("consentScan");
            object scan = Activator.CreateInstance(scanField.FieldType, true);
            scanField.SetValue(box, scan);
            return scan;
        }

        static SortedSet<string> Set(object scan, string name) => (SortedSet<string>)scan.GetType().GetField(name, Internal).GetValue(scan);

        static void Refill(CilboxProxy script) => Field("saveWindowWrites").SetValue(script.box, 0);

        static void Plant(string folder, string slot, byte[] payload)
        {
            uint checksum = 2166136261;
            foreach (byte value in payload) checksum = (checksum ^ value) * 16777619;
            byte[] file = new byte[16 + payload.Length];
            Encoding.ASCII.GetBytes("BSAV").CopyTo(file, 0);
            file[4] = 1;
            BitConverter.GetBytes(payload.Length).CopyTo(file, 8);
            BitConverter.GetBytes(checksum).CopyTo(file, 12);
            payload.CopyTo(file, 16);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(FileIn(folder, slot), file);
        }

        string Track(string folder)
        {
            folders.Add(folder);
            return folder;
        }

        string NewStore()
        {
            string store = "basis-save-test-" + Guid.NewGuid().ToString("N");
            Track(LegacyShared(store));
            Track(SharedOf(store));
            return store;
        }

        string NewContentId()
        {
            string id = BasisGenerateUniqueID.GenerateUniqueID();
            Track(ProtectedDefault(id));
            Track(SharedOf(id));
            Track(ReadOnlyOf(id, null));
            return id;
        }

        GameObject Content<T>(string id) where T : BasisContentBase
        {
            GameObject root = new GameObject(typeof(T).Name + " save test");
            hosts.Add(root);
            root.AddComponent<T>().BasisBundleDescription = new BasisBundleDescription { ContentGroupId = id };
            return root;
        }

        GameObject Prop(string id, bool spawnedHere)
        {
            GameObject root = Content<BasisProp>(id);
            root.GetComponent<BasisProp>().AssignContentIdentifier(new BasisNetworkContentBase.BasisContentInformation { LoadedNetID = "save-test", SpawnedByLocalPlayer = spawnedHere });
            return root;
        }

        GameObject Avatar(string id, ushort linkedPlayer)
        {
            GameObject root = Content<BasisAvatar>(id);
            root.GetComponent<BasisAvatar>().LinkedPlayerID = linkedPlayer;
            return root;
        }

        GameObject World(string id) => Content<BasisScene>(id);

        CilboxBasisCommon AddBox(Type boxType, Transform parent = null)
        {
            GameObject host = new GameObject(boxType.Name + " save test");
            if (parent != null) host.transform.SetParent(parent);
            else hosts.Add(host);
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            boxes.Add(box);
            return box;
        }

        CilboxProxy Script(Type boxType, Transform parent = null, bool consent = true)
        {
            if (parent == null && boxType == typeof(CilboxPropBasis)) parent = Prop(null, true).transform;
            if (parent == null && boxType == typeof(CilboxAvatarBasis)) parent = Avatar(null, LocalId).transform;
            CilboxBasisCommon box = AddBox(boxType, parent);
            if (consent) Grant(box, Granted);
            CilboxProxy proxy = new GameObject("Script").AddComponent<CilboxProxy>();
            proxy.transform.SetParent(box.transform);
            proxy.box = box;
            Use(proxy);
            return proxy;
        }

        void Use(CilboxProxy script)
        {
            foreach (CilboxBasisCommon box in boxes)
            {
                if (box != null) box.interpreterAccountingDepth = 0;
            }
            script.box.interpreterAccountingDepth = 1;
        }

        static bool Allowed(CilboxBasisCommon box, string method)
        {
            return box.CheckMethodAllowed(out MethodInfo _, typeof(BasisSaveShim), method, Array.Empty<SerializedTypeDescriptor>(), Array.Empty<SerializedTypeDescriptor>(), method);
        }

        [Test]
        public void WithoutConsent_NothingIsWrittenOrRead()
        {
            string store = NewStore();
            CilboxProxy script = Script(typeof(CilboxSceneBasis), consent: false);
            Assert.IsFalse(BasisSaveShim.Save(script, store, "slot", new byte[] { 1 }));
            Assert.IsNull(BasisSaveShim.Load(script, store, "slot"));
            Assert.IsFalse(Directory.Exists(SharedOf(store)));

            CilboxProxy approved = Script(typeof(CilboxSceneBasis));
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = false;
            Assert.IsFalse(BasisSaveShim.Save(approved, store, "slot", new byte[] { 1 }), "turning untrusted script permissions off must stop saving too");
            Assert.IsFalse(Directory.Exists(SharedOf(store)));
        }

        [Test]
        public void SaveLoadDelete_RoundTrip_InEveryBox()
        {
            byte[] data = Encoding.UTF8.GetBytes("level=3;score=1200");
            foreach (Type boxType in Boxes)
            {
                string store = NewStore();
                CilboxProxy script = Script(boxType);
                Assert.IsTrue(BasisSaveShim.Save(script, store, "progress", data), boxType.Name);
                Assert.IsTrue(File.Exists(FileIn(SharedOf(store), "progress")), boxType.Name);
                CollectionAssert.AreEqual(data, BasisSaveShim.Load(script, store, "progress"), boxType.Name);
                Assert.IsTrue(BasisSaveShim.Save(script, store, "progress", new byte[0]), boxType.Name);
                CollectionAssert.AreEqual(new byte[0], BasisSaveShim.Load(script, store, "progress"), boxType.Name + ": an empty save is still a save");
                Assert.IsTrue(BasisSaveShim.Delete(script, store, "progress"), boxType.Name);
                Assert.IsNull(BasisSaveShim.Load(script, store, "progress"), boxType.Name);
                Assert.IsFalse(BasisSaveShim.Delete(script, store, "progress"), boxType.Name);
                Assert.IsFalse(Directory.Exists(SharedOf(store)), boxType.Name + ": an emptied store should not leave its folder behind");
            }
        }

        [Test]
        public void SlotsAndStores_AreKeptApart()
        {
            string first = NewStore(), second = NewStore();
            CilboxProxy script = Script(typeof(CilboxPropBasis));
            Assert.IsTrue(BasisSaveShim.Save(script, first, "one", new byte[] { 1 }));
            Assert.IsTrue(BasisSaveShim.Save(script, first, "two", new byte[] { 2 }));
            Assert.IsTrue(BasisSaveShim.Save(script, second, "one", new byte[] { 3 }));
            CollectionAssert.AreEqual(new byte[] { 1 }, BasisSaveShim.Load(script, first, "one"));
            CollectionAssert.AreEqual(new byte[] { 2 }, BasisSaveShim.Load(script, first, "two"));
            CollectionAssert.AreEqual(new byte[] { 3 }, BasisSaveShim.Load(script, second, "one"));
            CollectionAssert.AreEqual(new byte[] { 1 }, BasisSaveShim.Load(script, "  " + first.ToUpperInvariant() + " ", "one"), "store keys ignore case and surrounding spaces");
            Assert.IsNull(BasisSaveShim.Load(script, first, "ONE"), "slot names are exact");
        }

        [Test]
        public void TheDefaultStore_IsTheContentId()
        {
            string propId = NewContentId();
            byte[] data = { 7, 7, 7 };
            CilboxProxy prop = Script(typeof(CilboxPropBasis), Prop(propId, true).transform);
            Assert.IsTrue(BasisSaveShim.Save(prop, "slot", data));
            Assert.IsTrue(File.Exists(FileIn(ProtectedDefault(propId), "slot")), "the default store keeps the folder older builds wrote");
            CollectionAssert.AreEqual(data, BasisSaveShim.Load(prop, "slot"));
            CollectionAssert.AreEqual(data, BasisSaveShim.Load(prop, BasisSaveScope.Protected, null, "slot"));
            Assert.IsNull(BasisSaveShim.Load(Script(typeof(CilboxSceneBasis), World(NewContentId()).transform), propId, "slot"), "naming another content's id as a store no longer reaches its default store");
            Assert.IsTrue(File.Exists(FileIn(ProtectedDefault(propId), "slot")));
        }

        [Test]
        public void AWorldBox_FindsItsBasisSceneThroughTheSceneRoots()
        {
            Assume.That(UnityEngine.Object.FindObjectsByType<BasisScene>(FindObjectsInactive.Include), Is.Empty, "the open scene already has a BasisScene, which this test would find instead");
            string worldId = NewStore();
            GameObject world = new GameObject("World");
            hosts.Add(world);
            world.AddComponent<BasisScene>().BasisBundleDescription = new BasisBundleDescription { ContentGroupId = worldId };
            CilboxProxy scene = Script(typeof(CilboxSceneBasis));
            Assert.IsTrue(BasisSaveShim.Save(scene, "slot", new byte[] { 7 }));
            Assert.IsTrue(File.Exists(FileIn(ProtectedDefault(worldId), "slot")), "a world box that is not under its BasisScene still finds it in the scene");
        }

        [Test]
        public void Limits_AreEnforced()
        {
            string store = NewStore();
            byte[] data = { 1, 2, 3 };
            CilboxProxy script = Script(typeof(CilboxSceneBasis));
            Assert.IsFalse(BasisSaveShim.Save(script, store, "big", new byte[BasisSaveShim.MaxSlotBytes + 1]));
            Assert.IsFalse(BasisSaveShim.Save(script, store, "slot", null));
            Assert.IsFalse(BasisSaveShim.Save(script, store, null, data));
            Assert.IsFalse(BasisSaveShim.Save(script, store, "", data));
            Assert.IsFalse(BasisSaveShim.Save(script, store, new string('s', BasisSaveShim.MaxNameLength + 1), data));
            Assert.IsFalse(BasisSaveShim.Save(script, "   ", "slot", data));
            Assert.IsFalse(BasisSaveShim.Save(script, new string('k', BasisSaveShim.MaxNameLength + 1), "slot", data));

            for (int i = 0; i < 3; i++) Assert.IsTrue(BasisSaveShim.Save(script, store, "chunk" + i, new byte[BasisSaveShim.MaxSlotBytes]));
            Assert.IsFalse(BasisSaveShim.Save(script, store, "chunk3", new byte[BasisSaveShim.MaxSlotBytes]), "a fourth full slot passes MaxStoreBytes");
            Assert.IsTrue(BasisSaveShim.Save(script, store, "chunk0", new byte[BasisSaveShim.MaxSlotBytes]), "rewriting a slot must not count it twice");

            string many = NewStore();
            for (int i = 0; i < BasisSaveShim.MaxSlots; i++)
            {
                Refill(script);
                Assert.IsTrue(BasisSaveShim.Save(script, many, "slot" + i, data), "slot " + i);
            }
            Refill(script);
            Assert.IsFalse(BasisSaveShim.Save(script, many, "one too many", data));
        }

        [Test]
        public void Writes_AreRateLimited_ButReadsAreNot()
        {
            string store = NewStore();
            CilboxProxy script = Script(typeof(CilboxAvatarBasis));
            for (int i = 0; i < 8; i++) Assert.IsTrue(BasisSaveShim.Save(script, store, "tick", new[] { (byte)i }), "write " + i);
            Assert.IsFalse(BasisSaveShim.Save(script, store, "tick", new byte[] { 99 }), "a ninth write inside one second");
            Assert.IsFalse(BasisSaveShim.Delete(script, store, "tick"), "deletes spend the same budget");
            for (int i = 0; i < 20; i++) CollectionAssert.AreEqual(new byte[] { 7 }, BasisSaveShim.Load(script, store, "tick"));
        }

        [Test]
        public void OneScript_CanOnlyReachAFewStores()
        {
            CilboxProxy script = Script(typeof(CilboxPropBasis));
            for (int i = 0; i < 4; i++)
            {
                Refill(script);
                Assert.IsTrue(BasisSaveShim.Save(script, NewStore(), "slot", new byte[] { 1 }), "store " + i);
            }
            Refill(script);
            Assert.IsFalse(BasisSaveShim.Save(script, NewStore(), "slot", new byte[] { 1 }));
            Assert.IsNull(BasisSaveShim.Load(script, NewStore(), "slot"));
        }

        [Test]
        public void DamagedFiles_LoadAsNull()
        {
            string store = NewStore();
            byte[] data = Encoding.UTF8.GetBytes("a save that will be damaged");
            CilboxProxy script = Script(typeof(CilboxSceneBasis));
            Assert.IsTrue(BasisSaveShim.Save(script, store, "slot", data));
            string file = FileIn(SharedOf(store), "slot");
            byte[] good = File.ReadAllBytes(file);
            Assert.AreEqual(16 + data.Length, good.Length);
            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("BSAV"), good.Take(4).ToArray());
            Assert.AreEqual(1, good[4]);

            Func<byte[], byte[]>[] damages =
            {
                b => { b[20] ^= 1; return b; },
                b => { b[0] = (byte)'X'; return b; },
                b => { b[4] = 2; return b; },
                b => { b[6] = 1; return b; },
                b => { b[9] ^= 1; return b; },
                b => { b[13] ^= 1; return b; },
                b => b.Take(b.Length - 1).ToArray(),
                b => b.Take(10).ToArray(),
                b => b.Concat(new byte[] { 0 }).ToArray(),
                b => new byte[16 + BasisSaveShim.MaxSlotBytes + 1],
            };
            for (int i = 0; i < damages.Length; i++)
            {
                File.WriteAllBytes(file, damages[i]((byte[])good.Clone()));
                Assert.IsNull(BasisSaveShim.Load(script, store, "slot"), "damage " + i);
            }
            File.WriteAllBytes(file, good);
            CollectionAssert.AreEqual(data, BasisSaveShim.Load(script, store, "slot"));
        }

        [Test]
        public void OnlyARunningScript_CanUseIt()
        {
            string store = NewStore();
            CilboxProxy script = Script(typeof(CilboxSceneBasis));
            Assert.IsFalse(BasisSaveShim.Save(null, store, "slot", new byte[] { 1 }));
            Assert.IsFalse(BasisSaveShim.Save("not a script", store, "slot", new byte[] { 1 }));
            Assert.IsFalse(BasisSaveShim.Save(script.box, store, "slot", new byte[] { 1 }));
            script.box.interpreterAccountingDepth = 0;
            Assert.IsFalse(BasisSaveShim.Save(script, store, "slot", new byte[] { 1 }), "a proxy whose box is not running must not be accepted");
            CilboxProxy other = Script(typeof(CilboxPropBasis));
            script.box.interpreterAccountingDepth = 1;
            Assert.IsFalse(BasisSaveShim.Save(script, store, "slot", new byte[] { 1 }), "while two boxes run, a script could hand in the other one's proxy");
            Assert.IsFalse(BasisSaveShim.Save(other, store, "slot", new byte[] { 1 }));
            Assert.IsFalse(Directory.Exists(SharedOf(store)));
        }

        [Test]
        public void TheSandbox_ResolvesOnlyTheEntryPoints_AndAsksForConsent()
        {
            string[] helpers = typeof(BasisSaveShim).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Select(m => m.Name).Distinct().ToArray();
            Assert.IsNotEmpty(helpers);
            foreach (Type boxType in Boxes)
            {
                CilboxBasisCommon box = AddBox(boxType);
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisSaveShim)), boxType.Name);
                foreach (string method in EntryPoints) Assert.IsTrue(Allowed(box, method), boxType.Name + " " + method);
                foreach (string helper in helpers) Assert.IsFalse(Allowed(box, helper), boxType.Name + " resolves the private helper " + helper);

                FieldInfo scanField = Field("consentScan");
                object scan = Activator.CreateInstance(scanField.FieldType, true);
                scanField.SetValue(box, scan);
                try
                {
                    foreach (string method in EntryPoints) Allowed(box, method);
                    Allowed(box, helpers[0]);
                }
                finally
                {
                    scanField.SetValue(box, null);
                }
                CollectionAssert.AreEquivalent(EntryPoints.Select(Item), (SortedSet<string>)scan.GetType().GetField("Items", Internal).GetValue(scan), boxType.Name);
                CollectionAssert.AreEquivalent(new[] { Item(helpers[0]) }, (SortedSet<string>)scan.GetType().GetField("Blocked", Internal).GetValue(scan), boxType.Name);
            }
        }

        [Test]
        public void TheConsentPrompt_CallsItSaving()
        {
            List<BasisCilboxPermissionText.Line> lines = BasisCilboxPermissionText.Describe(EntryPoints.Select(Item));
            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual(BasisLocalization.Get("settings.cilboxPermissions.cap.saves"), lines[0].Text);
            Assert.AreNotEqual(BasisLocalization.Get("settings.cilboxPermissions.cap.basisInternals"), lines[0].Text);
            Assert.IsFalse(lines[0].Severe);
        }

        [Test]
        public void TheScopedApi_ResolvesInEveryBox_AndReadingPublishedDataAsksForConsent()
        {
            foreach (Type boxType in Boxes)
            {
                CilboxBasisCommon box = AddBox(boxType);
                Assert.IsTrue(box.CheckTypeAllowed(typeof(BasisSaveScope)), boxType.Name);
                Assert.IsTrue(Allowed(box, nameof(BasisSaveShim.LoadReadOnly)), boxType.Name);
                object scan = BeginScan(box);
                try
                {
                    Allowed(box, nameof(BasisSaveShim.LoadReadOnly));
                }
                finally
                {
                    Field("consentScan").SetValue(box, null);
                }
                CollectionAssert.AreEquivalent(new[] { Item(nameof(BasisSaveShim.LoadReadOnly)) }, Set(scan, "Items"), boxType.Name);
            }

            string publisher = NewContentId();
            CilboxProxy owner = Script(typeof(CilboxPropBasis), Prop(publisher, true).transform);
            Assert.IsTrue(BasisSaveShim.Save(owner, BasisSaveScope.ReadOnly, null, "badge", new byte[] { 5 }));
            CilboxProxy reader = Script(typeof(CilboxSceneBasis), World(NewContentId()).transform, consent: false);
            Grant((CilboxBasisCommon)reader.box, EntryPoints);
            Assert.IsNull(BasisSaveShim.LoadReadOnly(reader, publisher, null, "badge"), "LoadReadOnly is its own consent item");
            Grant((CilboxBasisCommon)reader.box, Granted);
            CollectionAssert.AreEqual(new byte[] { 5 }, BasisSaveShim.LoadReadOnly(reader, publisher, null, "badge"));
        }

        [Test]
        public void ALegacyProtectedFolder_IsStillRead()
        {
            string id = NewContentId();
            byte[] data = { 3, 1, 4 };
            Plant(ProtectedDefault(id), "slot", data);
            CilboxProxy prop = Script(typeof(CilboxPropBasis), Prop(id, true).transform);
            CollectionAssert.AreEqual(data, BasisSaveShim.Load(prop, "slot"));
            CollectionAssert.AreEqual(data, BasisSaveShim.Load(prop, null, "slot"));
            CollectionAssert.AreEqual(data, BasisSaveShim.Load(prop, BasisSaveScope.Protected, null, "slot"));

            string scriptHash = new string('c', 64);
            Track(ProtectedDefault(scriptHash));
            Plant(ProtectedDefault(scriptHash), "slot", data);
            CilboxProxy unnamed = Script(typeof(CilboxPropBasis), Prop(null, true).transform);
            Field("consentHash").SetValue(unnamed.box, scriptHash);
            CollectionAssert.AreEqual(data, BasisSaveShim.Load(unnamed, "slot"), "content without a Content ID keeps its script-hash store");
        }

        [Test]
        public void ALegacySharedFolder_IsMovedIntoTheSharedNamespace()
        {
            string store = NewStore();
            byte[] data = { 4, 2 };
            Plant(LegacyShared(store), "slot", data);
            CilboxProxy world = Script(typeof(CilboxSceneBasis), World(NewContentId()).transform);
            CollectionAssert.AreEqual(data, BasisSaveShim.Load(world, "  " + store.ToUpperInvariant(), "slot"));
            Assert.IsFalse(Directory.Exists(LegacyShared(store)), "the legacy folder moves rather than being copied");
            Assert.IsTrue(File.Exists(FileIn(SharedOf(store), "slot")));
            CollectionAssert.AreEqual(data, BasisSaveShim.Load(world, BasisSaveScope.Shared, store, "slot"));
        }

        [Test]
        public void ALegacyFolderNamedLikeAContentKey_IsNotMoved()
        {
            string generated = NewContentId(), scriptHash = new string('a', 32) + new string('7', 32), own = NewStore();
            Track(LegacyShared(scriptHash));
            Track(SharedOf(scriptHash));
            foreach (string name in new[] { generated, scriptHash }) Plant(LegacyShared(name), "slot", new byte[] { 1 });
            CilboxProxy world = Script(typeof(CilboxSceneBasis), World(NewContentId()).transform);
            foreach (string name in new[] { generated, generated.ToUpperInvariant(), scriptHash })
            {
                Assert.IsNull(BasisSaveShim.Load(world, name, "slot"), name);
                Assert.IsTrue(File.Exists(FileIn(LegacyShared(name), "slot")), name + " may be that content's own store, so it stays where it is");
                Assert.IsFalse(Directory.Exists(SharedOf(name)), name);
            }

            Plant(LegacyShared(own), "slot", new byte[] { 2 });
            CilboxProxy owner = Script(typeof(CilboxPropBasis), Prop(own, true).transform);
            Assert.IsNull(BasisSaveShim.Load(owner, own, "slot"), "a store named after the caller's own key is never moved");
            Assert.IsTrue(File.Exists(FileIn(LegacyShared(own), "slot")));
            CollectionAssert.AreEqual(new byte[] { 2 }, BasisSaveShim.Load(owner, "slot"), "the owner still reads it as its default store");
        }

        [Test]
        public void ASharedStoreNamedLikeAContentId_CannotReachItsProtectedOrReadOnlyData()
        {
            string id = NewContentId(), sub = "notes-" + Guid.NewGuid().ToString("N");
            byte[] secret = { 1, 2, 3 }, published = { 4, 5, 6 };
            Track(ProtectedOf(id, sub));
            CilboxProxy owner = Script(typeof(CilboxPropBasis), Prop(id, true).transform);
            Assert.IsTrue(BasisSaveShim.Save(owner, "slot", secret));
            Assert.IsTrue(BasisSaveShim.Save(owner, BasisSaveScope.Protected, sub, "slot", secret));
            Assert.IsTrue(BasisSaveShim.Save(owner, BasisSaveScope.ReadOnly, null, "slot", published));

            CilboxProxy other = Script(typeof(CilboxSceneBasis), World(NewContentId()).transform);
            Assert.IsNull(BasisSaveShim.Load(other, id, "slot"), "a shared store named after a Content ID is not that content's default store");
            Assert.IsNull(BasisSaveShim.Load(other, BasisSaveScope.Shared, id.ToUpperInvariant(), "slot"));
            Assert.IsNull(BasisSaveShim.Load(other, BasisSaveScope.Shared, "readonly:" + id, "slot"), "a shared name cannot spell another namespace");
            Assert.IsNull(BasisSaveShim.Load(other, BasisSaveScope.Shared, "protected:" + id + "\n" + sub, "slot"));
            Assert.IsTrue(BasisSaveShim.Save(other, id, "slot", new byte[] { 9 }));
            Assert.IsNull(BasisSaveShim.LoadReadOnly(other, id, sub, "slot"), "LoadReadOnly never reaches a Protected store");
            CollectionAssert.AreEqual(published, BasisSaveShim.LoadReadOnly(other, id, null, "slot"));

            Use(owner);
            CollectionAssert.AreEqual(secret, BasisSaveShim.Load(owner, "slot"));
            CollectionAssert.AreEqual(secret, BasisSaveShim.Load(owner, BasisSaveScope.Protected, sub, "slot"));
            Assert.IsTrue(Directory.Exists(ProtectedDefault(id)), "the default store was not moved into the shared namespace");
        }

        [Test]
        public void AReadOnlyStore_CanOnlyBeChangedByItsOwner()
        {
            string ownerId = NewContentId(), otherId = NewContentId();
            byte[] badge = { 7 };
            CilboxProxy owner = Script(typeof(CilboxPropBasis), Prop(ownerId, true).transform);
            Assert.IsTrue(BasisSaveShim.Save(owner, BasisSaveScope.ReadOnly, null, "badge", badge));
            Assert.IsTrue(File.Exists(FileIn(ReadOnlyOf(ownerId, null), "badge")));

            CilboxProxy other = Script(typeof(CilboxPropBasis), Prop(otherId, true).transform);
            Assert.IsTrue(BasisSaveShim.Save(other, BasisSaveScope.ReadOnly, null, "badge", new byte[] { 0 }), "a ReadOnly write always lands in the caller's own space");
            Assert.IsTrue(BasisSaveShim.Delete(other, BasisSaveScope.ReadOnly, null, "badge"));
            Assert.IsTrue(BasisSaveShim.Save(other, ownerId, "badge", new byte[] { 0 }));
            CollectionAssert.AreEqual(badge, BasisSaveShim.LoadReadOnly(other, ownerId, null, "badge"), "nothing a non-owner can call changes the owner's ReadOnly data");
            foreach (MethodInfo method in typeof(BasisSaveShim).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                bool namesAnOwner = method.GetParameters().Any(p => p.Name.StartsWith("owner", StringComparison.OrdinalIgnoreCase));
                Assert.IsTrue(!namesAnOwner || (method.Name == nameof(BasisSaveShim.LoadReadOnly) && method.ReturnType == typeof(byte[])), method + " lets a caller name another owner");
            }

            Use(owner);
            Assert.IsTrue(BasisSaveShim.Save(owner, BasisSaveScope.ReadOnly, null, "badge", new byte[] { 8 }));
            Use(other);
            CollectionAssert.AreEqual(new byte[] { 8 }, BasisSaveShim.LoadReadOnly(other, " " + ownerId.ToUpperInvariant() + " ", null, "badge"));
        }

        [Test]
        public void ProtectedSubStores_BelongToTheirContentAlone()
        {
            string first = NewContentId(), second = NewContentId(), sub = "notes-" + Guid.NewGuid().ToString("N");
            Track(ProtectedOf(first, sub));
            Track(SharedOf(sub));
            CilboxProxy mine = Script(typeof(CilboxPropBasis), Prop(first, true).transform);
            Assert.IsTrue(BasisSaveShim.Save(mine, BasisSaveScope.Protected, " " + sub.ToUpperInvariant(), "slot", new byte[] { 1 }));
            Assert.IsTrue(File.Exists(FileIn(ProtectedOf(first, sub), "slot")));

            CilboxProxy theirs = Script(typeof(CilboxPropBasis), Prop(second, true).transform);
            Assert.IsNull(BasisSaveShim.Load(theirs, BasisSaveScope.Protected, sub, "slot"));
            Assert.IsNull(BasisSaveShim.Load(theirs, sub, "slot"));
            Assert.IsNull(BasisSaveShim.LoadReadOnly(theirs, first, sub, "slot"));

            Use(mine);
            CollectionAssert.AreEqual(new byte[] { 1 }, BasisSaveShim.Load(mine, BasisSaveScope.Protected, sub, "slot"));
            Assert.IsFalse(BasisSaveShim.Save(mine, BasisSaveScope.Protected, "   ", "slot", new byte[] { 1 }), "only null means the default store");
            Assert.IsFalse(BasisSaveShim.Save(mine, BasisSaveScope.Protected, "line\nbreak", "slot", new byte[] { 1 }));
            Assert.IsFalse(BasisSaveShim.Save(mine, BasisSaveScope.Shared, null, "slot", new byte[] { 1 }), "a shared store needs a name");
            Assert.IsFalse(BasisSaveShim.Save(mine, (BasisSaveScope)9, null, "slot", new byte[] { 1 }));
        }

        [Test]
        public void ReadingOtherContentsReadOnlyStores_HasItsOwnBudget()
        {
            string publisher = NewContentId(), late = NewContentId();
            CilboxProxy first = Script(typeof(CilboxPropBasis), Prop(publisher, true).transform);
            Assert.IsTrue(BasisSaveShim.Save(first, BasisSaveScope.ReadOnly, null, "slot", new byte[] { 1 }));
            CilboxProxy second = Script(typeof(CilboxPropBasis), Prop(late, true).transform);
            Assert.IsTrue(BasisSaveShim.Save(second, BasisSaveScope.ReadOnly, null, "slot", new byte[] { 2 }));

            CilboxProxy reader = Script(typeof(CilboxPropBasis));
            for (int i = 0; i < 4; i++)
            {
                Refill(reader);
                Assert.IsTrue(BasisSaveShim.Save(reader, NewStore(), "slot", new byte[] { 3 }), "store " + i);
            }
            CollectionAssert.AreEqual(new byte[] { 1 }, BasisSaveShim.LoadReadOnly(reader, publisher, null, "slot"), "reading published data does not take one of the four store claims");
            for (int i = 0; i < 15; i++) Assert.IsNull(BasisSaveShim.LoadReadOnly(reader, NewContentId(), null, "slot"));
            Assert.IsNull(BasisSaveShim.LoadReadOnly(reader, late, null, "slot"), "a script reads at most 16 other ReadOnly stores per session");
            CollectionAssert.AreEqual(new byte[] { 1 }, BasisSaveShim.LoadReadOnly(reader, publisher, null, "slot"), "stores it already read stay readable");
        }

        [Test]
        public void AnotherPlayersCopy_CannotTouchThisPlayersSaves()
        {
            string store = NewStore(), publisher = NewContentId();
            CilboxProxy owner = Script(typeof(CilboxPropBasis), Prop(publisher, true).transform);
            Assert.IsTrue(BasisSaveShim.Save(owner, BasisSaveScope.ReadOnly, null, "slot", new byte[] { 1 }));

            CilboxProxy theirAvatar = Script(typeof(CilboxAvatarBasis), Avatar(NewContentId(), RemoteId).transform);
            Assert.IsFalse(BasisSaveShim.Save(theirAvatar, store, "slot", new byte[] { 1 }), "another player's copy of an approved avatar");
            Assert.IsNull(BasisSaveShim.Load(theirAvatar, store, "slot"));
            Assert.IsNull(BasisSaveShim.LoadReadOnly(theirAvatar, publisher, null, "slot"));
            CilboxProxy theirProp = Script(typeof(CilboxPropBasis), Prop(NewContentId(), false).transform);
            Assert.IsFalse(BasisSaveShim.Save(theirProp, store, "slot", new byte[] { 1 }), "a prop another player spawned runs here too");
            Assert.IsNull(BasisSaveShim.LoadReadOnly(theirProp, publisher, null, "slot"));
            Assert.IsFalse(Directory.Exists(SharedOf(store)));

            Assert.IsTrue(BasisSaveShim.Save(Script(typeof(CilboxAvatarBasis), Avatar(NewContentId(), LocalId).transform), store, "slot", new byte[] { 1 }));
            Assert.IsTrue(BasisSaveShim.Save(Script(typeof(CilboxPropBasis), World(NewContentId()).transform), store, "slot", new byte[] { 2 }), "a prop that is part of the world");
        }
    }
}
