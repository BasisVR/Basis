using Basis.BasisUI;
using System;
using System.IO;
using UnityEngine;
public static class BasisCameraPhotoFolder
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    public static string Location => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Basis");
#else
    public static string Location => Application.persistentDataPath;
#endif
#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR
    public static bool CanOpen => true;
#else
    public static bool CanOpen => false;
#endif
    public static string PathFor(string fileName) => Path.Combine(Location, fileName);
    public static string Timestamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss");
    public static void Ensure()
    {
        string folder = Location;
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
    }
    public static bool TryEnsure(string label)
    {
        string folder = Location;
        try
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return true;
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"{label} recording could not reach {folder}: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Camera);
            return false;
        }
    }
    public static bool Open()
    {
#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR
        try
        {
            Ensure();
            return BasisFileBrowserUtility.Reveal(Location);
        }
        catch (Exception e)
        {
            BasisDebug.LogError($"Could not open photos folder: {e.GetType().Name}: {e.Message}", BasisDebug.LogTag.Camera);
            return false;
        }
#else
        return false;
#endif
    }
    public static bool Reveal(string path)
    {
#if UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX || UNITY_EDITOR
        if (!string.IsNullOrEmpty(path) && BasisFileBrowserUtility.Reveal(path, true)) return true;
        return Open();
#else
        return false;
#endif
    }
}
