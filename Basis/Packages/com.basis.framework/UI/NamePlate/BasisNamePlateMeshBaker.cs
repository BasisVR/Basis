using System.Collections.Generic;
using TMPro;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.Scripts.UI.NamePlate
{
    public interface IBasisNamePlateBakeTarget
    {
        string DisplayName { get; }
        MeshFilter MeshFilter { get; }
        MeshRenderer MeshRenderer { get; }
        string MeshName { get; }
        bool IsBakeTargetValid { get; }
        void OnBakeCompleted(Mesh mesh);
    }

    public static class BasisNamePlateMeshBaker
    {
        private static int maxBakesPerFrame = 2;
        private static float maxPlateHalfWidth = 40f;
        private static float roundEdges = 0.85f;
        private static int cornerVertexCount = 8;

        public static int MaxBakesPerFrame
        {
            get => maxBakesPerFrame;
            set => maxBakesPerFrame = Mathf.Max(1, value);
        }

        public static float MaxPlateHalfWidth
        {
            get => maxPlateHalfWidth;
            set => maxPlateHalfWidth = Mathf.Max(0.001f, value);
        }

        public static float RoundEdges
        {
            get => roundEdges;
            set => roundEdges = Mathf.Clamp01(value);
        }

        public static int CornerVertexCount
        {
            get => cornerVertexCount;
            set
            {
                int clamped = Mathf.Max(3, value);
                if (cornerVertexCount == clamped) return;
                cornerVertexCount = clamped;
                if (initialized)
                {
                    PrecomputeCornerData();
                }
            }
        }

        public static float zOffset = 0.06f;

        private const float BakeFontSize = 72f;
        private static readonly Matrix4x4 FlipX = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
        private static readonly Queue<IBasisNamePlateBakeTarget> bakeQueue = new Queue<IBasisNamePlateBakeTarget>(64);

        private static bool initialized;
        private static int cachedCornerCount;
        private static float[] sinTable;
        private static float[] cosTable;
        private static int[] cachedTriangles;
        private static int cachedRingVertexCount;
        private static int cachedVertexCount;
        private static Vector3[] workVertices;
        private static Vector3[] workNormals;
        private static Vector2[] workUVs;

        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;

            BasisNamePlateAssets.Initialize();
            PrecomputeCornerData();
        }

        public static void QueueBake(IBasisNamePlateBakeTarget target)
        {
            if (target == null || !target.IsBakeTargetValid) return;
            Initialize();
            bakeQueue.Enqueue(target);
        }

        public static void ProcessBakeQueue()
        {
            Initialize();
            if (!BasisNamePlateAssets.IsReady) return;

            int budget = MaxBakesPerFrame;
            while (budget > 0 && bakeQueue.Count > 0)
            {
                IBasisNamePlateBakeTarget target = bakeQueue.Dequeue();
                if (target == null || !target.IsBakeTargetValid) continue;

                Mesh mesh = BakeNow(target.DisplayName, target.MeshFilter, target.MeshRenderer, target.MeshName);
                target.OnBakeCompleted(mesh);
                budget--;
            }
        }

        public static Mesh BakeNow(string displayName, MeshFilter filter, MeshRenderer renderer, string meshName)
        {
            Initialize();
            if (!BasisNamePlateAssets.IsReady) return null;

            TextMeshPro text = BasisNamePlateAssets.TextBaker;
            if (text == null || filter == null || renderer == null) return null;

            text.gameObject.SetActive(true);
            text.fontSize = BakeFontSize;
            text.text = displayName;
            text.ForceMeshUpdate();

            const float horizontalPadding = 2f;
            Vector2 textSize = text.GetRenderedValues(true);
            float halfWidth = (textSize.x * 0.5f) + horizontalPadding;

            if (halfWidth > MaxPlateHalfWidth && textSize.x > 0.001f)
            {
                float maxTextWidth = (MaxPlateHalfWidth - horizontalPadding) * 2f;
                text.fontSize = BakeFontSize * (maxTextWidth / textSize.x);
                text.ForceMeshUpdate();
                textSize = text.GetRenderedValues(true);
                halfWidth = (textSize.x * 0.5f) + horizontalPadding;
            }

            Mesh plateMesh = GenerateRoundedQuad(halfWidth, 4.5f, "Rounded NamePlate Quad");

            TMP_TextInfo textInfo = text.textInfo;
            int subMeshLimit = 0;
            int textPartCount = 0;
            if (textInfo != null && textInfo.meshInfo != null)
            {
                subMeshLimit = math.min(textInfo.materialCount, textInfo.meshInfo.Length);
                for (int i = 0; i < subMeshLimit; i++)
                {
                    if (textInfo.meshInfo[i].vertexCount > 0)
                    {
                        textPartCount++;
                    }
                }
            }

            int totalParts = 1 + textPartCount;
            CombineInstance[] combine = new CombineInstance[totalParts];
            Material[] materials = new Material[totalParts];

            combine[0] = new CombineInstance { mesh = plateMesh, transform = Matrix4x4.identity };
            materials[0] = BasisNamePlateAssets.SelectedMaterial;

            int writeIdx = 1;
            for (int i = 0; i < subMeshLimit; i++)
            {
                TMP_MeshInfo info = textInfo.meshInfo[i];
                if (info.vertexCount == 0 || info.mesh == null) continue;

                combine[writeIdx] = new CombineInstance { mesh = info.mesh, transform = FlipX };
                materials[writeIdx] = info.material;
                writeIdx++;
            }

            Mesh combinedMesh = new Mesh { name = meshName };
            combinedMesh.CombineMeshes(combine, false);

            filter.sharedMesh = combinedMesh;
            renderer.sharedMaterials = materials;

            Object.Destroy(plateMesh);
            text.gameObject.SetActive(false);
            return combinedMesh;
        }

        public static Mesh GenerateRoundedQuad(float halfWidth, float halfHeight, string meshName)
        {
            Initialize();

            float width = halfWidth * 2f;
            float height = halfHeight * 2f;
            float maxRadius = Mathf.Min(halfWidth, halfHeight);
            float radius = Mathf.Clamp01(RoundEdges) * maxRadius;

            Vector2 uvOffset = new Vector2(0.5f, 0.5f);
            Vector2 uvScale = new Vector2(1f / width, 1f / height);

            workVertices[0] = new Vector3(0, 0, zOffset);
            workUVs[0] = uvOffset;

            for (int ci = 0; ci < cachedCornerCount; ci++)
            {
                float sin = sinTable[ci];
                float cos = cosTable[ci];

                float oneMinusCos = 1f - cos;
                float oneMinusSin = 1f - sin;

                Vector2 tl = new Vector2(-halfWidth + oneMinusCos * radius, halfHeight - oneMinusSin * radius);
                Vector2 tr = new Vector2(halfWidth - oneMinusSin * radius, halfHeight - oneMinusCos * radius);
                Vector2 br = new Vector2(halfWidth - oneMinusCos * radius, -halfHeight + oneMinusSin * radius);
                Vector2 bl = new Vector2(-halfWidth + oneMinusSin * radius, -halfHeight + oneMinusCos * radius);

                int idx1 = 1 + ci;
                int idx2 = idx1 + cachedCornerCount;
                int idx3 = idx2 + cachedCornerCount;
                int idx4 = idx3 + cachedCornerCount;

                workVertices[idx1] = new Vector3(tl.x, tl.y, zOffset);
                workVertices[idx2] = new Vector3(tr.x, tr.y, zOffset);
                workVertices[idx3] = new Vector3(br.x, br.y, zOffset);
                workVertices[idx4] = new Vector3(bl.x, bl.y, zOffset);

                workUVs[idx1] = tl * uvScale + uvOffset;
                workUVs[idx2] = tr * uvScale + uvOffset;
                workUVs[idx3] = br * uvScale + uvOffset;
                workUVs[idx4] = bl * uvScale + uvOffset;
            }

            return new Mesh
            {
                name = meshName,
                vertices = workVertices,
                normals = workNormals,
                uv = workUVs,
                triangles = cachedTriangles
            };
        }

        private static void PrecomputeCornerData()
        {
            cachedCornerCount = Mathf.Max(3, CornerVertexCount);
            cachedRingVertexCount = cachedCornerCount * 4;
            cachedVertexCount = cachedRingVertexCount + 1;

            float angleStep = Mathf.PI * 0.5f / (cachedCornerCount - 1);
            sinTable = new float[cachedCornerCount];
            cosTable = new float[cachedCornerCount];
            for (int ci = 0; ci < cachedCornerCount; ci++)
            {
                float angle = ci * angleStep;
                sinTable[ci] = Mathf.Sin(angle);
                cosTable[ci] = Mathf.Cos(angle);
            }

            cachedTriangles = new int[cachedRingVertexCount * 3];
            for (int i = 0; i < cachedRingVertexCount; i++)
            {
                int tri = i * 3;
                cachedTriangles[tri] = 0;
                cachedTriangles[tri + 1] = 1 + ((i + 1) % cachedRingVertexCount);
                cachedTriangles[tri + 2] = 1 + i;
            }

            workVertices = new Vector3[cachedVertexCount];
            workNormals = new Vector3[cachedVertexCount];
            workUVs = new Vector2[cachedVertexCount];

            for (int i = 0; i < cachedVertexCount; i++)
            {
                workNormals[i] = Vector3.forward;
            }
        }
    }
}
