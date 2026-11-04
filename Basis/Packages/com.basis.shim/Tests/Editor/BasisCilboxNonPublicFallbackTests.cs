using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Basis.BasisUI;
using Basis.Shims;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisCilboxNonPublicFallbackTests
    {
        const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
        static readonly Type[] Boxes = { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) };
        static readonly Type[] PublicOnly = { typeof(VideoPlayerShim), typeof(BasisStringDownloader), typeof(BasisImageDownloader) };
        static readonly string[] ParameterTypes = { "Type:Basis.Shims.VideoPlayerShim", "Type:Basis.IBasisStringDownload", "Type:Basis.IBasisImageDownload" };
        readonly List<GameObject> hosts = new List<GameObject>();
        bool untrustedBefore;

        [SetUp]
        public void SetUp()
        {
            untrustedBefore = BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue;
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = true;
        }

        [TearDown]
        public void TearDown()
        {
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = untrustedBefore;
            foreach (GameObject host in hosts)
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
            }
            hosts.Clear();
        }

        CilboxBasisCommon Box(Type boxType)
        {
            GameObject host = new GameObject(boxType.Name + " non-public test");
            hosts.Add(host);
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            if (boxType == typeof(CilboxAvatarBasis)) Grant(box, ParameterTypes);
            return box;
        }

        static FieldInfo Field(string name)
        {
            FieldInfo field = typeof(CilboxBasisCommon).GetField(name, Internal);
            Assert.IsNotNull(field, $"CilboxBasisCommon.{name} moved; this test cannot drive consent.");
            return field;
        }

        static void Grant(CilboxBasisCommon box, params string[] items)
        {
            FieldInfo state = Field("consentState");
            state.SetValue(box, Enum.Parse(state.FieldType, "Allowed"));
            Field("consentGranted").SetValue(box, new HashSet<string>(items, StringComparer.Ordinal));
        }

        static bool Allowed(CilboxBasisCommon box, MethodInfo method)
        {
            Type[] parameters = method.GetParameters().Select(p => p.ParameterType).ToArray();
            return box.CheckMethodAllowed(out _, method.DeclaringType, method.Name, Array.ConvertAll(parameters, SerializedTypeDescriptorBuilder.FromNativeType), Array.Empty<SerializedTypeDescriptor>(), method.ToString());
        }

        static bool AllowedWith(CilboxBasisCommon box, Type type, string name, params Type[] parameters)
        {
            return box.CheckMethodAllowed(out _, type, name, Array.ConvertAll(parameters, SerializedTypeDescriptorBuilder.FromNativeType), Array.Empty<SerializedTypeDescriptor>(), name);
        }

        static List<MethodInfo> HiddenMethods(Type type)
        {
            List<MethodInfo> hidden = type.GetMethods(Declared).Where(m => !m.IsPublic).ToList();
            foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                hidden.AddRange(nested.GetMethods(Declared).Where(m => !nested.IsVisible || !m.IsPublic));
            }
            return hidden.Where(m => !m.ContainsGenericParameters && m.GetParameters().All(p => !p.ParameterType.IsByRef && !p.ParameterType.IsPointer)).ToList();
        }

        [Test]
        public void HiddenMembers_NeverResolve_InAnyBox()
        {
            foreach (Type type in PublicOnly)
            {
                List<MethodInfo> hidden = HiddenMethods(type);
                Assert.IsNotEmpty(hidden, type.Name + " has no hidden members, so this test proves nothing for it");
                foreach (Type boxType in Boxes)
                {
                    CilboxBasisCommon box = Box(boxType);
                    foreach (MethodInfo method in hidden) Assert.IsFalse(Allowed(box, method), boxType.Name + " resolves " + method.DeclaringType.Name + "." + method.Name);
                }
            }
        }

        [Test]
        public void TheUrlPathsThatSkipThePrompt_StayClosed_EvenWithConsent()
        {
            foreach (Type boxType in Boxes)
            {
                CilboxBasisCommon box = Box(boxType);
                Grant(box, ParameterTypes.Concat(new[]
                {
                    "Method:Basis.Shims.VideoPlayerShim.ApplyPendingUrl",
                    "Method:Basis.BasisStringDownloader.BeginRequest",
                    "Method:Basis.BasisStringDownloader.BeginRequestAsync",
                    "Type:UnityEngine.Networking.UnityWebRequest",
                }).ToArray());
                Assert.IsTrue(box.CheckTypeAllowed("UnityEngine.Networking.UnityWebRequest"), boxType.Name + ": the grant itself did not take, so the refusals below would prove nothing.");
                Assert.IsFalse(AllowedWith(box, typeof(VideoPlayerShim), "ApplyPendingUrl", typeof(string)), boxType.Name);
                Assert.IsFalse(AllowedWith(box, typeof(BasisStringDownloader), "BeginRequest", typeof(string), typeof(Action<IBasisStringDownload>)), boxType.Name);
                Assert.IsFalse(AllowedWith(box, typeof(BasisStringDownloader), "BeginRequestAsync", typeof(string), typeof(Action<IBasisStringDownload>)), boxType.Name);
                Assert.IsFalse(box.CheckMethodAllowed(out _, typeof(VideoPlayerShim), "ApplyPendingUrl", null, null, "Void ApplyPendingUrl(System.String)"), boxType.Name + ": the SDK window's probe agrees");
            }
        }

        [Test]
        public void TheVideoPlayersOwnLifecycle_IsNotScriptApi()
        {
            foreach (Type boxType in Boxes)
            {
                CilboxBasisCommon box = Box(boxType);
                Assert.IsFalse(AllowedWith(box, typeof(VideoPlayerShim), nameof(VideoPlayerShim.Awake)), boxType.Name);
                Assert.IsFalse(AllowedWith(box, typeof(VideoPlayerShim), nameof(VideoPlayerShim.OnDestroy)), boxType.Name);
            }
        }

        [Test]
        public void ThePublicApi_StillResolves()
        {
            foreach (Type boxType in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis) })
            {
                CilboxBasisCommon box = Box(boxType);
                Assert.IsTrue(AllowedWith(box, typeof(VideoPlayerShim), "set_url", typeof(string)), boxType.Name);
                Assert.IsTrue(AllowedWith(box, typeof(VideoPlayerShim), "get_url"), boxType.Name);
                Assert.IsTrue(AllowedWith(box, typeof(VideoPlayerShim), nameof(VideoPlayerShim.Play)), boxType.Name);
                Assert.IsTrue(AllowedWith(box, typeof(VideoPlayerShim), nameof(VideoPlayerShim.SetDirectAudioVolume), typeof(ushort), typeof(float)), boxType.Name);
                Assert.IsTrue(AllowedWith(box, typeof(VideoPlayerShim), "add_prepareCompleted", typeof(VideoPlayerShim.EventHandlerShim)), boxType.Name);
                Assert.IsTrue(box.CheckMethodAllowed(out _, typeof(VideoPlayerShim), "set_url", null, null, "Void set_url(System.String)"), boxType.Name + ": the SDK window asks without parameters");
                Assert.IsTrue(AllowedWith(box, typeof(BasisStringDownloader), ".ctor"), boxType.Name + ": new BasisStringDownloader() must keep working");
                Assert.IsTrue(AllowedWith(box, typeof(BasisStringDownloader), nameof(BasisStringDownloader.DownloadString), typeof(string), typeof(Action<IBasisStringDownload>)), boxType.Name);
                Assert.IsTrue(AllowedWith(box, typeof(BasisStringDownloader), nameof(BasisStringDownloader.Dispose)), boxType.Name);
                Assert.IsTrue(AllowedWith(box, typeof(BasisImageDownloader), ".ctor"), boxType.Name);
                Assert.IsTrue(AllowedWith(box, typeof(BasisImageDownloader), nameof(BasisImageDownloader.DownloadImage), typeof(string), typeof(Action<IBasisImageDownload>)), boxType.Name);
            }
        }

        [Test]
        public void TheDryRun_ListsThemAsNeverAllowed()
        {
            CilboxBasisCommon prop = Box(typeof(CilboxPropBasis));
            FieldInfo scanField = Field("consentScan");
            object scan = Activator.CreateInstance(scanField.FieldType, true);
            scanField.SetValue(prop, scan);
            try
            {
                Assert.IsTrue(AllowedWith(prop, typeof(VideoPlayerShim), "ApplyPendingUrl", typeof(string)), "the dry run keeps going");
                Assert.IsTrue(AllowedWith(prop, typeof(VideoPlayerShim), "set_url", typeof(string)));
                Assert.IsTrue(AllowedWith(prop, typeof(BasisStringDownloader), "BeginRequest", typeof(string), typeof(Action<IBasisStringDownload>)));
            }
            finally
            {
                scanField.SetValue(prop, null);
            }
            CollectionAssert.AreEquivalent(new[] { "Method:Basis.Shims.VideoPlayerShim.ApplyPendingUrl", "Method:Basis.BasisStringDownloader.BeginRequest" }, (SortedSet<string>)scan.GetType().GetField("Blocked", Internal).GetValue(scan));
            CollectionAssert.IsEmpty((SortedSet<string>)scan.GetType().GetField("Items", Internal).GetValue(scan), "nothing here is something the player could be asked to allow");
        }
    }
}
