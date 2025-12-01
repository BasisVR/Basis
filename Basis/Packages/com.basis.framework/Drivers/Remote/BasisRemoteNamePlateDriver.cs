using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using TMPro;
using UnityEngine;

namespace Basis.Scripts.UI.NamePlate
{
    /// <summary>
    /// Handles the generation and management of remote player nameplates,
    /// including mesh creation, material selection, and text rendering.
    /// </summary>
    public class BasisRemoteNamePlateDriver : MonoBehaviour
    {
        /// <summary>
        /// Singleton instance of the <see cref="BasisRemoteNamePlateDriver"/>.
        /// </summary>
        public static BasisRemoteNamePlateDriver Instance;

        /// <summary>
        /// Default color for the nameplate when idle.
        /// </summary>
        public Color NormalColor;

        /// <summary>
        /// Color used when the player is talking.
        /// </summary>
        public Color IsTalkingColor;

        /// <summary>
        /// Color used when the player is out of range.
        /// </summary>
        public Color OutOfRangeColor;

        /// <summary>
        /// Duration of the color transition animation in seconds.
        /// </summary>
        [SerializeField]
        public static float transitionDuration = 0.3f;

        /// <summary>
        /// Delay before returning to the default color after an event.
        /// </summary>
        [SerializeField]
        public static float returnDelay = 0.4f;

        /// <summary>
        /// Cached static reference for the default color.
        /// </summary>
        public static Color StaticNormalColor;

        /// <summary>
        /// Cached static reference for the talking color.
        /// </summary>
        public static Color StaticIsTalkingColor;

        /// <summary>
        /// Cached static reference for the out-of-range color.
        /// </summary>
        public static Color StaticOutOfRangeColor;

        /// <summary>
        /// Reference to the text mesh used for displaying player names.
        /// </summary>
        public TextMeshPro Text;

        /// <summary>
        /// Transparent material used for nameplates.
        /// </summary>
        public Material TransParentNamePlateMaterial;

        /// <summary>
        /// Opaque material used for nameplates.
        /// </summary>
        public Material OpaqueNamePlateMaterial;

        /// <summary>
        /// The currently selected material for the nameplate,
        /// determined by device type (mobile vs. non-mobile).
        /// </summary>
        [HideInInspector]
        public Material SelectedNamePlateMaterial;

        /// <summary>
        /// The mesh with rounded corners for nameplates.
        /// </summary>
        [HideInInspector]
        public Mesh RoundedCornersMesh;

        /// <summary>
        /// Controls the curvature of the rounded corners (0–1 scale).
        /// 0 = sharp rectangle, 1 = maximum rounding given width/height.
        /// </summary>
        [Range(0f, 1f)]
        public float RoundEdges = 0.5f;

        /// <summary>
        /// Number of vertices used per rounded corner (must be greater than 2).
        /// </summary>
        public int CornerVertexCount = 8;

        /// <summary>
        /// Z-axis offset applied to the generated mesh.
        /// </summary>
        public float zOffset = 0.06f;

        /// <summary>
        /// Unity lifecycle method. Initializes the singleton,
        /// assigns materials, caches colors, and generates the rounded mesh.
        /// </summary>
        public void Awake()
        {
            Instance = this;
            if (BasisDeviceManagement.IsMobileHardware())
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

        /// <summary>
        /// Generates the text mesh for a remote player's nameplate.
        /// </summary>
        /// <param name="remotePlayer">The remote player whose display name will be shown.</param>
        /// <param name="namePlate">The target nameplate object to assign the mesh to.</param>
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

        /// <summary>
        /// Combines the rounded corner mesh and the text mesh
        /// into a single final mesh for rendering on the nameplate.
        /// </summary>
        /// <param name="namePlate">The nameplate object to update with the combined mesh.</param>
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

        /// <summary>
        /// Generates a rectangular mesh with rounded corners for the nameplate background.
        /// </summary>
        /// <returns>A mesh with rounded edges and UV mapping applied.</returns>
        public Mesh GenerateRoundedQuad()
        {
            int cornerCount = Mathf.Max(3, CornerVertexCount); // safety clamp
            int ringVertexCount = cornerCount * 4;
            int vertexCount = ringVertexCount + 1;
            int triangleCount = ringVertexCount;

            Vector3[] m_Vertices = new Vector3[vertexCount];
            Vector3[] m_Normals = new Vector3[vertexCount];
            Vector2[] m_UV = new Vector2[vertexCount];
            int[] m_Triangles = new int[triangleCount * 3];

            // Base dimensions of the quad
            float halfWidth = 30f;
            float halfHeight = 4.5f;
            float width = halfWidth * 2f;
            float height = halfHeight * 2f;

            // Max possible radius before the rounded corners break the shape
            float maxRadius = Mathf.Min(halfWidth, halfHeight);

            // Interpret RoundEdges as a 0–1 slider
            float radius = Mathf.Clamp01(RoundEdges) * maxRadius;

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

                // Calculate each rounded corner position using the radius
                Vector2 tl = new Vector2(
                    -halfWidth + (1f - cos) * radius,
                    halfHeight - (1f - sin) * radius
                );

                Vector2 tr = new Vector2(
                    halfWidth - (1f - sin) * radius,
                    halfHeight - (1f - cos) * radius
                );

                Vector2 br = new Vector2(
                    halfWidth - (1f - cos) * radius,
                    -halfHeight + (1f - sin) * radius
                );

                Vector2 bl = new Vector2(
                    -halfWidth + (1f - sin) * radius,
                    -halfHeight + (1f - cos) * radius
                );

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
