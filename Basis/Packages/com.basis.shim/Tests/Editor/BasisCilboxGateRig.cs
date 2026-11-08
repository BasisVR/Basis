using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Basis.BasisUI;
using Cilbox;
using NUnit.Framework;
using UnityEngine;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public abstract class BasisCilboxGateRig
    {
        protected const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        protected const BindingFlags StaticInternal = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        protected static readonly Type[] BoxTypes = { typeof(CilboxAvatarBasis), typeof(CilboxPropBasis), typeof(CilboxSceneBasis) };
        protected readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        readonly List<CilboxBasisCommon> running = new List<CilboxBasisCommon>();
        bool untrustedBefore;
        [SetUp]
        public void RigSetUp()
        {
            untrustedBefore = BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue;
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = true;
        }
        [TearDown]
        public void RigTearDown()
        {
            foreach (CilboxBasisCommon box in running) if (box != null) box.interpreterAccountingDepth = 0;
            running.Clear();
            BasisSettingsDefaults.AllowUntrustedScriptPermissions.RawValue = untrustedBefore;
            for (int i = owned.Count - 1; i >= 0; i--) if (owned[i] != null) UnityEngine.Object.DestroyImmediate(owned[i]);
            owned.Clear();
        }
        protected CilboxBasisCommon Box(Type boxType)
        {
            GameObject host = new GameObject(boxType.Name + " gate test");
            owned.Add(host);
            CilboxBasisCommon box = (CilboxBasisCommon)host.AddComponent(boxType);
            box.classes = new Dictionary<string, int>();
            return box;
        }
        protected T Own<T>(T item) where T : UnityEngine.Object
        {
            owned.Add(item);
            return item;
        }
        protected void Run(CilboxBasisCommon box)
        {
            box.interpreterAccountingDepth = 1;
            running.Add(box);
        }
        protected static FieldInfo Field(string name)
        {
            FieldInfo field = typeof(CilboxBasisCommon).GetField(name, Internal);
            Assert.IsNotNull(field, $"CilboxBasisCommon.{name} moved; this test cannot drive consent.");
            return field;
        }
        protected static void Grant(CilboxBasisCommon box, params string[] items)
        {
            FieldInfo state = Field("consentState");
            state.SetValue(box, Enum.Parse(state.FieldType, "Allowed"));
            Field("consentGranted").SetValue(box, new HashSet<string>(items, StringComparer.Ordinal));
        }
        protected static SortedSet<string> DryRun(CilboxBasisCommon box, Action run)
        {
            FieldInfo scanField = Field("consentScan");
            object scan = Activator.CreateInstance(scanField.FieldType, true);
            scanField.SetValue(box, scan);
            try
            {
                run();
            }
            finally
            {
                scanField.SetValue(box, null);
            }
            return (SortedSet<string>)scan.GetType().GetField("Items", Internal).GetValue(scan);
        }
        protected static SerializedTypeDescriptor[] Descriptors(params Type[] types) => types.Select(SerializedTypeDescriptorBuilder.FromNativeType).ToArray();
        protected static bool Resolve(CilboxBasisCommon box, Type declaringType, string name, out MethodInfo method, params Type[] parameters) => box.CheckMethodAllowed(out method, declaringType, name, Descriptors(parameters), Array.Empty<SerializedTypeDescriptor>(), name);
        protected static Type ShimType(string name)
        {
            Type type = typeof(CilboxBasisCommon).Assembly.GetType(name);
            Assert.IsNotNull(type, $"{name} moved; this test cannot reach it.");
            return type;
        }
        protected static MethodInfo ShimMethod(string type, string name)
        {
            MethodInfo method = ShimType(type).GetMethod(name, StaticInternal);
            Assert.IsNotNull(method, $"{type}.{name} moved; this test cannot reach it.");
            return method;
        }
    }
}
