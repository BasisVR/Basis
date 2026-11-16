using System;
using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

[AutoStaticsCleanup]
public static partial class BasisContentPoliceInclusions
{
    public const string ResourcesFolder = "BasisContentPolice";

    [Serializable]
    private class InclusionFile
    {
        public string[] Avatar;
        public string[] System;
        public string[] Prop;
        public string[] World;
    }

    private static readonly Dictionary<string, string> Empty = new Dictionary<string, string>(0, StringComparer.Ordinal);
    private static Dictionary<string, string>[] _byKind;

    public static int Version { get; private set; }

    public static IReadOnlyDictionary<string, string> Get(BundledContentHolder.Selector kind)
    {
        if (_byKind == null) Load();
        int index = (int)kind;
        return index >= 0 && index < _byKind.Length ? _byKind[index] : Empty;
    }

    public static void Reload()
    {
        _byKind = null;
        Version++;
    }

    private static void Load()
    {
        var byKind = new Dictionary<string, string>[4];
        for (int index = 0; index < byKind.Length; index++) byKind[index] = new Dictionary<string, string>(StringComparer.Ordinal);
        TextAsset[] files = Resources.LoadAll<TextAsset>(ResourcesFolder);
        for (int index = 0; index < files.Length; index++)
        {
            TextAsset file = files[index];
            InclusionFile parsed;
            try { parsed = JsonUtility.FromJson<InclusionFile>(file.text); }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Content police inclusion {file.name} could not be read: {ex.Message}", BasisDebug.LogTag.System);
                continue;
            }
            if (parsed == null) continue;
            Add(byKind[(int)BundledContentHolder.Selector.Avatar], parsed.Avatar, file.name);
            Add(byKind[(int)BundledContentHolder.Selector.System], parsed.System, file.name);
            Add(byKind[(int)BundledContentHolder.Selector.Prop], parsed.Prop, file.name);
            Add(byKind[(int)BundledContentHolder.Selector.World], parsed.World, file.name);
            Resources.UnloadAsset(file);
        }
        _byKind = byKind;
    }

    private static void Add(Dictionary<string, string> into, string[] typeNames, string source)
    {
        if (typeNames == null) return;
        for (int index = 0; index < typeNames.Length; index++)
        {
            string typeName = typeNames[index]?.Trim();
            if (!string.IsNullOrEmpty(typeName)) into.TryAdd(typeName, source);
        }
    }
}
