using BasisSerializer.OdinSerializer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public static class BasisBundleBuild
{
    public static event Func<BasisContentBase, List<BuildTarget>, Task> PreBuildBundleEvents;
   
    public static async Task<(bool, string)> GameObjectBundleBuild(BasisContentBase BasisContentBase, List<BuildTarget> Targets, bool useProvidedPassword = false, string OverridenPassword = "")
    {
        int TargetCount = Targets.Count;
        for (int Index = 0; Index < TargetCount; Index++)
        {
            if(CheckTarget(Targets[Index]) == false)
            {
                return new(false, "Please Install build Target for " + Targets[Index].ToString());
            }
        }
        return await BuildBundle(BasisContentBase, Targets, useProvidedPassword,OverridenPassword, (content, obj, hex, target) => BasisAssetBundlePipeline.BuildAssetBundle(content.gameObject, obj, hex, target));
    }

    public static bool CheckTarget(BuildTarget target)
    {
        bool isSupported = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target) ||
                           BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, target);

        Debug.Log($"{target.ToString()} Build Target Installed: {isSupported}");
        return isSupported;
    }
    public static async Task<(bool, string)> SceneBundleBuild(BasisContentBase BasisContentBase, List<BuildTarget> Targets, bool useProvidedPassword = false, string OverridenPassword = "")
    {
        int TargetCount = Targets.Count;
        for (int Index = 0; Index < TargetCount; Index++)
        {
            if (CheckTarget(Targets[Index]) == false)
            {
                return new(false, "Please Install build Target for " + Targets[Index].ToString());
            }
        }
        UnityEngine.SceneManagement.Scene Scene = BasisContentBase.gameObject.scene;
        return await BuildBundle(BasisContentBase, Targets, useProvidedPassword, OverridenPassword, (content, obj, hex, target) => BasisAssetBundlePipeline.BuildAssetBundle(Scene, obj, hex, target));
    }
    public static async Task<(bool, string)> BuildBundle(BasisContentBase basisContentBase, List<BuildTarget> targets, bool useProvidedPassword, string OverridenPassword,Func<BasisContentBase, BasisAssetBundleObject, string, BuildTarget, Task<(bool, (BasisBundleGenerated, AssetBundleBuilder.InformationHash))>> buildFunction)
    {
        try
        {
            // Invoke pre build event and wait for all subscribers to complete
            if (PreBuildBundleEvents != null)
            {
                List<Task> eventTasks = new List<Task>();
                Delegate[] events = PreBuildBundleEvents.GetInvocationList();
                int Length = events.Length;
                for (int ctr = 0; ctr < Length; ctr++)
                {
                    Func<BasisContentBase, List<BuildTarget>, Task> handler = (Func<BasisContentBase, List<BuildTarget>, Task>)events[ctr];
                    eventTasks.Add(handler(basisContentBase, targets));
                }

                await Task.WhenAll(eventTasks);
                Debug.Log($"{Length} Pre BuildBundle Event(s)...");
            }
            
            Debug.Log("Starting BuildBundle...");
            EditorUtility.DisplayProgressBar("Starting Bundle Build", "Starting Bundle Build", 0);

            BuildTarget originalActiveTarget = EditorUserBuildSettings.activeBuildTarget;

            if (!ErrorChecking(basisContentBase, out string error))
            {
                return (false, error);
            }

            Debug.Log("Passed error checking for BuildBundle...");
            AdjustBuildTargetOrder(targets);

            BasisAssetBundleObject assetBundleObject = AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);
            ClearAssetBundleDirectory(assetBundleObject.AssetBundleDirectory);
            string Password = string.Empty;
            if (useProvidedPassword)
            {
                Password = OverridenPassword;
            }
            else
            {
                Password = GenerateHexString(32);
            }

            int targetsLength = targets.Count;
            BasisBundleGenerated[] bundles = new BasisBundleGenerated[targetsLength];
            List<string> paths = new List<string>();

            for (int Index = 0; Index < targetsLength; Index++)
            {
                BuildTarget target = targets[Index];
                var (success, result) = await buildFunction(basisContentBase, assetBundleObject, Password, target);
                if (!success)
                {
                    return (false, $"Failure While Building for {target}");
                }

                bundles[Index] = result.Item1;
                string hashPath = PathConversion(result.Item2.EncyptedPath);
                paths.Add(hashPath);

                BasisDebug.Log("Adding " + result.Item2.EncyptedPath);
            }

            EditorUtility.DisplayProgressBar("Starting Bundle Build", "Starting Bundle Build", 10);

            string generatedID = BasisGenerateUniqueID.GenerateUniqueID();
            BasisBundleConnector basisBundleConnector = new BasisBundleConnector(generatedID, basisContentBase.BasisBundleDescription, bundles);

            byte[] BasisbundleconnectorUnEncrypted = BasisSerializer.OdinSerializer.SerializationUtility.SerializeValue<BasisBundleConnector>(basisBundleConnector, DataFormat.JSON);
            var BasisPassword = new BasisEncryptionWrapper.BasisPassword
            {
                VP = Password
            };
            string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
            BasisProgressReport report = new BasisProgressReport();
            byte[] EncryptedConnector = await BasisEncryptionWrapper.EncryptDataAsync(UniqueID, BasisbundleconnectorUnEncrypted, BasisPassword, report);

            EditorUtility.DisplayProgressBar("Starting Bundle Combining", "Starting Bundle Combining", 100);

            string FilePath = Path.Combine(assetBundleObject.AssetBundleDirectory, $"{generatedID}{assetBundleObject.BasisEncryptedExtension}");
            await CombineFiles(FilePath, paths, EncryptedConnector);

            await AssetBundleBuilder.SaveFileAsync(assetBundleObject.AssetBundleDirectory, assetBundleObject.ProtectedPasswordFileName, "txt", Password);

            DeleteFolders(assetBundleObject.AssetBundleDirectory);
            if (assetBundleObject.OpenFolderOnDisc)
            {
                OpenRelativePath(assetBundleObject.AssetBundleDirectory);
            }
            RestoreOriginalBuildTarget(originalActiveTarget);

            Debug.Log("Successfully built asset bundle.");

            EditorUtility.ClearProgressBar();
            return (true, "Success");
        }
        catch (Exception ex)
        {
            Debug.LogError($"BuildBundle error: {ex.Message}");
            EditorUtility.ClearProgressBar();
            return (false, $"BuildBundle Exception: {ex.Message}");
        }
    }

    private static void AdjustBuildTargetOrder(List<BuildTarget> targets)
    {
        BuildTarget activeTarget = EditorUserBuildSettings.activeBuildTarget;
        if (!targets.Contains(activeTarget))
        {
            Debug.LogWarning($"Active build target {activeTarget} not in list of targets.");
        }
        else
        {
            targets.Remove(activeTarget);
            targets.Insert(0, activeTarget);
        }
    }
    private static void ClearAssetBundleDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, true);
        }
    }
    private static string GenerateHexString(int length)
    {
        byte[] randomBytes = GenerateRandomBytes(length);
        return ByteArrayToHexString(randomBytes);
    }
    private static void RestoreOriginalBuildTarget(BuildTarget originalTarget)
    {
        if (EditorUserBuildSettings.activeBuildTarget != originalTarget)
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildPipeline.GetBuildTargetGroup(originalTarget), originalTarget);
            Debug.Log($"Switched back to original build target: {originalTarget}");
        }
    }
    public static async Task CombineFiles(string outputPath, List<string> bundlePaths, byte[] EncryptedConnector, int bufferSize = 327680)
    {
        BasisDebug.Log("Combining files: " + bundlePaths.Count);

        try
        {
            long headerLength = EncryptedConnector.Length;
            using (FileStream outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true))
            {
                byte[] buffer = new byte[bufferSize];
                int totalFiles = bundlePaths.Count;

                byte[] headerBytes = BitConverter.GetBytes(headerLength);

                if (headerBytes.Length != 8)
                {
                    throw new Exception($"Header byte conversion failed! {headerBytes.Length} was not 8 bytes!");
                }

                await outputStream.WriteAsync(headerBytes, 0, 8);
                await outputStream.WriteAsync(EncryptedConnector, 0, EncryptedConnector.Length);

                for (int i = 0; i < totalFiles; i++)
                {
                    string path = bundlePaths[i];

                    if (!File.Exists(path))
                    {
                        BasisDebug.LogError($"File not found: {path}");
                        throw new FileNotFoundException($"ERROR File not found: {path}");
                    }

                    float progress = (float)i / totalFiles;
                    EditorUtility.DisplayProgressBar("Combining Files", $"Processing: {Path.GetFileName(path)}", progress);

                    BasisDebug.Log("Combining " + path);
                    await AppendFileToOutput(path, buffer, outputStream, bufferSize);
                }
            }

            BasisDebug.Log($"Files combined successfully into: {outputPath}");
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Error combining files: {ex.Message}");
            EditorUtility.ClearProgressBar();
            throw; // propagate up so BuildBundle catch block can handle
        }
    }
    private static async Task AppendFileToOutput(string path, byte[] buffer, FileStream outputStream, int bufferSize = 327680)
    {
        try
        {
            using (FileStream inputStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                int bytesRead;
                while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await outputStream.WriteAsync(buffer, 0, bytesRead);
                }
            }
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"Error appending file to output: {ex.Message}");
            throw;
        }
    }
    public static string PathConversion(string relativePath)
    {
        // Get the root path of the project (up to the Assets folder)
        string projectRoot = Application.dataPath.Replace("/Assets", "");

        // If the relative path starts with './', remove it
        if (relativePath.StartsWith("./"))
        {
            relativePath = relativePath.Substring(2); // Remove './'
        }

        // Combine the root with the relative path
        string fullPath = Path.Combine(projectRoot, relativePath);
        return fullPath;
    }
    static void DeleteFolders(string parentDir)
    {
        if (!Directory.Exists(parentDir))
        {
            BasisDebug.Log("Directory does not exist.");
            return;
        }

        foreach (string subDir in Directory.GetDirectories(parentDir))
        {
            try
            {
                Directory.Delete(subDir, true);
                BasisDebug.Log($"Deleted folder: {subDir}");
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Error processing {subDir}: {ex.Message}");
            }
        }
    }
    public static string OpenRelativePath(string relativePath)
    {
        // Get the root path of the project (up to the Assets folder)
        string projectRoot = Application.dataPath.Replace("/Assets", "");

        // If the relative path starts with './', remove it
        if (relativePath.StartsWith("./"))
        {
            relativePath = relativePath.Substring(2); // Remove './'
        }

        // Combine the root with the relative path
        string fullPath = Path.Combine(projectRoot, relativePath);

        // Open the folder or file in explorer
        OpenFolderInExplorer(fullPath);
        return fullPath;
    }
    // Convert a Unity path to a Windows-compatible path and open it in File Explorer
    public static void OpenFolderInExplorer(string folderPath)
    {
#if UNITY_EDITOR_LINUX
        string osPath = folderPath;
#else
        // Convert Unity-style file path (forward slashes) to Windows-style (backslashes)
        string osPath = folderPath.Replace("/", "\\");
#endif

        // Check if the path exists
        if (Directory.Exists(osPath) || File.Exists(osPath))
        {
#if UNITY_EDITOR_LINUX
            // On Windows, use 'explorer' to open the folder or highlight the file
            System.Diagnostics.Process.Start("open", osPath);
#else
            // On Windows, use 'explorer' to open the folder or highlight the file
            System.Diagnostics.Process.Start("explorer.exe", osPath);
#endif
        }
        else
        {
            Debug.LogError("Path does not exist: " + osPath);
        }
    }
    public static bool ErrorChecking(BasisContentBase BasisContentBase, out string Error)
    {
        Error = string.Empty; // Initialize the error variable

        if (string.IsNullOrEmpty(BasisContentBase.BasisBundleDescription.AssetBundleName))
        {
            Error = "Name was empty! Please provide a name in the field.";
            return false;
        }

        return true;
    }
    // Generates a random byte array of specified length
    public static byte[] GenerateRandomBytes(int length)
    {
        Debug.Log($"Generating {length} random bytes...");
        byte[] randomBytes = new byte[length];
        using (var rng = new RNGCryptoServiceProvider())
        {
            rng.GetBytes(randomBytes);
        }
        Debug.Log("Random bytes generated successfully.");
        return randomBytes;
    }

    // Converts a byte array to a Base64 encoded string
    public static string ByteArrayToBase64String(byte[] byteArray)
    {
        Debug.Log("Converting byte array to Base64 string...");
        return Convert.ToBase64String(byteArray);
    }

    // Converts a byte array to a hexadecimal string
    public static string ByteArrayToHexString(byte[] byteArray)
    {
        Debug.Log("Converting byte array to hexadecimal string...");
        StringBuilder hex = new StringBuilder(byteArray.Length * 2);
        foreach (byte b in byteArray)
        {
            hex.AppendFormat("{0:x2}", b);
        }
        Debug.Log("Hexadecimal string conversion successful.");
        return hex.ToString();
    }
}
