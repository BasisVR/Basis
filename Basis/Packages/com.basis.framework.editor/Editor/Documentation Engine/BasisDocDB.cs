using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

[Serializable]
public class DocEntry
{
    public string TypeFullName;   // e.g., "Basis.Scripts.BasisSdk.Players.BasisLocalPlayer"
    public string MemberName;     // e.g., "Teleport"
    public string Kind;           // "Type" | "Enum" | "Delegate" | "Field" | "Property" | "Method" | "Event" | "Constructor" | "Indexer"
    public int ParamCount;        // for methods
    public List<string> ParamNames = new(); // aligns with ParamDocs
    public List<string> ParamDocs = new();  // aligns with ParamNames

    // Type-level metadata (only meaningful when Kind == "Type"/"Enum"/"Delegate")
    public string BaseTypeName;             // raw text after ':' (first item) — null if none
    public List<string> Interfaces = new(); // remaining items after ':'
    public bool IsAbstract;
    public bool IsSealed;
    public bool IsStatic;
    public bool IsPartial;

    // Member-level modifiers
    public bool IsMemberStatic;
    public bool IsMemberReadOnly;
    public bool IsMemberConst;
    public bool IsMemberVirtual;
    public bool IsMemberOverride;

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
    private Dictionary<string, DocEntry> _typeEntryByKey;
    private static readonly Regex Arity = new(@"`\d+", RegexOptions.Compiled);
    // [[GenArg, asm]] payload trailing a generic type's reflection FullName
    private static readonly Regex GenericPayload = new(@"\[\[.*?\]\]", RegexOptions.Compiled);
    private static readonly string[] TypeKinds = { "Type", "Enum", "Delegate" };

    private static string NormalizeTypeKey(string fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return fullName;
        // Reflection's FullName for generics looks like "Ns.Foo`1[[System.Int32, mscorlib]]".
        // Strip both the [[…]] payload and the `N arity so it matches what the parser
        // produces ("Ns.Foo"). Keep '+' so nested types remain distinguishable.
        var s = GenericPayload.Replace(fullName, string.Empty);
        s = Arity.Replace(s, string.Empty);
        return s;
    }

    public void BuildIndex()
    {
        _byType = new Dictionary<string, List<DocEntry>>();
        _typeEntryByKey = new Dictionary<string, DocEntry>();

        foreach (var e in Entries)
        {
            var key = NormalizeTypeKey(e.TypeFullName);
            if (!_byType.TryGetValue(key, out var list))
            {
                list = new List<DocEntry>();
                _byType[key] = list;
            }
            list.Add(e);

            // Type entries share their bucket with their members; map the type
            // full-name key to the type-self entry for quick FindType lookup.
            if (Array.IndexOf(TypeKinds, e.Kind) >= 0)
                _typeEntryByKey[key] = e;
        }
    }

    public DocEntry FindFor(string typeFullName, string memberName, string kind, int paramCount)
    {
        if (_byType == null) BuildIndex();

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

        foreach (var e in Entries)
        {
            if (NormalizeTypeKey(e.TypeFullName) != key) continue;
            if (!string.Equals(e.MemberName, memberName, StringComparison.Ordinal)) continue;
            if (!string.Equals(e.Kind, kind, StringComparison.Ordinal)) continue;
            return e;
        }
        return null;
    }

    /// <summary>
    /// Returns the type's own DocEntry (the entry whose Kind is "Type"/"Enum"/"Delegate"
    /// and which represents the type itself), or null when not documented.
    /// </summary>
    public DocEntry FindType(string typeFullName)
    {
        if (_typeEntryByKey == null) BuildIndex();
        return _typeEntryByKey.TryGetValue(NormalizeTypeKey(typeFullName), out var e) ? e : null;
    }

    /// <summary>
    /// Returns all member entries declared on the given type (no inheritance).
    /// </summary>
    public IReadOnlyList<DocEntry> FindAllMembers(string typeFullName)
    {
        if (_byType == null) BuildIndex();
        return _byType.TryGetValue(NormalizeTypeKey(typeFullName), out var list)
            ? (IReadOnlyList<DocEntry>)list
            : Array.Empty<DocEntry>();
    }
}
