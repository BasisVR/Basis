#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class BasisBuildBlendshapeStripper
{
    /// <summary>
    /// Callback so other scripts can remap their own data after we strip/bake.
    ///
    /// Called once per SkinnedMeshRenderer that was processed, AFTER the new mesh is assigned.
    ///
    /// Params:
    /// - buildRoot: the clone root being built
    /// - avatarOnClone: the clone avatar component
    /// - smr: the renderer whose mesh was replaced
    /// - oldMesh: the original mesh before strip/bake
    /// - newMesh: the new stripped mesh asset that is now assigned
    /// - keptBlendshapeNames: blendshape names that still exist on newMesh (excluded from baking)
    /// </summary>
    public static event Action<GameObject, Basis.Scripts.BasisSdk.BasisAvatar, SkinnedMeshRenderer, Mesh, Mesh, IReadOnlyCollection<string>>
        OnRendererMeshRemapped;

    /// <summary>
    /// Callback fired once at end after all renderers processed and default avatar remap finished.
    /// Useful if you want to run a second pass that depends on the avatar arrays being updated.
    ///
    /// Params:
    /// - buildRoot: the clone root being built
    /// - avatarOnClone: the clone avatar component (already remapped)
    /// - meshRemaps: list of (smr, oldMesh, newMesh) for all processed renderers
    /// </summary>
    public static event Action<GameObject, Basis.Scripts.BasisSdk.BasisAvatar, IReadOnlyList<MeshRemapInfo>>
        OnAllMeshesRemapped;

    public readonly struct MeshRemapInfo
    {
        public readonly SkinnedMeshRenderer Renderer;
        public readonly Mesh OldMesh;
        public readonly Mesh NewMesh;
        public readonly IReadOnlyCollection<string> KeptBlendshapeNames;

        public MeshRemapInfo(SkinnedMeshRenderer renderer, Mesh oldMesh, Mesh newMesh, IReadOnlyCollection<string> kept)
        {
            Renderer = renderer;
            OldMesh = oldMesh;
            NewMesh = newMesh;
            KeptBlendshapeNames = kept;
        }
    }

    /// <summary>
    /// Requirement (your rule):
    /// - Only blendshapes in keep set remain as blendshapes.
    /// - Everything else gets baked into base mesh using current SMR weights.
    /// - Then we remap BasisAvatar indices to the new meshes.
    /// - Finally, callbacks fire so other systems can remap too.
    /// </summary>
    public static void StripForBuild(BasisAssetBundleObject settings, GameObject buildRoot,Basis.Scripts.BasisSdk.BasisAvatar avatarOnClone)
    {
        if (buildRoot == null) throw new ArgumentNullException(nameof(buildRoot));
        if (avatarOnClone == null) throw new ArgumentNullException(nameof(avatarOnClone));

        var keepAsBlendshape = new Dictionary<SkinnedMeshRenderer, HashSet<string>>();

        AddDefaultAvatarRequirements(avatarOnClone, keepAsBlendshape);
        BasisBlendshapeBuildHooks.Collect(keepAsBlendshape);

        foreach (var reqComp in buildRoot.GetComponentsInChildren<MonoBehaviour>(true)
                     .OfType<IBasisBlendshapeBuildRequirement>())
        {
            reqComp.CollectBlendshapeRequirements(keepAsBlendshape);
        }

        TemporaryStorageHandler.EnsureDirectoryExists(settings.TemporaryStorage);
        if (string.IsNullOrWhiteSpace(settings.TemporaryStorage) || !settings.TemporaryStorage.StartsWith("Assets/"))
            throw new Exception($"TemporaryStorage must be under Assets/. Got: {settings.TemporaryStorage}");

        // Capture old meshes for the avatar fields remap
        Mesh oldVisemeMesh = avatarOnClone.FaceVisemeMesh != null ? avatarOnClone.FaceVisemeMesh.sharedMesh : null;
        Mesh oldBlinkMesh = avatarOnClone.FaceBlinkMesh != null ? avatarOnClone.FaceBlinkMesh.sharedMesh : null;

        Mesh newVisemeMesh = null;
        Mesh newBlinkMesh = null;

        var remaps = new List<MeshRemapInfo>(64);

        foreach (var smr in buildRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr == null) continue;
            if (smr.sharedMesh == null) continue;

            var srcMesh = smr.sharedMesh;
            if (srcMesh.blendShapeCount == 0) continue;

            keepAsBlendshape.TryGetValue(smr, out var keepNames);
            keepNames ??= new HashSet<string>();

            var stripped = CreateMesh_BakeAllExceptKeep(smr, keepNames);
            if (stripped == null) continue;

            string path = AssetDatabase.GenerateUniqueAssetPath($"{settings.TemporaryStorage}/{stripped.name}.asset");
            AssetDatabase.CreateAsset(stripped, path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var loaded = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            smr.sharedMesh = loaded;

            if (smr.sharedMesh == null)
            {
                Debug.LogError($"Missing Mesh at {path}");
                continue;
            }

            // Track for avatar remap
            if (avatarOnClone.FaceVisemeMesh == smr) newVisemeMesh = loaded;
            if (avatarOnClone.FaceBlinkMesh == smr) newBlinkMesh = loaded;

            // Record remap info
            var keptReadOnly = (IReadOnlyCollection<string>)keepNames.ToArray();
            var info = new MeshRemapInfo(smr, srcMesh, loaded, keptReadOnly);
            remaps.Add(info);

            // Per-renderer callback (lets other scripts remap immediately)
            try
            {
                OnRendererMeshRemapped?.Invoke(buildRoot, avatarOnClone, smr, srcMesh, loaded, keptReadOnly);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"{ex.Message} {ex.StackTrace}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh( ImportAssetOptions.ForceSynchronousImport);

        // Remap default avatar indices (by name)
        UpdateAvatarBlendshapeIndicesAfterStrip(
            avatarOnClone,
            oldVisemeMesh, newVisemeMesh,
            oldBlinkMesh, newBlinkMesh);

        ValidateAvatarIndices(avatarOnClone);
        EditorUtility.SetDirty(avatarOnClone);

        // End-of-pass callback (after avatar arrays are updated)
        try
        {
            OnAllMeshesRemapped?.Invoke(buildRoot, avatarOnClone, remaps);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"{ex.Message} {ex.StackTrace}");
        }
    }

    // -----------------------
    // Public helper for other scripts (optional convenience)
    // -----------------------

    /// <summary>
    /// Remap a blendshape index from oldMesh to newMesh by matching the blendshape name.
    /// Returns -1 if oldIndex invalid or name not found on newMesh.
    /// </summary>
    public static int RemapBlendshapeIndexByName(Mesh oldMesh, Mesh newMesh, int oldIndex)
    {
        if (oldMesh == null || newMesh == null) return -1;
        if (oldIndex < 0 || oldIndex >= oldMesh.blendShapeCount) return -1;
        string name = oldMesh.GetBlendShapeName(oldIndex);
        return newMesh.GetBlendShapeIndex(name);
    }

    /// <summary>
    /// Remap an array of blendshape indices from oldMesh to newMesh by matching names.
    /// </summary>
    public static void RemapBlendshapeIndexArrayByName(Mesh oldMesh, Mesh newMesh, int[] arr)
    {
        if (arr == null) return;
        for (int i = 0; i < arr.Length; i++)
            arr[i] = RemapBlendshapeIndexByName(oldMesh, newMesh, arr[i]);
    }

    // -----------------------
    // Requirement collection
    // -----------------------

    private static void AddDefaultAvatarRequirements(
        Basis.Scripts.BasisSdk.BasisAvatar avatar,
        Dictionary<SkinnedMeshRenderer, HashSet<string>> req)
    {
        if (avatar.FaceVisemeMesh != null && avatar.FaceVisemeMesh.sharedMesh != null)
        {
            var mesh = avatar.FaceVisemeMesh.sharedMesh;
            var set = GetOrCreate(req, avatar.FaceVisemeMesh);

            if (avatar.FaceVisemeMovement != null)
            {
                foreach (int idx in avatar.FaceVisemeMovement)
                    AddIndexNameIfValid(mesh, idx, set);
            }
        }

        if (avatar.FaceBlinkMesh != null && avatar.FaceBlinkMesh.sharedMesh != null)
        {
            var mesh = avatar.FaceBlinkMesh.sharedMesh;
            var set = GetOrCreate(req, avatar.FaceBlinkMesh);

            if (avatar.BlinkViseme != null)
            {
                foreach (int idx in avatar.BlinkViseme)
                    AddIndexNameIfValid(mesh, idx, set);
            }
        }

        if (avatar.laughterBlendTarget >= 0 &&
            avatar.FaceVisemeMesh != null &&
            avatar.FaceVisemeMesh.sharedMesh != null)
        {
            AddIndexNameIfValid(
                avatar.FaceVisemeMesh.sharedMesh,
                avatar.laughterBlendTarget,
                GetOrCreate(req, avatar.FaceVisemeMesh));
        }
    }

    private static HashSet<string> GetOrCreate(
        Dictionary<SkinnedMeshRenderer, HashSet<string>> req,
        SkinnedMeshRenderer smr)
    {
        if (!req.TryGetValue(smr, out var set))
        {
            set = new HashSet<string>();
            req[smr] = set;
        }
        return set;
    }

    private static void AddIndexNameIfValid(Mesh mesh, int idx, HashSet<string> set)
    {
        if (mesh == null) return;
        if (idx < 0 || idx >= mesh.blendShapeCount) return;
        set.Add(mesh.GetBlendShapeName(idx));
    }

    // -----------------------
    // Core: bake all except keep
    // -----------------------

    private static Mesh CreateMesh_BakeAllExceptKeep(SkinnedMeshRenderer smr, HashSet<string> keepNames)
    {
        var src = smr.sharedMesh;
        if (src == null) return null;

        var originalWeights = CaptureWeightsByName(smr);

        ApplyWeightsForBake(smr, keepNames, originalWeights);

        var bakedBase = new Mesh { name = src.name + "_BakedBase" };
#if UNITY_2020_2_OR_NEWER
        smr.BakeMesh(bakedBase, true);
#else
        smr.BakeMesh(bakedBase);
#endif

        RestoreWeightsAfterBake(smr, keepNames, originalWeights);

        var dst = new Mesh { name = $"{src.name}_Stripped" };
        CopyMeshCore_FromBakedBase(src, bakedBase, dst);
        AddBlendshapesByName(src, dst, keepNames);
        dst.RecalculateBounds();

        UnityEngine.Object.DestroyImmediate(bakedBase);
        return dst;
    }

    private static Dictionary<string, float> CaptureWeightsByName(SkinnedMeshRenderer smr)
    {
        var mesh = smr.sharedMesh;
        var dict = new Dictionary<string, float>(mesh.blendShapeCount);
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            string name = mesh.GetBlendShapeName(i);
            dict[name] = smr.GetBlendShapeWeight(i);
        }
        return dict;
    }

    private static void ApplyWeightsForBake(
        SkinnedMeshRenderer smr,
        HashSet<string> keepNames,
        Dictionary<string, float> originalWeightsByName)
    {
        var mesh = smr.sharedMesh;
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            string name = mesh.GetBlendShapeName(i);

            if (keepNames != null && keepNames.Contains(name))
            {
                smr.SetBlendShapeWeight(i, 0f);
                continue;
            }

            if (originalWeightsByName != null && originalWeightsByName.TryGetValue(name, out float w))
                smr.SetBlendShapeWeight(i, w);
            else
                smr.SetBlendShapeWeight(i, 0f);
        }
    }

    private static void RestoreWeightsAfterBake(
        SkinnedMeshRenderer smr,
        HashSet<string> keepNames,
        Dictionary<string, float> originalWeightsByName)
    {
        var mesh = smr.sharedMesh;
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            string name = mesh.GetBlendShapeName(i);

            if (keepNames == null || !keepNames.Contains(name))
            {
                smr.SetBlendShapeWeight(i, 0f);
                continue;
            }

            if (originalWeightsByName != null && originalWeightsByName.TryGetValue(name, out float w))
                smr.SetBlendShapeWeight(i, w);
            else
                smr.SetBlendShapeWeight(i, 0f);
        }
    }

    private static void AddBlendshapesByName(Mesh src, Mesh dst, HashSet<string> keepNames)
    {
        if (keepNames == null || keepNames.Count == 0) return;

        var dv = new Vector3[src.vertexCount];
        var dn = new Vector3[src.vertexCount];
        var dt = new Vector3[src.vertexCount];

        for (int shapeIndex = 0; shapeIndex < src.blendShapeCount; shapeIndex++)
        {
            string bsName = src.GetBlendShapeName(shapeIndex);
            if (!keepNames.Contains(bsName)) continue;

            int frames = src.GetBlendShapeFrameCount(shapeIndex);
            for (int f = 0; f < frames; f++)
            {
                float weight = src.GetBlendShapeFrameWeight(shapeIndex, f);
                src.GetBlendShapeFrameVertices(shapeIndex, f, dv, dn, dt);
                dst.AddBlendShapeFrame(bsName, weight, dv, dn, dt);
            }
        }
    }

    // -----------------------
    // Mesh copy: baked base + src indices/topology + src skinning
    // -----------------------

    private static void CopyMeshCore_FromBakedBase(Mesh src, Mesh bakedBase, Mesh dst)
    {
        dst.indexFormat = src.indexFormat;

        dst.vertices = bakedBase.vertices;
        dst.normals = bakedBase.normals;
        dst.tangents = bakedBase.tangents;
        dst.colors = bakedBase.colors;

        dst.uv = src.uv;
        dst.uv2 = src.uv2;
        dst.uv3 = src.uv3;
        dst.uv4 = src.uv4;
#if UNITY_2018_2_OR_NEWER
        dst.uv5 = src.uv5;
        dst.uv6 = src.uv6;
        dst.uv7 = src.uv7;
        dst.uv8 = src.uv8;
#endif

        dst.bindposes = src.bindposes;
#if UNITY_2020_1_OR_NEWER
        dst.SetBoneWeights(src.GetBonesPerVertex(), src.GetAllBoneWeights());
#else
        dst.boneWeights = src.boneWeights;
#endif

        dst.subMeshCount = src.subMeshCount;
        for (int s = 0; s < src.subMeshCount; s++)
        {
            var topo = src.GetTopology(s);
            var indices = src.GetIndices(s);
            dst.SetIndices(indices, topo, s, true);
        }
    }

    // -----------------------
    // Default avatar remap (by name)
    // -----------------------

    private static int RemapIndexByName(Mesh oldMesh, Mesh newMesh, int oldIndex)
    {
        if (oldMesh == null || newMesh == null) return -1;
        if (oldIndex < 0 || oldIndex >= oldMesh.blendShapeCount) return -1;
        string name = oldMesh.GetBlendShapeName(oldIndex);
        return newMesh.GetBlendShapeIndex(name);
    }

    private static void RemapIntArrayByName(Mesh oldMesh, Mesh newMesh, int[] arr)
    {
        if (arr == null) return;
        for (int i = 0; i < arr.Length; i++)
            arr[i] = RemapIndexByName(oldMesh, newMesh, arr[i]);
    }

    private static void UpdateAvatarBlendshapeIndicesAfterStrip(
        Basis.Scripts.BasisSdk.BasisAvatar avatarOnClone,
        Mesh oldVisemeMesh, Mesh newVisemeMesh,
        Mesh oldBlinkMesh, Mesh newBlinkMesh)
    {
        if (avatarOnClone == null) return;

        if (oldVisemeMesh != null && newVisemeMesh != null)
            RemapIntArrayByName(oldVisemeMesh, newVisemeMesh, avatarOnClone.FaceVisemeMovement);

        if (oldBlinkMesh != null && newBlinkMesh != null)
            RemapIntArrayByName(oldBlinkMesh, newBlinkMesh, avatarOnClone.BlinkViseme);

        if (oldVisemeMesh != null && newVisemeMesh != null)
            avatarOnClone.laughterBlendTarget = RemapIndexByName(oldVisemeMesh, newVisemeMesh, avatarOnClone.laughterBlendTarget);
    }

    private static void ValidateAvatarIndices(Basis.Scripts.BasisSdk.BasisAvatar avatar)
    {
        if (avatar == null) return;

        if (avatar.FaceVisemeMesh != null && avatar.FaceVisemeMesh.sharedMesh != null)
        {
            int count = avatar.FaceVisemeMesh.sharedMesh.blendShapeCount;

            if (avatar.FaceVisemeMovement != null)
            {
                for (int i = 0; i < avatar.FaceVisemeMovement.Length; i++)
                {
                    int idx = avatar.FaceVisemeMovement[i];
                    if (idx != -1 && (idx < 0 || idx >= count))
                        Debug.LogError($"FaceVisemeMovement[{i}] invalid after strip: {idx} (mesh count {count})");
                }
            }

            int laugh = avatar.laughterBlendTarget;
            if (laugh != -1 && (laugh < 0 || laugh >= count))
                Debug.LogError($"laughterBlendTarget invalid after strip: {laugh} (mesh count {count})");
        }

        if (avatar.FaceBlinkMesh != null && avatar.FaceBlinkMesh.sharedMesh != null)
        {
            int count = avatar.FaceBlinkMesh.sharedMesh.blendShapeCount;

            if (avatar.BlinkViseme != null)
            {
                for (int i = 0; i < avatar.BlinkViseme.Length; i++)
                {
                    int idx = avatar.BlinkViseme[i];
                    if (idx != -1 && (idx < 0 || idx >= count))
                        Debug.LogError($"BlinkViseme[{i}] invalid after strip: {idx} (mesh count {count})");
                }
            }
        }
    }
}
#endif
