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

            // Everything the OpenVR-owned buffers hold is block-copied to managed memory here, so
            // the native model can be freed immediately and the per-vertex conversion can run on a
            // worker instead of one Marshal.PtrToStructure per vertex on the main thread.
            RenderModel_t renderModel = Marshal.PtrToStructure<RenderModel_t>(pRenderModel);
            int vertexCount = (int)renderModel.unVertexCount;
            int vertexStride = Marshal.SizeOf<RenderModel_Vertex_t>();
            byte[] vertexBytes = new byte[vertexCount * vertexStride];
            Marshal.Copy(renderModel.rVertexData, vertexBytes, 0, vertexBytes.Length);
            int indexCount = (int)renderModel.unTriangleCount * 3;
            short[] rawIndices = new short[indexCount];
            Marshal.Copy(renderModel.rIndexData, rawIndices, 0, indexCount);

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

            byte[] textureData = null;
            int textureWidth = 0;
            int textureHeight = 0;
            if (textureError == EVRRenderModelError.None && pTexture != IntPtr.Zero)
            {
                RenderModel_TextureMap_t textureMap = Marshal.PtrToStructure<RenderModel_TextureMap_t>(pTexture);
                if (textureMap.format == EVRRenderModelTextureFormat.RGBA8_SRGB)
                {
                    textureWidth = textureMap.unWidth;
                    textureHeight = textureMap.unHeight;
                    textureData = new byte[textureWidth * textureHeight * 4];
                    Marshal.Copy(textureMap.rubTextureMapData, textureData, 0, textureData.Length);
                }
                else
                {
                    BasisDebug.LogError($"OpenVR render model texture format {textureMap.format} unsupported; rendering untextured.");
                }
                renderModels.FreeTexture(pTexture);
            }
            else
            {
                BasisDebug.LogError($"OpenVR render model '{renderModelName}' texture failed to load: {textureError}");
            }

            renderModels.FreeRenderModel(pRenderModel);

            var convert = System.Threading.Tasks.Task.Run(() => ConvertModel(vertexBytes, vertexStride, vertexCount, rawIndices));
            while (!convert.IsCompleted)
            {
                yield return null;
            }
            if (convert.IsFaulted)
            {
                BasisDebug.LogError($"OpenVR render model '{renderModelName}' conversion failed: {convert.Exception?.GetBaseException()}");
                Fallback();
                yield break;
            }

            Mesh mesh = BuildMesh(convert.Result, vertexCount);

            // The texture upload (with mip generation) gets its own frame rather than sharing the
            // mesh build's, so several devices connecting at once fan out instead of stacking.
            yield return null;
            Texture2D texture = textureData != null ? BuildTexture(textureData, textureWidth, textureHeight) : null;

            loadRoutine = null;
            ApplyToGameObject(mesh, texture);
        }

        private struct ConvertedModel
        {
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector2[] Uv;
            public int[] Triangles;
        }

        private static ConvertedModel ConvertModel(byte[] vertexBytes, int vertexStride, int vertexCount, short[] rawIndices)
        {
            Vector3[] vertices = new Vector3[vertexCount];
            Vector3[] normals = new Vector3[vertexCount];
            Vector2[] uv = new Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                int o = i * vertexStride;
                vertices[i] = new Vector3(BitConverter.ToSingle(vertexBytes, o), BitConverter.ToSingle(vertexBytes, o + 4), -BitConverter.ToSingle(vertexBytes, o + 8));
                normals[i] = new Vector3(BitConverter.ToSingle(vertexBytes, o + 12), BitConverter.ToSingle(vertexBytes, o + 16), -BitConverter.ToSingle(vertexBytes, o + 20));
                uv[i] = new Vector2(BitConverter.ToSingle(vertexBytes, o + 24), BitConverter.ToSingle(vertexBytes, o + 28));
            }

            int indexCount = rawIndices.Length;
            int[] triangles = new int[indexCount];
            for (int i = 0; i < indexCount; i += 3)
            {
                triangles[i] = (ushort)rawIndices[i + 2];
                triangles[i + 1] = (ushort)rawIndices[i + 1];
                triangles[i + 2] = (ushort)rawIndices[i];
            }

            return new ConvertedModel
            {
                Vertices = vertices,
                Normals = normals,
                Uv = uv,
                Triangles = triangles,
            };
        }

        private Mesh BuildMesh(ConvertedModel model, int vertexCount)
        {
            generatedMesh = new Mesh
            {
                name = "OpenVRRenderModel",
                indexFormat = vertexCount > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
                vertices = model.Vertices,
                normals = model.Normals,
                uv = model.Uv,
            };
            generatedMesh.triangles = model.Triangles;
            generatedMesh.RecalculateBounds();
            return generatedMesh;
        }

        private Texture2D BuildTexture(byte[] data, int width, int height)
        {
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
