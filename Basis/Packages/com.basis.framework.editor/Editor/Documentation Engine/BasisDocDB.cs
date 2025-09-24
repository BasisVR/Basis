// Assets/Editor/DocDB.cs
using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class DocEntry
{
    public string TypeFullName;   // e.g., "Basis.Scripts.BasisSdk.Players.BasisLocalPlayer"
    public string MemberName;     // e.g., "Teleport"
    public string Kind;           // "Type" | "Field" | "Property" | "Method" | "Event"
    public int ParamCount;        // for methods
    public List<string> ParamNames = new(); // optional; helps user display
    public string Summary;
    public string Remarks;
    public string Returns;
    public List<string> ParamDocs = new(); // aligns with ParamNames by index
    public string Example;
}

public class BasisDocDB : ScriptableObject
{
    public List<DocEntry> Entries = new();

    // Quick lookup cache at runtime in editor
    private Dictionary<string, List<DocEntry>> _byType;

    public void BuildIndex()
    {
        _byType = new Dictionary<string, List<DocEntry>>();
        foreach (var e in Entries)
        {
            if (!_byType.TryGetValue(e.TypeFullName, out var list))
            {
                list = new List<DocEntry>();
                _byType[e.TypeFullName] = list;
            }
            list.Add(e);
        }
    }

    public DocEntry FindFor(string typeFullName, string memberName, string kind, int paramCount)
    {
        if (_byType == null) BuildIndex();
        if (!_byType.TryGetValue(typeFullName, out var list)) return null;

        // Prefer exact kind + paramCount match, then fall back
        DocEntry best = null;
        foreach (var e in list)
        {
            if (!string.Equals(e.MemberName, memberName, StringComparison.Ordinal)) continue;
            if (!string.Equals(e.Kind, kind, StringComparison.Ordinal)) continue;

            if (e.ParamCount == paramCount) return e;
            best ??= e; // fallback if overload count differs
        }
        return best;
    }
}
