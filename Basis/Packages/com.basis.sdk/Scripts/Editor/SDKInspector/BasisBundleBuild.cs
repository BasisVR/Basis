using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
public static class BasisBundleBuild
{
    public static event Func<BasisContentBase, List<BuildTarget>, Task> PreBuildBundleEvents;

    public static async Task<(bool, string)> GameObjectBundleBuild(string Image, BasisContentBase BasisContentBase, List<BuildTarget> Targets,bool useProvidedPassword = false, string OverridenPassword = "")
    {
        int TargetCount = Targets.Count;
        for (int Index = 0; Index < TargetCount; Index++)
        {
            if (CheckTarget(Targets[Index]) == false)
            {
                return new(false, "Please Install build Target for " + Targets[Index].ToString());
            }
        }
        return await BuildBundle(BasisContentBase,Image, Targets, useProvidedPassword, OverridenPassword, (content, obj, hex, target) => BasisAssetBundlePipeline.BuildAssetBundle(content.gameObject, obj, hex, target));
    }

    public static bool CheckTarget(BuildTarget target)
    {
        bool isSupported = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target) ||
                           BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, target);

        Debug.Log($"{target.ToString()} Build Target Installed: {isSupported}");
        return isSupported;
    }
    public static async Task<(bool, string)> SceneBundleBuild(string Image,BasisContentBase BasisContentBase, List<BuildTarget> Targets,bool useProvidedPassword = false, string OverridenPassword = "")
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
        return await BuildBundle(BasisContentBase, Image, Targets, useProvidedPassword, OverridenPassword, (content, obj, hex, target) => BasisAssetBundlePipeline.BuildAssetBundle(Scene, obj, hex, target));
    }
    public static async Task<(bool, string)> BuildBundle(BasisContentBase basisContentBase, string Images, List<BuildTarget> targets, bool useProvidedPassword, string OverridenPassword, Func<BasisContentBase, BasisAssetBundleObject, string, BuildTarget, Task<(bool, (BasisBundleGenerated, AssetBundleBuilder.InformationHash))>> buildFunction)
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
            BasisBundleConnector basisBundleConnector = new BasisBundleConnector(generatedID, basisContentBase.BasisBundleDescription, bundles,Images);

            byte[] BasisbundleconnectorUnEncrypted = BasisSerialization.SerializeValue<BasisBundleConnector>(basisBundleConnector);
            var BasisPassword = new BasisEncryptionWrapper.BasisPassword
            {
                VP = Password
            };
            string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
            BasisProgressReport report = new BasisProgressReport();
            byte[] EncryptedConnector = await BasisEncryptionWrapper.EncryptToBytesAsync(UniqueID, BasisPassword, BasisbundleconnectorUnEncrypted, report);

            EditorUtility.DisplayProgressBar("Starting Bundle Combining", "Starting Bundle Combining", 100);

            string FilePath = Path.Combine(assetBundleObject.AssetBundleDirectory, $"{generatedID}{assetBundleObject.BasisEncryptedExtension}");
            await CombineFiles(FilePath, paths, EncryptedConnector);

            EditorUtility.DisplayProgressBar("Saving Generated BEE file", "Saving Generated BEE file", 100);

            await AssetBundleBuilder.SaveFileAsync(assetBundleObject.AssetBundleDirectory, assetBundleObject.ProtectedPasswordFileName, "txt", Password);

            EditorUtility.DisplayProgressBar("Finshed File Combining", "Finshed File Combining", 100);

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
            Debug.LogException(ex);
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

    private static int GetAdaptiveBufferSize(long fileSize)
    {
        const int minBufferSize = 4 * 1024 * 1024;       // 8MB minimum
        const int maxBufferSize = 64 * 1024 * 1024;      // 32MB maximum

        if (fileSize <= 0)
            return minBufferSize;

        // Scale with file size, using a power-of-two approach
        int bufferSize = (int)Math.Min(
            maxBufferSize,
            Math.Max(
                minBufferSize,
                NextPowerOfTwo(fileSize / 64) // Less aggressive divisor for bigger buffers
            )
        );

        return bufferSize;
    }

    private static long NextPowerOfTwo(long value)
    {
        if (value < 1)
            return 1;

        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        value |= value >> 32;

        return value + 1;
    }

    public static async Task CombineFiles(string outputPath,List<string> bundlePaths,byte[] encryptedConnector, CancellationToken ct = default(CancellationToken))
    {
        // --- prep: total lengths for preallocation + progress ---
        long headerLen = encryptedConnector != null ? encryptedConnector.Length : 0L;
        long dataLen = 0;
        for (int i = 0; i < bundlePaths.Count; i++)
        {
            string p = bundlePaths[i];
            if (!File.Exists(p))
                throw new FileNotFoundException("File not found", p);
            dataLen += new FileInfo(p).Length;
        }
        long totalLen = 8L + headerLen + dataLen; // 8 bytes: header length prefix

        // --- big reusable buffer from the pool ---
        const int BufferSize = 8 * 1024 * 1024;  // try 4–8 MiB; 8 MiB if RAM allows
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        var lenBytes = BitConverter.GetBytes(headerLen); // little-endian

        long bytesDone = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long nextUiMs = 0;

        try
        {
            using (var output = new FileStream(outputPath,FileMode.Create,FileAccess.Write,FileShare.Read, BufferSize, useAsync: true))
            {
                // pre-size once — reduces fragmentation and page faults
                output.SetLength(totalLen);

                // write 8-byte length + header
                await output.WriteAsync(lenBytes, 0, lenBytes.Length, ct);
                bytesDone += lenBytes.Length;

                if (headerLen > 0)
                {
                    await output.WriteAsync(encryptedConnector, 0, encryptedConnector.Length, ct);
                    bytesDone += encryptedConnector.Length;
                }

                // stream all input files
                for (int i = 0; i < bundlePaths.Count; i++)
                {
                    string path = bundlePaths[i];
                    using (var input = new FileStream(path,FileMode.Open,FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, BufferSize, ct)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, read, ct);
                            bytesDone += read;

                            // throttle UI to ~5 Hz
                            if (sw.ElapsedMilliseconds >= nextUiMs)
                            {
                                float progress = (float)((double)bytesDone / (double)totalLen);
                                EditorUtility.DisplayProgressBar("Combining Files","Processing: " + Path.GetFileName(path),progress);
                                nextUiMs = sw.ElapsedMilliseconds + 200;
                            }
                        }
                    }
                }
            }
            BasisDebug.Log("Files combined successfully into: " + outputPath);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError("Error combining files: " + ex.Message);
            throw;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            ArrayPool<byte>.Shared.Return(buffer); // important: return to pool
        }
    }
    public static string PathConversion(string relativePath)
    {
        // Get the root path of the project (up to the Assets folder)
        string projectRoot = Application.dataPath.Replace("/Assets", "");
        if (string.IsNullOrEmpty(relativePath)) {
            return projectRoot;
        }

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
    // Convert a Unity path to a platform-compatible path and open it in File Explorer
    public static void OpenFolderInExplorer(string folderPath)
    {
#if UNITY_EDITOR_LINUX
        string osPath = folderPath;
#elif UNITY_EDITOR_OSX
        string osPath = folderPath;
#else
        // Convert Unity-style file path (forward slashes) to Windows-style (backslashes)
        string osPath = folderPath.Replace("/", "\\");
#endif

        // Check if the path exists
        if (Directory.Exists(osPath) || File.Exists(osPath))
        {
#if UNITY_EDITOR_LINUX
            // On Linux, use 'xdg-open'
            System.Diagnostics.Process.Start("xdg-open", osPath);
#elif UNITY_EDITOR_OSX
            // On Mac, use 'open'
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
public static class OcclusionCullingTools
{
    public static void ClearOcclusion(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;

        // Ensure scene is active so APIs behave consistently
        Scene prev = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(scene);

        // Unity editor API for clearing occlusion data
        StaticOcclusionCulling.Clear();

        // Mark dirty so it can be saved if you choose to
        EditorSceneManager.MarkSceneDirty(scene);

        SceneManager.SetActiveScene(prev);
        Debug.Log($"Cleared occlusion data for scene: {scene.path}");
    }

    public static void BakeOcclusion(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;

        Scene prev = SceneManager.GetActiveScene();
        SceneManager.SetActiveScene(scene);

        // Bake (can take time; runs in editor)
        StaticOcclusionCulling.GenerateInBackground();

        // Optionally wait until it's done if you need deterministic output:
        // while (StaticOcclusionCulling.isRunning) { /* pump editor? */ }

        EditorSceneManager.MarkSceneDirty(scene);

        SceneManager.SetActiveScene(prev);
        Debug.Log($"Started occlusion bake for scene: {scene.path}");
    }
}
public static class OcclusionPolicyConfig
    {
        // Decide what you want per platform.
        public static OcclusionPolicy ForTarget(BuildTarget t)
        {
            // Mobile currently unsupported: do nothing (or clear, if you prefer)
            if (t == BuildTarget.Android || t == BuildTarget.iOS)
                return OcclusionPolicy.LeaveAsIs;

            // Example:
            // - Windows/Mac/Linux: bake
            // - WebGL: clear (often not worth it / can be problematic depending on your pipeline)
            switch (t)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneOSX:
                case BuildTarget.StandaloneLinux64:
                    return OcclusionPolicy.Bake;

                case BuildTarget.WebGL:
                    return OcclusionPolicy.Clear;

                default:
                    return OcclusionPolicy.LeaveAsIs;
            }
        }
    }

    public enum OcclusionPolicy
    {
        LeaveAsIs,
        Clear,
        Bake
    }
}
