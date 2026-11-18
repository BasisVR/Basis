using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Basis.Scripts.Drivers;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Basis.Scripts.Rendering
{
    /// <summary>
    /// Supplies the mip demand missing from Unity's native streamer for texture overrides in
    /// MaterialPropertyBlocks. Never changes a renderer's materials or property blocks.
    /// </summary>
    [AutoStaticsCleanup]
    public partial class BasisMaterialPropertyBlockStreaming : MonoBehaviour
    {
        private static BasisMaterialPropertyBlockStreaming instance;
        private const float UpdateInterval = 0.1f;
        // Keep demand across skipped renders, including a handheld capture limited to 1 Hz.
        private const float RenderedViewGraceSeconds = 1.5f;
        private const int RenderersPerFrame = 128;
        private const int PrimaryUvChannel = 0;
        private const float MinimumUvTilingArea = 0.000001f;
        private const float MinimumFallbackUvMetric = 0.0001f;

        private sealed class RendererState
        {
            internal Renderer Renderer;
            internal MeshFilter Filter;
        }

        private struct TextureProperty
        {
            internal int Id;
            internal int ScaleOffsetId;
        }

        private sealed class View
        {
            internal readonly Plane[] Planes = new Plane[6];
            internal Vector3 Position;
            internal int CullingMask;
            internal bool Orthographic;
            internal float ProjectionArea;
            internal float Bias;
        }

        private struct CameraView
        {
            internal Matrix4x4 CullingMatrix;
            internal Vector3 Position;
            internal int CullingMask;
            internal bool Orthographic;
            internal float ProjectionArea;
        }

        private struct RenderedView
        {
            internal CameraView View;
            internal float RenderedAt;
        }

        private struct Request
        {
            internal int Previous;
            internal int Applied;
        }

        private readonly List<RendererState> renderers = new List<RendererState>();
        private readonly HashSet<Renderer> registered = new HashSet<Renderer>();
        private readonly List<Renderer> discovery = new List<Renderer>();
        private readonly List<Material> materials = new List<Material>();
        private readonly ConditionalWeakTable<Shader, TextureProperty[]> shaderProperties = new();
        private readonly Dictionary<Camera, RenderedView> renderedViews = new Dictionary<Camera, RenderedView>(8);
        private readonly List<Camera> expiredViews = new List<Camera>(8);
        private readonly List<View> views = new List<View>();
        private MaterialPropertyBlock rendererBlock;
        private MaterialPropertyBlock materialBlock;
        private readonly Dictionary<Texture2D, int> demands = new Dictionary<Texture2D, int>();
        private readonly Dictionary<Texture2D, Request> requests = new Dictionary<Texture2D, Request>();
        private readonly List<Texture2D> releases = new List<Texture2D>();
        private int viewCount;
        private int scanIndex;
        private int scanCount;
        private bool scanning;
        private bool scanStreaming;
        private float nextScan;

        internal int TrackedRendererCount => renderers.Count;
        internal BasisMaterialPropertyBlockMipBatch MipBatch { get; set; }

        public static void EnsureRunning()
        {
#if !UNITY_SERVER
            if (!Application.isPlaying || instance != null || !SystemInfo.supportsMipStreaming) return;
            var host = new GameObject(nameof(BasisMaterialPropertyBlockStreaming));
            DontDestroyOnLoad(host);
            host.AddComponent<BasisMaterialPropertyBlockStreaming>();
#endif
        }

        /// <summary>
        /// Content/scene loads and avatar installation register automatically. Runtime producers
        /// that add renderers after activation can register them here without a scene-wide poll.
        /// Destroyed renderers are removed during the next demand scan.
        /// </summary>
        public static void RegisterRenderers(IEnumerable<Renderer> items)
        {
            EnsureRunning();
            if (instance == null || items == null) return;
            instance.TrackRenderers(items);
        }

        internal void TrackRenderers(IEnumerable<Renderer> items)
        {
            foreach (var renderer in items)
            {
                if (renderer == null || !registered.Add(renderer)) continue;
                renderer.TryGetComponent<MeshFilter>(out var filter);
                renderers.Add(new RendererState { Renderer = renderer, Filter = filter });
            }
        }

        private void Awake()
        {
            instance = this;
            MipBatch = BasisMaterialPropertyBlockMipBatch.TryCreate();
            ContentPoliceControl.OnBeforeContentActivated += RegisterRoot;
            SceneManager.sceneLoaded += SceneLoaded;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) SceneLoaded(scene, LoadSceneMode.Additive);
            }
        }

        private void RegisterRoot(GameObject root)
        {
            root.GetComponentsInChildren(true, discovery);
            TrackRenderers(discovery);
            discovery.Clear();
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            foreach (var root in scene.GetRootGameObjects()) RegisterRoot(root);
        }

        public static void Tick()
        {
            if (instance != null)
            {
                instance.ProcessFrame(Time.unscaledTime,
                    instance.isActiveAndEnabled && QualitySettings.streamingMipmapsActive);
            }
        }

        internal void ProcessFrame(float now, bool streamingActive)
        {
            if (!streamingActive)
            {
                if (requests.Count != 0 || demands.Count != 0) ReleaseAll();
                renderedViews.Clear();
            }
            if (scanStreaming != streamingActive)
            {
                scanStreaming = streamingActive;
                scanning = false;
                nextScan = now;
            }
            if (!scanning)
            {
                if (now < nextScan) return;
                nextScan = now + UpdateInterval;
                demands.Clear();
                scanIndex = 0;
                scanCount = renderers.Count;
                scanning = true;
            }

            int budget = Mathf.Clamp(QualitySettings.streamingMipmapsRenderersPerFrame, 1, RenderersPerFrame);
            if (streamingActive) PrepareViews(now);
            try
            {
                for (int visited = 0; visited < budget && scanIndex < scanCount; visited++)
                {
                    var state = renderers[scanIndex];
                    if (state.Renderer == null)
                    {
                        registered.Remove(state.Renderer);
                        // Fill the hole with an unvisited entry. Registrations appended during
                        // this sweep stay beyond its boundary and are visited on the next one.
                        int lastInScan = --scanCount;
                        int last = renderers.Count - 1;
                        renderers[scanIndex] = renderers[lastInScan];
                        renderers[lastInScan] = renderers[last];
                        renderers.RemoveAt(last);
                        continue;
                    }
                    scanIndex++;
                    if (streamingActive) Collect(state);
                }
                FlushMipBatch();
            }
            finally
            {
                MipBatch?.Clear();
                materials.Clear();
                rendererBlock?.Clear();
                materialBlock?.Clear();
            }
            if (scanIndex < scanCount) return;
            if (streamingActive) ApplyRequests();
            scanning = false;
        }

        private void PrepareViews(float now)
        {
            viewCount = 0;
            var main = BasisLocalCameraDriver.CameraInstance;
            bool liveMain = main != null && main.isActiveAndEnabled;
            if (liveMain && TryCaptureCameraView(main, out var mainView)) AddView(main, mainView);

            // The culling registry also includes idle cameras. Only recent renders supply secondary mip demand.
            foreach (var entry in renderedViews)
            {
                var camera = entry.Key;
                if (camera == null || now - entry.Value.RenderedAt > RenderedViewGraceSeconds)
                {
                    expiredViews.Add(camera);
                    continue;
                }
                if (!liveMain || camera != main) AddView(camera, entry.Value.View);
            }
            foreach (var camera in expiredViews) renderedViews.Remove(camera);
            expiredViews.Clear();
        }

        private void AddView(Camera camera, CameraView cameraView)
        {
            bool hasController = camera.TryGetComponent<StreamingController>(out var controller);
            if (hasController ? !controller.enabled : !QualitySettings.streamingMipmapsAddAllCameras) return;
            if (views.Count == viewCount) views.Add(new View());
            var view = views[viewCount++];
            view.Position = cameraView.Position;
            view.CullingMask = cameraView.CullingMask;
            view.Orthographic = cameraView.Orthographic;
            view.Bias = hasController ? controller.streamingMipmapBias : 0f;
            view.ProjectionArea = cameraView.ProjectionArea;
            // Mirrors use an explicit culling matrix without the oblique clipping plane.
            GeometryUtility.CalculateFrustumPlanes(cameraView.CullingMatrix, view.Planes);
        }

        private static bool TryCaptureCameraView(Camera camera, out CameraView view)
        {
            view = default;
            if (camera == null || camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.SceneView) return false;
            int height = camera.pixelHeight;
            if (height <= 0 || camera.pixelWidth <= 0) return false;
            float pixelsPerUnit = 0.5f * height * camera.projectionMatrix.m11;
            view = new CameraView
            {
                CullingMatrix = camera.cullingMatrix,
                Position = camera.transform.position,
                CullingMask = camera.cullingMask,
                Orthographic = camera.orthographic,
                ProjectionArea = pixelsPerUnit * pixelsPerUnit * Mathf.Max(1f, camera.aspect),
            };
            return true;
        }

        internal void OnBeginCameraRendering(ScriptableRenderContext _, Camera camera)
        {
            if (!isActiveAndEnabled || !QualitySettings.streamingMipmapsActive || camera == null) return;
            if (camera == BasisLocalCameraDriver.CameraInstance && camera.isActiveAndEnabled) return;
            ObserveRenderedCamera(camera, Time.unscaledTime);
        }

        internal void ObserveRenderedCamera(Camera camera, float now)
        {
            // Render requests can use a disabled camera and temporarily replace its render target.
            if (TryCaptureCameraView(camera, out var view))
                renderedViews[camera] = new RenderedView { View = view, RenderedAt = now };
        }

        private void Collect(RendererState state)
        {
            var renderer = state.Renderer;
            if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy || !renderer.HasPropertyBlock()) return;
            Bounds bounds = renderer.bounds;
            bool visible = false;
            for (int i = 0; i < viewCount; i++)
                if (Sees(views[i], renderer, bounds)) { visible = true; break; }
            if (!visible) return;

            float metric = 0f;
            rendererBlock ??= new MaterialPropertyBlock();
            materialBlock ??= new MaterialPropertyBlock();
            renderer.GetSharedMaterials(materials);
            renderer.GetPropertyBlock(rendererBlock);
            int materialCount = materials.Count;
            for (int slot = 0; slot < materialCount; slot++)
            {
                var material = materials[slot];
                if (material == null || material.shader == null) continue;
                renderer.GetPropertyBlock(materialBlock, slot);
                // Unity selects the per-material block wholesale when it exists; it does not
                // merge missing properties from the renderer-wide block into that block.
                var block = SelectBlock(rendererBlock, materialBlock);
                if (block.isEmpty) continue;
                var properties = shaderProperties.GetValue(material.shader, BuildTextureProperties);
                foreach (var property in properties)
                {
                    var texture = block.GetTexture(property.Id) as Texture2D;
                    if (texture == null || !texture.streamingMipmaps) continue;
                    if (metric == 0f) metric = GetUvMetric(state, bounds);
                    Vector2 uvScale = material.GetTextureScale(property.Id);
                    if (block.HasVector(property.ScaleOffsetId))
                    {
                        Vector4 st = block.GetVector(property.ScaleOffsetId);
                        uvScale = new Vector2(st.x, st.y);
                    }
                    float uvArea = Mathf.Max(Mathf.Abs(uvScale.x * uvScale.y), MinimumUvTilingArea);
                    int mipCount = texture.mipmapCount;
                    int mip = mipCount - 1;
                    int width = texture.width;
                    int height = texture.height;
                    float uvMetric = metric / uvArea;
                    for (int v = 0; v < viewCount; v++)
                    {
                        var view = views[v];
                        if (!Sees(view, renderer, bounds)) continue;
                        float distanceSquared = view.Orthographic ? 1f : bounds.SqrDistance(view.Position);
                        if (MipBatch == null)
                        {
                            mip = Mathf.Min(mip, CalculateMip(width, height, distanceSquared, uvMetric, view.ProjectionArea, view.Bias, mipCount));
                            continue;
                        }
                        MipBatch.Add(texture, new BasisMaterialPropertyBlockMipBatch.Sample
                        {
                            Width = width, Height = height, MipCount = mipCount,
                            DistanceSquared = distanceSquared, UvMetric = uvMetric, ProjectionArea = view.ProjectionArea, Bias = view.Bias,
                        });
                        if (MipBatch.IsFull) FlushMipBatch();
                    }
                    if (MipBatch == null) MergeDemand(texture, mip);
                }
            }
        }

        private void FlushMipBatch()
        {
            var batch = MipBatch;
            if (batch == null || batch.Count == 0) return;
            batch.Calculate();
            int i = 0;
            while (i < batch.Count)
            {
                var texture = batch.Textures[i];
                int mip = batch.Mips[i++];
                while (i < batch.Count && ReferenceEquals(texture, batch.Textures[i]))
                    mip = Mathf.Min(mip, batch.Mips[i++]);
                MergeDemand(texture, mip);
            }
            batch.Clear();
        }

        private void MergeDemand(Texture2D texture, int mip)
        {
            // A texture can also appear in a material closer to the camera.
            int nativeMip = texture.calculatedMipmapLevel;
            if (nativeMip >= 0) mip = Mathf.Min(mip, nativeMip);
            if (!demands.TryGetValue(texture, out int existing) || mip < existing)
            {
                demands[texture] = mip;
                ApplyRequest(texture, mip, upgradesOnly: true);
            }
        }

        private static TextureProperty[] BuildTextureProperties(Shader shader)
        {
            var found = new List<TextureProperty>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                found.Add(new TextureProperty
                {
                    Id = shader.GetPropertyNameId(i),
                    ScaleOffsetId = Shader.PropertyToID(shader.GetPropertyName(i) + "_ST"),
                });
            }
            return found.ToArray();
        }

        private static float GetUvMetric(RendererState state, Bounds bounds)
        {
            var renderer = state.Renderer;
            if (state.Filter == null && renderer is MeshRenderer)
                renderer.TryGetComponent(out state.Filter);
            Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                : state.Filter != null ? state.Filter.sharedMesh : null;
            Vector3 scale = renderer.transform.lossyScale;
            float areaScale = Mathf.Max(Mathf.Abs(scale.x * scale.y),
                Mathf.Max(Mathf.Abs(scale.x * scale.z), Mathf.Abs(scale.y * scale.z)));
            float metric = mesh != null ? mesh.GetUVDistributionMetric(PrimaryUvChannel) * areaScale : 0f;
            // Fall back to one UV tile across the bounds when the mesh has no UV metric.
            return metric > 0f && !float.IsInfinity(metric) ? metric : Mathf.Max(bounds.size.sqrMagnitude, MinimumFallbackUvMetric);
        }

        private static bool Sees(View view, Renderer renderer, Bounds bounds) =>
            (view.CullingMask & (1 << renderer.gameObject.layer)) != 0
            && GeometryUtility.TestPlanesAABB(view.Planes, bounds);

        internal static MaterialPropertyBlock SelectBlock(MaterialPropertyBlock renderer, MaterialPropertyBlock material) => material.isEmpty ? renderer : material;

        // Derived from Unity's Mesh.GetUVDistributionMetric example: UV texel area divided by
        // projected world area, converted from an area ratio into a mip (linear) ratio.
        internal static int CalculateMip(int width, int height, float distanceSquared, float uvMetric, float projectionArea, float bias, int mipCount)
        {
            const float minimum = BasisMaterialPropertyBlockMipBatch.MinimumMipFactor;
            float ratio = (float)width * height * Mathf.Max(distanceSquared, minimum)
                / Mathf.Max(uvMetric * projectionArea, minimum);
            float mip = BasisMaterialPropertyBlockMipBatch.AreaLogToMipScale * Mathf.Log(ratio, 2f) + bias;
            return Mathf.Clamp(Mathf.FloorToInt(mip), 0, Mathf.Max(0, mipCount - 1));
        }

        private void ApplyRequests()
        {
            releases.Clear();
            foreach (var entry in requests)
                if (entry.Key == null || !demands.ContainsKey(entry.Key)) releases.Add(entry.Key);
            foreach (var texture in releases)
            {
                Release(texture, requests[texture]);
                requests.Remove(texture);
            }
            releases.Clear();
            foreach (var demand in demands)
                if (demand.Key != null) ApplyRequest(demand.Key, demand.Value, upgradesOnly: false);
            demands.Clear();
        }

        private void ApplyRequest(Texture2D texture, int demand, bool upgradesOnly)
        {
            int current = texture.requestedMipmapLevel;
            bool owned = requests.TryGetValue(texture, out var request);
            if (!owned || current != request.Applied)
            {
                if (owned) requests.Remove(texture);
                request.Previous = current;
            }
            int mip = Mathf.Min(demand, Mathf.Max(0, QualitySettings.streamingMipmapsMaxLevelReduction));
            if (request.Previous >= 0) mip = Mathf.Min(request.Previous, mip);
            // Only claim values we actually write. A completed sweep may reduce demand,
            // but a partial sweep must preserve requests from unvisited renderers.
            if (current == mip || (upgradesOnly && current >= 0 && mip > current)) return;
            texture.requestedMipmapLevel = mip;
            // Unity can clamp a request. Track the value it actually accepted so the next
            // sweep does not mistake the clamp for a write from another caller.
            request.Applied = texture.requestedMipmapLevel;
            if (request.Applied == current) return;
            requests[texture] = request;
        }

        private static void Release(Texture2D texture, Request request)
        {
            if (texture == null || texture.requestedMipmapLevel != request.Applied) return;
            if (request.Previous >= 0) texture.requestedMipmapLevel = request.Previous;
            else texture.ClearRequestedMipmapLevel();
        }

        private void ReleaseAll()
        {
            foreach (var entry in requests) Release(entry.Key, entry.Value);
            requests.Clear();
            demands.Clear();
            releases.Clear();
        }

        private void OnDisable()
        {
            ReleaseAll();
            renderedViews.Clear();
            scanning = false;
        }

        private void OnDestroy()
        {
            ReleaseAll();
            ContentPoliceControl.OnBeforeContentActivated -= RegisterRoot;
            SceneManager.sceneLoaded -= SceneLoaded;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            if (instance == this) instance = null;
        }
    }
}
