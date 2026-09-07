using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

[CreateAssetMenu(fileName = "ContentPoliceSelector", menuName = "Basis/ContentPoliceSelector")]
public class ContentPoliceSelector : ScriptableObject
{
    public AudioMixerGroup AudioMixer;
    [SerializeField] public List<string> selectedTypes = new();

    // Runtime caches (not serialized)
    [NonSerialized] private HashSet<Type> _approvedTypes;
    [NonSerialized] private HashSet<string> _approvedTypeNames;
    [NonSerialized] private Dictionary<Type, bool> _approvalByType;
    [NonSerialized] private bool _cacheBuilt;

    /// <summary>
    /// Approval memoized per runtime Type so the content walk pays the FullName string
    /// hash once per type instead of once per component. Same answer as
    /// <see cref="ApprovedTypeNames"/>.Contains(type.FullName), always.
    /// </summary>
    public bool IsTypeApproved(Type type)
    {
        if (!_cacheBuilt) BuildCache();
        if (_approvalByType.TryGetValue(type, out bool approved)) return approved;
        approved = _approvedTypeNames.Contains(type.FullName);
        _approvalByType[type] = approved;
        return approved;
    }

    public HashSet<Type> ApprovedTypes
    {
        get
        {
            if (!_cacheBuilt) BuildCache();
            return _approvedTypes;
        }
    }

    /// <summary>
    /// O(1) string lookup for approved type FullNames.
    /// Use this instead of selectedTypes.Contains() which is O(n).
    /// </summary>
    public HashSet<string> ApprovedTypeNames
    {
        get
        {
            if (!_cacheBuilt) BuildCache();
            return _approvedTypeNames;
        }
    }

    private void OnEnable() => BuildCache();

    private void BuildCache()
    {
        _approvedTypes = new HashSet<Type>();
        _approvedTypeNames = new HashSet<string>(selectedTypes.Count, StringComparer.Ordinal);
        _approvalByType = new Dictionary<Type, bool>();
        for (int i = 0; i < selectedTypes.Count; i++)
        {
            var typeName = selectedTypes[i];
            if (string.IsNullOrEmpty(typeName)) continue;

            _approvedTypeNames.Add(typeName);

            var t = Type.GetType(typeName, throwOnError: false);
            if (t != null) _approvedTypes.Add(t);
        }
        _cacheBuilt = true;
    }
}
