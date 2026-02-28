using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
public static class BasisBundleBuild
{
    public static event Func<BasisContentBase, List<BuildTarget>, Task> PreBuildBundleEvents;

    public static async Task<(bool, string)> GameObjectBundleBuild(
        string Image,
        BasisContentBase BasisContentBase,
        List<BuildTarget> Targets,
        bool useProvidedPassword = false,
        string OverridenPassword = "")
    {
        int TargetCount = Targets.Count;
        for (int Index = 0; Index < TargetCount; Index++)
        {
            if (CheckTarget(Targets[Index]) == false)
            {
                return (false, "Please Install build Target for " + Targets[Index].ToString());
            }
        }

        Bounds unitybounds = CalculateLocalRenderBounds(BasisContentBase.gameObject);
        BasisBounds BasisBounds = new BasisBounds(unitybounds.center, unitybounds.size);

        var meta = GenerateMetaData(BasisContentBase.gameObject);
        string FolderPath = MakeSafeFolderName(BasisContentBase.BasisBundleDescription.AssetBundleName);
        return await BuildBundle(FolderPath,
            basisContentBase: BasisContentBase,
            MetaData: meta,
            BasisBounds: BasisBounds,
            Images: Image,
            targets: Targets,
            useProvidedPassword: useProvidedPassword,
            OverridenPassword: OverridenPassword,
            buildFunction: (content, obj, hex, target, buildId) =>
                BasisAssetBundlePipeline.BuildAssetBundle(content.gameObject, obj, hex, target, FolderPath));
    }
    /// <summary>
    /// Calculates bounds of all child renderers in PARENT LOCAL SPACE (pivot-relative).
    /// This is stable even if the object is moved/rotated in the world before measuring.
    /// </summary>
    public static Bounds CalculateLocalRenderBounds(GameObject parent)
    {
        var renderers = parent.GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0)
            return new Bounds(Vector3.zero, Vector3.zero);

        Matrix4x4 parentWorldToLocal = parent.transform.worldToLocalMatrix;

        bool hasAny = false;
        Bounds accum = default;

        foreach (var r in renderers)
        {
            if (r == null) continue;

            Bounds srcLocal;

            if (r is SkinnedMeshRenderer smr)
            {
                // In smr local space
                srcLocal = smr.localBounds;
            }
            else if (r is MeshRenderer mr)
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                // In mesh local space (same as MeshFilter transform local space)
                srcLocal = mf.sharedMesh.bounds;
            }
            else
            {
                continue; // ignore other renderer types for now
            }

            // Map from renderer local -> world -> parent local
            Matrix4x4 toParentLocal = parentWorldToLocal * r.transform.localToWorldMatrix;

            // Transform bounds center and extents to new AABB in parent local space
            Bounds transformed = TransformBoundsAABB(srcLocal, toParentLocal);

            if (!hasAny)
            {
                accum = transformed;
                hasAny = true;
            }
            else
            {
                accum.Encapsulate(transformed.min);
                accum.Encapsulate(transformed.max);
            }
        }

        if (!hasAny)
            return new Bounds(Vector3.zero, Vector3.zero);

        if (accum.extents == Vector3.zero)
            accum = new Bounds(accum.center, new Vector3(0.1f, 0.1f, 0.1f));

        return accum;
    }

    private static Bounds TransformBoundsAABB(Bounds b, Matrix4x4 m)
    {
        // Standard affine bounds transform:
        Vector3 c = m.MultiplyPoint3x4(b.center);

        Vector3 ex = m.MultiplyVector(new Vector3(b.extents.x, 0f, 0f));
        Vector3 ey = m.MultiplyVector(new Vector3(0f, b.extents.y, 0f));
        Vector3 ez = m.MultiplyVector(new Vector3(0f, 0f, b.extents.z));

        Vector3 e = new Vector3(
            Mathf.Abs(ex.x) + Mathf.Abs(ey.x) + Mathf.Abs(ez.x),
            Mathf.Abs(ex.y) + Mathf.Abs(ey.y) + Mathf.Abs(ez.y),
            Mathf.Abs(ex.z) + Mathf.Abs(ey.z) + Mathf.Abs(ez.z)
        );

        return new Bounds(c, e * 2f);
    }
    public static bool CheckTarget(BuildTarget target)
    {
        bool isSupported = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target) ||
                           BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, target);

        Debug.Log($"{target.ToString()} Build Target Installed: {isSupported}");
        return isSupported;
    }
    public static async Task<(bool, string)> SceneBundleBuild(
     string Image,
     BasisContentBase BasisContentBase,
     List<BuildTarget> Targets,
     bool useProvidedPassword = false,
     string OverridenPassword = "")
    {
        int TargetCount = Targets.Count;
        for (int Index = 0; Index < TargetCount; Index++)
        {
            if (CheckTarget(Targets[Index]) == false)
            {
                return (false, "Please Install build Target for " + Targets[Index].ToString());
            }
        }

        UnityEngine.SceneManagement.Scene scene = BasisContentBase.gameObject.scene;

        var unitybounds = CalculateSceneBounds(scene);
        BasisBounds BasisBounds = new BasisBounds(unitybounds.center, unitybounds.size);

        var meta = GenerateSceneMetaData(scene);
        string FolderName = MakeSafeFolderName(BasisContentBase.BasisBundleDescription.AssetBundleName);
        return await BuildBundle(FolderName,
            basisContentBase: BasisContentBase,
            MetaData: meta,
            BasisBounds: BasisBounds,
            Images: Image,
            targets: Targets,
            useProvidedPassword: useProvidedPassword,
            OverridenPassword: OverridenPassword,
            buildFunction: (content, obj, hex, target, buildId) => BasisAssetBundlePipeline.BuildAssetBundle(scene, obj, hex, target, FolderName));
    }
    // Windows reserved device names (case-insensitive)
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };

    public static string MakeSafeFolderName(string input, int maxLength = 64)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Folder";

        // Normalize to avoid weird unicode combining issues
        input = input.Normalize(NormalizationForm.FormKC);

        // Remove invalid path chars (cross-platform safe)
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(input.Length);

        foreach (char c in input)
        {
            if (invalidChars.Contains(c) || char.IsControl(c))
                builder.Append('_');
            else
                builder.Append(c);
        }

        string result = builder.ToString();

        // Remove trailing dots/spaces (Windows hates these)
        result = result.Trim().TrimEnd('.', ' ');

        // Collapse repeated underscores
        result = Regex.Replace(result, "_{2,}", "_");

        // Prevent empty
        if (string.IsNullOrWhiteSpace(result))
            result = "Folder";

        // Prevent reserved names (Windows)
        if (ReservedNames.Any(r =>
            string.Equals(r, result, StringComparison.OrdinalIgnoreCase)))
        {
            result = "_" + result;
        }

        // Enforce max length
        if (result.Length > maxLength)
            result = result.Substring(0, maxLength);

        return result;
    }
    public static Bounds CalculateSceneBounds(Scene scene)
    {
        var rootObjects = scene.GetRootGameObjects();

        bool hasBounds = false;
        Bounds sceneBounds = new Bounds(Vector3.zero, new Vector3(0.1f, 0.1f, 0.1f));

        foreach (var root in rootObjects)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);

            foreach (var renderer in renderers)
            {
                if (!hasBounds)
                {
                    sceneBounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    sceneBounds.Encapsulate(renderer.bounds);
                }
            }
        }
        return sceneBounds;
    }
    public static BasisBundleConnector.BasisMetaData GenerateMetaData(GameObject root)
    {

        BasisBundleConnector.BasisMetaData meta = new BasisBundleConnector.BasisMetaData();
        long triangleCount = 0;
        long materialCount = 0;
        long bonesCount = 0;
        Dictionary<string, int> componentCounts = new Dictionary<string, int>();
        var meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
        foreach (var mf in meshFilters)
        {
            if (mf.sharedMesh != null)
            {
                EnsureReadWriteEnabled(mf.sharedMesh);
                triangleCount += mf.sharedMesh.triangles.Length / 3;
            }
        }
        var skinnedMeshes = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var smr in skinnedMeshes)
        {
            if (smr.sharedMesh != null)
            {
                EnsureReadWriteEnabled(smr.sharedMesh);
                triangleCount += smr.sharedMesh.triangles.Length / 3;
            }

            if (smr.bones != null)
            {
                bonesCount += smr.bones.Length;
            }
        }
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        HashSet<Material> uniqueMaterials = new HashSet<Material>();

        foreach (var r in renderers)
        {
            foreach (var mat in r.sharedMaterials)
            {
                if (mat != null)
                {
                    uniqueMaterials.Add(mat);
                }
            }
        }
        materialCount = uniqueMaterials.Count;
        var components = root.GetComponentsInChildren<Component>(true);
        foreach (var comp in components)
        {
            if (comp == null)
            {
                continue;
            }

            string typeName = comp.GetType().Name;

            if (componentCounts.ContainsKey(typeName))
            {
                componentCounts[typeName]++;
            }
            else
            {
                componentCounts[typeName] = 1;
            }
        }

        meta.TrianglesCount = triangleCount;
        meta.MaterialCount = materialCount;
        meta.BonesCount = bonesCount;
        meta.ComponentNames = componentCounts
            .Select(kvp => new BasisBundleConnector.BasisComponentName
            {
                Name = kvp.Key,
                count = kvp.Value
            })
            .ToArray();

        return meta;
    }
    public static void EnsureReadWriteEnabled(Mesh mesh)
    {
        if (mesh == null)
        {
            return;
        }

        string path = AssetDatabase.GetAssetPath(mesh);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer != null && importer.isReadable == false)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
        }
    }
    public static BasisBundleConnector.BasisMetaData GenerateSceneMetaData(Scene scene)
    {
        var roots = scene.GetRootGameObjects();

        BasisBundleConnector.BasisMetaData combined = new BasisBundleConnector.BasisMetaData();

        long triangles = 0;
        long materials = 0;
        long bones = 0;
        Dictionary<string, int> componentCounts = new Dictionary<string, int>();

        foreach (var root in roots)
        {
            var meta = GenerateMetaData(root);

            triangles += meta.TrianglesCount;
            materials += meta.MaterialCount;
            bones += meta.BonesCount;

            if (meta.ComponentNames != null)
            {
                foreach (var c in meta.ComponentNames)
                {
                    if (componentCounts.ContainsKey(c.Name))
                        componentCounts[c.Name] += c.count;
                    else
                        componentCounts[c.Name] = c.count;
                }
            }
        }

        combined.TrianglesCount = triangles;
        combined.MaterialCount = materials;
        combined.BonesCount = bones;

        combined.ComponentNames = componentCounts
            .Select(kvp => new BasisBundleConnector.BasisComponentName
            {
                Name = kvp.Key,
                count = kvp.Value
            })
            .ToArray();

        return combined;
    }
    public static async Task<(bool, string)> BuildBundle(string FolderName,
      BasisContentBase basisContentBase,
      BasisBundleConnector.BasisMetaData MetaData,
      BasisBounds BasisBounds,
      string Images,
      List<BuildTarget> targets,
      bool useProvidedPassword,
      string OverridenPassword,
      Func<BasisContentBase, BasisAssetBundleObject, string, BuildTarget, string,
           Task<(bool, (BasisBundleGenerated, AssetBundleBuilder.InformationHash))>> buildFunction)
    {
        string generatedID = null;
        string stagingRoot = null;

        try
        {
            if (PreBuildBundleEvents != null)
            {
                List<Task> eventTasks = new List<Task>();
                Delegate[] events = PreBuildBundleEvents.GetInvocationList();
                int Length = events.Length;
                for (int ctr = 0; ctr < Length; ctr++)
                {
                    var handler = (Func<BasisContentBase, List<BuildTarget>, Task>)events[ctr];
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

            AdjustBuildTargetOrder(targets);

            BasisAssetBundleObject assetBundleObject =
                AssetDatabase.LoadAssetAtPath<BasisAssetBundleObject>(BasisAssetBundleObject.AssetBundleObject);

            // Final output folder (combined result)
            string rootOutDir = assetBundleObject.AssetBundleDirectory;
            Directory.CreateDirectory(rootOutDir);

            generatedID = BasisGenerateUniqueID.GenerateUniqueID();
            string buildOutDir = EnsureBuildOutputDirectory(rootOutDir, FolderName, deleteIfExists: true);

            // Staging output folder (uncombined per-target Unity output)
            string uncombinedRoot = PathConversion(assetBundleObject.AssetBundleUnCombined);
            stagingRoot = Path.Combine(uncombinedRoot, FolderName);
            Directory.CreateDirectory(stagingRoot);

            string Password = useProvidedPassword ? OverridenPassword : GenerateHexString(32);

            int targetsLength = targets.Count;
            BasisBundleGenerated[] bundles = new BasisBundleGenerated[targetsLength];
            List<string> paths = new List<string>();

            for (int Index = 0; Index < targetsLength; Index++)
            {
                BuildTarget target = targets[Index];

                // CHANGED: pass buildId (generatedID) into buildFunction
                var (success, result) = await buildFunction(basisContentBase, assetBundleObject, Password, target, generatedID);
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

            BasisBundleConnector basisBundleConnector = new BasisBundleConnector(
                generatedID,
                basisContentBase.BasisBundleDescription,
                bundles,
                Images,
                BasisBounds,
                MetaData
            );

            byte[] BasisbundleconnectorUnEncrypted =
                BasisSerialization.SerializeValue<BasisBundleConnector>(basisBundleConnector);

            var BasisPassword = new BasisEncryptionWrapper.BasisPassword { VP = Password };

            string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
            BasisProgressReport report = new BasisProgressReport();
            byte[] EncryptedConnector =
                await BasisEncryptionWrapper.EncryptToBytesAsync(UniqueID, BasisPassword, BasisbundleconnectorUnEncrypted, report);

            EditorUtility.DisplayProgressBar("Starting Bundle Combining", "Starting Bundle Combining", 100);

            string FilePath = Path.Combine(buildOutDir, $"{generatedID}{assetBundleObject.BasisEncryptedExtension}");
            await CombineFiles(FilePath, paths, EncryptedConnector);

            EditorUtility.DisplayProgressBar("Saving Generated BEE file", "Saving Generated BEE file", 100);

            await AssetBundleBuilder.SaveFileAsync(buildOutDir, assetBundleObject.ProtectedPasswordFileName, "txt", Password);

            EditorUtility.DisplayProgressBar("Finshed File Combining", "Finshed File Combining", 100);

            DeleteFolders(buildOutDir);

            // cleanup staging (uncombined) outputs
            try
            {
                if (!string.IsNullOrEmpty(stagingRoot) && Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, true);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Failed to delete staging folder {stagingRoot}: {ex.Message}");
            }

            if (assetBundleObject.OpenFolderOnDisc)
            {
                OpenRelativePath(buildOutDir);
            }

            RestoreOriginalBuildTarget(originalActiveTarget);

            BasisDebug.Log("Successfully built asset bundle.");
            EditorUtility.ClearProgressBar();
            return (true, "Success");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            BasisDebug.LogError($"BuildBundle error: {ex.Message}");

            // cleanup staging even on failure
            try
            {
                if (!string.IsNullOrEmpty(stagingRoot) && Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, true);
            }
            catch { /* ignore */ }

            EditorUtility.ClearProgressBar();
            return (false, $"BuildBundle Exception: {ex.Message}");
        }
    }
    private static string EnsureBuildOutputDirectory(string rootOutDir, string folderName, bool deleteIfExists)
    {
        if (string.IsNullOrEmpty(rootOutDir))
            throw new ArgumentException("rootOutDir is null/empty", nameof(rootOutDir));
        if (string.IsNullOrEmpty(folderName))
            throw new ArgumentException("folderName is null/empty", nameof(folderName));

        string buildOutDir = Path.Combine(rootOutDir, folderName);

        if (Directory.Exists(buildOutDir))
        {
            if (deleteIfExists)
                Directory.Delete(buildOutDir, true);
            else
                buildOutDir = Path.Combine(rootOutDir, folderName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        }

        Directory.CreateDirectory(buildOutDir);
        return buildOutDir;
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
    public static async Task CombineFiles(string outputPath, List<string> bundlePaths, byte[] encryptedConnector, CancellationToken ct = default(CancellationToken))
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
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true))
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
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
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
                                EditorUtility.DisplayProgressBar("Combining Files", "Processing: " + Path.GetFileName(path), progress);
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
        if (string.IsNullOrEmpty(relativePath))
        {
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
