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
using UnityEditor;
using UnityEditor.Compilation;
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

        [MenuItem("Basis/Update link.xml (all platforms union, filtered)")]
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
            if (Cancelable("Link.xml", 0.03f, "Collecting Unity player assemblies...")) return;
            AddUnityPlayerAssemblies(assemblies);

            // 2) Scan .dll file names (union, not target filtered) but exclude obvious editor/test names
            if (Cancelable("Link.xml", 0.10f, "Scanning DLLs in Assets/...")) return;
            AddDllNamesUnderRoot(ScanAssetsRoot, assemblies, progressBase: 0.10f, progressSpan: 0.22f);

            if (Cancelable("Link.xml", 0.32f, "Scanning DLLs in Packages/...")) return;
            AddDllNamesUnderRoot(ScanPackagesRoot, assemblies, progressBase: 0.32f, progressSpan: 0.22f);

            // 3) Parse .rsp references
            if (Cancelable("Link.xml", 0.54f, "Parsing .rsp files...")) return;
            AddRspAssemblyNames(ScanAssetsRoot, assemblies, progressBase: 0.54f, progressSpan: 0.08f);
            AddRspAssemblyNames(ScanPackagesRoot, assemblies, progressBase: 0.62f, progressSpan: 0.08f);

            // 4) Parse asmdefs (resolve GUID references, include precompiledReferences)
            if (Cancelable("Link.xml", 0.70f, "Parsing .asmdef files (resolving GUID refs)...")) return;
            AddAsmdefReferences(ScanAssetsRoot, assemblies, guidAsmdefNameCache, progressBase: 0.70f, progressSpan: 0.10f);
            AddAsmdefReferences(ScanPackagesRoot, assemblies, guidAsmdefNameCache, progressBase: 0.80f, progressSpan: 0.10f);

            // Final filtering pass (removes editor/test/invalid like GUID:...)
            if (Cancelable("Link.xml", 0.90f, "Filtering + sorting assemblies...")) return;
            var sorted = new List<string>(assemblies.Count);
            foreach (var a in assemblies)
            {
                if (string.IsNullOrWhiteSpace(a)) continue;
                if (!IsValidPlayerAssemblyName(a)) continue;
                sorted.Add(a);
            }
            sorted.Sort(StringComparer.Ordinal);

            if (Cancelable("Link.xml", 0.95f, "Building XML...")) return;
            string xml = BuildLinkerXml(sorted);

            if (Cancelable("Link.xml", 0.97f, "Ensuring output folder exists...")) return;
            EnsureParentFolderExists(OutputLinkXml);

            if (Cancelable("Link.xml", 0.985f, "Saving link.xml (only if changed)...")) return;
            bool wrote = WriteIfChanged(OutputLinkXml, xml);

            if (Cancelable("Link.xml", 0.995f, "Importing link.xml...")) return;
            if (wrote)
                AssetDatabase.ImportAsset(OutputLinkXml, ImportAssetOptions.ForceUpdate);

            sw.Stop();
            BasisDebug.Log($"Generated link.xml with {sorted.Count} assemblies at: {OutputLinkXml} (changed={wrote}) in {sw.ElapsedMilliseconds} ms");
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
                    if (EditorUtility.DisplayCancelableProgressBar("Link.xml", $"Checking DLL: {path}", p))
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
                    if (EditorUtility.DisplayCancelableProgressBar("Link.xml", $"Parsing RSP: {rspPath}", p))
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
                    if (EditorUtility.DisplayCancelableProgressBar("Link.xml", $"Parsing asmdef: {asmdefPath}", p))
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

        private static string BuildLinkerXml(List<string> assembliesSorted)
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
