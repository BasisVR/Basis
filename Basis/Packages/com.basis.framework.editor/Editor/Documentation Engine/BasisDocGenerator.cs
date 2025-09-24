using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

public static class BasisDocGenerator
{
    // Where the DB asset should live (Unity project-relative path)
    private const string DbAssetPath = "Packages/com.basis.framework.editor/Editor/Documentation Engine/BasisDocDB.asset";

    // Package IDs we want to scan (directories will be resolved at runtime)
    private static readonly string[] PackageIdsToScan = new[]
    {
        "com.basis.framework",
        "com.basis.examples",
        "com.basis.sdk",
        "com.basis.framework.editor" // include your editor package too (optional)
    };

    [MenuItem("Basis/Docs/Rebuild Doc Database")]
    public static void Rebuild()
    {
        var projectRoot = GetProjectRoot(); // .../YourProject
        var packagesRoot = Path.Combine(projectRoot, "Packages");

        // Build absolute filesystem roots for each package that exists locally
        var roots = PackageIdsToScan
            .Select(id => Path.Combine(packagesRoot, id))
            .Where(Directory.Exists)
            .ToList();

        if (roots.Count == 0)
        {
            Debug.LogWarning("BasisDocGenerator: No package roots found. Make sure the packages are embedded/local under /Packages.");
            return;
        }

        // Collect .cs files
        var csPaths = roots
            .SelectMany(r => Directory.GetFiles(r, "*.cs", SearchOption.AllDirectories))
            // avoid generating from our own Generated/ folder if it exists anywhere
            .Where(p => !p.Replace('\\', '/').Contains("/Editor/Documentation Engine/Generated/"))
            .ToList();

        var entries = new List<DocEntry>();
        foreach (var path in csPaths)
        {
            try { ParseFile(path, entries); }
            catch (Exception ex)
            {
                Debug.LogWarning($"Doc parse skipped for {path}: {ex.Message}");
            }
        }

        // Ensure destination folder exists on disk (works for embedded packages)
        EnsureFolderExistsForAsset(DbAssetPath);

        // Write/overwrite the ScriptableObject
        var db = AssetDatabase.LoadAssetAtPath<BasisDocDB>(DbAssetPath);
        if (db == null)
        {
            db = ScriptableObject.CreateInstance<BasisDocDB>();
            AssetDatabase.CreateAsset(db, DbAssetPath);
        }

        db.Entries = entries;
        EditorUtility.SetDirty(db);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"DocDB rebuilt: {entries.Count} entries → {DbAssetPath}");
    }

    // ---------- parsing (unchanged except for removing rel/path assumptions) ----------

    private static void ParseFile(string path, List<DocEntry> sink)
    {
        var lines = File.ReadAllLines(path);
        var i = 0;

        string currentNs = null;
        var typeStack = new Stack<string>();
        List<string> pendingDocLines = null;

        while (i < lines.Length)
        {
            var raw = lines[i];
            var line = raw.Trim();

            if (line.StartsWith("namespace "))
                currentNs = line.Substring("namespace ".Length).Split('{')[0].Trim();

            if (StartsWithAny(line, "public ", "internal ", "protected internal ", "private ", "partial ", "sealed ", "abstract ")
                && (line.Contains(" class ") || line.Contains(" struct ") || line.Contains(" interface ")))
            {
                var typeName = ExtractIdentifierAfter(line, new[] { "class", "struct", "interface" });
                if (!string.IsNullOrEmpty(typeName))
                {
                    typeStack.Push(typeName);
                    if (pendingDocLines != null)
                    {
                        var entry = ParseXmlDocIntoEntry(pendingDocLines);
                        entry.Kind = "Type";
                        entry.MemberName = typeName;
                        entry.TypeFullName = BuildTypeFullName(currentNs, typeStack);
                        sink.Add(entry);
                        pendingDocLines = null;
                    }
                }
            }

            if (line.StartsWith("///"))
            {
                pendingDocLines ??= new List<string>();
                pendingDocLines.Add(line.Substring(3));
                i++;
                continue;
            }

            if (pendingDocLines != null && line.StartsWith("public "))
            {
                var entry = ParseXmlDocIntoEntry(pendingDocLines);
                pendingDocLines = null;

                var typeFullName = BuildTypeFullName(currentNs, typeStack);

                if (line.Contains("(") && line.Contains(")") && line.Contains("{") == false)
                {
                    entry.Kind = "Method";
                    entry.MemberName = ExtractMethodName(line);
                    entry.ParamNames = ExtractParamNames(line);
                    entry.ParamCount = entry.ParamNames.Count;
                    entry.TypeFullName = typeFullName;
                    sink.Add(entry);
                }
                else if (line.Contains("{") && (line.Contains(" get;") || line.Contains(" set;")))
                {
                    entry.Kind = "Property";
                    entry.MemberName = ExtractPropertyName(line);
                    entry.TypeFullName = typeFullName;
                    entry.ParamCount = 0;
                    sink.Add(entry);
                }
                else if (line.Contains(" event "))
                {
                    entry.Kind = "Event";
                    entry.MemberName = ExtractIdentifierAfter(line, new[] { "event" });
                    entry.TypeFullName = typeFullName;
                    sink.Add(entry);
                }
                else
                {
                    entry.Kind = "Field";
                    entry.MemberName = ExtractFieldName(line);
                    entry.TypeFullName = typeFullName;
                    sink.Add(entry);
                }
            }

            if (raw.StartsWith("}"))
            {
                if (typeStack.Count > 0) typeStack.Pop();
            }

            i++;
        }
    }

    private static bool StartsWithAny(string s, params string[] prefixes)
        => prefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal));

    private static string BuildTypeFullName(string ns, Stack<string> types)
    {
        var arr = types.Reverse().ToArray();
        var tn = string.Join("+", arr);
        return string.IsNullOrEmpty(ns) ? tn : ns + "." + tn;
    }

    private static DocEntry ParseXmlDocIntoEntry(List<string> docLines)
    {
        var xml = "<root>\n" + string.Join("\n", docLines) + "\n</root>";
        var e = new DocEntry();

        try
        {
            var x = XDocument.Parse(xml);

            e.Summary = x.Root.Element("summary")?.Value?.Trim();
            e.Remarks = x.Root.Element("remarks")?.Value?.Trim();
            e.Returns = x.Root.Element("returns")?.Value?.Trim();
            e.Example = x.Root.Element("example")?.Value?.Trim();

            var paramElems = x.Root.Elements("param").ToList();
            foreach (var pe in paramElems)
            {
                var nameAttr = pe.Attribute("name")?.Value ?? "";
                if (!string.IsNullOrEmpty(nameAttr))
                {
                    e.ParamNames.Add(nameAttr);
                    e.ParamDocs.Add(pe.Value?.Trim() ?? "");
                }
            }
        }
        catch
        {
            e.Summary = string.Join("\n", docLines);
        }
        return e;
    }

    private static string ExtractIdentifierAfter(string line, string[] keywords)
    {
        foreach (var k in keywords)
        {
            var idx = line.IndexOf($" {k} ", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var after = line.Substring(idx + k.Length + 2).Trim();
                var end = after.IndexOfAny(new[] { ' ', '<', ':', '{', '(' });
                return end >= 0 ? after.Substring(0, end) : after;
            }
        }
        return null;
    }

    private static string ExtractMethodName(string line)
    {
        var paren = line.IndexOf('(');
        if (paren < 0) return null;
        var before = line.Substring(0, paren).Trim();
        var parts = before.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : null;
    }

    private static string ExtractPropertyName(string line)
    {
        var brace = line.IndexOf('{');
        if (brace < 0) return null;
        var before = line.Substring(0, brace).Trim();
        var parts = before.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : null;
    }

    private static string ExtractFieldName(string line)
    {
        var semi = line.IndexOf(';');
        if (semi < 0) semi = line.Length;
        var before = line.Substring(0, semi).Trim();
        var parts = before.Split(new[] { ' ', '\t', '=' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : null;
    }

    private static List<string> ExtractParamNames(string line)
    {
        var list = new List<string>();
        var l = line.IndexOf('(');
        var r = line.LastIndexOf(')');
        if (l < 0 || r < 0 || r <= l + 1) return list;

        var inside = line.Substring(l + 1, r - l - 1);
        var parts = inside.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);
        foreach (var p in parts)
        {
            var tokens = p.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0)
            {
                var name = tokens[^1];
                var eq = name.IndexOf('=');
                if (eq >= 0) name = name.Substring(0, eq).Trim();
                if (name is "in" or "ref" or "out")
                {
                    if (tokens.Length >= 2) name = tokens[^2];
                }
                list.Add(name);
            }
        }
        return list;
    }

    // ---------- helpers ----------

    private static string GetProjectRoot()
    {
        // Application.dataPath = <project>/Assets
        var assets = Application.dataPath;
        return Path.GetFullPath(Path.Combine(assets, ".."));
    }

    private static void EnsureFolderExistsForAsset(string assetPath)
    {
        // Convert "Packages/..." or "Assets/..." to absolute path and mkdir -p
        var projectRoot = GetProjectRoot();
        var absolute = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        var dir = Path.GetDirectoryName(absolute);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }
}
