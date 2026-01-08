using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "ContentPoliceSelector", menuName = "Basis/ContentPoliceSelector")]
public class ContentPoliceSelector : ScriptableObject
{
    [SerializeField] public List<string> selectedTypes = new();

    // Runtime cache (not serialized)
    [NonSerialized] private HashSet<Type> _approvedTypes;
    [NonSerialized] private bool _cacheBuilt;

    public HashSet<Type> ApprovedTypes
    {
        get
        {
            if (!_cacheBuilt) BuildCache();
            return _approvedTypes;
        }
    }

    private void OnEnable() => BuildCache();

    private void BuildCache()
    {
        _approvedTypes = new HashSet<Type>();
        for (int i = 0; i < selectedTypes.Count; i++)
        {
            var typeName = selectedTypes[i];
            if (string.IsNullOrEmpty(typeName)) continue;

            // Works if the string is AssemblyQualifiedName; if you only store FullName,
            // you may need to search assemblies (slower) or store AssemblyQualifiedName instead.
            var t = Type.GetType(typeName, throwOnError: false);
            if (t != null) _approvedTypes.Add(t);
        }
        _cacheBuilt = true;
    }
}
