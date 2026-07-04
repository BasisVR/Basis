using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        "com.basis.bundlemanagement",
        "com.basis.common",
        "com.basis.developer.exceptions",
        "com.basis.developer.recorder",
        "com.basis.eventdriver",
        "com.basis.examples",
        "com.basis.framework",
        "com.basis.framework.editor",
        "com.basis.gizmos",
        "com.basis.integration.audiolink",
        "com.basis.integration.ytdlp",
        "com.basis.mediapipe",
        "com.basis.mediaplayer",
        "com.basis.openlipsync",
        "com.basis.openvr",
        "com.basis.openxr",
        "com.basis.pooltable",
        "com.basis.profilerintergration",
        "com.basis.provider.servers",
        "com.basis.sdk",
        "com.basis.server",
        "com.basis.settings",
        "com.basis.shim",
        "com.basis.textmeshpro",
        "com.basis.vehicles",
        "com.basis.visualtrackers",
    };

    [MenuItem("Basis/Tools/Rebuild Doc Database")]
    public static void Rebuild()
    {
        var projectRoot = GetProjectRoot();
        var packagesRoot = Path.Combine(projectRoot, "Packages");

        var roots = PackageIdsToScan
            .Select(id => Path.Combine(packagesRoot, id))
            .Where(Directory.Exists)
            .ToList();

        if (roots.Count == 0)
        {
            Debug.LogWarning("BasisDocGenerator: No package roots found. Make sure the packages are embedded/local under /Packages.");
            return;
        }

        var csPaths = roots
            .SelectMany(r => Directory.GetFiles(r, "*.cs", SearchOption.AllDirectories))
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

        EnsureFolderExistsForAsset(DbAssetPath);

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

    private static readonly string[] AccessPrefixes = new[]
    {
        "public ", "internal ", "protected internal ", "private protected ",
        "private ", "protected ",
        // also allow modifier-first lines (e.g. "static class Foo", rare without access)
        "static ", "abstract ", "sealed ", "partial ", "readonly ", "unsafe ",
    };

    private static readonly string[] TypeKeywords = { "class", "struct", "interface", "enum", "delegate", "record" };

    private static void ParseFile(string path, List<DocEntry> sink)
    {
        var lines = File.ReadAllLines(path);
        var i = 0;

        string currentNs = null;
        var typeStack = new Stack<string>();
        List<string> pendingDocLines = null;
        var attributeBuffer = new List<string>();

        while (i < lines.Length)
        {
            var raw = lines[i];
            var line = raw.Trim();

            // namespace
            if (line.StartsWith("namespace "))
            {
                var rest = line.Substring("namespace ".Length).Trim();
                if (rest.EndsWith(";"))
                    currentNs = rest.TrimEnd(';').Trim();
                else
                    currentNs = rest.Split('{')[0].Trim();
            }

            // attributes like [Obsolete("msg")]
            if (line.StartsWith("["))
            {
                attributeBuffer.Add(line);
                i++;
                continue;
            }

            // XML doc lines
            if (line.StartsWith("///"))
            {
                pendingDocLines ??= new List<string>();
                pendingDocLines.Add(line.Substring(3));
                i++;
                continue;
            }

            // Build a "logical line" for declarations that wrap (e.g. long base-type lists,
            // wrapped method parameters). Stop at the first { ; or =>, or when the next line
            // is blank/attr/doc/closing brace.
            var logical = line;
            var consumed = 1;
            if (LooksLikeDeclarationStart(line) && !DeclarationLineComplete(line))
            {
                var sb = new StringBuilder(line);
                for (int k = i + 1; k < lines.Length && consumed < 8; k++)
                {
                    var nxt = lines[k].Trim();
                    if (nxt.Length == 0) break;
                    if (nxt.StartsWith("///") || nxt.StartsWith("//") || nxt.StartsWith("[")) break;
                    if (nxt.StartsWith("}") || nxt.StartsWith("#")) break;
                    sb.Append(' ').Append(nxt);
                    consumed++;
                    if (DeclarationLineComplete(sb.ToString())) break;
                }
                logical = sb.ToString();
            }

            // type declarations (class/struct/interface/enum/delegate/record)
            if (LooksLikeTypeDecl(logical))
            {
                string kindForType = "Type";
                if (ContainsWholeWord(logical, "enum")) kindForType = "Enum";
                else if (ContainsWholeWord(logical, "delegate")) kindForType = "Delegate";

                var typeName = ExtractIdentifierAfter(logical, TypeKeywords);
                if (!string.IsNullOrEmpty(typeName))
                {
                    typeStack.Push(typeName);
                    if (pendingDocLines != null)
                    {
                        var entry = ParseXmlDocIntoEntry(pendingDocLines);
                        entry.Kind = kindForType;
                        entry.MemberName = typeName;
                        // Type entries share their bucket with the type's members:
                        // TypeFullName == the type's own full name (e.g. "Foo.Bar" for
                        // top-level, "Foo.Bar+Inner" for nested). DB.FindType picks
                        // the entry whose Kind is one of Type/Enum/Delegate.
                        entry.TypeFullName = BuildTypeFullName(currentNs, typeStack);

                        entry.IsAbstract = HasModifier(logical, "abstract");
                        entry.IsSealed = HasModifier(logical, "sealed");
                        entry.IsStatic = HasModifier(logical, "static");
                        entry.IsPartial = HasModifier(logical, "partial");

                        ExtractBaseAndInterfaces(logical, out var baseName, out var ifaces);
                        entry.BaseTypeName = baseName;
                        entry.Interfaces = ifaces;

                        entry.ObsoleteMsg ??= ExtractObsoleteMsg(attributeBuffer);
                        attributeBuffer.Clear();

                        sink.Add(entry);
                        pendingDocLines = null;
                    }
                }
            }
            // member declarations that follow a doc block
            else if (pendingDocLines != null && IsMemberStart(logical))
            {
                var entry = ParseXmlDocIntoEntry(pendingDocLines);
                pendingDocLines = null;

                var typeFullName = BuildTypeFullName(currentNs, typeStack);

                bool hasParen = logical.Contains("(") && logical.Contains(")");
                bool hasBrace = logical.Contains("{");
                bool hasArrow = logical.Contains("=>");
                bool hasEvent = ContainsWholeWord(logical, "event");
                bool looksLikeIndexer =
                    logical.Contains(" this[", StringComparison.Ordinal) ||
                    logical.StartsWith("public this[", StringComparison.Ordinal) ||
                    logical.Contains(" this (") || logical.Contains(" this(");
                var currentTypeName = typeStack.Count > 0 ? typeStack.Peek() : null;

                entry.IsMemberStatic = HasModifier(logical, "static");
                entry.IsMemberReadOnly = HasModifier(logical, "readonly");
                entry.IsMemberConst = HasModifier(logical, "const");
                entry.IsMemberVirtual = HasModifier(logical, "virtual");
                entry.IsMemberOverride = HasModifier(logical, "override");

                if (hasEvent)
                {
                    entry.Kind = "Event";
                    entry.MemberName = ExtractIdentifierAfter(logical, new[] { "event" });
                    entry.TypeFullName = typeFullName;
                }
                else if (hasParen)
                {
                    entry.Kind = "Method";
                    entry.MemberName = ExtractMethodName(logical);
                    if (!string.IsNullOrEmpty(currentTypeName) &&
                        string.Equals(entry.MemberName, currentTypeName, StringComparison.Ordinal))
                        entry.Kind = "Constructor";

                    entry.ParamNames = ExtractParamNames(logical);
                    entry.ParamCount = entry.ParamNames.Count;
                    entry.TypeFullName = typeFullName;
                }
                else if ((hasBrace && (logical.Contains(" get;") || logical.Contains(" set;") || logical.Contains(" init;")))
                         || (!hasBrace && hasArrow))
                {
                    entry.Kind = looksLikeIndexer ? "Indexer" : "Property";
                    entry.MemberName = looksLikeIndexer ? "this" : ExtractPropertyName(logical);
                    entry.TypeFullName = typeFullName;
                    entry.ParamCount = looksLikeIndexer ? ExtractIndexerParamCount(logical) : 0;
                }
                else
                {
                    entry.Kind = "Field";
                    entry.MemberName = ExtractFieldName(logical);
                    entry.TypeFullName = typeFullName;
                }

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

            i += consumed;
        }
    }

    // ---------- declaration shape helpers ----------

    private static bool LooksLikeDeclarationStart(string line) =>
        AccessPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal));

    private static bool DeclarationLineComplete(string s)
    {
        // Complete if it has ';', '{', or '=>' at outer nesting depth
        if (IndexOfAnyOutside(s, new[] { ';', '{' }) >= 0) return true;
        // '=>' isn't a single char; check via IndexOf at depth 0 — approximate: if '=>'
        // appears anywhere it usually is at depth 0 for expression bodies.
        if (s.Contains("=>", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool LooksLikeTypeDecl(string line) =>
        LooksLikeDeclarationStart(line) &&
        (ContainsWholeWord(line, "class") || ContainsWholeWord(line, "struct") ||
         ContainsWholeWord(line, "interface") || ContainsWholeWord(line, "enum") ||
         ContainsWholeWord(line, "delegate") || ContainsWholeWord(line, "record"));

    private static bool IsMemberStart(string line) =>
        line.StartsWith("public ", StringComparison.Ordinal) ||
        line.StartsWith("protected ", StringComparison.Ordinal) ||
        line.StartsWith("internal ", StringComparison.Ordinal);

    private static bool HasModifier(string line, string modifier)
    {
        // Only count as modifier if it appears before the first paren / first '{' / first '='
        int firstSep = -1;
        foreach (var c in new[] { '(', '{', '=' })
        {
            int idx = line.IndexOf(c);
            if (idx >= 0 && (firstSep < 0 || idx < firstSep)) firstSep = idx;
        }
        var head = firstSep > 0 ? line.Substring(0, firstSep) : line;
        return ContainsWholeWord(head, modifier);
    }

    private static void ExtractBaseAndInterfaces(string line, out string baseName, out List<string> interfaces)
    {
        baseName = null;
        interfaces = new List<string>();

        // Cut off 'where' constraints and body openers.
        int whereIdx = line.IndexOf(" where ", StringComparison.Ordinal);
        if (whereIdx > 0) line = line.Substring(0, whereIdx);
        int brace = line.IndexOf('{');
        if (brace > 0) line = line.Substring(0, brace);

        // Find the colon that separates type-name(generics) from base-list. Skip colons inside <>.
        int colon = -1;
        int angle = 0;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '<') angle++;
            else if (c == '>') { if (angle > 0) angle--; }
            else if (c == ':' && angle == 0) { colon = i; break; }
        }
        if (colon < 0) return;

        var after = line.Substring(colon + 1).Trim();
        var parts = SplitTopLevelCommas(after);
        if (parts.Count == 0) return;

        baseName = parts[0].Trim();
        for (int i = 1; i < parts.Count; i++) interfaces.Add(parts[i].Trim());
    }

    private static List<string> SplitTopLevelCommas(string s)
    {
        var result = new List<string>();
        int angle = 0, paren = 0;
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c == '<') angle++;
            else if (c == '>') { if (angle > 0) angle--; }
            else if (c == '(') paren++;
            else if (c == ')') { if (paren > 0) paren--; }
            else if (c == ',' && angle == 0 && paren == 0)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

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

            foreach (var ex in x.Root.Elements("example"))
                e.Examples.Add(NormalizeInline(ex));

            foreach (var pe in x.Root.Elements("param"))
            {
                var name = pe.Attribute("name")?.Value ?? "";
                if (name.Length == 0) continue;
                e.ParamNames.Add(name);
                e.ParamDocs.Add(NormalizeInline(pe));
            }

            foreach (var te in x.Root.Elements("typeparam"))
            {
                var name = te.Attribute("name")?.Value ?? "";
                if (name.Length == 0) continue;
                e.TypeParamNames.Add(name);
                e.TypeParamDocs.Add(NormalizeInline(te));
            }

            foreach (var ex in x.Root.Elements("exception"))
            {
                var cref = ex.Attribute("cref")?.Value ?? "";
                e.ExceptionCrefs.Add(cref);
                e.ExceptionDocs.Add(NormalizeInline(ex));
            }

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
            e.Summary = string.Join("\n", docLines);
        }
        return e;
    }

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
            XElement e => string.Concat(e.Nodes().Select(Recurse)),
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
        // Strip generic argument list <T,U> from the tail
        if (before.EndsWith(">"))
        {
            int depth = 1;
            int j = before.Length - 2;
            while (j >= 0 && depth > 0)
            {
                if (before[j] == '>') depth++;
                else if (before[j] == '<') depth--;
                j--;
            }
            if (depth == 0) before = before.Substring(0, j + 1).Trim();
        }
        var parts = before.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : null;
    }

    private static string ExtractPropertyName(string line)
    {
        int brace = line.IndexOf('{');
        int arrow = line.IndexOf("=>", StringComparison.Ordinal);
        int end = line.Length;
        if (brace >= 0) end = Math.Min(end, brace);
        if (arrow >= 0) end = Math.Min(end, arrow);

        var before = line.Substring(0, end).Trim();

        if (before.Contains(" this[", StringComparison.Ordinal) ||
            before.EndsWith(" this", StringComparison.Ordinal))
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

    // Robust field-name extraction with awareness of strings, comments, generics, arrays,
    // tuple types, and multi-declarators (returns first declarator's name).
    private static string ExtractFieldName(string originalLine)
    {
        if (string.IsNullOrEmpty(originalLine)) return null;

        var line = StripLineComments(originalLine);
        int end = IndexOfAnyOutside(line, new[] { ';', '{' });
        if (end < 0) end = line.Length;
        var span = line.AsSpan(0, end).Trim();
        if (span.Length == 0) return null;

        int depthParen = 0, depthBracket = 0, depthAngle = 0, depthBrace = 0;
        bool inString = false;
        char stringQuote = '\0';
        bool inChar = false;
        bool escaped = false;

        string lastIdentifier = null;

        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];

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

            if (c == '"') { inString = true; stringQuote = '"'; continue; }
            if (c == '\'') { inChar = true; continue; }

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

            if (depthParen == 0 && depthBracket == 0 && depthAngle == 0 && depthBrace == 0)
            {
                if (c == '=' || c == ',')
                    return lastIdentifier;
            }

            if (IsIdentStart(c) || (c == '@' && i + 1 < span.Length && IsIdentStart(span[i + 1])))
            {
                int start = i;
                i++;
                while (i < span.Length && IsIdentPart(span[i])) i++;
                lastIdentifier = span.Slice(start, i - start).ToString();
                i--;
                continue;
            }

            if (c == ';' && depthParen == 0 && depthBracket == 0 && depthAngle == 0 && depthBrace == 0)
                return lastIdentifier;
        }

        return lastIdentifier;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '@';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';

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

            if (c == '/' && n == '/')
                return s.Substring(0, i).TrimEnd();

            if (c == '/' && n == '*')
            {
                int blockEnd = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (blockEnd < 0) return s.Substring(0, i).TrimEnd();
                s = s.Remove(i, blockEnd + 2 - i);
                i = Math.Max(-1, i - 1);
            }
        }
        return s.TrimEnd();
    }

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
        var parts = SplitTopLevelCommas(inside).Select(s => s.Trim()).Where(s => s.Length > 0);
        foreach (var p in parts)
        {
            var tokens = p.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0)
            {
                var name = tokens[^1];
                var eq = name.IndexOf('=');
                if (eq >= 0) name = name.Substring(0, eq).Trim();

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
        var assets = Application.dataPath;
        return Path.GetFullPath(Path.Combine(assets, ".."));
    }

    private static void EnsureFolderExistsForAsset(string assetPath)
    {
        var projectRoot = GetProjectRoot();
        var absolute = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        var dir = Path.GetDirectoryName(absolute);
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static string ExtractObsoleteMsg(List<string> attributeLines)
    {
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
                return "";
            }
        }
        return null;
    }
}
