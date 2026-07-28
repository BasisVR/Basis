using System;
using System.Collections;
using System.Runtime.InteropServices;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;
using Valve.VR;

namespace Basis.Scripts.Device_Management.Devices.OpenVR
{
    /// <summary>
    /// Loads the real SteamVR render model (mesh + diffuse texture) for a tracked device at runtime
    /// through <see cref="Valve.VR.CVRRenderModels"/>, so controllers, trackers and base stations
    /// render as their physical geometry instead of the generic sphere fallback. A compact stand-in
    /// for SteamVR's stripped <c>SteamVR_RenderModel</c> component.
    /// </summary>
    public class BasisOpenVRRenderModel : MonoBehaviour
    {
        private BasisInput owner;
        private Mesh generatedMesh;
        private Texture2D generatedTexture;
        private Material generatedMaterial;
        private Coroutine loadRoutine;

        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int GlossinessId = Shader.PropertyToID("_Glossiness");

        /// <summary>
        /// Local rotation (Euler) applied to the loaded model to reconcile the SteamVR render-model
        /// authoring axes with the device node's frame. Tune if a device renders facing the wrong way.
        /// </summary>
        public static Vector3 RotationOffsetEuler = Vector3.zero;

        /// <summary>
        /// Shared entry point for a device's <see cref="BasisInput.TryShowRuntimeDeviceModel"/>.
        /// Spawns a loader for the owner's SteamVR render-model name when SteamVR is active, reusing
        /// the one already in flight. Returns true when the runtime path is handling the visual.
        /// </summary>
        public static bool TryLoad(BasisInput owner, ref BasisOpenVRRenderModel active)
        {
            if (!SteamVR.active || string.IsNullOrEmpty(owner.CommonDeviceIdentifier))
            {
                return false;
            }
            if (active != null)
            {
                return true;
            }
            GameObject host = new GameObject("OpenVRRenderModel");
            host.transform.SetParent(owner.GetVisualAnchor(), false);
            active = host.AddComponent<BasisOpenVRRenderModel>();
            active.Load(owner.CommonDeviceIdentifier, owner);
            return true;
        }

        /// <summary>
        /// Begins an async load of the given SteamVR render-model name and, on success, attaches the
        /// mesh/texture plus a <see cref="BasisVisualTracker"/> to this GameObject. On failure the
        /// owner is asked to show its baked/sphere fallback and this GameObject is destroyed.
        /// </summary>
        public void Load(string renderModelName, BasisInput basisInput)
        {
            owner = basisInput;
            loadRoutine = StartCoroutine(LoadRoutine(renderModelName));
        }

        private IEnumerator LoadRoutine(string renderModelName)
        {
            CVRRenderModels renderModels = Valve.VR.OpenVR.RenderModels;
            if (renderModels == null || string.IsNullOrEmpty(renderModelName))
            {
                Fallback();
                yield break;
            }

            IntPtr pRenderModel = IntPtr.Zero;
            EVRRenderModelError modelError;
            while (true)
            {
                modelError = renderModels.LoadRenderModel_Async(renderModelName, ref pRenderModel);
                if (modelError != EVRRenderModelError.Loading)
                {
                    break;
                }
                yield return null;
            }

            if (modelError != EVRRenderModelError.None || pRenderModel == IntPtr.Zero)
            {
                BasisDebug.LogError($"OpenVR render model '{renderModelName}' failed to load: {modelError}");
                Fallback();
                yield break;
            }

            RenderModel_t renderModel = Marshal.PtrToStructure<RenderModel_t>(pRenderModel);
            Mesh mesh = BuildMesh(renderModel);

            IntPtr pTexture = IntPtr.Zero;
            EVRRenderModelError textureError;
            while (true)
            {
                textureError = renderModels.LoadTexture_Async(renderModel.diffuseTextureId, ref pTexture);
                if (textureError != EVRRenderModelError.Loading)
                {
                    break;
                }
                yield return null;
            }

            Texture2D texture = null;
            if (textureError == EVRRenderModelError.None && pTexture != IntPtr.Zero)
            {
                texture = BuildTexture(pTexture);
                renderModels.FreeTexture(pTexture);
            }
            else
            {
                BasisDebug.LogError($"OpenVR render model '{renderModelName}' texture failed to load: {textureError}");
            }

            renderModels.FreeRenderModel(pRenderModel);

            loadRoutine = null;
            ApplyToGameObject(mesh, texture);
        }

        private Mesh BuildMesh(RenderModel_t renderModel)
        {
            int vertexCount = (int)renderModel.unVertexCount;
            Vector3[] vertices = new Vector3[vertexCount];
            Vector3[] normals = new Vector3[vertexCount];
            Vector2[] uv = new Vector2[vertexCount];

            int vertexStride = Marshal.SizeOf<RenderModel_Vertex_t>();
            long vertexBase = renderModel.rVertexData.ToInt64();
            for (int i = 0; i < vertexCount; i++)
            {
                IntPtr ptr = new IntPtr(vertexBase + i * vertexStride);
                RenderModel_Vertex_t vert = Marshal.PtrToStructure<RenderModel_Vertex_t>(ptr);
                vertices[i] = new Vector3(vert.vPosition.v0, vert.vPosition.v1, -vert.vPosition.v2);
                normals[i] = new Vector3(vert.vNormal.v0, vert.vNormal.v1, -vert.vNormal.v2);
                uv[i] = new Vector2(vert.rfTextureCoord0, vert.rfTextureCoord1);
            }

            int indexCount = (int)renderModel.unTriangleCount * 3;
            short[] rawIndices = new short[indexCount];
            Marshal.Copy(renderModel.rIndexData, rawIndices, 0, indexCount);
            int[] triangles = new int[indexCount];
            for (int i = 0; i < indexCount; i += 3)
            {
                triangles[i] = (ushort)rawIndices[i + 2];
                triangles[i + 1] = (ushort)rawIndices[i + 1];
                triangles[i + 2] = (ushort)rawIndices[i];
            }

            generatedMesh = new Mesh
            {
                name = "OpenVRRenderModel",
                indexFormat = vertexCount > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
                vertices = vertices,
                normals = normals,
                uv = uv,
            };
            generatedMesh.triangles = triangles;
            generatedMesh.RecalculateBounds();
            return generatedMesh;
        }

        private Texture2D BuildTexture(IntPtr pTexture)
        {
            RenderModel_TextureMap_t textureMap = Marshal.PtrToStructure<RenderModel_TextureMap_t>(pTexture);
            if (textureMap.format != EVRRenderModelTextureFormat.RGBA8_SRGB)
            {
                BasisDebug.LogError($"OpenVR render model texture format {textureMap.format} unsupported; rendering untextured.");
                return null;
            }

            int width = textureMap.unWidth;
            int height = textureMap.unHeight;
            int byteCount = width * height * 4;
            byte[] data = new byte[byteCount];
            Marshal.Copy(textureMap.rubTextureMapData, data, 0, byteCount);

            generatedTexture = new Texture2D(width, height, TextureFormat.RGBA32, true, false)
            {
                name = "OpenVRRenderModelTexture",
                wrapMode = TextureWrapMode.Clamp,
            };
            generatedTexture.SetPixelData(data, 0);
            generatedTexture.Apply(true, false);
            return generatedTexture;
        }

        private static Shader ResolveDeviceShader()
        {
            BundledContentHolder holder = BundledContentHolder.Instance;
            Shader shader = holder != null ? holder.UrpShader : null;
            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Lit");
            }
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }
            return shader;
        }

        private void ApplyToGameObject(Mesh mesh, Texture2D texture)
        {
            if (owner == null)
            {
                return;
            }

            MeshFilter meshFilter = gameObject.AddComponent<MeshFilter>();
            MeshRenderer meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshFilter.sharedMesh = mesh;

            generatedMaterial = new Material(ResolveDeviceShader()) { name = "OpenVRRenderModelMaterial" };
            if (texture != null)
            {
                if (generatedMaterial.HasProperty(BaseMapId))
                {
                    generatedMaterial.SetTexture(BaseMapId, texture);
                }
                if (generatedMaterial.HasProperty(MainTexId))
                {
                    generatedMaterial.SetTexture(MainTexId, texture);
                }
            }
            if (generatedMaterial.HasProperty(MetallicId))
            {
                generatedMaterial.SetFloat(MetallicId, 0f);
            }
            if (generatedMaterial.HasProperty(SmoothnessId))
            {
                generatedMaterial.SetFloat(SmoothnessId, 0.25f);
            }
            if (generatedMaterial.HasProperty(GlossinessId))
            {
                generatedMaterial.SetFloat(GlossinessId, 0.25f);
            }
            meshRenderer.sharedMaterial = generatedMaterial;

            BasisVisualTracker tracker = gameObject.AddComponent<BasisVisualTracker>();
            tracker.ModelRotationOffset = Quaternion.Euler(RotationOffsetEuler);
            owner.BasisVisualTracker = tracker;
            tracker.Initialization(owner);
        }

        private void Fallback()
        {
            loadRoutine = null;
            BasisInput recover = owner;
            owner = null;
            if (recover != null)
            {
                recover.ShowBakedOrFallbackVisual();
            }
            if (this != null)
            {
                Destroy(gameObject);
            }
        }

        public void OnDestroy()
        {
            if (loadRoutine != null)
            {
                StopCoroutine(loadRoutine);
                loadRoutine = null;
            }
            if (generatedMesh != null)
            {
                Destroy(generatedMesh);
            }
            if (generatedTexture != null)
            {
                Destroy(generatedTexture);
            }
            if (generatedMaterial != null)
            {
                Destroy(generatedMaterial);
            }
        }
    }
}
