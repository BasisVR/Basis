using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Transmitters;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Basis.Tests.Sync
{
    /// <summary>
    /// Prop and avatar scripts built before the self overloads call the desktop and controller shims
    /// without passing this. Those calls resolve to a gated copy that has to find the running box itself.
    /// </summary>
    [NoAutoStaticsCleanup]
    public sealed class BasisCilboxInputLegacyTests
    {
        const ushort LocalId = 7, RemoteId = 8;
        static readonly PropertyInfo LocalPlayerInstance = typeof(BasisLocalPlayer).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        readonly List<GameObject> hosts = new List<GameObject>();
        readonly List<CilboxBasisCommon> boxes = new List<CilboxBasisCommon>();
        BasisNetworkTransmitter transmitterBefore;
        BasisLocalPlayer localBefore;
        Mouse mouse;

        [SetUp]
        public void SetUp()
        {
            transmitterBefore = BasisNetworkManagement.Transmitter;
            localBefore = BasisLocalPlayer.Instance;
            BasisNetworkManagement.Transmitter = new BasisNetworkTransmitter(LocalId);
            LocalPlayerInstance.SetValue(null, null);
            mouse = InputSystem.AddDevice<Mouse>();
            mouse.MakeCurrent();
            InputState.Change(mouse, new MouseState().WithButton(MouseButton.Left));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (CilboxBasisCommon box in boxes) if (box != null) box.interpreterAccountingDepth = 0;
            boxes.Clear();
            InputSystem.RemoveDevice(mouse);
            BasisNetworkManagement.Transmitter = transmitterBefore;
            LocalPlayerInstance.SetValue(null, localBefore);
            for (int i = hosts.Count - 1; i >= 0; i--) if (hosts[i] != null) UnityEngine.Object.DestroyImmediate(hosts[i]);
            hosts.Clear();
        }

        CilboxBasisCommon Box(Type boxType, Func<GameObject> content)
        {
            GameObject root = content?.Invoke();
            GameObject host = new GameObject(boxType.Name + " legacy input test");
            if (root != null) host.transform.SetParent(root.transform);
            else hosts.Add(host);
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            boxes.Add(box);
            return box;
        }

        GameObject Avatar(ushort linkedPlayer)
        {
            GameObject root = new GameObject("legacy input avatar");
            hosts.Add(root);
            root.AddComponent<BasisAvatar>().LinkedPlayerID = linkedPlayer;
            return root;
        }

        GameObject Prop(bool spawnedHere)
        {
            GameObject root = new GameObject("legacy input prop");
            hosts.Add(root);
            root.AddComponent<BasisProp>().AssignContentIdentifier(new BasisNetworkContentBase.BasisContentInformation { LoadedNetID = "legacy-input-test", SpawnedByLocalPlayer = spawnedHere });
            return root;
        }

        static MethodInfo ResolveMouseButton(CilboxBasisCommon box)
        {
            SerializedTypeDescriptor[] parameters = new[] { typeof(int) }.Select(SerializedTypeDescriptorBuilder.FromNativeType).ToArray();
            Assert.IsTrue(box.CheckMethodAllowed(out MethodInfo method, typeof(BasisDesktopInputShim), nameof(BasisDesktopInputShim.GetMouseButton), parameters, Array.Empty<SerializedTypeDescriptor>(), "Boolean GetMouseButton(Int32)"), box.GetType().Name);
            return method;
        }

        static bool Pressed(CilboxBasisCommon box, MethodInfo method)
        {
            box.interpreterAccountingDepth = 1;
            try
            {
                return (bool)method.Invoke(null, new object[] { 0 });
            }
            finally
            {
                box.interpreterAccountingDepth = 0;
            }
        }

        [Test]
        public void OldCalls_ResolveToTheGatedCopy_OutsideWorlds()
        {
            Assert.IsTrue(BasisDesktopInputShim.GetMouseButton(0), "the test mouse is not pressed, so nothing below proves anything");
            Assert.IsNull(ResolveMouseButton(Box(typeof(CilboxSceneBasis), null)), "worlds keep the original method");
            foreach (Type boxType in new[] { typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                MethodInfo method = ResolveMouseButton(Box(boxType, () => Prop(true)));
                Assert.IsNotNull(method, boxType.Name);
                Assert.AreNotEqual(typeof(BasisDesktopInputShim), method.DeclaringType, boxType.Name);
            }
        }

        [Test]
        public void ThePlayersOwnContent_ReadsInput_OtherPlayersContentDoesNot()
        {
            CilboxBasisCommon mine = Box(typeof(CilboxAvatarBasis), () => Avatar(LocalId));
            CilboxBasisCommon theirs = Box(typeof(CilboxAvatarBasis), () => Avatar(RemoteId));
            CilboxBasisCommon spawned = Box(typeof(CilboxPropBasis), () => Prop(true));
            CilboxBasisCommon foreign = Box(typeof(CilboxPropBasis), () => Prop(false));
            Assert.IsTrue(Pressed(mine, ResolveMouseButton(mine)), "the avatar this player wears");
            Assert.IsFalse(Pressed(theirs, ResolveMouseButton(theirs)), "another player's avatar must not read this player's input");
            Assert.IsTrue(Pressed(spawned, ResolveMouseButton(spawned)), "a prop this player spawned");
            Assert.IsFalse(Pressed(foreign, ResolveMouseButton(foreign)), "another player's prop");
        }

        [Test]
        public void ADeadBoxWithAStaleDepth_DoesNotHideTheCaller()
        {
            CilboxBasisCommon dead = Box(typeof(CilboxPropBasis), () => Prop(true));
            ResolveMouseButton(dead);
            dead.disabled = true;
            dead.interpreterAccountingDepth = 1;
            CilboxBasisCommon mine = Box(typeof(CilboxAvatarBasis), () => Avatar(LocalId));
            Assert.IsTrue(Pressed(mine, ResolveMouseButton(mine)), "a disabled box left counted as running made every old call read nothing");
        }

        [Test]
        public void TwoLiveScriptsRunning_ReadsNothing()
        {
            CilboxBasisCommon mine = Box(typeof(CilboxAvatarBasis), () => Avatar(LocalId));
            CilboxBasisCommon theirs = Box(typeof(CilboxAvatarBasis), () => Avatar(RemoteId));
            MethodInfo method = ResolveMouseButton(mine);
            ResolveMouseButton(theirs);
            theirs.interpreterAccountingDepth = 1;
            Assert.IsFalse(Pressed(mine, method), "with two scripts mid-call the gate cannot tell which one asked");
        }

        [Test]
        public void NoRunningScript_ReadsNothing()
        {
            CilboxBasisCommon mine = Box(typeof(CilboxAvatarBasis), () => Avatar(LocalId));
            Assert.IsFalse((bool)ResolveMouseButton(mine).Invoke(null, new object[] { 0 }));
        }
    }
}
