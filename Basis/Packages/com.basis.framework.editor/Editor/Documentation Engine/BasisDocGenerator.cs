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
        "com.basis.settings",
        "com.basis.bundlemanagement",
        "com.basis.common",
        "com.basis.console",
        "com.basis.eventdriver",
        "com.basis.gizmos",
        "com.basis.openvr",
        "com.basis.openxr",
        "com.basis.profilerintergration",
        "com.basis.server",
        "com.basis.settingsmanager",
        "com.basis.visualtrackers",
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
        db.BuildIndex();
        EditorUtility.SetDirty(db);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"DocDB rebuilt: {entries.Count} entries → {DbAssetPath}");
    }

    // ---------- parsing ----------

    private static void ParseFile(string path, List<DocEntry> sink)
    {
        var lines = File.ReadAllLines(path);
        var i = 0;

        string currentNs = null;
        var typeStack = new Stack<string>();
        List<string> pendingDocLines = null;
        // Track attribute lines immediately preceding a declaration
        var attributeBuffer = new List<string>();

        // Local helper: property name that works for both brace and arrow bodies
        static string ExtractPropertyNameLoose(string ln)
        {
            // cut off before '{' or '=>', whichever comes first
            int brace = ln.IndexOf('{');
            int arrow = ln.IndexOf("=>", StringComparison.Ordinal);
            int end = ln.Length;
            if (brace >= 0) end = Math.Min(end, brace);
            if (arrow >= 0) end = Math.Min(end, arrow);

            var before = ln.Substring(0, end).Trim();

            // indexer?
            if (before.Contains(" this[", StringComparison.Ordinal) ||
                before.EndsWith(" this", StringComparison.Ordinal))
                return "this";

            var parts = before.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[^1] : null;
        }

        while (i < lines.Length)
        {
            var raw = lines[i];
            var line = raw.Trim();

            // namespace
            if (line.StartsWith("namespace "))
            {
                var rest = line.Substring("namespace ".Length).Trim();

                // file-scoped: `namespace Foo.Bar;`
                if (rest.EndsWith(";"))
                    currentNs = rest.TrimEnd(';').Trim();
                else
                    // block-scoped: `namespace Foo.Bar {`
                    currentNs = rest.Split('{')[0].Trim();
            }

            // accumulate attributes like [Obsolete("msg")] sitting on the lines above declarations
            if (line.StartsWith("["))
            {
                attributeBuffer.Add(line);
                i++;
                continue;
            }

            // type declarations (class/struct/interface/enum/delegate)
            if (StartsWithAny(line, "public ", "internal ", "protected internal ", "private ", "partial ", "sealed ", "abstract ")
                && (ContainsWholeWord(line, "class") || ContainsWholeWord(line, "struct") || ContainsWholeWord(line, "interface")
                    || ContainsWholeWord(line, "enum") || ContainsWholeWord(line, "delegate")))
            {
                string kindForType = "Type";
                if (ContainsWholeWord(line, "enum")) kindForType = "Enum";
                else if (ContainsWholeWord(line, "delegate")) kindForType = "Delegate";

                var typeName = ExtractIdentifierAfter(line, new[] { "class", "struct", "interface", "enum", "delegate" });
                if (!string.IsNullOrEmpty(typeName))
                {
                    typeStack.Push(typeName);
                    if (pendingDocLines != null)
                    {
                        var entry = ParseXmlDocIntoEntry(pendingDocLines);
                        entry.Kind = kindForType;
                        entry.MemberName = typeName;
                        entry.TypeFullName = BuildTypeFullName(currentNs, typeStack);

                        // attributes on type (Obsolete)
                        entry.ObsoleteMsg ??= ExtractObsoleteMsg(attributeBuffer);
                        attributeBuffer.Clear();

                        sink.Add(entry);
                        pendingDocLines = null;
                    }
                }
            }

            // accumulate XML doc lines
            if (line.StartsWith("///"))
            {
                pendingDocLines ??= new List<string>();
                // Strip leading "///" but preserve rest
                pendingDocLines.Add(line.Substring(3));
                i++;
                continue;
            }

            // member declarations that follow a doc block
            if (pendingDocLines != null && (line.StartsWith("public ") || line.StartsWith("protected ") || line.StartsWith("internal ")))
            {
                var entry = ParseXmlDocIntoEntry(pendingDocLines);
                pendingDocLines = null;

                var typeFullName = BuildTypeFullName(currentNs, typeStack);

                // detect member kind by shape
                bool hasParen = line.Contains("(") && line.Contains(")");
                bool hasBrace = line.Contains("{");
                bool hasArrow = line.Contains("=>");
                bool hasEvent = ContainsWholeWord(line, "event");
                bool looksLikeIndexer =
                    line.Contains(" this[") || line.StartsWith("public this[", StringComparison.Ordinal) ||
                    line.Contains(" this (") || line.Contains(" this("); // tolerant
                var currentTypeName = typeStack.Count > 0 ? typeStack.Peek() : null;

                if (hasEvent)
                {
                    entry.Kind = "Event";
                    entry.MemberName = ExtractIdentifierAfter(line, new[] { "event" });
                    entry.TypeFullName = typeFullName;
                }
                else if (hasParen) // method-like (with or without body; arrow-bodies included)
                {
                    entry.Kind = "Method";
                    entry.MemberName = ExtractMethodName(line);
                    if (!string.IsNullOrEmpty(currentTypeName) &&
                        string.Equals(entry.MemberName, currentTypeName, StringComparison.Ordinal))
                        entry.Kind = "Constructor";

                    entry.ParamNames = ExtractParamNames(line);
                    entry.ParamCount = entry.ParamNames.Count;
                    entry.TypeFullName = typeFullName;
                }
                else if ((hasBrace && (line.Contains(" get;") || line.Contains(" set;") || line.Contains(" init;")))
                       || (!hasBrace && hasArrow)) // properties incl. expression-bodied
                {
                    entry.Kind = looksLikeIndexer ? "Indexer" : "Property";
                    entry.MemberName = looksLikeIndexer ? "this" : ExtractPropertyNameLoose(line);
                    entry.TypeFullName = typeFullName;
                    entry.ParamCount = looksLikeIndexer ? ExtractIndexerParamCount(line) : 0;
                }
                else
                {
                    // Field (robust extraction)
                    entry.Kind = "Field";
                    entry.MemberName = ExtractFieldName(line);
                    entry.TypeFullName = typeFullName;
                }

                // attributes on member (Obsolete)
                entry.ObsoleteMsg ??= ExtractObsoleteMsg(attributeBuffer);
                attributeBuffer.Clear();

                sink.Add(entry);
            }

            // close type scope (be tolerant of indentation)
            var trimmedLeading = raw.TrimStart();
            if (trimmedLeading.StartsWith("}"))
            {
                if (typeStack.Count > 0) typeStack.Pop();
            }

            // reset attribute buffer if we hit a blank/non-attr/non-doc line before a declaration
            if (attributeBuffer.Count > 0 && !line.StartsWith("[") && !line.StartsWith("///"))
                attributeBuffer.Clear();

            i++;
        }
    }


    private static bool StartsWithAny(string s, params string[] prefixes)
        => prefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal));

    private static bool ContainsWholeWord(string s, string word)
    {
        var idx = s.IndexOf(word, StringComparison.Ordinal);
        if (idx < 0) return false;
        bool leftOk = idx == 0 || !char.IsLetterOrDigit(s[idx - 1]);
        int end = idx + word.Length;
        bool rightOk = end >= s.Length || !char.IsLetterOrDigit(s[end]);
        return leftOk && rightOk;
    }

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
            string T(XElement el) => el == null ? null : NormalizeInline(el);

            e.Summary = T(x.Root.Element("summary"));
            e.Remarks = T(x.Root.Element("remarks"));
            e.Returns = T(x.Root.Element("returns"));
            e.Value = T(x.Root.Element("value"));

            // <example> can appear multiple times
            foreach (var ex in x.Root.Elements("example"))
                e.Examples.Add(NormalizeInline(ex));

            // <param>
            foreach (var pe in x.Root.Elements("param"))
            {
                var name = pe.Attribute("name")?.Value ?? "";
                if (name.Length == 0) continue;
                e.ParamNames.Add(name);
                e.ParamDocs.Add(NormalizeInline(pe));
            }

            // <typeparam>
            foreach (var te in x.Root.Elements("typeparam"))
            {
                var name = te.Attribute("name")?.Value ?? "";
                if (name.Length == 0) continue;
                e.TypeParamNames.Add(name);
                e.TypeParamDocs.Add(NormalizeInline(te));
            }

            // <exception cref="...">
            foreach (var ex in x.Root.Elements("exception"))
            {
                var cref = ex.Attribute("cref")?.Value ?? "";
                e.ExceptionCrefs.Add(cref);
                e.ExceptionDocs.Add(NormalizeInline(ex));
            }

            // <see>, <seealso> (store cref only; text is already normalized)
            foreach (var se in x.Root.Elements("see"))
            {
                var cref = se.Attribute("cref")?.Value ?? "";
                if (!string.IsNullOrEmpty(cref)) e.SeeCrefs.Add(cref);
            }
            foreach (var sa in x.Root.Elements("seealso"))
            {
                var cref = sa.Attribute("cref")?.Value ?? "";
                if (!string.IsNullOrEmpty(cref)) e.SeeAlsoCrefs.Add(cref);
            }

            // custom tags: <since>, <obsolete>, <platform>
            e.Since = x.Root.Element("since")?.Value?.Trim();
            e.ObsoleteMsg = x.Root.Element("obsolete")?.Value?.Trim();
            foreach (var p in x.Root.Elements("platform"))
            {
                var v = p.Value?.Trim();
                if (!string.IsNullOrEmpty(v)) e.Platforms.Add(v);
            }
        }
        catch
        {
            // If malformed XML (common while drafting), keep raw text in Summary
            e.Summary = string.Join("\n", docLines);
        }
        return e;
    }

    // Convert inline XML to a viewer-friendly string (markdown-ish)
    private static string NormalizeInline(XElement el)
    {
        string Recurse(XNode n) => n switch
        {
            XText t => t.Value,
            XElement e when e.Name.LocalName == "para" => "\n\n" + string.Concat(e.Nodes().Select(Recurse)) + "\n\n",
            XElement e when e.Name.LocalName == "c" => "`" + string.Concat(e.Nodes().Select(Recurse)) + "`",
            XElement e when e.Name.LocalName == "code" => "```\n" + string.Concat(e.Nodes().Select(Recurse)) + "\n```",
            XElement e when e.Name.LocalName == "paramref" => "`" + (e.Attribute("name")?.Value ?? "") + "`",
            XElement e when e.Name.LocalName == "typeparamref" => "`" + (e.Attribute("name")?.Value ?? "") + "`",
            XElement e when e.Name.LocalName == "see" => e.Attribute("cref")?.Value ?? string.Concat(e.Nodes().Select(Recurse)),
            XElement e when e.Name.LocalName == "list" => RenderList(e),
            XElement e => string.Concat(e.Nodes().Select(Recurse)), // default: flatten unknown tags
            _ => ""
        };
        return string.Concat(el.Nodes().Select(Recurse)).Trim();
    }

    private static string RenderList(XElement list)
    {
        var type = (list.Attribute("type")?.Value ?? "bullet").ToLowerInvariant();
        if (type is "bullet" or "number")
        {
            var i = 1;
            var lines = new List<string>();
            foreach (var item in list.Elements("item"))
            {
                var hasTerm = item.Element("term") != null;
                var text = hasTerm
                    ? $"{NormalizeInline(item.Element("term"))}: {NormalizeInline(item.Element("description") ?? item)}"
                    : NormalizeInline(item);
                lines.Add(type == "bullet" ? $"- {text}" : $"{i++}. {text}");
            }
            return "\n" + string.Join("\n", lines) + "\n";
        }
        if (type == "table")
        {
            // simple 2-col: term | description
            var rows = list.Elements("item").Select(it =>
                $"{NormalizeInline(it.Element("term") ?? new XElement("x"))} | {NormalizeInline(it.Element("description") ?? it)}");
            return "\n" + string.Join("\n", rows) + "\n";
        }
        return "";
    }

    private static string ExtractIdentifierAfter(string line, string[] keywords)
    {
        foreach (var k in keywords)
        {
            var idx = line.IndexOf($" {k} ", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var after = line.Substring(idx + k.Length + 2).Trim();
                var end = after.IndexOfAny(new[] { ' ', '<', ':', '{', '(', '=' });
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
        // Compute the cut-off before body: '{' or '=>', whichever comes first
        int brace = line.IndexOf('{');
        int arrow = line.IndexOf("=>", StringComparison.Ordinal);
        int end = line.Length;
        if (brace >= 0) end = Math.Min(end, brace);
        if (arrow >= 0) end = Math.Min(end, arrow);

        var before = line.Substring(0, end).Trim();

        // Indexer
        if (before.Contains(" this[") || before.EndsWith(" this", StringComparison.Ordinal))
            return "this";

        var parts = before.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : null;
    }

    private static int ExtractIndexerParamCount(string line)
    {
        var l = line.IndexOf('[');
        var r = line.IndexOf(']');
        if (l < 0 || r < 0 || r <= l + 1) return 0;
        var inside = line.Substring(l + 1, r - l - 1);
        return inside.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Count();
    }

    // >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>
    // ROBUST FIELD NAME EXTRACTION
    // Handles:
    //   public string CurrentMode = BasisConstants.None;
    //   public readonly int a, b = 3, c;
    //   private unsafe int* p;
    //   protected (int x, int y) tuple;
    //   internal Foo<Bar<Baz[]>>[] items;
    //   public int[] arr = new int[3] { 1, 2, 3 }; // with comments
    //   public int @class;
    //   volatile bool _flag;
    // We return only the *first* declarator name on the line, which aligns with XML doc lines
    // applying to the first symbol in multi-declarators.
    private static string ExtractFieldName(string originalLine)
    {
        if (string.IsNullOrEmpty(originalLine)) return null;

        // Trim trailing line comments, but respect quotes.
        var line = StripLineComments(originalLine);

        // Consider only up to semicolon (declaration end) or brace (in case someone wrote a field-like thing that actually isn't).
        int end = IndexOfAnyOutside(line, new[] { ';', '{' });
        if (end < 0) end = line.Length;
        var span = line.AsSpan(0, end).Trim();

        if (span.Length == 0) return null;

        // We want the last identifier token *before* we hit '=', ',', or ';' at nesting depth 0.
        // We'll scan once, tracking nesting for (), [], <>, {} so commas/equal signs inside initializers/generics don't confuse us.
        int depthParen = 0, depthBracket = 0, depthAngle = 0, depthBrace = 0;
        bool inString = false;
        char stringQuote = '\0';
        bool inChar = false;
        bool escaped = false;

        // Best effort "last identifier" before hitting separator at depth 0.
        string lastIdentifier = null;

        // Additionally, in multi-declarators, we want the first declarator (after the type).
        // We'll stop at the first ',' or '=' encountered at depth 0.
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];

            // string/char literal handling to avoid eating '//' inside quotes
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == stringQuote) { inString = false; stringQuote = '\0'; }
                continue;
            }
            if (inChar)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '\'') { inChar = false; }
                continue;
            }

            // Enter string/char
            if (c == '"') { inString = true; stringQuote = '"'; continue; }
            if (c == '\'') { inChar = true; continue; }

            // Track balanced delimiters
            switch (c)
            {
                case '(': depthParen++; continue;
                case ')': if (depthParen > 0) depthParen--; continue;
                case '[': depthBracket++; continue;
                case ']': if (depthBracket > 0) depthBracket--; continue;
                case '{': depthBrace++; continue;
                case '}': if (depthBrace > 0) depthBrace--; continue;
                case '<': depthAngle++; continue;
                case '>': if (depthAngle > 0) depthAngle--; continue;
            }

            // At depth 0, hitting '=' or ',' means we've passed the name for the current declarator.
            if (depthParen == 0 && depthBracket == 0 && depthAngle == 0 && depthBrace == 0)
            {
                if (c == '=' || c == ',')
                {
                    // lastIdentifier is the name we want for the first declarator
                    return lastIdentifier;
                }
            }

            // Capture identifiers (support @identifiers)
            if (IsIdentStart(c) || (c == '@' && i + 1 < span.Length && IsIdentStart(span[i + 1])))
            {
                int start = i;
                i++; // move past first char
                while (i < span.Length && IsIdentPart(span[i])) i++;

                // The token we just read
                var token = span.Slice(start, i - start).ToString();

                // We collect *every* identifier; the field name will be the last identifier before '=', ',', or ';' at depth 0.
                lastIdentifier = token;

                // Move one step back because the for-loop will i++ again
                i--;
                continue;
            }

            // Reaching ';' at depth 0 terminates the declaration; return what we have (the last identifier).
            if (c == ';' && depthParen == 0 && depthBracket == 0 && depthAngle == 0 && depthBrace == 0)
                return lastIdentifier;
        }

        // If we never hit a terminator, return the last identifier we saw.
        return lastIdentifier;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '@';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';

    // Cuts off // comments while respecting string/char literals
    private static string StripLineComments(string s)
    {
        bool inString = false, inChar = false, escaped = false;
        char quote = '\0';
        for (int i = 0; i < s.Length - 1; i++)
        {
            char c = s[i];
            char n = s[i + 1];

            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == quote) { inString = false; quote = '\0'; }
                continue;
            }
            if (inChar)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '\'') { inChar = false; }
                continue;
            }

            if (c == '"') { inString = true; quote = '"'; continue; }
            if (c == '\'') { inChar = true; continue; }

            // // comment start
            if (c == '/' && n == '/')
            {
                return s.Substring(0, i).TrimEnd();
            }

            // skip /* ... */ blocks if someone had weird formatting on a single line
            if (c == '/' && n == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return s.Substring(0, i).TrimEnd(); // treat as cut
                // remove the block and continue scanning
                s = s.Remove(i, end + 2 - i);
                // step back one char so the loop re-checks at i
                i = Math.Max(-1, i - 1);
            }
        }
        return s.TrimEnd();
    }

    // Finds the first index of any char in 'chars' that is not nested inside (),[],<>,{}
    private static int IndexOfAnyOutside(string s, char[] chars)
    {
        int depthParen = 0, depthBracket = 0, depthAngle = 0, depthBrace = 0;
        bool inString = false, inChar = false, escaped = false;
        char quote = '\0';

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];

            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == quote) { inString = false; quote = '\0'; }
                continue;
            }
            if (inChar)
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == '\'') { inChar = false; }
                continue;
            }

            if (c == '"') { inString = true; quote = '"'; continue; }
            if (c == '\'') { inChar = true; continue; }

            switch (c)
            {
                case '(': depthParen++; break;
                case ')': if (depthParen > 0) depthParen--; break;
                case '[': depthBracket++; break;
                case ']': if (depthBracket > 0) depthBracket--; break;
                case '{': depthBrace++; break;
                case '}': if (depthBrace > 0) depthBrace--; break;
                case '<': depthAngle++; break;
                case '>': if (depthAngle > 0) depthAngle--; break;
            }

            if (depthParen == 0 && depthBracket == 0 && depthAngle == 0 && depthBrace == 0)
            {
                for (int j = 0; j < chars.Length; j++)
                {
                    if (c == chars[j]) return i;
                }
            }
        }

        return -1;
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

                // handle ref/out/in modifiers
                if (name is "in" or "ref" or "out" || name == "params")
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

    private static string ExtractObsoleteMsg(List<string> attributeLines)
    {
        // crude but effective parser for [Obsolete("message")] or [System.Obsolete("message")]
        // Returns null if not found.
        for (int i = attributeLines.Count - 1; i >= 0; --i)
        {
            var s = attributeLines[i].Trim();
            if (!s.StartsWith("[")) continue;
            if (s.Contains("Obsolete", StringComparison.Ordinal))
            {
                var firstQuote = s.IndexOf('"');
                if (firstQuote >= 0)
                {
                    var secondQuote = s.IndexOf('"', firstQuote + 1);
                    if (secondQuote > firstQuote)
                        return s.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                }
                return ""; // obsolete w/o message
            }
        }
        return null;
    }
}
