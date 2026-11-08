using System;
using System.Collections.Generic;
using System.Reflection;
using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests.Sync
{
    public sealed class BasisCilboxInstantiateSelectorTests : BasisCilboxGateRig
    {
        const string SpawnProps = "Method:UnityEngine.Object.Instantiate";
        static BundledContentHolder.Selector SelectorFor(CilboxBasisCommon box)
        {
            MethodInfo method = typeof(BasisCilboxInstantiateShim).GetMethod("SelectorFor", StaticInternal);
            Assert.IsNotNull(method, "BasisCilboxInstantiateShim.SelectorFor moved; this test cannot read the spawn selector.");
            return (BundledContentHolder.Selector)method.Invoke(null, new object[] { box });
        }
        static BundledContentHolder.Selector RunningSelector()
        {
            MethodInfo method = typeof(BasisCilboxInstantiateShim).GetMethod("RunningSelector", StaticInternal);
            Assert.IsNotNull(method, "BasisCilboxInstantiateShim.RunningSelector moved; this test cannot read the spawn selector.");
            return (BundledContentHolder.Selector)method.Invoke(null, null);
        }
        [Test]
        public void EachBoxKind_SpawnsWithItsOwnList()
        {
            Assert.AreEqual(BundledContentHolder.Selector.Avatar, SelectorFor(Box(typeof(CilboxAvatarBasis))));
            Assert.AreEqual(BundledContentHolder.Selector.Prop, SelectorFor(Box(typeof(CilboxPropBasis))));
            Assert.AreEqual(BundledContentHolder.Selector.World, SelectorFor(Box(typeof(CilboxSceneBasis))));
            Assert.AreEqual(BundledContentHolder.Selector.Avatar, SelectorFor(null), "an unknown caller gets the smallest list");
        }
        [Test]
        public void AnAvatarWithTheSpawnPropsGrant_UsesThePropList()
        {
            CilboxBasisCommon avatar = Box(typeof(CilboxAvatarBasis));
            Grant(avatar, SpawnProps);
            Assert.AreEqual(BundledContentHolder.Selector.Prop, SelectorFor(avatar));
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = false;
            Assert.AreEqual(BundledContentHolder.Selector.Avatar, SelectorFor(avatar), "the grant opens nothing while untrusted permissions are off");
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = true;
            CilboxBasisCommon other = Box(typeof(CilboxAvatarBasis));
            Grant(other, "Method:UnityEngine.GameObject.AddComponent");
            Assert.AreEqual(BundledContentHolder.Selector.Avatar, SelectorFor(other), "another grant does not open the prop list");
        }
        [Test]
        public void TheRunningBox_PicksTheList()
        {
            CilboxBasisCommon avatar = Box(typeof(CilboxAvatarBasis));
            CilboxBasisCommon prop = Box(typeof(CilboxPropBasis));
            CilboxBasisCommon world = Box(typeof(CilboxSceneBasis));
            Run(avatar);
            Assert.AreEqual(BundledContentHolder.Selector.Avatar, RunningSelector());
            avatar.interpreterAccountingDepth = 0;
            Run(prop);
            Assert.AreEqual(BundledContentHolder.Selector.Prop, RunningSelector());
            prop.interpreterAccountingDepth = 0;
            Run(world);
            Assert.AreEqual(BundledContentHolder.Selector.World, RunningSelector());
            Run(avatar);
            Assert.AreEqual(BundledContentHolder.Selector.Avatar, RunningSelector(), "two boxes running at once is treated as unknown");
        }
        [Test]
        public void AnAvatarsInstantiate_StillLoads_AndAsksForSpawnProps()
        {
            foreach (Type boxType in BoxTypes)
            {
                CilboxBasisCommon box = Box(boxType);
                MethodInfo method = null;
                SortedSet<string> items = DryRun(box, () => Assert.IsTrue(Resolve(box, typeof(UnityEngine.Object), "Instantiate", out method, typeof(UnityEngine.Object)), boxType.Name));
                Assert.IsNotNull(method, boxType.Name);
                Assert.AreEqual(typeof(BasisCilboxInstantiateShim), method.DeclaringType, boxType.Name);
                Assert.AreEqual(boxType == typeof(CilboxAvatarBasis), items.Contains(SpawnProps), boxType.Name);
                Assert.IsTrue(Resolve(box, typeof(UnityEngine.Object), "Instantiate", out MethodInfo plain, typeof(UnityEngine.Object)), $"{boxType.Name} must load without the grant");
                Assert.AreEqual(typeof(BasisCilboxInstantiateShim), plain.DeclaringType, boxType.Name);
            }
        }
        [Test]
        public void ThePromptNamesSpawnProps()
        {
            List<BasisCilboxPermissionText.Line> lines = BasisCilboxPermissionText.Describe(new[] { SpawnProps });
            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual(BasisLocalization.Get("settings.cilboxPermissions.cap.spawnProps"), lines[0].Text);
            Assert.IsFalse(lines[0].Severe);
        }
        [Test]
        public void AnAvatarClone_LosesPropOnlyComponents_UnlessGranted()
        {
            LogAssert.ignoreFailingMessages = true;
            BundledContentHolder previousHolder = BundledContentHolder.Instance;
            BasisDeviceManagement previousManagement = BasisDeviceManagement.Instance;
            try
            {
                GameObject holderObject = Own(new GameObject("spawn selector holder"));
                holderObject.SetActive(false);
                BundledContentHolder holder = holderObject.AddComponent<BundledContentHolder>();
                holder.AvatarScriptSelector = Own(ScriptableObject.CreateInstance<ContentPoliceSelector>());
                holder.PropScriptSelector = Own(ScriptableObject.CreateInstance<ContentPoliceSelector>());
                holder.WorldScriptSelector = Own(ScriptableObject.CreateInstance<ContentPoliceSelector>());
                holder.PropScriptSelector.selectedTypes.Add(typeof(BoxCollider).FullName);
                holder.WorldScriptSelector.selectedTypes.Add(typeof(BoxCollider).FullName);
                System.Reflection.MethodInfo rebuild = typeof(ContentPoliceSelector).GetMethod("BuildCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                rebuild.Invoke(holder.PropScriptSelector, null);
                rebuild.Invoke(holder.WorldScriptSelector, null);
                BundledContentHolder.Instance = holder;
                GameObject managementObject = Own(new GameObject("spawn selector management"));
                managementObject.SetActive(false);
                BasisDeviceManagement management = managementObject.AddComponent<BasisDeviceManagement>();
                GameObject parking = Own(new GameObject("spawn selector parking"));
                parking.SetActive(false);
                management.CreationGameobject = parking;
                BasisDeviceManagement.Instance = management;
                GameObject prefab = Own(new GameObject("spawn selector prefab"));
                prefab.AddComponent<BoxCollider>();
                CilboxBasisCommon avatar = Box(typeof(CilboxAvatarBasis));
                Run(avatar);
                GameObject plain = Own((GameObject)BasisCilboxInstantiateShim.Instantiate((UnityEngine.Object)prefab));
                Assert.IsNotNull(plain);
                Assert.IsNull(plain.GetComponent<BoxCollider>(), "an avatar spawn keeps only what avatars may contain");
                Grant(avatar, SpawnProps);
                GameObject granted = Own((GameObject)BasisCilboxInstantiateShim.Instantiate((UnityEngine.Object)prefab));
                Assert.IsNotNull(granted);
                Assert.IsNotNull(granted.GetComponent<BoxCollider>(), "with spawn props an avatar spawn keeps what props may contain");
            }
            finally
            {
                BundledContentHolder.Instance = previousHolder;
                BasisDeviceManagement.Instance = previousManagement;
                LogAssert.ignoreFailingMessages = false;
            }
        }
    }
}
