using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using TMPro;
using UnityEngine;

namespace Basis.Scripts.UI.NamePlate
{
    public class BasisRemoteNamePlateDriver : MonoBehaviour
    {
        public static BasisRemoteNamePlateDriver Instance;
        public Color NormalColor;
        public Color IsTalkingColor;
        public Color OutOfRangeColor;
        [SerializeField]
        public static float transitionDuration = 0.3f;
        [SerializeField]
        public static float returnDelay = 0.4f;
        public static Color StaticNormalColor;
        public static Color StaticIsTalkingColor;
        public static Color StaticOutOfRangeColor;
        public TextMeshPro Text;
        public Material TransParentNamePlateMaterial;
        public Material OpaqueNamePlateMaterial;
        [HideInInspector]
        public Material SelectedNamePlateMaterial;
        [HideInInspector]
        public Mesh RoundedCornersMesh;
        public void Awake()
        {
            Instance = this;
            if (BasisDeviceManagement.IsMobile())
            {
                SelectedNamePlateMaterial = OpaqueNamePlateMaterial;
            }
            else
            {
                SelectedNamePlateMaterial = TransParentNamePlateMaterial;
            }
            StaticNormalColor = NormalColor;
            StaticIsTalkingColor = IsTalkingColor;
            StaticOutOfRangeColor = OutOfRangeColor;
            // Convert Sprite to Mesh with custom width and height
            RoundedCornersMesh = GenerateRoundedQuad();
        }
        public void GenerateTextFactory(BasisRemotePlayer remotePlayer, BasisRemoteNamePlate namePlate)
        {
            Text.gameObject.SetActive(true);
            Text.text = remotePlayer.DisplayName;
            Text.ForceMeshUpdate();

            // Generate a new mesh from the text
            Mesh textMesh = new Mesh();
            textMesh = Instantiate(Text.mesh);  // Unity handles proper copy

            // Assign to nameplate
            namePlate.bakedMesh = textMesh;
            namePlate.Filter.sharedMesh = textMesh;

            // Combine meshes
            CreateFinalMesh(namePlate);
            Text.gameObject.SetActive(false);
        }

        private void CreateFinalMesh(BasisRemoteNamePlate namePlate)
        {
            CombineInstance[] combine = new CombineInstance[2];

            combine[0] = new CombineInstance
            {
                mesh = RoundedCornersMesh,
                transform = Matrix4x4.identity
            };

            combine[1] = new CombineInstance
            {
                mesh = namePlate.bakedMesh,
                transform = Matrix4x4.identity
            };

            Mesh combinedMesh = new Mesh
            {
                name = "CombinedNameplateMesh"
            };
            combinedMesh.CombineMeshes(combine, false); // false = keep submeshes

            // Assign final mesh and materials
            namePlate.Filter.sharedMesh = combinedMesh;
            namePlate.Renderer.materials = new Material[]
            {
            SelectedNamePlateMaterial,
            namePlate.Renderer.material
            };
        }
        public float RoundEdges = 0.85f;
        public int CornerVertexCount = 8; // Must be > 2
        public float zOffset = 0.06f; // Move mesh back on Z-axis;
        public Mesh GenerateRoundedQuad()
        {
            int cornerCount = CornerVertexCount;
            int ringVertexCount = cornerCount * 4;
            int vertexCount = ringVertexCount + 1;
            int triangleCount = ringVertexCount;

            Vector3[] m_Vertices = new Vector3[vertexCount];
            Vector3[] m_Normals = new Vector3[vertexCount];
            Vector2[] m_UV = new Vector2[vertexCount];
            int[] m_Triangles = new int[triangleCount * 3];

            float halfWidth = 25;
            float halfHeight = 4.5f;
            float width = 50;
            float height = 9;

            float maxRadius = Mathf.Min(width, height) * 0.5f;
            float radius = Mathf.Min(RoundEdges, maxRadius);

            float angleStep = Mathf.PI * 0.5f / (cornerCount - 1);
            Vector2 uvOffset = new Vector2(0.5f, 0.5f);
            Vector2 uvScale = new Vector2(1f / width, 1f / height);

            // Center vertex
            m_Vertices[0] = new Vector3(0, 0, zOffset);
            m_UV[0] = uvOffset;
            m_Normals[0] = -Vector3.forward;

            for (int CornerIndex = 0; CornerIndex < cornerCount; CornerIndex++)
            {
                float angle = CornerIndex * angleStep;
                float sin = Mathf.Sin(angle);
                float cos = Mathf.Cos(angle);

                // Calculate each rounded corner position
                Vector2 tl = new Vector2(-halfWidth + (1f - cos) * radius, halfHeight - (1f - sin) * radius);
                Vector2 tr = new Vector2(halfWidth - (1f - sin) * radius, halfHeight - (1f - cos) * radius);
                Vector2 br = new Vector2(halfWidth - (1f - cos) * radius, -halfHeight + (1f - sin) * radius);
                Vector2 bl = new Vector2(-halfWidth + (1f - sin) * radius, -halfHeight + (1f - cos) * radius);

                int baseIndex = 1 + CornerIndex;
                m_Vertices[baseIndex] = new Vector3(tl.x, tl.y, zOffset);
                m_Vertices[baseIndex + cornerCount] = new Vector3(tr.x, tr.y, zOffset);
                m_Vertices[baseIndex + cornerCount * 2] = new Vector3(br.x, br.y, zOffset);
                m_Vertices[baseIndex + cornerCount * 3] = new Vector3(bl.x, bl.y, zOffset);

                m_UV[baseIndex] = tl * uvScale + uvOffset;
                m_UV[baseIndex + cornerCount] = tr * uvScale + uvOffset;
                m_UV[baseIndex + cornerCount * 2] = br * uvScale + uvOffset;
                m_UV[baseIndex + cornerCount * 3] = bl * uvScale + uvOffset;

                m_Normals[baseIndex] = -Vector3.forward;
                m_Normals[baseIndex + cornerCount] = -Vector3.forward;
                m_Normals[baseIndex + cornerCount * 2] = -Vector3.forward;
                m_Normals[baseIndex + cornerCount * 3] = -Vector3.forward;
            }

            // Triangle fan around center
            for (int i = 0; i < ringVertexCount; i++)
            {
                int triIndex = i * 3;
                m_Triangles[triIndex] = 0;
                m_Triangles[triIndex + 1] = 1 + i;
                m_Triangles[triIndex + 2] = 1 + ((i + 1) % ringVertexCount);
            }

            Mesh mesh = new Mesh
            {
                name = "Rounded NamePlate Quad",
                vertices = m_Vertices,
                normals = m_Normals,
                uv = m_UV,
                triangles = m_Triangles
            };

            return mesh;
        }
    }
}
