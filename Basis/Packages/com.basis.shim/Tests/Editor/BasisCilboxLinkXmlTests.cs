using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Cilbox;
using LinkerGenerator;
using NUnit.Framework;

namespace Basis.Tests.Sync
{
    [NoAutoStaticsCleanup]
    public sealed class BasisCilboxLinkXmlTests
    {
        const string ShimLinkXml = "Packages/com.basis.shim/Shims/link.xml";
        const string GeneratedLinkXml = "Assets/Basis/link.xml";

        static readonly (string name, Assembly assembly)[] Bcl =
        {
            ("mscorlib", typeof(object).Assembly),
            ("System", typeof(System.Diagnostics.Stopwatch).Assembly),
            ("System.Core", typeof(System.Linq.Enumerable).Assembly),
        };

        static string Entry(string type)
        {
            return $"<type fullname=\"{type}\" preserve=\"all\" />";
        }

        static SortedDictionary<string, SortedSet<string>> MergeShim(HashSet<string> assemblies)
        {
            var entries = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            BasisLinkGenerator.MergePackageLinkXmlFiles(new[] { ShimLinkXml }, assemblies, entries);
            return entries;
        }

        static bool Has(SortedDictionary<string, SortedSet<string>> entries, string assembly, string type)
        {
            return entries.TryGetValue(assembly, out var set) && set.Contains(Entry(type));
        }

        static HashSet<string> Preserved(string xml, string assembly)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            Match block = Regex.Match(xml, "<assembly fullname=\"" + Regex.Escape(assembly) + "\">(.*?)</assembly>", RegexOptions.Singleline);
            if (!block.Success) return set;
            foreach (Match m in Regex.Matches(block.Groups[1].Value, "<type fullname=\"([^\"]+)\"[^>]*preserve=\"all\""))
            {
                set.Add(m.Groups[1].Value.Replace("&lt;", "<").Replace("&gt;", ">").Replace('/', '+'));
            }
            return set;
        }

        static List<string> WhitelistedTypes()
        {
            var names = new List<string>();
            FieldInfo common = typeof(CilboxBasisCommon).GetField("commonWhiteListType", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(common, "CilboxBasisCommon no longer has a commonWhiteListType field; this test cannot see what it exposes.");
            names.AddRange((HashSet<string>)common.GetValue(null));
            foreach (Type box in new[] { typeof(CilboxSceneBasis), typeof(CilboxPropBasis), typeof(CilboxAvatarBasis) })
            {
                FieldInfo extra = box.GetField("extraWhiteListType", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(extra, $"{box.Name} no longer has an extraWhiteListType field; this test cannot see what it exposes.");
                names.AddRange((HashSet<string>)extra.GetValue(null));
            }
            return names;
        }

        [Test]
        public void TheShimLinkXml_IsFoldedIntoTheGeneratedFile()
        {
            var entries = MergeShim(new HashSet<string>(StringComparer.Ordinal));
            Assert.IsTrue(Has(entries, "mscorlib", "System.DateTime"), "System.DateTime is not preserved under mscorlib.");
            Assert.IsTrue(Has(entries, "mscorlib", "System.Collections.Generic.List`1"), "List`1 is not preserved under mscorlib.");
            Assert.IsTrue(Has(entries, "System", "System.Diagnostics.Stopwatch"), "Stopwatch is not preserved under System, where the player profile defines it.");
            Assert.IsTrue(Has(entries, "System.Core", "System.Collections.Generic.HashSet`1"), "HashSet`1 is not preserved under System.Core, where the player profile defines it.");
            Assert.IsTrue(Has(entries, "UnityEngine.AudioModule", "UnityEngine.AudioSource"), "AudioSource was not re-homed from the UnityEngine facade to UnityEngine.AudioModule.");
            Assert.IsFalse(Has(entries, "UnityEngine", "UnityEngine.AudioSource"), "AudioSource stayed under the UnityEngine facade instead of its module assembly.");
            Assert.IsFalse(entries.ContainsKey("netstandard"), "Entries stayed under the netstandard facade instead of their defining assembly.");
            foreach (var pair in entries)
            {
                foreach (var entry in pair.Value)
                {
                    Assert.IsFalse(entry.Contains("+") || entry.Contains("[T]"), $"{pair.Key}: {entry} is not a Cecil type name, so UnityLinker would ignore it.");
                }
            }
        }

        [Test]
        public void WholesaleAssemblies_GetNoTypeBlock()
        {
            var assemblies = new HashSet<string>(StringComparer.Ordinal) { "UnityEngine.AudioModule" };
            var entries = MergeShim(assemblies);
            string xml = BasisLinkGenerator.BuildLinkerXml(new List<string>(assemblies), entries);
            StringAssert.Contains("<assembly fullname=\"UnityEngine.AudioModule\" preserve=\"all\" />", xml);
            StringAssert.DoesNotContain("<assembly fullname=\"UnityEngine.AudioModule\">", xml);
            StringAssert.Contains("<assembly fullname=\"mscorlib\">", xml);
            StringAssert.Contains(Entry("System.DateTime"), xml);
        }

        [Test]
        public void GeneratedLinkXml_CarriesTheCilboxTypes()
        {
            string xml = File.ReadAllText(GeneratedLinkXml);
            StringAssert.Contains(Entry("System.DateTime"), xml, "Assets/Basis/link.xml predates the package link.xml merge; run Basis/Build/Update Link XML.");
            StringAssert.Contains(Entry("System.Collections.Generic.List`1"), xml, "Assets/Basis/link.xml predates the package link.xml merge; run Basis/Build/Update Link XML.");
        }

        [Test]
        public void EveryPreservedBclType_Exists()
        {
            string xml = File.ReadAllText(ShimLinkXml);
            var missing = new List<string>();
            foreach ((string name, Assembly assembly) in Bcl)
            {
                foreach (string type in Preserved(xml, name))
                {
                    if (assembly.GetType(type, false) == null) missing.Add(name + ": " + type);
                }
            }
            Assert.IsEmpty(missing, "The shim link.xml names types UnityLinker cannot resolve, so they preserve nothing: " + string.Join(", ", missing));
        }

        [Test]
        public void EveryWhitelistedBclType_IsPreserved()
        {
            string xml = File.ReadAllText(ShimLinkXml);
            var preserved = new Dictionary<string, HashSet<string>>();
            foreach ((string name, Assembly assembly) in Bcl) preserved[name] = Preserved(xml, name);
            var unpreserved = new List<string>();
            foreach (string entry in WhitelistedTypes())
            {
                if (entry.Contains("*") || entry.StartsWith("<", StringComparison.Ordinal)) continue;
                bool found = false, ok = false;
                foreach ((string name, Assembly assembly) in Bcl)
                {
                    for (int arity = 0; arity <= 8; arity++)
                    {
                        Type type = assembly.GetType(arity == 0 ? entry : entry + "`" + arity, false);
                        if (type == null || type.Assembly != assembly) continue;
                        found = true;
                        if (preserved[name].Contains(type.FullName)) ok = true;
                    }
                }
                if (found && !ok) unpreserved.Add(entry);
            }
            Assert.IsEmpty(unpreserved, "Whitelisted BCL types without a preserve=\"all\" entry in the shim link.xml. IL2CPP strips their unreferenced members and scripts then fail with 'Could not find reference to': " + string.Join(", ", unpreserved));
        }
    }
}
