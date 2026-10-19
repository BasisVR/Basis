using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Where a capture is allowed to be written.
///
/// Both captures (the managed per-frame one and the engine's own) take a
/// filename rather than a path and resolve it under
/// <c>Application.persistentDataPath</c>. Anything escaping that root is
/// refused: these fields sit on a component, so a world could author one, and
/// an unchecked path names any file the process can write.
/// </summary>
public static class BasisMediaCapturePath
{
    /// <summary>
    /// Resolves <paramref name="nameOrPath"/> under
    /// <c>Application.persistentDataPath</c>, falling back to
    /// <paramref name="defaultName"/> when it is empty. Returns null with a
    /// reason in <paramref name="refusal"/> when the result would sit outside
    /// that root.
    /// </summary>
    public static string Resolve(string nameOrPath, string defaultName, out string refusal)
    {
        refusal = null;
        string root = Path.GetFullPath(Application.persistentDataPath);
        if (string.IsNullOrEmpty(nameOrPath))
            return Path.Combine(root, defaultName);

        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(nameOrPath)
                ? nameOrPath
                : Path.Combine(root, nameOrPath));
        }
        catch (Exception e)
        {
            refusal = $"could not resolve the capture path: {e.Message}";
            return null;
        }

        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString())
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) && full != root)
        {
            refusal = "the capture path must sit under Application.persistentDataPath";
            return null;
        }
        return full;
    }
}
