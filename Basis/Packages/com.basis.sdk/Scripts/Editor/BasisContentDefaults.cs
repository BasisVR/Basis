using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Shared defaults for the "set a default name / description" validator fixes on avatars, props and
/// scenes, so all three produce the same quality of starting text.
/// </summary>
public static class BasisContentDefaults
{
    private static readonly Regex DuplicateSuffix = new Regex(@"\s*\((?:Clone|\d+)\)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex Whitespace = new Regex(@"\s+");
    private const string LocalDidPreference = "DIDID";
    private const string DidKeyPrefix = "did:key:z";

    public static string LocalDidKey()
    {
        string did = PlayerPrefs.GetString(LocalDidPreference, string.Empty);
        return IsDidKey(did) ? did : string.Empty;
    }

    public static bool IsDidKey(string value)
    {
        if (value == null || value.Length <= DidKeyPrefix.Length || value.Length > 128 || !value.StartsWith(DidKeyPrefix, StringComparison.Ordinal)) return false;
        for (int Index = DidKeyPrefix.Length; Index < value.Length; Index++)
        {
            char c = value[Index];
            if (c > 127 || !char.IsLetterOrDigit(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// Produces a display name for a piece of content. Prefers the prefab asset's name over the scene
    /// instance's, since the instance is the one that picks up duplicate suffixes, and otherwise keeps
    /// the name spelled exactly as the author wrote it.
    /// </summary>
    /// <param name="source">The content's GameObject.</param>
    /// <param name="fallback">Returned when nothing usable survives cleaning.</param>
    public static string ResolveName(GameObject source, string fallback)
    {
        if (source == null) return fallback;

        string name = source.name;

        string prefabPath = string.Empty;
        if (PrefabUtility.IsPartOfPrefabInstance(source))
        {
            prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source);
        }
        else if (PrefabUtility.IsPartOfPrefabAsset(source))
        {
            prefabPath = AssetDatabase.GetAssetPath(source);
        }
        if (!string.IsNullOrEmpty(prefabPath))
        {
            name = Path.GetFileNameWithoutExtension(prefabPath);
        }

        name = Clean(name);
        return string.IsNullOrEmpty(name) ? fallback : name;
    }

    private static string Clean(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        name = DuplicateSuffix.Replace(name, string.Empty);

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, ' ');
        }

        return Whitespace.Replace(name, " ").Trim();
    }

    /// <summary>
    /// Pushes a value the validator just wrote onto the model into the matching inspector field, so
    /// the fix is visible instead of only taking effect behind the UI.
    /// </summary>
    public static void SyncField(VisualElement root, string fieldName, string value)
    {
        root?.Q<TextField>(fieldName)?.SetValueWithoutNotify(value);
    }
}
