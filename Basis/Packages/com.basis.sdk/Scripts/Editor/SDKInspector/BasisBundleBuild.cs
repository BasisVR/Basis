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
using Basis.Editor.Localization;
using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
public static class BasisBundleBuild
{
    public static event Func<BasisContentBase, List<BuildTarget>, Task> PreBuildBundleEvents;

    public static async Task<(bool, string)> GameObjectBundleBuild(string Image, BasisContentBase BasisContentBase, List<BuildTarget> Targets, bool useProvidedPassword = false, string OverriddenPassword = "")
    {
        BasisContentGroupId.EnsurePersistent(BasisContentBase);
        int TargetCount = Targets.Count;
        for (int Index = 0; Index < TargetCount; Index++)
        {
            if (CheckTarget(Targets[Index]) == false)
            {
                return (false, "Please install build target for " + Targets[Index].ToString());
            }
        }

        Bounds unitybounds = CalculateLocalRenderBounds(BasisContentBase.gameObject);
        BasisBounds BasisBounds = new BasisBounds(unitybounds.center, unitybounds.size);

        // Far avatar generation runs once here (before the per-platform loop) on the live build
        // clone, while its real materials are still intact. Failure is never fatal to the build.
        string farLodBase64 = null;
        if (BasisContentBase is BasisAvatar farLodSourceAvatar)
        {
            try
            {
                farLodBase64 = BasisFarLodGenerator.GenerateBase64(farLodSourceAvatar);
            }
            catch (Exception ex)
            {
                BasisFarLodGenerator.LastFailureReason = $"generation threw {ex.GetType().Name}: {ex.Message}";
                Debug.LogException(ex);
                Debug.LogWarning("Far avatar generation failed — building the bundle without a far avatar.");
            }
        }

        string FolderPath = MakeSafeFolderName(BasisContentBase.BasisBundleDescription.AssetBundleName);
        return await BuildBundle(FolderPath,
            basisContentBase: BasisContentBase,
            BasisBounds: BasisBounds,
            Images: Image,
            targets: Targets,
            useProvidedPassword: useProvidedPassword,
            OverriddenPassword: OverriddenPassword,
            buildFunction: (content, obj, hex, target, buildId) =>
                BasisAssetBundlePipeline.BuildAssetBundle(content.gameObject, obj, hex, target, FolderPath),
            FarLodBase64: farLodBase64);
    }
    /// <summary>
    /// Calculates bounds of all child renderers in PARENT LOCAL SPACE (pivot-relative).
    /// This is stable even if the object is moved/rotated in the world before measuring.
    /// </summary>
    public static Bounds CalculateLocalRenderBounds(GameObject parent)
    {
        var renderers = parent.GetComponentsInChildren<Renderer>(true);
        var rects = parent.GetComponentsInChildren<RectTransform>(true);
        if ((renderers == null || renderers.Length == 0) && (rects == null || rects.Length == 0))
            return new Bounds(Vector3.zero, Vector3.zero);

        Matrix4x4 parentWorldToLocal = parent.transform.worldToLocalMatrix;

        bool hasAny = false;
        Bounds accum = default;

        foreach (var r in renderers)
        {
            if (r == null) continue;

            Bounds transformed;

            if (r is SkinnedMeshRenderer smr)
            {
                // Transform bounds center and extents to new AABB in parent local space
                transformed = TransformBoundsAABB(smr.bounds, parentWorldToLocal);
            }
            else if (r is MeshRenderer mr)
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                // In mesh local space (same as MeshFilter transform local space)
                var srcLocal = mf.sharedMesh.bounds;
                // Map from renderer local -> world -> parent local
                Matrix4x4 toParentLocal = parentWorldToLocal * r.transform.localToWorldMatrix;
                // Transform bounds center and extents to new AABB in parent local space
                transformed = TransformBoundsAABB(srcLocal, toParentLocal);
            }
            else
            {
                continue; // ignore other renderer types for now
            }

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

        Vector3[] corners = new Vector3[4];
        foreach (var rect in rects)
        {
            if (rect == null) continue;
            rect.GetWorldCorners(corners);
            for (int i = 0; i < 4; i++)
            {
                Vector3 corner = parentWorldToLocal.MultiplyPoint3x4(corners[i]);
                if (!hasAny)
                {
                    accum = new Bounds(corner, Vector3.zero);
                    hasAny = true;
                }
                else
                {
                    accum.Encapsulate(corner);
                }
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
     string OverriddenPassword = "")
    {
        BasisContentGroupId.EnsurePersistent(BasisContentBase);
        int TargetCount = Targets.Count;
        for (int Index = 0; Index < TargetCount; Index++)
        {
            if (CheckTarget(Targets[Index]) == false)
            {
                return (false, "Please install build target for " + Targets[Index].ToString());
            }
        }

        UnityEngine.SceneManagement.Scene scene = BasisContentBase.gameObject.scene;

        var unitybounds = CalculateSceneBounds(scene);
        BasisBounds BasisBounds = new BasisBounds(unitybounds.center, unitybounds.size);

        string FolderName = MakeSafeFolderName(BasisContentBase.BasisBundleDescription.AssetBundleName);
        return await BuildBundle(FolderName,
            basisContentBase: BasisContentBase,
            BasisBounds: BasisBounds,
            Images: Image,
            targets: Targets,
            useProvidedPassword: useProvidedPassword,
            OverriddenPassword: OverriddenPassword,
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

        BasisContentHarvest harvest = BasisContentHarvest.BuildFrom(root, true);
        List<Component> components = harvest.Components;
        List<BasisComponentKind> kinds = harvest.Kinds;

        // Dedupe skinned bones across all SMRs. Every SMR's bones[] array points at
        // the same skeleton Transforms, so summing smr.bones.Length multiplied the
        // real skeleton size by SMR count — an avatar with one 100-bone skeleton
        // and five SMRs used to report 500 bones. HashSet lookup is O(n) total.
        List<SkinnedMeshRenderer> skinnedMeshes = harvest.SkinnedMeshRenderers;
        HashSet<Transform> uniqueBones = new HashSet<Transform>();
        foreach (var smr in skinnedMeshes)
        {
            if (smr.sharedMesh != null)
            {
                EnsureReadWriteEnabled(smr.sharedMesh);
                triangleCount += smr.sharedMesh.triangles.Length / 3;
            }

            if (smr.bones != null)
            {
                for (int i = 0; i < smr.bones.Length; i++)
                {
                    Transform bone = smr.bones[i];
                    if (bone != null)
                    {
                        uniqueBones.Add(bone);
                    }
                }
            }
        }
        bonesCount = uniqueBones.Count;

        // Materials + textures: memory-bound, so include inactive renderers. A
        // hidden outfit variant's textures still sit in GPU/CPU memory.
        List<Renderer> allRenderers = harvest.Renderers;
        HashSet<Material> uniqueMaterials = new HashSet<Material>();
        foreach (var r in allRenderers)
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

        long textureMemoryBytes = 0;
        HashSet<Texture> uniqueTextures = new HashSet<Texture>();
        foreach (var mat in uniqueMaterials)
        {
            int[] texturePropertyIds = mat.GetTexturePropertyNameIDs();
            for (int i = 0; i < texturePropertyIds.Length; i++)
            {
                Texture tex = mat.GetTexture(texturePropertyIds[i]);
                if (tex != null && uniqueTextures.Add(tex))
                {
                    textureMemoryBytes += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);
                }
            }
        }
        int length = components.Count;
        for (int i = 0; i < length; i++)
        {
            Component comp = components[i];
            if (comp == null)
            {
                continue;
            }

            if (kinds[i] == BasisComponentKind.MeshFilter)
            {
                MeshFilter mf = harvest.UnsafeAs<MeshFilter>(i);
                if (mf.sharedMesh != null)
                {
                    EnsureReadWriteEnabled(mf.sharedMesh);
                    triangleCount += mf.sharedMesh.triangles.Length / 3;
                }
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
        meta.TextureMemoryBytes = textureMemoryBytes;
        meta.GraphicsPipeline = DetectGraphicsPipeline();
        if (root.TryGetComponent(out BasisProp prop))
        {
            meta.PropSpawn = prop.SpawnMetaData;
        }
        meta.ComponentNames = componentCounts
            .Select(kvp => new BasisBundleConnector.BasisComponentName
            {
                Name = kvp.Key,
                count = kvp.Value
            })
            .ToArray();

        return meta;
    }
    // Identifies the render pipeline the bundle is being built against. Stored
    // verbatim as the asset type name (e.g. "UniversalRenderPipelineAsset",
    // "HDRenderPipelineAsset", or any custom SRP asset class) so future or
    // third-party pipelines are captured without a mapping update. When no SRP
    // asset is assigned, GraphicsSettings.currentRenderPipeline is null and the
    // project is on the legacy built-in pipeline.
    public static string DetectGraphicsPipeline()
    {
        RenderPipelineAsset activePipeline = GraphicsSettings.currentRenderPipeline;
        return activePipeline == null ? "Built-in" : activePipeline.GetType().Name;
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
        combined.GraphicsPipeline = DetectGraphicsPipeline();

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
      BasisBounds BasisBounds,
      string Images,
      List<BuildTarget> targets,
      bool useProvidedPassword,
      string OverriddenPassword,
      Func<BasisContentBase, BasisAssetBundleObject, string, BuildTarget, string,
           Task<(bool, BasisBundleBuildResult)>> buildFunction,
      string FarLodBase64 = null)
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
            EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("sdk.bundleBuild.progress.start"), BasisEditorLocalization.Get("sdk.bundleBuild.progress.start"), 0);

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

            string Password = useProvidedPassword ? OverriddenPassword : GenerateHexString(32);

            int targetsLength = targets.Count;
            List<BasisBundleGenerated> bundles = new List<BasisBundleGenerated>(targetsLength + 1);
            List<string> paths = new List<string>();

            bool metaDataSet = false;
            BasisBundleConnector.BasisMetaData MetaData = default;
            for (int Index = 0; Index < targetsLength; Index++)
            {
                BuildTarget target = targets[Index];

                // CHANGED: pass buildId (generatedID) into buildFunction
                var (success, result) = await buildFunction(basisContentBase, assetBundleObject, Password, target, generatedID);
                if (!success)
                {
                    return (false, $"Failure While Building for {target}");
                }

                if (!metaDataSet)
                {
                    MetaData = result.BasisMetaData;
                    metaDataSet = true;
                }

                bundles.Add(result.BasisBundleGenerated);

                string hashPath = PathConversion(result.InformationHash.EncyptedPath);
                paths.Add(hashPath);

                BasisDebug.Log("Adding " + result.InformationHash.EncyptedPath);
            }

            // Avatars additionally get a platform-agnostic Generic (glTF) section, appended
            // after the platform sections so platforms without a purpose-built AssetBundle can
            // still load the avatar. Appending last keeps every platform section's byte range
            // where old clients expect it, and failure is never fatal to the build.
            if (basisContentBase is BasisAvatar genericSourceAvatar && assetBundleObject.GenerateGenericGLTF)
            {
                try
                {
                    var (genericGenerated, genericEncryptedPath) = await BasisGenericAvatarExporter.ExportEncryptedGlb(genericSourceAvatar, assetBundleObject, Password, stagingRoot);
                    if (genericGenerated != null && !string.IsNullOrEmpty(genericEncryptedPath))
                    {
                        bundles.Add(genericGenerated);
                        paths.Add(genericEncryptedPath);
                        BasisDebug.Log("Adding generic (glTF) section " + genericEncryptedPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    Debug.LogWarning("Generic (glTF) section generation failed — building the bundle without it.");
                }
            }

            EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("sdk.bundleBuild.progress.start"), BasisEditorLocalization.Get("sdk.bundleBuild.progress.start"), 10);

            // Stamped from the component the SDK was asked to build, not from the census walked off
            // the build clone: build hooks are free to add components to that clone, and one adding
            // a BasisAvatar to a prop used to be indistinguishable from an avatar afterwards.
            MetaData.ContentKind = ResolveContentKind(basisContentBase);

            BasisBundleConnector basisBundleConnector = new BasisBundleConnector(
                generatedID,
                basisContentBase.BasisBundleDescription,
                bundles.ToArray(),
                Images,
                BasisBounds,
                MetaData,
                FarLodBase64
            );

            byte[] BasisbundleconnectorUnEncrypted =
                BasisSerialization.SerializeValue<BasisBundleConnector>(basisBundleConnector);

            var BasisPassword = new BasisEncryptionWrapper.BasisPassword { VP = Password };

            string UniqueID = BasisGenerateUniqueID.GenerateUniqueID();
            BasisProgressReport report = new BasisProgressReport();
            byte[] EncryptedConnector =
                await BasisEncryptionWrapper.EncryptToBytesAsync(UniqueID, BasisPassword, BasisbundleconnectorUnEncrypted, report);

            EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("sdk.bundleBuild.progress.combine"), BasisEditorLocalization.Get("sdk.bundleBuild.progress.combine"), 100);

            string FilePath = Path.Combine(buildOutDir, $"{generatedID}{assetBundleObject.BasisEncryptedExtension}");
            await CombineFiles(FilePath, paths, EncryptedConnector);

            EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("sdk.bundleBuild.progress.saveBee"), BasisEditorLocalization.Get("sdk.bundleBuild.progress.saveBee"), 100);

            await AssetBundleBuilder.SaveFileAsync(buildOutDir, assetBundleObject.ProtectedPasswordFileName, "txt", Password);

            // A missing far avatar is diagnosable from the build output alone: the reason lands
            // next to the bee instead of only in a console that scrolls away.
            if (basisContentBase is BasisAvatar && string.IsNullOrEmpty(FarLodBase64))
            {
                string skipReason = string.IsNullOrEmpty(BasisFarLodGenerator.LastFailureReason) ? "unknown (no reason recorded)" : BasisFarLodGenerator.LastFailureReason;
                await AssetBundleBuilder.SaveFileAsync(buildOutDir, "faravatar_skip", "txt", $"{DateTime.UtcNow:o}\nFar avatar was not included in this bundle.\nReason: {skipReason}\n");
            }

            EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("sdk.bundleBuild.progress.combineDone"), BasisEditorLocalization.Get("sdk.bundleBuild.progress.combineDone"), 100);

            DeleteFolders(buildOutDir);

            try
            {
                string premadeConfigPath = BasisPremadeServerConfig.Write(buildOutDir, basisContentBase, FilePath, Password);
                if (!string.IsNullOrEmpty(premadeConfigPath))
                {
                    BasisDebug.Log("Wrote server config to " + premadeConfigPath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Debug.LogWarning("Failed to write the premade server config — the bundle itself built fine.");
            }

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
    /// <summary>
    /// The kind the connector declares, read off the content component the build was started from.
    /// Null for anything else, which leaves the reader on the component census.
    /// </summary>
    public static string ResolveContentKind(BasisContentBase basisContentBase)
    {
        if (basisContentBase is BasisAvatar) return BasisBundleConnector.AvatarContentKind;
        if (basisContentBase is BasisProp) return BasisBundleConnector.PropContentKind;
        if (basisContentBase is BasisScene) return BasisBundleConnector.SceneContentKind;
        return null;
    }

    private static string EnsureBuildOutputDirectory(string rootOutDir, string folderName, bool deleteIfExists)
    {
        if (string.IsNullOrEmpty(rootOutDir))
            throw new ArgumentException("rootOutDir is null or empty", nameof(rootOutDir));
        if (string.IsNullOrEmpty(folderName))
            throw new ArgumentException("folderName is null or empty", nameof(folderName));

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
                                EditorUtility.DisplayProgressBar(BasisEditorLocalization.Get("sdk.bundleBuild.progress.combineFiles"), BasisEditorLocalization.Get("sdk.bundleBuild.progress.combineFiles.body", Path.GetFileName(path)), progress);
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
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            Debug.LogError("Path was empty, nothing to open.");
            return;
        }

        string osPath = Path.GetFullPath(folderPath);

        // Check if the path exists
        if (Directory.Exists(osPath) || File.Exists(osPath))
        {
            EditorUtility.OpenWithDefaultApp(osPath);
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

    public class BasisBundleBuildResult
    {
        public BasisBundleBuildResult(BasisBundleGenerated basisBundleGenerated, AssetBundleBuilder.InformationHash informationHash, BasisBundleConnector.BasisMetaData basisMetaData)
        {
            BasisBundleGenerated = basisBundleGenerated;
            InformationHash = informationHash;
            BasisMetaData = basisMetaData;
        }

        public BasisBundleGenerated BasisBundleGenerated { get; }
        public AssetBundleBuilder.InformationHash InformationHash { get; }
        public BasisBundleConnector.BasisMetaData BasisMetaData { get; }
    }
}
