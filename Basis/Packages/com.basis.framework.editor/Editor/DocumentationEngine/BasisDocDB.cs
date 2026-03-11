using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

[Serializable]
public class DocEntry
{
    public string TypeFullName;   // e.g., "Basis.Scripts.BasisSdk.Players.BasisLocalPlayer"
    public string MemberName;     // e.g., "Teleport"
    public string Kind;           // "Type" | "Field" | "Property" | "Method" | "Event" | "Constructor" | "Indexer"
    public int ParamCount;        // for methods
    public List<string> ParamNames = new(); // aligns with ParamDocs
    public List<string> ParamDocs = new();  // aligns with ParamNames

    // Core doc
    public string Summary;
    public string Remarks;
    public string Returns;
    public string Value;          // <value> tag, often used for properties
    public List<string> Examples = new(); // allow multiple examples

    // Generics
    public List<string> TypeParamNames = new();
    public List<string> TypeParamDocs = new();

    // Exceptions
    public List<string> ExceptionCrefs = new();
    public List<string> ExceptionDocs = new();

    // Cross-links
    public List<string> SeeCrefs = new();
    public List<string> SeeAlsoCrefs = new();

    // Metadata / custom tags
    public string Since;          // version introduced
    public string ObsoleteMsg;    // message if obsolete
    public List<string> Platforms = new(); // e.g., "Editor", "Runtime", "Android"
}

public class BasisDocDB : ScriptableObject
{
    public List<DocEntry> Entries = new();

    private Dictionary<string, List<DocEntry>> _byType;
    private static readonly System.Text.RegularExpressions.Regex Arity = new(@"`\d+", RegexOptions.Compiled);

    private static string NormalizeTypeKey(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return fullName;
        // Strip generic arity (`1) and any assembly generic argument payloads, keep + for nested
        var s = Arity.Replace(fullName, string.Empty);
        return s;
    }

    public void BuildIndex()
    {
        // Always rebuild from Entries
        _byType = new Dictionary<string, List<DocEntry>>();
        foreach (var e in Entries)
        {
            var key = NormalizeTypeKey(e.TypeFullName);
            if (!_byType.TryGetValue(key, out var list))
            {
                list = new List<DocEntry>();
                _byType[key] = list;
            }
            list.Add(e);
        }
    }

    public DocEntry FindFor(string typeFullName, string memberName, string kind, int paramCount)
    {
        if (_byType == null) BuildIndex();

        // 1) Exact lookup with normalized key
        var key = NormalizeTypeKey(typeFullName);
        if (_byType.TryGetValue(key, out var list))
        {
            DocEntry best = null;
            foreach (var e in list)
            {
                if (!string.Equals(e.MemberName, memberName, StringComparison.Ordinal)) continue;
                if (!string.Equals(e.Kind, kind, StringComparison.Ordinal)) continue;
                if (e.ParamCount == paramCount) return e;
                best ??= e;
            }
            if (best != null) return best;
        }

        // 2) Fallback: very tolerant scan (handles rare mismatch cases)
        foreach (var e in Entries)
        {
            if (NormalizeTypeKey(e.TypeFullName) != key) continue;
            if (!string.Equals(e.MemberName, memberName, StringComparison.Ordinal)) continue;
            if (!string.Equals(e.Kind, kind, StringComparison.Ordinal)) continue;
            return e;
        }
        return null;
    }
}
