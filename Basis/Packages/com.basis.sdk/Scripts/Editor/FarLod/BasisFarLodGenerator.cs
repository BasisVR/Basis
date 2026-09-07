using System;
using System.Collections.Generic;
using Basis.Scripts.BasisSdk;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds a <see cref="BasisFarLodPayload"/> for an avatar at bundle build time.
///
/// Pipeline: force the runtime T-pose → snapshot every active renderer into animator-root
/// space with skin weights collapsed onto the core humanoid bones → QEM-decimate to a small
/// triangle budget → unwrap → bake the avatar's real rendered appearance into a small atlas
/// via multi-view capture → serialize. The avatar's pose and position are restored afterwards,
/// so the build clone ships unchanged.
///
/// The captured skeleton uses the same T-pose the runtime bone system calibrates against
/// (Animated TPose.controller), which is what keeps the networked per-bone deltas
/// bit-compatible between the real avatar and the far avatar.
/// </summary>
public static class BasisFarLodGenerator
{
    public static int TargetTriangleCount = 8000;
    public static int AtlasSize = 1024;
    public static int CaptureSize = 1024;

    /// <summary>Per-stage timing/count logs, enabled by the far avatar tester.</summary>
    public static bool VerboseLogging;

    /// <summary>
    /// Structured stage report for tooling. Attach before calling <see cref="Generate"/>;
    /// each stage records its label, duration and detail line (counts etc.).
    /// </summary>
    public sealed class GenerationReport
    {
        public struct Entry
        {
            public string Label;
            public double Seconds;
            public string Detail;
        }

        public readonly List<Entry> Entries = new List<Entry>();
        public double TotalSeconds;
    }

    public static GenerationReport ActiveReport;

    private static double _stageStart;

    private static void Stage(string label, float progress)
    {
        double now = EditorApplication.timeSinceStartup;
        CloseStage(now);
        EditorUtility.DisplayProgressBar("Far Avatar Generation", label, progress);
        ActiveReport?.Entries.Add(new GenerationReport.Entry { Label = label });
        if (VerboseLogging)
        {
            Debug.Log($"[FarAvatar] {label}");
        }
        _stageStart = now;
    }

    private static void CloseStage(double now)
    {
        if (ActiveReport != null && ActiveReport.Entries.Count > 0)
        {
            GenerationReport.Entry entry = ActiveReport.Entries[ActiveReport.Entries.Count - 1];
            if (entry.Seconds == 0)
            {
                entry.Seconds = now - _stageStart;
                ActiveReport.Entries[ActiveReport.Entries.Count - 1] = entry;
            }
        }
    }

    private static void StageDetail(string detail)
    {
        if (ActiveReport != null && ActiveReport.Entries.Count > 0)
        {
            GenerationReport.Entry entry = ActiveReport.Entries[ActiveReport.Entries.Count - 1];
            entry.Detail = detail;
            ActiveReport.Entries[ActiveReport.Entries.Count - 1] = entry;
        }
        if (VerboseLogging)
        {
            Debug.Log($"[FarAvatar] {detail}");
        }
    }

    /// <summary>
    /// The controller the runtime calibrates against (BasisPlayerFactory.TPose loads the same
    /// asset through its Addressables key). It ships INSIDE the SDK package; the name search
    /// covers projects that relocated it.
    /// </summary>
    public const string TposeControllerPath = "Packages/com.basis.sdk/Animator/Animated TPose.controller";

    private static RuntimeAnimatorController LoadTposeController()
    {
        RuntimeAnimatorController tpose = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(TposeControllerPath);
        if (tpose == null)
        {
            string[] guids = AssetDatabase.FindAssets("\"Animated TPose\" t:RuntimeAnimatorController");
            for (int i = 0; i < guids.Length; i++)
            {
                RuntimeAnimatorController candidate = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (candidate != null)
                {
                    return candidate;
                }
            }
        }
        return tpose;
    }

    /// <summary>
    /// Core humanoid bones the far avatar keeps, ordered parents-first. Fingers, toes, eyes and
    /// jaw collapse into their nearest ancestor here — they are sub-pixel at far avatar range and
    /// dropping them keeps the runtime skeleton around 20 transforms per player.
    /// </summary>
    private static readonly HumanBodyBones[] CoreBones =
    {
        HumanBodyBones.Hips,
        HumanBodyBones.Spine,
        HumanBodyBones.Chest,
        HumanBodyBones.UpperChest,
        HumanBodyBones.Neck,
        HumanBodyBones.Head,
        HumanBodyBones.LeftShoulder,
        HumanBodyBones.RightShoulder,
        HumanBodyBones.LeftUpperArm,
        HumanBodyBones.RightUpperArm,
        HumanBodyBones.LeftLowerArm,
        HumanBodyBones.RightLowerArm,
        HumanBodyBones.LeftHand,
        HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg,
        HumanBodyBones.RightUpperLeg,
        HumanBodyBones.LeftLowerLeg,
        HumanBodyBones.RightLowerLeg,
        HumanBodyBones.LeftFoot,
        HumanBodyBones.RightFoot,
    };

    private static HumanBodyBones[] ParentChain(HumanBodyBones bone)
    {
        switch (bone)
        {
            case HumanBodyBones.Spine: return new[] { HumanBodyBones.Hips };
            case HumanBodyBones.Chest: return new[] { HumanBodyBones.Spine, HumanBodyBones.Hips };
            case HumanBodyBones.UpperChest: return new[] { HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips };
            case HumanBodyBones.Neck:
            case HumanBodyBones.LeftShoulder:
            case HumanBodyBones.RightShoulder:
                return new[] { HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips };
            case HumanBodyBones.Head: return new[] { HumanBodyBones.Neck, HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips };
            case HumanBodyBones.LeftUpperArm: return new[] { HumanBodyBones.LeftShoulder, HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips };
            case HumanBodyBones.RightUpperArm: return new[] { HumanBodyBones.RightShoulder, HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips };
            case HumanBodyBones.LeftLowerArm: return new[] { HumanBodyBones.LeftUpperArm };
            case HumanBodyBones.RightLowerArm: return new[] { HumanBodyBones.RightUpperArm };
            case HumanBodyBones.LeftHand: return new[] { HumanBodyBones.LeftLowerArm };
            case HumanBodyBones.RightHand: return new[] { HumanBodyBones.RightLowerArm };
            case HumanBodyBones.LeftUpperLeg:
            case HumanBodyBones.RightUpperLeg:
                return new[] { HumanBodyBones.Hips };
            case HumanBodyBones.LeftLowerLeg: return new[] { HumanBodyBones.LeftUpperLeg };
            case HumanBodyBones.RightLowerLeg: return new[] { HumanBodyBones.RightUpperLeg };
            case HumanBodyBones.LeftFoot: return new[] { HumanBodyBones.LeftLowerLeg };
            case HumanBodyBones.RightFoot: return new[] { HumanBodyBones.RightLowerLeg };
            default: return Array.Empty<HumanBodyBones>();
        }
    }

    public sealed class FarLodSkeleton
    {
        public readonly List<HumanBodyBones> Bones = new List<HumanBodyBones>();
        public readonly List<int> ParentIndex = new List<int>();
        public readonly List<Transform> Transforms = new List<Transform>();
        public readonly List<Vector3> RootSpacePosition = new List<Vector3>();
        public readonly List<Quaternion> RootSpaceRotation = new List<Quaternion>();
        public readonly List<Vector3> RestLocalPosition = new List<Vector3>();
        public readonly List<Quaternion> RestLocalRotation = new List<Quaternion>();
        public readonly Dictionary<Transform, int> TransformToBone = new Dictionary<Transform, int>();
        public int Count => Bones.Count;
    }

    /// <summary>
    /// Why the last <see cref="Generate"/> call produced no payload — set by every abort path
    /// here and in the atlas baker, written next to the built bee so a missing far avatar can be
    /// diagnosed from the build output alone.
    /// </summary>
    public static string LastFailureReason;

    public static string GenerateBase64(BasisAvatar avatar)
    {
        BasisFarLodPayload payload = Generate(avatar);
        if (payload == null)
        {
            return null;
        }
        byte[] bytes = payload.Serialize();
        // Round-trip through the runtime parser: a payload every client would refuse (bad
        // source data like an implausible eye/mouth position, or a writer/format regression)
        // must fail the build here instead of shipping inside the bee.
        if (BasisFarLodPayload.TryParse(bytes) == null)
        {
            throw new System.InvalidOperationException("Generated far avatar payload failed its validation round-trip — check the avatar's eye/mouth positions and rig for implausible values.");
        }
        return System.Convert.ToBase64String(bytes);
    }

    public static BasisFarLodPayload Generate(BasisAvatar avatar)
    {
        LastFailureReason = null;
        if (avatar == null || avatar.Animator == null || avatar.Animator.avatar == null || !avatar.Animator.avatar.isHuman)
        {
            LastFailureReason = "avatar is not humanoid";
            Debug.LogWarning("Far avatar generation skipped: avatar is not humanoid.");
            return null;
        }

        Animator animator = avatar.Animator;
        Transform root = animator.transform;
        double startTime = EditorApplication.timeSinceStartup;
        _stageStart = startTime;

        TransformPoseSnapshot poseSnapshot = TransformPoseSnapshot.Capture(root);
        RuntimeAnimatorController savedController = animator.runtimeAnimatorController;
        try
        {
            // Park the clone on an isolated island so multi-view captures see nothing else.
            root.position = new Vector3(4096f, 4096f, 4096f);

            Stage("T-pose", 0.05f);
            ApplyTPose(animator);

            FarLodSkeleton skeleton = CaptureSkeleton(animator, root);
            if (skeleton.Count == 0)
            {
                LastFailureReason = "no humanoid bones resolved";
                Debug.LogWarning("Far avatar generation skipped: no humanoid bones resolved.");
                return null;
            }

            Stage("Snapshot geometry", 0.15f);
            SnapshotSoup soup = SnapshotGeometry(animator, root, skeleton);
            StageDetail($"{soup.Positions.Count} verts, {soup.Indices.Count / 3} tris across the avatar, {skeleton.Count} bones kept");
            if (soup.Indices.Count < 3)
            {
                LastFailureReason = "no triangle geometry found";
                Debug.LogWarning("Far avatar generation skipped: no triangle geometry found.");
                return null;
            }

            // Exterior-visibility cull before decimation: budget spent on skin under clothing,
            // linings and inner mouths is budget stolen from the silhouette.
            Stage("Visibility cull", 0.24f);
            int hiddenRemoved = BasisFarLodVisibilityCuller.RemoveHiddenTriangles(soup.Positions, soup.Indices, root, TargetTriangleCount, out byte[] vertexHiddenFlags);
            StageDetail(hiddenRemoved > 0
                ? $"{hiddenRemoved} exterior-invisible tris removed, {soup.Indices.Count / 3} remain"
                : "nothing removed");
            List<byte> hiddenFlags = new List<byte>(soup.Positions.Count);
            if (vertexHiddenFlags != null)
            {
                hiddenFlags.AddRange(vertexHiddenFlags);
            }
            else
            {
                for (int i = 0; i < soup.Positions.Count; i++)
                {
                    hiddenFlags.Add(0);
                }
            }

            // Part-id mask from the CULLED soup, before Simplify mutates it. Removed triangles
            // were never the nearest surface, so the per-pixel identity/depth image is
            // unchanged — the mask pass just renders cheaper.
            BasisFarLodAtlasBaker.BakeMask bakeMask = BuildBakeMask(skeleton, soup);

            Stage("Simplify", 0.3f);
            int soupTriangles = soup.Indices.Count / 3;
            BasisFarLodMeshSimplifier.Simplify(soup.Positions, soup.BoneA, soup.BoneB, soup.WeightA, hiddenFlags, soup.Indices, TargetTriangleCount);
            StageDetail($"{soupTriangles} → {soup.Indices.Count / 3} tris ({soup.Positions.Count} verts)");
            if (soup.Positions.Count == 0 || soup.Indices.Count < 3)
            {
                LastFailureReason = $"simplification left no geometry ({soup.Positions.Count} verts, {soup.Indices.Count / 3} tris)";
                Debug.LogWarning($"Far avatar generation skipped: {LastFailureReason}.");
                return null;
            }

            Stage("Unwrap", 0.5f);
            Mesh unwrapped = BuildUnwrappedMesh(soup, hiddenFlags, out byte[] boneA, out byte[] boneB, out byte[] weightA, out byte[] texelHidden);
            RepackChartsByImportance(unwrapped, boneA, texelHidden, skeleton);
            try
            {
                Vector3[] positions = unwrapped.vertices;
                Vector3[] normals = unwrapped.normals;
                Vector2[] uv = unwrapped.uv;
                int[] indices = unwrapped.triangles;
                if (positions.Length == 0 || positions.Length > BasisFarLodPayload.MaxVertices || indices.Length == 0)
                {
                    LastFailureReason = $"decimated mesh out of range ({positions.Length} verts)";
                    Debug.LogWarning($"Far avatar generation skipped: decimated mesh out of range ({positions.Length} verts).");
                    return null;
                }

                Stage("Bake atlas", 0.65f);
                BasisFarLodAtlasBaker.RegionOfInterest[] regions = BuildCaptureRegions(skeleton, positions, boneA, boneB, weightA);
                bakeMask.TexelVertexGroup = new byte[boneA.Length];
                for (int i = 0; i < boneA.Length; i++)
                {
                    bakeMask.TexelVertexGroup[i] = GroupOfBone(skeleton.Bones[boneA[i]]);
                }
                bakeMask.TexelHidden = texelHidden;
                // Captures must keep pace with the atlas or a big atlas just magnifies blur.
                int effectiveCaptureSize = Mathf.Max(CaptureSize, AtlasSize);
                BasisFarLodPayload.FarLodTexture[] textures = BasisFarLodAtlasBaker.Bake(
                    root, unwrapped, positions, normals, uv, indices, AtlasSize, effectiveCaptureSize, regions, bakeMask);
                if (textures == null || textures.Length == 0)
                {
                    if (string.IsNullOrEmpty(LastFailureReason))
                    {
                        LastFailureReason = "atlas bake failed (no specific reason recorded)";
                    }
                    Debug.LogWarning("Far avatar generation skipped: atlas bake failed.");
                    return null;
                }
                StageDetail($"{AtlasSize}px atlas, {textures.Length} compressed payload(s)");

                Stage("Serialize", 0.95f);
                BasisFarLodPayload payload = AssemblePayload(avatar, root, skeleton, positions, normals, uv, indices, boneA, boneB, weightA, textures);
                double elapsed = EditorApplication.timeSinceStartup - startTime;
                Debug.Log($"Far avatar generated: {indices.Length / 3} triangles, {positions.Length} vertices, {skeleton.Count} bones, {AtlasSize}px atlas, {elapsed:0.00}s.");
                return payload;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(unwrapped);
            }
        }
        finally
        {
            double endTime = EditorApplication.timeSinceStartup;
            CloseStage(endTime);
            if (ActiveReport != null)
            {
                ActiveReport.TotalSeconds = endTime - startTime;
            }
            EditorUtility.ClearProgressBar();
            animator.runtimeAnimatorController = savedController;
            poseSnapshot.Restore();
        }
    }

    public static void ApplyTPose(Animator animator)
    {
        RuntimeAnimatorController tpose = LoadTposeController();
        if (tpose != null)
        {
            animator.runtimeAnimatorController = tpose;
            // Edit-mode animators are usually uninitialized; Rebind is what makes Update
            // actually evaluate and write the pose outside play mode.
            animator.Rebind();
            animator.Update(0f);
            animator.Update(0.02f);
            return;
        }

        // The runtime computes bone deltas against Animated TPose's clip, and BasisTPose.anim
        // is NOT muscle-zero — a payload baked from this fallback WILL desync limb bends.
        Debug.LogError("[FarAvatar] Animated TPose.controller not found in this project — falling back to muscle-zero T-pose. Runtime deltas are computed against that controller's clip, so far avatar limb bends will not match the real avatar. Make sure the SDK's Animator folder is present.");
        HumanPoseHandler handler = new HumanPoseHandler(animator.avatar, animator.transform);
        try
        {
            HumanPose pose = new HumanPose();
            handler.GetHumanPose(ref pose);
            for (int i = 0; i < pose.muscles.Length; i++)
            {
                pose.muscles[i] = 0f;
            }
            handler.SetHumanPose(ref pose);
        }
        finally
        {
            handler.Dispose();
        }
    }

    /// <summary>
    /// Per-core-bone T-pose local rotations in the avatar's ACTUAL hierarchy (relative to each
    /// bone's real transform parent — the sender-side frame the wire deltas are computed
    /// against). Used by the far avatar tester to reproduce the runtime's
    /// `rest * delta` composition. Momentarily T-poses the avatar and restores it.
    /// </summary>
    public static Dictionary<HumanBodyBones, Quaternion> CaptureActualTposeLocals(Animator animator)
    {
        Dictionary<HumanBodyBones, Quaternion> locals = new Dictionary<HumanBodyBones, Quaternion>(CoreBones.Length);
        TransformPoseSnapshot snapshot = TransformPoseSnapshot.Capture(animator.transform);
        RuntimeAnimatorController savedController = animator.runtimeAnimatorController;
        try
        {
            ApplyTPose(animator);
            for (int i = 0; i < CoreBones.Length; i++)
            {
                Transform bone = animator.GetBoneTransform(CoreBones[i]);
                if (bone != null)
                {
                    locals[CoreBones[i]] = bone.localRotation;
                }
            }
        }
        finally
        {
            animator.runtimeAnimatorController = savedController;
            snapshot.Restore();
        }
        return locals;
    }

    private static FarLodSkeleton CaptureSkeleton(Animator animator, Transform root)
    {
        FarLodSkeleton skeleton = new FarLodSkeleton();
        Matrix4x4 rootWorldToLocal = root.worldToLocalMatrix;
        Quaternion rootRotationInverse = Quaternion.Inverse(root.rotation);

        Dictionary<HumanBodyBones, int> boneToIndex = new Dictionary<HumanBodyBones, int>();
        for (int i = 0; i < CoreBones.Length; i++)
        {
            HumanBodyBones bone = CoreBones[i];
            Transform boneTransform = animator.GetBoneTransform(bone);
            if (boneTransform == null)
            {
                continue;
            }

            // Parent by ACTUAL transform ancestry first: the canonical humanoid chain lies on
            // rigs whose mapped bones aren't each other's transform ancestors, and a wrong
            // parent makes the far avatar bone follow rotations the real bone never sees.
            int parentIndex = -1;
            Transform ancestor = boneTransform.parent;
            while (ancestor != null)
            {
                if (skeleton.TransformToBone.TryGetValue(ancestor, out int found))
                {
                    parentIndex = found;
                    break;
                }
                if (ancestor == root)
                {
                    break;
                }
                ancestor = ancestor.parent;
            }
            if (parentIndex < 0)
            {
                HumanBodyBones[] chain = ParentChain(bone);
                for (int c = 0; c < chain.Length; c++)
                {
                    if (boneToIndex.TryGetValue(chain[c], out int found))
                    {
                        Debug.LogWarning($"[FarAvatar] {bone} is not a transform descendant of any captured core bone — falling back to canonical parent {chain[c]}; its far avatar rotations may not track the real avatar.");
                        parentIndex = found;
                        break;
                    }
                }
            }
            if (parentIndex < 0 && bone != HumanBodyBones.Hips)
            {
                parentIndex = 0;
            }

            Vector3 rootPos = rootWorldToLocal.MultiplyPoint3x4(boneTransform.position);
            Quaternion rootRot = rootRotationInverse * boneTransform.rotation;

            int index = skeleton.Count;
            skeleton.Bones.Add(bone);
            skeleton.ParentIndex.Add(parentIndex);
            skeleton.Transforms.Add(boneTransform);
            skeleton.RootSpacePosition.Add(rootPos);
            skeleton.RootSpaceRotation.Add(rootRot);
            if (parentIndex < 0)
            {
                skeleton.RestLocalPosition.Add(rootPos);
                skeleton.RestLocalRotation.Add(rootRot);
            }
            else
            {
                Quaternion parentInverse = Quaternion.Inverse(skeleton.RootSpaceRotation[parentIndex]);
                skeleton.RestLocalPosition.Add(parentInverse * (rootPos - skeleton.RootSpacePosition[parentIndex]));
                skeleton.RestLocalRotation.Add(parentInverse * rootRot);
            }
            boneToIndex[bone] = index;
            skeleton.TransformToBone[boneTransform] = index;
        }
        return skeleton;
    }

    public sealed class SnapshotSoup
    {
        public readonly List<Vector3> Positions = new List<Vector3>(65536);
        public readonly List<byte> BoneA = new List<byte>(65536);
        public readonly List<byte> BoneB = new List<byte>(65536);
        public readonly List<byte> WeightA = new List<byte>(65536);
        public readonly List<int> Indices = new List<int>(196608);
    }

    private static SnapshotSoup SnapshotGeometry(Animator animator, Transform root, FarLodSkeleton skeleton)
    {
        SnapshotSoup soup = new SnapshotSoup();
        Matrix4x4 rootWorldToLocal = root.worldToLocalMatrix;
        Dictionary<Transform, int> ancestorCache = new Dictionary<Transform, int>();
        float[] weightScratch = new float[skeleton.Count];
        int[] touchedScratch = new int[8];

        Renderer[] renderers = animator.GetComponentsInChildren<Renderer>(false);
        for (int r = 0; r < renderers.Length; r++)
        {
            Renderer renderer = renderers[r];
            if (renderer == null || !renderer.enabled || renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
            {
                continue;
            }

            if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
            {
                AppendSkinnedMesh(soup, skinned, root, rootWorldToLocal, skeleton, ancestorCache, weightScratch, touchedScratch);
            }
            else if (renderer is MeshRenderer meshRenderer)
            {
                MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    AppendRigidMesh(soup, filter.sharedMesh, meshRenderer.transform, root, rootWorldToLocal, skeleton, ancestorCache);
                }
            }
        }
        return soup;
    }

    private static int ResolveAncestorBone(Transform transform, Transform root, FarLodSkeleton skeleton, Dictionary<Transform, int> cache)
    {
        if (transform == null)
        {
            return 0;
        }
        if (cache.TryGetValue(transform, out int cached))
        {
            return cached;
        }
        int result = 0;
        Transform current = transform;
        while (current != null)
        {
            if (skeleton.TransformToBone.TryGetValue(current, out int boneIndex))
            {
                result = boneIndex;
                break;
            }
            if (current == root)
            {
                break;
            }
            current = current.parent;
        }
        cache[transform] = result;
        return result;
    }

    private static void AppendSkinnedMesh(SnapshotSoup soup, SkinnedMeshRenderer skinned, Transform root, Matrix4x4 rootWorldToLocal,
        FarLodSkeleton skeleton, Dictionary<Transform, int> ancestorCache, float[] weightScratch, int[] touchedScratch)
    {
        Mesh mesh = skinned.sharedMesh;
        Transform[] bones = skinned.bones;
        if (bones == null || bones.Length == 0)
        {
            AppendRigidMesh(soup, mesh, skinned.rootBone != null ? skinned.rootBone : skinned.transform, root, rootWorldToLocal, skeleton, ancestorCache);
            return;
        }

        Vector3[] vertices = mesh.vertices;
        ApplyActiveBlendShapes(skinned, mesh, vertices);

        Matrix4x4[] bindposes = mesh.bindposes;
        int boneCount = Mathf.Min(bones.Length, bindposes.Length);
        Matrix4x4[] skinMatrices = new Matrix4x4[boneCount];
        int[] boneToFarLodBone = new int[boneCount];
        for (int b = 0; b < boneCount; b++)
        {
            skinMatrices[b] = bones[b] != null ? bones[b].localToWorldMatrix * bindposes[b] : Matrix4x4.identity;
            boneToFarLodBone[b] = ResolveAncestorBone(bones[b] != null ? bones[b] : skinned.transform, root, skeleton, ancestorCache);
        }

        var bonesPerVertex = mesh.GetBonesPerVertex();
        var allWeights = mesh.GetAllBoneWeights();
        if (bonesPerVertex.Length != vertices.Length)
        {
            AppendRigidMesh(soup, mesh, skinned.transform, root, rootWorldToLocal, skeleton, ancestorCache);
            return;
        }

        int vertexBase = soup.Positions.Count;
        int weightCursor = 0;
        for (int v = 0; v < vertices.Length; v++)
        {
            int influenceCount = bonesPerVertex[v];
            Vector3 world = Vector3.zero;
            int touchedCount = 0;
            float totalWeight = 0f;
            for (int i = 0; i < influenceCount; i++)
            {
                BoneWeight1 weight = allWeights[weightCursor++];
                if (weight.boneIndex < 0 || weight.boneIndex >= boneCount || weight.weight <= 0f)
                {
                    continue;
                }
                world += skinMatrices[weight.boneIndex].MultiplyPoint3x4(vertices[v]) * weight.weight;
                totalWeight += weight.weight;

                int farLodBone = boneToFarLodBone[weight.boneIndex];
                if (weightScratch[farLodBone] == 0f && touchedCount < touchedScratch.Length)
                {
                    touchedScratch[touchedCount++] = farLodBone;
                }
                weightScratch[farLodBone] += weight.weight;
            }

            if (totalWeight <= 1e-6f)
            {
                world = skinned.transform.localToWorldMatrix.MultiplyPoint3x4(vertices[v]);
                weightScratch[boneToFarLodBone.Length > 0 ? boneToFarLodBone[0] : 0] = 1f;
                if (touchedCount == 0 && touchedScratch.Length > 0)
                {
                    touchedScratch[touchedCount++] = boneToFarLodBone.Length > 0 ? boneToFarLodBone[0] : 0;
                }
            }
            else if (totalWeight < 0.999f)
            {
                world /= totalWeight;
            }

            // Keep the two heaviest collapsed influences.
            int bestBone = 0, secondBone = 0;
            float bestWeight = -1f, secondWeight = -1f;
            for (int t = 0; t < touchedCount; t++)
            {
                int bone = touchedScratch[t];
                float w = weightScratch[bone];
                if (w > bestWeight)
                {
                    secondBone = bestBone; secondWeight = bestWeight;
                    bestBone = bone; bestWeight = w;
                }
                else if (w > secondWeight)
                {
                    secondBone = bone; secondWeight = w;
                }
                weightScratch[bone] = 0f;
            }
            if (bestWeight <= 0f)
            {
                bestBone = 0; bestWeight = 1f; secondBone = 0; secondWeight = 0f;
            }
            if (secondWeight < 0f)
            {
                secondBone = bestBone; secondWeight = 0f;
            }

            float normalized = bestWeight / (bestWeight + secondWeight);
            soup.Positions.Add(rootWorldToLocal.MultiplyPoint3x4(world));
            soup.BoneA.Add((byte)bestBone);
            soup.BoneB.Add((byte)secondBone);
            soup.WeightA.Add((byte)Mathf.Clamp(Mathf.RoundToInt(normalized * 255f), 0, 255));
        }

        AppendTriangles(soup, mesh, vertexBase);
    }

    private static void ApplyActiveBlendShapes(SkinnedMeshRenderer skinned, Mesh mesh, Vector3[] vertices)
    {
        int shapeCount = mesh.blendShapeCount;
        if (shapeCount == 0)
        {
            return;
        }
        Vector3[] deltaScratch = null;
        for (int s = 0; s < shapeCount; s++)
        {
            float weight = skinned.GetBlendShapeWeight(s);
            if (Mathf.Abs(weight) < 0.001f)
            {
                continue;
            }
            deltaScratch ??= new Vector3[vertices.Length];
            int frame = mesh.GetBlendShapeFrameCount(s) - 1;
            mesh.GetBlendShapeFrameVertices(s, frame, deltaScratch, null, null);
            float frameWeight = mesh.GetBlendShapeFrameWeight(s, frame);
            float amount = frameWeight > 0f ? weight / frameWeight : weight * 0.01f;
            for (int v = 0; v < vertices.Length; v++)
            {
                vertices[v] += deltaScratch[v] * amount;
            }
        }
    }

    private static void AppendRigidMesh(SnapshotSoup soup, Mesh mesh, Transform meshTransform, Transform root, Matrix4x4 rootWorldToLocal,
        FarLodSkeleton skeleton, Dictionary<Transform, int> ancestorCache)
    {
        int bone = ResolveAncestorBone(meshTransform, root, skeleton, ancestorCache);
        Matrix4x4 toRoot = rootWorldToLocal * meshTransform.localToWorldMatrix;
        Vector3[] vertices = mesh.vertices;
        int vertexBase = soup.Positions.Count;
        for (int v = 0; v < vertices.Length; v++)
        {
            soup.Positions.Add(toRoot.MultiplyPoint3x4(vertices[v]));
            soup.BoneA.Add((byte)bone);
            soup.BoneB.Add((byte)bone);
            soup.WeightA.Add(255);
        }
        AppendTriangles(soup, mesh, vertexBase);
    }

    private static void AppendTriangles(SnapshotSoup soup, Mesh mesh, int vertexBase)
    {
        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            if (mesh.GetTopology(sub) != MeshTopology.Triangles)
            {
                continue;
            }
            int[] indices = mesh.GetTriangles(sub);
            for (int i = 0; i < indices.Length; i++)
            {
                soup.Indices.Add(vertexBase + indices[i]);
            }
        }
    }

    /// <summary>
    /// Coarse body group per humanoid bone, used by the part-id mask: a texel only samples
    /// capture pixels belonging to its own group, so side views can't paint torso onto arms.
    /// </summary>
    public static byte GroupOfBone(HumanBodyBones bone)
    {
        switch (bone)
        {
            case HumanBodyBones.Head: return 1;
            case HumanBodyBones.LeftShoulder:
            case HumanBodyBones.LeftUpperArm:
            case HumanBodyBones.LeftLowerArm:
            case HumanBodyBones.LeftHand:
                return 2;
            case HumanBodyBones.RightShoulder:
            case HumanBodyBones.RightUpperArm:
            case HumanBodyBones.RightLowerArm:
            case HumanBodyBones.RightHand:
                return 3;
            case HumanBodyBones.LeftUpperLeg:
            case HumanBodyBones.LeftLowerLeg:
            case HumanBodyBones.LeftFoot:
                return 4;
            case HumanBodyBones.RightUpperLeg:
            case HumanBodyBones.RightLowerLeg:
            case HumanBodyBones.RightFoot:
                return 5;
            default: return 0; // torso: hips, spine, chest, upper chest, neck
        }
    }

    private static BasisFarLodAtlasBaker.BakeMask BuildBakeMask(FarLodSkeleton skeleton, SnapshotSoup soup)
    {
        int vertexCount = soup.Positions.Count;
        BasisFarLodAtlasBaker.BakeMask mask = new BasisFarLodAtlasBaker.BakeMask
        {
            Positions = soup.Positions.ToArray(),
            Indices = soup.Indices.ToArray(),
            Colors = new Color32[vertexCount],
        };
        for (int i = 0; i < vertexCount; i++)
        {
            byte group = GroupOfBone(skeleton.Bones[soup.BoneA[i]]);
            byte encoded = BasisFarLodAtlasBaker.EncodeGroup(group);
            mask.Colors[i] = new Color32(encoded, encoded, encoded, 255);
        }
        return mask;
    }

    /// <summary>
    /// Close-up capture regions for small, detail-dense parts. In a whole-body frame a hand is
    /// a few dozen pixels — these bounds get their own tightly-framed captures in the baker.
    /// </summary>
    private static BasisFarLodAtlasBaker.RegionOfInterest[] BuildCaptureRegions(FarLodSkeleton skeleton,
        Vector3[] positions, byte[] boneA, byte[] boneB, byte[] weightA)
    {
        HumanBodyBones[] targets =
        {
            HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
            HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
            HumanBodyBones.Head,
        };
        List<BasisFarLodAtlasBaker.RegionOfInterest> regions = new List<BasisFarLodAtlasBaker.RegionOfInterest>(targets.Length);
        for (int t = 0; t < targets.Length; t++)
        {
            int boneIndex = skeleton.Bones.IndexOf(targets[t]);
            if (boneIndex < 0)
            {
                continue;
            }
            bool hasBounds = false;
            Bounds bounds = default;
            for (int i = 0; i < positions.Length; i++)
            {
                bool influencedByA = boneA[i] == boneIndex && weightA[i] > 32;
                bool influencedByB = boneB[i] == boneIndex && weightA[i] < 223;
                if (!influencedByA && !influencedByB)
                {
                    continue;
                }
                if (!hasBounds)
                {
                    bounds = new Bounds(positions[i], Vector3.zero);
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(positions[i]);
                }
            }
            if (hasBounds && bounds.size.magnitude > 0.01f)
            {
                regions.Add(new BasisFarLodAtlasBaker.RegionOfInterest
                {
                    Name = targets[t].ToString(),
                    RootBounds = bounds,
                });
            }
        }
        return regions.ToArray();
    }

    private static Mesh BuildUnwrappedMesh(SnapshotSoup soup, List<byte> hiddenFlags, out byte[] boneA, out byte[] boneB, out byte[] weightA, out byte[] hidden)
    {
        Mesh mesh = new Mesh
        {
            hideFlags = HideFlags.HideAndDontSave,
            indexFormat = soup.Positions.Count > 65534 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
        };
        mesh.SetVertices(soup.Positions);
        mesh.SetTriangles(soup.Indices, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // The unwrap can split vertices along UV seams. Positions were welded during
        // simplification, so an exact-position lookup maps every split copy back to its
        // source vertex for the bone attributes.
        Dictionary<Vector3, int> positionToSource = new Dictionary<Vector3, int>(soup.Positions.Count);
        for (int i = 0; i < soup.Positions.Count; i++)
        {
            positionToSource[soup.Positions[i]] = i;
        }

        UnwrapParam.SetDefaults(out UnwrapParam unwrapParam);
        // Fewer, larger charts: default unwrap settings shatter a high-budget decimated mesh
        // into hundreds of tiny islands, and bilinear/mip sampling across their borders reads
        // as texture misalignment. Distortion inside big charts is harmless here — the texture
        // is projected onto the final UVs, not authored against them.
        unwrapParam.hardAngle = 180f;
        unwrapParam.angleError = 0.25f;
        unwrapParam.areaError = 0.35f;
        unwrapParam.packMargin = 4f / AtlasSize;
        Unwrapping.GenerateSecondaryUVSet(mesh, unwrapParam);

        Vector3[] vertices = mesh.vertices;
        Vector2[] uv2 = mesh.uv2;
        boneA = new byte[vertices.Length];
        boneB = new byte[vertices.Length];
        weightA = new byte[vertices.Length];
        hidden = new byte[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            int source = positionToSource.TryGetValue(vertices[i], out int index) ? index : Mathf.Min(i, soup.Positions.Count - 1);
            boneA[i] = soup.BoneA[source];
            boneB[i] = soup.BoneB[source];
            weightA[i] = soup.WeightA[source];
            hidden[i] = hiddenFlags[source];
        }

        mesh.uv = uv2;
        mesh.RecalculateNormals();
        SmoothNormalsAcrossSeams(mesh);
        return mesh;
    }

    private static float ImportanceOfBone(HumanBodyBones bone)
    {
        switch (bone)
        {
            case HumanBodyBones.Head: return 2f;
            case HumanBodyBones.LeftHand:
            case HumanBodyBones.RightHand:
                return 1.7f;
            case HumanBodyBones.LeftFoot:
            case HumanBodyBones.RightFoot:
                return 1.4f;
            default: return 1f;
        }
    }

    /// <summary>
    /// GenerateSecondaryUVSet allocates atlas area by surface area, so a face gets the same
    /// texel density as a shoulder blade. Rescale each UV island by the importance of the body
    /// part it belongs to and shelf-pack everything back into [0,1] — faces and hands stay
    /// legible at the same atlas size.
    /// </summary>
    private static void RepackChartsByImportance(Mesh mesh, byte[] boneA, byte[] hidden, FarLodSkeleton skeleton)
    {
        Vector2[] uv = mesh.uv;
        int[] triangles = mesh.triangles;
        int vertexCount = uv.Length;
        if (vertexCount == 0 || triangles.Length == 0)
        {
            return;
        }

        // Union-find over triangle connectivity: post-unwrap, index connectivity == chart.
        int[] parent = new int[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            parent[i] = i;
        }
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            int a = Find(triangles[t]);
            int b = Find(triangles[t + 1]);
            int c = Find(triangles[t + 2]);
            if (b != a) parent[b] = a;
            if (c != a) parent[c] = a;
        }

        Dictionary<int, ChartIsland> islands = new Dictionary<int, ChartIsland>(64);
        for (int i = 0; i < vertexCount; i++)
        {
            int root = Find(i);
            if (!islands.TryGetValue(root, out ChartIsland island))
            {
                island = new ChartIsland { Min = uv[i], Max = uv[i], Importance = 1f, AllHidden = true };
                islands[root] = island;
            }
            island.Min = Vector2.Min(island.Min, uv[i]);
            island.Max = Vector2.Max(island.Max, uv[i]);
            island.Importance = Mathf.Max(island.Importance, ImportanceOfBone(skeleton.Bones[boneA[i]]));
            island.AllHidden &= hidden != null && hidden[i] != 0;
            island.Vertices.Add(i);
        }

        List<ChartIsland> sorted = new List<ChartIsland>(islands.Values);
        for (int s = 0; s < sorted.Count; s++)
        {
            // Charts made only of exterior-invisible remnants (hem seals) never show a texel —
            // shrink them and hand the area to surfaces that exist.
            if (sorted[s].AllHidden)
            {
                sorted[s].Importance *= 0.5f;
            }
        }
        float margin = 4f / AtlasSize;

        // Shrink the global scale until everything shelf-packs into [0,1].
        float scaledArea = 0f;
        foreach (ChartIsland island in sorted)
        {
            Vector2 size = island.Max - island.Min;
            scaledArea += (size.x * island.Importance + margin * 2f) * (size.y * island.Importance + margin * 2f);
        }
        float globalScale = Mathf.Min(1.5f, Mathf.Sqrt(0.82f / Mathf.Max(scaledArea, 1e-6f)));
        for (int attempt = 0; attempt < 48; attempt++)
        {
            if (TryShelfPack(sorted, globalScale, margin))
            {
                for (int s = 0; s < sorted.Count; s++)
                {
                    ChartIsland island = sorted[s];
                    float scale = island.Importance * globalScale;
                    for (int v = 0; v < island.Vertices.Count; v++)
                    {
                        int index = island.Vertices[v];
                        uv[index] = island.PackedOrigin + (uv[index] - island.Min) * scale;
                    }
                }
                mesh.uv = uv;
                return;
            }
            globalScale *= 0.93f;
        }
        Debug.LogWarning("[FarAvatar] Chart repacking failed to converge — keeping the default unwrap packing.");
    }

    private sealed class ChartIsland
    {
        public Vector2 Min;
        public Vector2 Max;
        public float Importance;
        public bool AllHidden;
        public Vector2 PackedOrigin;
        public readonly List<int> Vertices = new List<int>(64);
    }

    private static bool TryShelfPack(List<ChartIsland> islands, float globalScale, float margin)
    {
        islands.Sort((a, b) =>
        {
            float heightA = (a.Max.y - a.Min.y) * a.Importance;
            float heightB = (b.Max.y - b.Min.y) * b.Importance;
            return heightB.CompareTo(heightA);
        });

        float cursorX = 0f;
        float cursorY = 0f;
        float shelfHeight = 0f;
        for (int i = 0; i < islands.Count; i++)
        {
            ChartIsland island = islands[i];
            float scale = island.Importance * globalScale;
            float width = (island.Max.x - island.Min.x) * scale + margin * 2f;
            float height = (island.Max.y - island.Min.y) * scale + margin * 2f;
            if (width > 1f || height > 1f)
            {
                return false;
            }
            if (cursorX + width > 1f)
            {
                cursorY += shelfHeight;
                cursorX = 0f;
                shelfHeight = 0f;
            }
            if (cursorY + height > 1f)
            {
                return false;
            }
            island.PackedOrigin = new Vector2(cursorX + margin, cursorY + margin);
            cursorX += width;
            shelfHeight = Mathf.Max(shelfHeight, height);
        }
        return true;
    }

    /// <summary>
    /// RecalculateNormals treats the UV-split copies along chart seams as separate vertices,
    /// hardening every seam into a visible facet line. Average the normals of all vertices
    /// sharing a position so the low-poly surface shades smooth.
    /// </summary>
    private static void SmoothNormalsAcrossSeams(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        Vector3[] normals = mesh.normals;
        Dictionary<Vector3, Vector3> accumulated = new Dictionary<Vector3, Vector3>(vertices.Length);
        for (int i = 0; i < vertices.Length; i++)
        {
            accumulated.TryGetValue(vertices[i], out Vector3 sum);
            accumulated[vertices[i]] = sum + normals[i];
        }
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 smoothed = accumulated[vertices[i]];
            float magnitude = smoothed.magnitude;
            if (magnitude > 1e-6f)
            {
                normals[i] = smoothed / magnitude;
            }
        }
        mesh.normals = normals;
    }

    private static BasisFarLodPayload AssemblePayload(BasisAvatar avatar, Transform root, FarLodSkeleton skeleton,
        Vector3[] positions, Vector3[] normals, Vector2[] uv, int[] indices,
        byte[] boneA, byte[] boneB, byte[] weightA, BasisFarLodPayload.FarLodTexture[] textures)
    {
        BasisFarLodPayload payload = new BasisFarLodPayload
        {
            AvatarEyePosition = avatar.AvatarEyePosition,
            AvatarMouthPosition = avatar.AvatarMouthPosition,
            AuthoredRootScale = root.localScale,
            Textures = textures,
            MinBrightness = BasisFarLodAtlasBaker.LastMinBrightness,
            MaxBrightness = BasisFarLodAtlasBaker.LastMaxBrightness,
        };

        int boneCount = skeleton.Count;
        payload.BoneHumanBodyBone = new byte[boneCount];
        payload.BoneParentIndex = new byte[boneCount];
        payload.BoneRestLocalPosition = new Vector3[boneCount];
        payload.BoneRestLocalRotation = new Quaternion[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            payload.BoneHumanBodyBone[i] = (byte)skeleton.Bones[i];
            payload.BoneParentIndex[i] = skeleton.ParentIndex[i] < 0 ? (byte)0xFF : (byte)skeleton.ParentIndex[i];
            payload.BoneRestLocalPosition[i] = skeleton.RestLocalPosition[i];
            payload.BoneRestLocalRotation[i] = skeleton.RestLocalRotation[i];
        }

        int headIndex = skeleton.Bones.IndexOf(HumanBodyBones.Head);
        int hipsIndex = skeleton.Bones.IndexOf(HumanBodyBones.Hips);
        if (headIndex >= 0)
        {
            payload.TposeHeadFromRootPosition = skeleton.RootSpacePosition[headIndex];
            payload.TposeHeadFromRootRotation = skeleton.RootSpaceRotation[headIndex];
        }
        if (hipsIndex >= 0)
        {
            payload.TposeHipsFromRootPosition = skeleton.RootSpacePosition[hipsIndex];
            payload.TposeHipsFromRootRotation = skeleton.RootSpaceRotation[hipsIndex];
        }

        Vector3 boundsMin = positions[0];
        Vector3 boundsMax = positions[0];
        for (int i = 1; i < positions.Length; i++)
        {
            boundsMin = Vector3.Min(boundsMin, positions[i]);
            boundsMax = Vector3.Max(boundsMax, positions[i]);
        }
        Vector3 range = boundsMax - boundsMin;
        if (range.x < 1e-4f) { boundsMax.x += 1e-4f; range.x = 1e-4f; }
        if (range.y < 1e-4f) { boundsMax.y += 1e-4f; range.y = 1e-4f; }
        if (range.z < 1e-4f) { boundsMax.z += 1e-4f; range.z = 1e-4f; }
        payload.PositionBoundsMin = boundsMin;
        payload.PositionBoundsMax = boundsMax;

        // Renderer bounds in hips space, padded for posing (rootBone = hips at runtime).
        int hips = Mathf.Max(hipsIndex, 0);
        Quaternion hipsInverse = Quaternion.Inverse(skeleton.RootSpaceRotation[hips]);
        Vector3 hipsPos = skeleton.RootSpacePosition[hips];
        Vector3 localMin = Vector3.positiveInfinity;
        Vector3 localMax = Vector3.negativeInfinity;
        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 local = hipsInverse * (positions[i] - hipsPos);
            localMin = Vector3.Min(localMin, local);
            localMax = Vector3.Max(localMax, local);
        }
        payload.LocalBoundsCenter = (localMin + localMax) * 0.5f;
        payload.LocalBoundsExtents = (localMax - localMin) * 0.5f * 1.5f;

        int vertexCount = positions.Length;
        payload.VertexCount = vertexCount;
        payload.PositionsQ = new ushort[vertexCount * 3];
        payload.NormalsOct = new ushort[vertexCount];
        payload.UvQ = new ushort[vertexCount * 2];
        payload.BoneIndexA = boneA;
        payload.BoneIndexB = boneB;
        payload.BoneWeightA = weightA;
        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 p = positions[i];
            payload.PositionsQ[i * 3] = BasisFarLodPayload.QuantizeUnorm((p.x - boundsMin.x) / range.x);
            payload.PositionsQ[i * 3 + 1] = BasisFarLodPayload.QuantizeUnorm((p.y - boundsMin.y) / range.y);
            payload.PositionsQ[i * 3 + 2] = BasisFarLodPayload.QuantizeUnorm((p.z - boundsMin.z) / range.z);
            payload.NormalsOct[i] = BasisFarLodPayload.OctEncodeNormal(i < normals.Length ? normals[i] : Vector3.up);
            Vector2 texcoord = i < uv.Length ? uv[i] : Vector2.zero;
            payload.UvQ[i * 2] = BasisFarLodPayload.QuantizeUnorm(texcoord.x);
            payload.UvQ[i * 2 + 1] = BasisFarLodPayload.QuantizeUnorm(texcoord.y);
        }

        payload.Indices = new ushort[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            payload.Indices[i] = (ushort)indices[i];
        }
        return payload;
    }

    /// <summary>Records local TRS for a whole hierarchy and restores it after generation.</summary>
    private sealed class TransformPoseSnapshot
    {
        private Transform[] _transforms;
        private Vector3[] _localPositions;
        private Quaternion[] _localRotations;
        private Vector3[] _localScales;

        public static TransformPoseSnapshot Capture(Transform root)
        {
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            TransformPoseSnapshot snapshot = new TransformPoseSnapshot
            {
                _transforms = transforms,
                _localPositions = new Vector3[transforms.Length],
                _localRotations = new Quaternion[transforms.Length],
                _localScales = new Vector3[transforms.Length],
            };
            for (int i = 0; i < transforms.Length; i++)
            {
                transforms[i].GetLocalPositionAndRotation(out snapshot._localPositions[i], out snapshot._localRotations[i]);
                snapshot._localScales[i] = transforms[i].localScale;
            }
            return snapshot;
        }

        public void Restore()
        {
            for (int i = 0; i < _transforms.Length; i++)
            {
                Transform transform = _transforms[i];
                if (transform == null)
                {
                    continue;
                }
                transform.SetLocalPositionAndRotation(_localPositions[i], _localRotations[i]);
                transform.localScale = _localScales[i];
            }
        }
    }
}
