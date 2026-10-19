// BasisLinkGenerator.cs
// Put this file under an Editor folder (e.g. Assets/Editor/BasisLinkGenerator.cs)
//
// Generates Assets/Basis/link.xml with a UNION of assemblies likely to appear in Player builds
// across Windows/macOS/Linux/Android/iOS/etc.
//
// Fixes vs prior version:
// ✅ Resolves asmdef "references" entries that are "GUID:..." into actual assembly names (by loading the referenced asmdef)
// ✅ Filters out Editor-only and Test assemblies (so no *.Editor, TestRunner, nunit, etc)
// ✅ Still scans Assets/ + Packages/ for .dll names, parses .rsp, parses asmdef precompiledReferences
// ✅ Cancelable throttled progress bar
// ✅ Writes only if content changed; imports only the link.xml asset
//
// Notes:
// - This remains intentionally conservative (may include extra runtime assemblies).
// - It will NOT include "GUID:..." in output. If a GUID can't be resolved, it's skipped (with a warning).
// - We exclude editor/test by name heuristics + asmdef includePlatforms/excludePlatforms when available.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using Basis.Editor.Localization;
using UnityEditor;
using UnityEditor.Compilation;
using System.Reflection;
using UnityEngine;

namespace LinkerGenerator
{
    public static class BasisLinkGenerator
    {
        private const string OutputLinkXml = "Assets/Basis/link.xml";
        private const string ScanAssetsRoot = "Assets";
        private const string ScanPackagesRoot = "Packages";

        // Throttle progress UI updates (too frequent updates can slow scans).
        private const double ProgressUpdateMinSeconds = 0.05;

        [MenuItem("Basis/Build/Update Link XML", false, 321)]
        public static void GenerateLinkXml()
        {
            try
            {
                Generate();
            }
            catch (OperationCanceledException)
            {
                BasisDebug.Log("link.xml generation canceled.");
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        public static void Generate()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var assemblies = new HashSet<string>(StringComparer.Ordinal);

            // Cache for GUID->asmdef name to avoid repeated JSON loads.
            var guidAsmdefNameCache = new Dictionary<string, string>(StringComparer.Ordinal);

            // 1) Unity player assemblies (already excludes tests)
            if (Cancelable(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), 0.03f, BasisEditorLocalization.Get("sdk.linkGenerator.progress.collectingPlayerAssemblies"))) return;
            AddUnityPlayerAssemblies(assemblies);

            var externalPackageRoots = GetExternalPackageRoots();

            // 2) Scan .dll file names (union, not target filtered) but exclude obvious editor/test names
            if (Cancelable(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), 0.10f, BasisEditorLocalization.Get("sdk.linkGenerator.progress.scanningAssets"))) return;
            AddDllNamesUnderRoot(ScanAssetsRoot, assemblies, progressBase: 0.10f, progressSpan: 0.22f);

            if (Cancelable(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), 0.32f, BasisEditorLocalization.Get("sdk.linkGenerator.progress.scanningPackages"))) return;
            AddDllNamesUnderRoot(ScanPackagesRoot, assemblies, progressBase: 0.32f, progressSpan: 0.22f);
            foreach (var externalRoot in externalPackageRoots)
                AddDllNamesUnderRoot(externalRoot, assemblies, progressBase: 0.32f, progressSpan: 0.22f);

            // 3) Parse .rsp references
            if (Cancelable(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), 0.54f, BasisEditorLocalization.Get("sdk.linkGenerator.progress.parsingRspFiles"))) return;
            AddRspAssemblyNames(ScanAssetsRoot, assemblies, progressBase: 0.54f, progressSpan: 0.08f);
            AddRspAssemblyNames(ScanPackagesRoot, assemblies, progressBase: 0.62f, progressSpan: 0.08f);
            foreach (var externalRoot in externalPackageRoots)
                AddRspAssemblyNames(externalRoot, assemblies, progressBase: 0.62f, progressSpan: 0.08f);

            // 4) Parse asmdefs (resolve GUID references, include precompiledReferences)
            if (Cancelable(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), 0.70f, BasisEditorLocalization.Get("sdk.linkGenerator.progress.parsingAsmdefFiles"))) return;
            AddAsmdefReferences(ScanAssetsRoot, assemblies, guidAsmdefNameCache, progressBase: 0.70f, progressSpan: 0.10f);
            AddAsmdefReferences(ScanPackagesRoot, assemblies, guidAsmdefNameCache, progressBase: 0.80f, progressSpan: 0.10f);
            foreach (var externalRoot in externalPackageRoots)
                AddAsmdefReferences(externalRoot, assemblies, guidAsmdefNameCache, progressBase: 0.80f, progressSpan: 0.10f);

            // 5) Fold in the link.xml files shipped inside packages; Unity only collects Assets/**/link.xml
            if (Cancelable(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), 0.90f, BasisEditorLocalization.Get("sdk.linkGenerator.progress.checkingCilbox"))) return;
            var packageLinkXmlFiles = FindPackageLinkXmlFiles(ScanPackagesRoot, externalPackageRoots);
            var packageTypeEntries = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            MergePackageLinkXmlFiles(packageLinkXmlFiles, assemblies, packageTypeEntries);

            // Final filtering pass (removes editor/test/invalid like GUID:...)
            if (Cancelable(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), 0.92f, BasisEditorLocalization.Get("sdk.linkGenerator.progress.filteringAssemblies"))) return;
            var sorted = new List<string>(assemblies.Count);
            foreach (var a in assemblies)
            {
                if (string.IsNullOrWhiteSpace(a)) continue;
                if (!IsValidPlayerAssemblyName(a)) continue;
                sorted.Add(a);
            }
            sorted.Sort(StringComparer.Ordinal);

            if (Cancelable(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), 0.95f, BasisEditorLocalization.Get("sdk.linkGenerator.progress.buildingXml"))) return;
            int packageTypeEntryCount = 0;
            foreach (var pair in packageTypeEntries)
            {
                if (!assemblies.Contains(pair.Key))
                    packageTypeEntryCount += pair.Value.Count;
            }
            string xml = BuildLinkerXml(sorted, packageTypeEntries);

            if (Cancelable(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), 0.97f, BasisEditorLocalization.Get("sdk.linkGenerator.progress.ensuringFolder"))) return;
            EnsureParentFolderExists(OutputLinkXml);

            if (Cancelable(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), 0.985f, BasisEditorLocalization.Get("sdk.linkGenerator.progress.savingLinkXml"))) return;
            bool wrote = WriteIfChanged(OutputLinkXml, xml);

            if (Cancelable(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), 0.995f, BasisEditorLocalization.Get("sdk.linkGenerator.progress.importingLinkXml"))) return;
            if (wrote)
                AssetDatabase.ImportAsset(OutputLinkXml, ImportAssetOptions.ForceUpdate);

            sw.Stop();
            BasisDebug.Log($"Generated link.xml with {sorted.Count} assemblies and {packageTypeEntryCount} type entries from {packageLinkXmlFiles.Count} package link.xml files at: {OutputLinkXml} (changed={wrote}) in {sw.ElapsedMilliseconds} ms");
        }

        // -------------------- Discovery --------------------

        private static void AddUnityPlayerAssemblies(HashSet<string> output)
        {
            var unityAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies);
            for (int i = 0; i < unityAssemblies.Length; i++)
            {
                var name = unityAssemblies[i].name;
                if (!string.IsNullOrWhiteSpace(name) && IsValidPlayerAssemblyName(name))
                    output.Add(name);
            }

            output.Add("UnityEngine.CoreModule");
        }

        private static List<string> GetExternalPackageRoots()
        {
            var roots = new List<string>();

            UnityEditor.PackageManager.PackageInfo[] packages;
            try
            {
                packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"link.xml: failed to enumerate registered packages: {ex.Message}");
                return roots;
            }

            if (packages == null)
                return roots;

            foreach (var package in packages)
            {
                if (package == null)
                    continue;

                var source = package.source;
                if (source == UnityEditor.PackageManager.PackageSource.BuiltIn
                    || source == UnityEditor.PackageManager.PackageSource.Embedded
                    || source == UnityEditor.PackageManager.PackageSource.Registry)
                    continue;

                string resolved = package.resolvedPath;
                if (!string.IsNullOrEmpty(resolved) && Directory.Exists(resolved))
                    roots.Add(resolved);
            }

            return roots;
        }

        private static void AddDllNamesUnderRoot(string root, HashSet<string> output, float progressBase, float progressSpan)
        {
            if (!Directory.Exists(root))
                return;

            double lastUi = EditorApplication.timeSinceStartup;

            foreach (var path in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
            {
                // Throttled UI updates
                double now = EditorApplication.timeSinceStartup;
                if (now - lastUi > ProgressUpdateMinSeconds)
                {
                    lastUi = now;
                    float p = Mathf.Clamp01(progressBase + progressSpan * 0.5f);
                    if (EditorUtility.DisplayCancelableProgressBar(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), BasisEditorLocalization.Get("sdk.linkGenerator.progress.checkingDll", path), p))
                        throw new OperationCanceledException();
                }

                // Exclude editor-only plugins if importer metadata exists.
                if (IsEditorOnlyPlugin(path))
                    continue;

                var name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(name) && IsValidPlayerAssemblyName(name))
                    output.Add(name);
            }
        }

        private static void AddRspAssemblyNames(string root, HashSet<string> output, float progressBase, float progressSpan)
        {
            if (!Directory.Exists(root))
                return;

            double lastUi = EditorApplication.timeSinceStartup;

            foreach (var rspPath in Directory.EnumerateFiles(root, "*.rsp", SearchOption.AllDirectories))
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - lastUi > ProgressUpdateMinSeconds)
                {
                    lastUi = now;
                    float p = Mathf.Clamp01(progressBase + progressSpan * 0.5f);
                    if (EditorUtility.DisplayCancelableProgressBar(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), BasisEditorLocalization.Get("sdk.linkGenerator.progress.parsingRsp", rspPath), p))
                        throw new OperationCanceledException();
                }

                foreach (var rawLine in File.ReadLines(rspPath))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0) continue;

                    if (TryParseReferenceArg(line, out var value))
                        ExtractAssemblyNamesFromReferenceValue(value, output);
                }
            }
        }

        private static void AddAsmdefReferences(
            string root,
            HashSet<string> output,
            Dictionary<string, string> guidAsmdefNameCache,
            float progressBase,
            float progressSpan)
        {
            if (!Directory.Exists(root))
                return;

            double lastUi = EditorApplication.timeSinceStartup;

            foreach (var asmdefPath in Directory.EnumerateFiles(root, "*.asmdef", SearchOption.AllDirectories))
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - lastUi > ProgressUpdateMinSeconds)
                {
                    lastUi = now;
                    float p = Mathf.Clamp01(progressBase + progressSpan * 0.5f);
                    if (EditorUtility.DisplayCancelableProgressBar(BasisEditorLocalization.Get("sdk.linkGenerator.progress.title"), BasisEditorLocalization.Get("sdk.linkGenerator.progress.parsingAsmdef", asmdefPath), p))
                        throw new OperationCanceledException();
                }

                string json;
                try
                {
                    json = File.ReadAllText(asmdefPath, Encoding.UTF8);
                }
                catch
                {
                    continue;
                }

                AsmdefData data;
                try
                {
                    data = JsonUtility.FromJson<AsmdefData>(json);
                }
                catch
                {
                    continue;
                }

                if (data == null) continue;

                // If asmdef is explicitly Editor-only, skip it.
                // Heuristic: includePlatforms contains "Editor" or excludePlatforms excludes everything except Editor.
                if (AsmdefIsEditorOnly(data))
                    continue;

                // references can be assembly names OR GUID:... entries.
                if (data.references != null)
                {
                    for (int i = 0; i < data.references.Length; i++)
                    {
                        var r = data.references[i];
                        if (string.IsNullOrWhiteSpace(r)) continue;

                        r = r.Trim();

                        if (r.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
                        {
                            var guid = r.Substring("GUID:".Length).Trim();
                            var resolvedName = ResolveAsmdefNameFromGuid(guid, guidAsmdefNameCache);
                            if (!string.IsNullOrWhiteSpace(resolvedName) && IsValidPlayerAssemblyName(resolvedName))
                                output.Add(resolvedName);
                        }
                        else
                        {
                            if (IsValidPlayerAssemblyName(r))
                                output.Add(r);
                        }
                    }
                }

                // precompiledReferences are usually DLL names (sometimes paths).
                if (data.precompiledReferences != null)
                {
                    for (int i = 0; i < data.precompiledReferences.Length; i++)
                    {
                        var p = data.precompiledReferences[i];
                        if (string.IsNullOrWhiteSpace(p)) continue;

                        var name = Path.GetFileNameWithoutExtension(p.Trim());
                        if (!string.IsNullOrWhiteSpace(name) && IsValidPlayerAssemblyName(name))
                            output.Add(name);
                    }
                }
            }
        }

        private static List<string> FindPackageLinkXmlFiles(string packagesRoot, List<string> externalPackageRoots)
        {
            var files = new List<string>();
            var roots = new List<string> { packagesRoot };
            roots.AddRange(externalPackageRoots);
            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var path in Directory.EnumerateFiles(root, "link.xml", SearchOption.AllDirectories))
                {
                    if (!IsUnityIgnoredPath(path))
                        files.Add(path);
                }
            }
            return files;
        }

        private static bool IsUnityIgnoredPath(string path)
        {
            string[] parts = path.Replace('\\', '/').Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                if (part.Length == 0) continue;
                if (part.EndsWith("~", StringComparison.Ordinal) || part.StartsWith(".", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public static void MergePackageLinkXmlFiles(IEnumerable<string> linkXmlFiles, HashSet<string> assemblies, SortedDictionary<string, SortedSet<string>> typeEntries)
        {
            foreach (var file in linkXmlFiles)
            {
                var doc = new XmlDocument();
                try
                {
                    doc.Load(file);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogWarning($"link.xml: skipping unreadable {file}: {ex.Message}");
                    continue;
                }

                XmlElement linker = doc.DocumentElement;
                if (linker == null || linker.Name != "linker")
                    continue;

                foreach (XmlNode assemblyNode in linker.ChildNodes)
                {
                    if (!(assemblyNode is XmlElement assemblyElement) || assemblyElement.Name != "assembly")
                        continue;

                    string assemblyName = assemblyElement.GetAttribute("fullname").Trim();
                    if (assemblyName.Length == 0)
                        continue;

                    bool hasElements = false;
                    foreach (XmlNode child in assemblyElement.ChildNodes)
                    {
                        if (child is XmlElement)
                        {
                            hasElements = true;
                            break;
                        }
                    }

                    if (!hasElements)
                    {
                        if (assemblyElement.GetAttribute("preserve") == "all" && IsValidPlayerAssemblyName(assemblyName))
                            assemblies.Add(assemblyName);
                        continue;
                    }

                    foreach (XmlNode child in assemblyElement.ChildNodes)
                    {
                        if (!(child is XmlElement element))
                            continue;

                        if (element.Name != "type")
                        {
                            AddTypeEntry(typeEntries, assemblyName, element.OuterXml);
                            continue;
                        }

                        string fullName = element.GetAttribute("fullname").Trim();
                        List<Type> resolved = ResolveLinkXmlTypes(fullName, assemblyName);
                        if (resolved.Count == 0)
                        {
                            if (fullName.IndexOf('*') < 0)
                                BasisDebug.LogWarning($"link.xml: {file} preserves {assemblyName} type '{fullName}', which does not resolve in the editor; kept as written.");
                            AddTypeEntry(typeEntries, assemblyName, element.OuterXml);
                            continue;
                        }

                        for (int i = 0; i < resolved.Count; i++)
                        {
                            Type type = resolved[i];
                            element.SetAttribute("fullname", type.FullName.Replace('+', '/'));
                            AddTypeEntry(typeEntries, type.Assembly.GetName().Name, element.OuterXml);
                        }
                    }
                }
            }
        }

        private static void AddTypeEntry(SortedDictionary<string, SortedSet<string>> typeEntries, string assemblyName, string entry)
        {
            if (!IsValidPlayerAssemblyName(assemblyName))
                return;

            if (!typeEntries.TryGetValue(assemblyName, out var entries))
            {
                entries = new SortedSet<string>(StringComparer.Ordinal);
                typeEntries[assemblyName] = entries;
            }
            entries.Add(entry);
        }

        private static List<Type> ResolveLinkXmlTypes(string fullName, string declaredAssemblyName)
        {
            var result = new List<Type>();
            if (string.IsNullOrEmpty(fullName))
                return result;

            var loaded = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
            int star = fullName.IndexOf('*');
            if (star < 0)
            {
                string name = fullName.Replace('/', '+');
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int i = 0; i < loaded.Count; i++)
                    {
                        bool declared = loaded[i].GetName().Name == declaredAssemblyName;
                        if ((pass == 0) != declared)
                            continue;

                        Type type;
                        try { type = loaded[i].GetType(name, false); }
                        catch { continue; }
                        if (type != null)
                        {
                            result.Add(type);
                            return result;
                        }
                    }
                }
                return result;
            }

            string prefix = fullName.Substring(0, star).Replace('/', '+');
            string suffix = fullName.Substring(star + 1).Replace('/', '+');
            for (int i = 0; i < loaded.Count; i++)
            {
                if (!IsValidPlayerAssemblyName(loaded[i].GetName().Name))
                    continue;

                Type[] types;
                try { types = loaded[i].GetExportedTypes(); }
                catch { continue; }
                for (int t = 0; t < types.Length; t++)
                {
                    string candidate = types[t].FullName;
                    if (candidate != null && candidate.StartsWith(prefix, StringComparison.Ordinal) && candidate.EndsWith(suffix, StringComparison.Ordinal))
                        result.Add(types[t]);
                }
            }
            return result;
        }

        [Serializable]
        private class AsmdefData
        {
            public string name;
            public string[] references;
            public string[] precompiledReferences;

            // These fields exist on many asmdefs; JsonUtility ignores if absent.
            public string[] includePlatforms;
            public string[] excludePlatforms;
            public bool allowUnsafeCode;
            public bool overrideReferences;
            public bool autoReferenced;
        }

        private static bool AsmdefIsEditorOnly(AsmdefData data)
        {
            // If includePlatforms explicitly includes only Editor -> editor only.
            // If includePlatforms contains Editor but also others, we don't treat as editor-only.
            if (data.includePlatforms != null && data.includePlatforms.Length > 0)
            {
                bool hasEditor = false;
                bool hasNonEditor = false;
                for (int i = 0; i < data.includePlatforms.Length; i++)
                {
                    var p = data.includePlatforms[i];
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    if (string.Equals(p.Trim(), "Editor", StringComparison.OrdinalIgnoreCase))
                        hasEditor = true;
                    else
                        hasNonEditor = true;
                }

                if (hasEditor && !hasNonEditor)
                    return true;
            }

            // excludePlatforms includes Editor doesn't necessarily mean editor-only, so we don't over-filter.
            return false;
        }

        // -------------------- GUID reference resolution --------------------

        private static string ResolveAsmdefNameFromGuid(string guid, Dictionary<string, string> cache)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return null;

            if (cache.TryGetValue(guid, out var cached))
                return cached;

            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
            {
                cache[guid] = null;
                // Keep warnings light; GUID refs can exist for non-asmdef assets in some ecosystems.
                // Debug.LogWarning($"GUID reference did not resolve to an asmdef: {guid} -> '{assetPath}'");
                return null;
            }

            try
            {
                string json = File.ReadAllText(assetPath, Encoding.UTF8);
                var data = JsonUtility.FromJson<AsmdefNameOnly>(json);
                string name = data != null ? data.name : null;

                cache[guid] = name;
                return name;
            }
            catch
            {
                cache[guid] = null;
                return null;
            }
        }

        [Serializable]
        private class AsmdefNameOnly
        {
            public string name;
        }

        // -------------------- Filtering --------------------

        private static bool IsValidPlayerAssemblyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            name = name.Trim();

            // Definitely not an assembly name (was leaking from asmdef references)
            if (name.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
                return false;

            // Filter out editor-only by convention
            // (Many packages use *.Editor or *Editor).
            if (name.EndsWith(".Editor", StringComparison.OrdinalIgnoreCase))
                return false;

            // Common editor helpers that aren't in player
            if (name.EndsWith("_EditorHelper", StringComparison.OrdinalIgnoreCase))
                return false;

            // Filter out test assemblies by common naming patterns
            if (name.IndexOf("TestRunner", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (name.IndexOf("nunit", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (name.IndexOf(".Tests", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase))
                return false;

            // You can add more filters here if you see more editor/test patterns in output.
            return true;
        }

        // -------------------- PluginImporter filtering (Editor-only exclusion) --------------------

        private static bool IsEditorOnlyPlugin(string filePath)
        {
            // If we can’t find importer metadata, we assume it’s not editor-only and include it.
            string assetPath = NormalizeToUnityPath(filePath);
            var importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
            if (importer == null)
                return false;

            // Heuristic: editor-only typically means compatible with Editor, not compatible with Any Platform.
            bool editor = importer.GetCompatibleWithEditor();
            bool any = importer.GetCompatibleWithAnyPlatform();
            return editor && !any;
        }

        private static string NormalizeToUnityPath(string path)
        {
            path = path.Replace('\\', '/');

            if (path.StartsWith("Assets/", StringComparison.Ordinal) || path == "Assets")
                return path;

            if (path.StartsWith("Packages/", StringComparison.Ordinal) || path == "Packages")
                return path;

            // If absolute, make relative to project root (parent of Assets).
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
            if (path.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                string rel = path.Substring(projectRoot.Length).TrimStart('/');
                return rel;
            }

            return path;
        }

        // -------------------- XML build/write --------------------

        public static string BuildLinkerXml(List<string> assembliesSorted, SortedDictionary<string, SortedSet<string>> typeEntries)
        {
            int cap = 64 + assembliesSorted.Count * 64;
            var sb = new StringBuilder(cap);

            sb.AppendLine("<linker>");
            sb.AppendLine();

            for (int i = 0; i < assembliesSorted.Count; i++)
            {
                sb.Append("    <assembly fullname=\"");
                AppendEscapedXmlAttr(sb, assembliesSorted[i]);
                sb.AppendLine("\" preserve=\"all\" />");
            }

            var wholesale = new HashSet<string>(assembliesSorted, StringComparer.Ordinal);
            foreach (var pair in typeEntries)
            {
                if (wholesale.Contains(pair.Key) || pair.Value.Count == 0)
                    continue;

                sb.AppendLine();
                sb.Append("    <assembly fullname=\"");
                AppendEscapedXmlAttr(sb, pair.Key);
                sb.AppendLine("\">");
                foreach (var entry in pair.Value)
                {
                    sb.Append("        ");
                    sb.AppendLine(entry);
                }
                sb.AppendLine("    </assembly>");
            }

            sb.AppendLine();
            sb.AppendLine("</linker>");
            return sb.ToString();
        }

        private static void AppendEscapedXmlAttr(StringBuilder sb, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    default: sb.Append(c); break;
                }
            }
        }

        private static void EnsureParentFolderExists(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string abs = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            string dir = Path.GetDirectoryName(abs);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private static bool WriteIfChanged(string assetPath, string newContent)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string abs = Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(abs))
            {
                string old = File.ReadAllText(abs, Encoding.UTF8);
                if (string.Equals(old, newContent, StringComparison.Ordinal))
                    return false;
            }

            File.WriteAllText(abs, newContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }

        // -------------------- .rsp parsing helpers --------------------

        private static bool TryParseReferenceArg(string trimmedLine, out string value)
        {
            const string r1 = "-r:";
            const string r2 = "-reference:";

            if (trimmedLine.StartsWith(r1, StringComparison.OrdinalIgnoreCase))
            {
                value = trimmedLine.Substring(r1.Length).Trim();
                return true;
            }

            if (trimmedLine.StartsWith(r2, StringComparison.OrdinalIgnoreCase))
            {
                value = trimmedLine.Substring(r2.Length).Trim();
                return true;
            }

            value = null;
            return false;
        }

        private static void ExtractAssemblyNamesFromReferenceValue(string value, HashSet<string> output)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            value = TrimQuotes(value);

            // Some tooling separates refs by ';'
            int start = 0;
            for (int i = 0; i <= value.Length; i++)
            {
                bool atEnd = i == value.Length;
                if (atEnd || value[i] == ';')
                {
                    int len = i - start;
                    if (len > 0)
                    {
                        var part = value.Substring(start, len).Trim();
                        part = TrimQuotes(part);
                        if (!string.IsNullOrEmpty(part))
                        {
                            var name = Path.GetFileNameWithoutExtension(part);
                            if (!string.IsNullOrWhiteSpace(name) && IsValidPlayerAssemblyName(name))
                                output.Add(name);
                        }
                    }
                    start = i + 1;
                }
            }
        }

        private static string TrimQuotes(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2).Trim();
            return s;
        }

        // -------------------- UI helpers --------------------

        private static bool Cancelable(string title, float progress01, string info)
        {
            return EditorUtility.DisplayCancelableProgressBar(title, info, Mathf.Clamp01(progress01));
        }
    }
}
