using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
namespace Basis.Scripts.Avatar
{
    public static class BasisAvatarSole
    {
        public const float FootWeightThreshold = 0.5f, MaxOffsetOfEyeHeight = 0.1f;
        public static bool TryMeasure(Transform root, Transform leftFoot, Transform rightFoot, float scale, float eyeHeight, out float soleHeight)
        {
            soleHeight = 0f;
            if (root == null || (leftFoot == null && rightFoot == null) || !(scale > 1e-6f))
            {
                return false;
            }
            try
            {
                var feet = new HashSet<Transform>();
                AddSubtree(feet, leftFoot);
                AddSubtree(feet, rightFoot);
                root.GetPositionAndRotation(out Vector3 rootPos, out Quaternion rootRot);
                Vector3 up = rootRot * Vector3.up;
                float lowest = float.PositiveInfinity;
                var measured = new HashSet<(Mesh, Transform)>();
                foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    if (renderer.enabled && measured.Add((renderer.sharedMesh, renderer.rootBone)))
                    {
                        lowest = Mathf.Min(lowest, LowestSkinnedFootPoint(renderer, feet, rootPos, up));
                    }
                }
                foreach (Transform part in feet)
                {
                    if (part.gameObject.activeInHierarchy && part.TryGetComponent(out MeshFilter filter) && part.TryGetComponent(out MeshRenderer renderer) && renderer.enabled)
                    {
                        lowest = Mathf.Min(lowest, LowestRigidPoint(filter.sharedMesh, part.localToWorldMatrix, rootPos, up));
                    }
                }
                float ankle = Mathf.Min(leftFoot != null ? Vector3.Dot(leftFoot.position - rootPos, up) : float.PositiveInfinity, rightFoot != null ? Vector3.Dot(rightFoot.position - rootPos, up) : float.PositiveInfinity);
                if (!(lowest < ankle) || !(Mathf.Abs(lowest) <= MaxOffsetOfEyeHeight * eyeHeight * scale))
                {
                    return false;
                }
                soleHeight = lowest / scale;
                return true;
            }
            catch (System.Exception exception)
            {
                BasisDebug.LogWarning($"Avatar sole measurement skipped: {exception.Message}", BasisDebug.LogTag.Avatar);
                soleHeight = 0f;
                return false;
            }
        }
        static void AddSubtree(HashSet<Transform> set, Transform foot)
        {
            if (foot == null)
            {
                return;
            }
            foreach (Transform part in foot.GetComponentsInChildren<Transform>(true))
            {
                set.Add(part);
            }
        }
        static float LowestSkinnedFootPoint(SkinnedMeshRenderer renderer, HashSet<Transform> feet, Vector3 rootPos, Vector3 up)
        {
            Mesh mesh = renderer.sharedMesh;
            Transform[] bones = renderer.bones;
            if (mesh == null || !mesh.isReadable || bones == null || bones.Length == 0)
            {
                return float.PositiveInfinity;
            }
            Matrix4x4[] bindposes = mesh.bindposes;
            int boneCount = Mathf.Min(bones.Length, bindposes.Length);
            var isFoot = new bool[boneCount];
            var isValid = new bool[boneCount];
            var skin = new Matrix4x4[boneCount];
            bool any = false;
            for (int index = 0; index < boneCount; index++)
            {
                Transform bone = bones[index];
                isValid[index] = bone != null;
                if (isValid[index])
                {
                    isFoot[index] = feet.Contains(bone);
                    any |= isFoot[index];
                    skin[index] = bone.localToWorldMatrix * bindposes[index];
                }
            }
            if (!any)
            {
                return float.PositiveInfinity;
            }
            bool shaped = false;
            for (int shape = 0; shape < mesh.blendShapeCount && !shaped; shape++)
            {
                shaped = renderer.GetBlendShapeWeight(shape) != 0f;
            }
            Mesh baked = null;
            if (shaped)
            {
                baked = new Mesh();
                renderer.BakeMesh(baked, true);
            }
            Matrix4x4 bakedToWorld = renderer.transform.localToWorldMatrix;
            NativeArray<byte> bonesPerVertex = mesh.GetBonesPerVertex();
            NativeArray<BoneWeight1> weights = mesh.GetAllBoneWeights();
            float lowest = float.PositiveInfinity;
            try
            {
                using (Mesh.MeshDataArray data = Mesh.AcquireReadOnlyMeshData(shaped ? baked : mesh))
                {
                    var vertices = new NativeArray<Vector3>(data[0].vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    data[0].GetVertices(vertices);
                    int count = Mathf.Min(vertices.Length, bonesPerVertex.Length);
                    for (int vertex = 0, first = 0; vertex < count; first += bonesPerVertex[vertex], vertex++)
                    {
                        int influences = bonesPerVertex[vertex];
                        float footWeight = 0f;
                        bool usable = first + influences <= weights.Length;
                        for (int k = 0; usable && k < influences; k++)
                        {
                            BoneWeight1 weight = weights[first + k];
                            usable = (uint)weight.boneIndex < (uint)boneCount && isValid[weight.boneIndex];
                            footWeight += usable && isFoot[weight.boneIndex] ? weight.weight : 0f;
                        }
                        if (!usable || footWeight < FootWeightThreshold)
                        {
                            continue;
                        }
                        Vector3 point = Vector3.zero;
                        if (shaped)
                        {
                            point = bakedToWorld.MultiplyPoint3x4(vertices[vertex]);
                        }
                        else
                        {
                            for (int k = 0; k < influences; k++)
                            {
                                BoneWeight1 weight = weights[first + k];
                                point += skin[weight.boneIndex].MultiplyPoint3x4(vertices[vertex]) * weight.weight;
                            }
                        }
                        lowest = Mathf.Min(lowest, Vector3.Dot(point - rootPos, up));
                    }
                    vertices.Dispose();
                }
            }
            finally
            {
                if (baked != null)
                {
                    Object.DestroyImmediate(baked);
                }
            }
            return lowest;
        }
        static float LowestRigidPoint(Mesh mesh, Matrix4x4 localToWorld, Vector3 rootPos, Vector3 up)
        {
            if (mesh == null || !mesh.isReadable)
            {
                return float.PositiveInfinity;
            }
            float lowest = float.PositiveInfinity;
            using (Mesh.MeshDataArray data = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                var vertices = new NativeArray<Vector3>(mesh.vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                data[0].GetVertices(vertices);
                for (int vertex = 0; vertex < vertices.Length; vertex++)
                {
                    lowest = Mathf.Min(lowest, Vector3.Dot(localToWorld.MultiplyPoint3x4(vertices[vertex]) - rootPos, up));
                }
                vertices.Dispose();
            }
            return lowest;
        }
    }
}
