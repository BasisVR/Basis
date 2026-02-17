using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BasisExportSourceFiles
{
    // Menu item in Unity: Tools/Basis/Export Server Sources
    [MenuItem("Basis/Export Server Sources")]
    public static void Run()
    {
        try
        {
            // Unity project root: ...\Basis
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

            // Basis Unity dir: ...\Basis Unity
            var basisUnityDir = Path.GetFullPath(Path.Combine(projectRoot, ".."));

            // Source: ...\Basis Unity\Basis Server
            var sourceDir = Path.Combine(basisUnityDir, "Basis Server");

            // Destination: ...\Basis\Packages\com.basis.server
            // (adjust this if your destination differs)
            var destinationDir = Path.Combine(projectRoot, "Packages", "com.basis.server");

            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

            if (!Directory.Exists(destinationDir))
                throw new DirectoryNotFoundException($"Destination directory not found: {destinationDir}");

            // 1) Remove all .cs files in destination
            foreach (var cs in Directory.EnumerateFiles(destinationDir, "*.cs", SearchOption.AllDirectories))
            {
                File.SetAttributes(cs, FileAttributes.Normal);
                File.Delete(cs);
            }

            // 2) Copy from source to destination with exclusions
            CopyDirectoryFiltered(sourceDir, destinationDir);

            AssetDatabase.Refresh();

            Debug.Log($"Export complete.\nSource: {sourceDir}\nDestination: {destinationDir}");
        }
        catch (Exception ex)
        {
            Debug.LogError("Export failed: " + ex);
        }
    }

    private static void CopyDirectoryFiltered(string sourceDir, string destinationDir)
    {
        // Ensure destination root exists
        Directory.CreateDirectory(destinationDir);

        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, dir);

            // Exclude obj folders anywhere
            if (ContainsPathSegment(rel, "obj"))
                continue;

            // Exclude Contrib\PersistentKv subtree
            if (rel.Replace('/', '\\').IndexOf(@"Contrib\PersistentKv", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            Directory.CreateDirectory(Path.Combine(destinationDir, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);

            // Exclude obj folders anywhere
            if (ContainsPathSegment(rel, "obj"))
                continue;

            // Exclude Contrib\PersistentKv subtree
            if (rel.Replace('/', '\\').IndexOf(@"Contrib\PersistentKv", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            var ext = Path.GetExtension(file);
            if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (ext.Equals(".asmdef", StringComparison.OrdinalIgnoreCase)) continue;

            var destPath = Path.Combine(destinationDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }
    }

    private static bool ContainsPathSegment(string relativePath, string segment)
    {
        var parts = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (p.Equals(segment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
