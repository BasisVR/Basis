using System;
using System.Collections;
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
        private const int InitialTextureCapacity = 512;
        private const int InitialMaterialSlots = 32;
        private const int PendingBindingsPerInterval = 64;
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
            internal bool CheckFilter;
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

        private sealed class CameraState
        {
            internal readonly View View = new View();
            internal StreamingController Controller;
            internal CameraView RenderedView;
            internal float RenderedAt;
            internal bool HasRendered;
        }

        private struct CameraView
        {
            internal Matrix4x4 CullingMatrix;
            internal Vector3 Position;
            internal int CullingMask;
            internal bool Orthographic;
            internal float ProjectionArea;
        }

        private struct CameraObservation
        {
            internal Camera Camera;
            internal CameraView View;
            internal float RenderedAt;
            internal bool HasRendered;
        }

        private struct Request
        {
            internal int Previous;
            internal int Applied;
        }

        private readonly List<RendererState> renderers = new List<RendererState>();
        private readonly Dictionary<Renderer, RendererState> registered = new Dictionary<Renderer, RendererState>();
        private readonly List<Renderer> discovery = new List<Renderer>();
        private readonly List<Material> materials = new List<Material>(InitialMaterialSlots);
        private readonly ConditionalWeakTable<Shader, TextureProperty[]> shaderProperties = new();
        private readonly Dictionary<Camera, CameraState> cameras = new Dictionary<Camera, CameraState>();
        private readonly List<Camera> expiredCameras = new List<Camera>();
        private readonly CameraObservation[] pendingCameras = new CameraObservation[PendingBindingsPerInterval];
        private readonly Shader[] pendingShaders = new Shader[PendingBindingsPerInterval];
        private readonly RendererState[] pendingFilters = new RendererState[PendingBindingsPerInterval];
        private readonly List<View> views = new List<View>();
        private MaterialPropertyBlock rendererBlock;
        private MaterialPropertyBlock materialBlock;
        private readonly Dictionary<Texture2D, int> demands = new Dictionary<Texture2D, int>(InitialTextureCapacity);
        private readonly Dictionary<Texture2D, Request> requests = new Dictionary<Texture2D, Request>(InitialTextureCapacity);
        private readonly List<Texture2D> releases = new List<Texture2D>(InitialTextureCapacity);
        private Coroutine refreshBindings;
        private int pendingCameraCount;
        private int pendingShaderCount;
        private int pendingFilterCount;
        private int textureCapacity = InitialTextureCapacity;
        private bool growTextureBuffers;
        private bool incompleteScan;
        private bool tickFailureLogged;
        private int scanIndex;
        private int scanCount;
        private bool scanning;
        private bool scanStreaming;
        private float nextScan;

        internal int TrackedRendererCount => renderers.Count;
        internal BasisMaterialPropertyBlockMipBatch MipBatch;

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
        /// that add renderers or replace MeshFilters after activation can register them here.
        /// Destroyed renderers are removed during the next demand scan.
        /// </summary>
        public static void RegisterRenderers(IEnumerable<Renderer> items)
        {
            EnsureRunning();
            if (instance == null || items == null) return;
            instance.TrackRenderers(items);
        }

        internal static void RegisterContentRoot(GameObject root)
        {
            if (root == null) return;
            EnsureRunning();
            if (instance != null) instance.RegisterRoot(root);
        }

        internal void TrackRenderers(IEnumerable<Renderer> items)
        {
            // TrackRenderers can also be called by Edit Mode tests before Awake.
            rendererBlock ??= new MaterialPropertyBlock();
            materialBlock ??= new MaterialPropertyBlock();
            foreach (var renderer in items)
            {
                if (renderer == null) continue;
                if (!registered.TryGetValue(renderer, out var state))
                {
                    state = new RendererState { Renderer = renderer };
                    registered.Add(renderer, state);
                    renderers.Add(state);
                }
                if (renderer is MeshRenderer)
                {
                    state.CheckFilter = true;
                    renderer.TryGetComponent(out state.Filter);
                }
                try
                {
                    // Reserve scratch space for the material layout that arrived with the renderer.
                    renderer.GetSharedMaterials(materials);
                    if (!renderer.HasPropertyBlock()) continue;
                    int count = materials.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var material = materials[i];
                        if (material != null && material.shader != null)
                            shaderProperties.GetValue(material.shader, BuildTextureProperties);
                    }
                }
                finally
                {
                    materials.Clear();
                }
            }
        }

        private void Awake()
        {
            instance = this;
            rendererBlock = new MaterialPropertyBlock();
            materialBlock = new MaterialPropertyBlock();
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

        private void OnEnable()
        {
            if (Application.isPlaying) refreshBindings = StartCoroutine(RefreshBindingsPeriodically());
        }

        private IEnumerator RefreshBindingsPeriodically()
        {
            var interval = new WaitForSecondsRealtime(UpdateInterval);
            bool loggedFailure = false;
            while (true)
            {
                try
                {
                    if (QualitySettings.streamingMipmapsActive)
                        RefreshPendingBindings(Time.unscaledTime);
                }
                catch (Exception ex)
                {
                    if (!loggedFailure)
                        BasisDebug.LogErrorOnce(ref loggedFailure, $"Mip streaming binding refresh failed: {ex}", BasisDebug.LogTag.Event);
                }
                yield return interval;
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
            var service = instance;
            if (service == null) return;

            // Contain streaming errors here so the event driver can finish the frame.
            try
            {
                service.ProcessFrame(Time.unscaledTime,
                    service.isActiveAndEnabled && QualitySettings.streamingMipmapsActive);
            }
            catch (Exception ex)
            {
                if (!service.tickFailureLogged)
                    BasisDebug.LogErrorOnce(ref service.tickFailureLogged, $"Mip streaming tick failed: {ex}", BasisDebug.LogTag.Event);
            }
        }

        internal void ProcessFrame(float now, bool streamingActive)
        {
            if (!streamingActive)
            {
                if (requests.Count != 0 || demands.Count != 0) ReleaseAll();
                ClearCameraViews();
                ClearPendingRendererBindings();
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
                incompleteScan = false;
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
            if (streamingActive && !incompleteScan && pendingCameraCount == 0) ApplyRequests();
            scanning = false;
        }

        private void PrepareViews(float now)
        {
            views.Clear();
            var main = BasisLocalCameraDriver.CameraInstance;
            Camera liveMain = null;
            if (main != null && main.isActiveAndEnabled && TryCaptureCameraView(main, out var mainView))
            {
                liveMain = main;
                if (cameras.TryGetValue(main, out var state))
                    AddView(state, mainView);
                else
                    QueueCamera(new CameraObservation { Camera = main });
            }

            // The culling registry also includes idle cameras. Only recent renders supply secondary mip demand.
            foreach (var entry in cameras)
            {
                var camera = entry.Key;
                var state = entry.Value;
                if (camera != null && camera != liveMain && state.HasRendered && now - state.RenderedAt <= RenderedViewGraceSeconds)
                    AddView(state, state.RenderedView);
            }
        }

        private void AddView(CameraState state, CameraView cameraView)
        {
            var controller = state.Controller;
            bool hasController = controller != null;
            if (hasController ? !controller.enabled : !QualitySettings.streamingMipmapsAddAllCameras) return;
            var view = state.View;
            view.Position = cameraView.Position;
            view.CullingMask = cameraView.CullingMask;
            view.Orthographic = cameraView.Orthographic;
            view.Bias = hasController ? controller.streamingMipmapBias : 0f;
            view.ProjectionArea = cameraView.ProjectionArea;
            // Mirrors use an explicit culling matrix without the oblique clipping plane.
            GeometryUtility.CalculateFrustumPlanes(cameraView.CullingMatrix, view.Planes);
            views.Add(view);
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
            if (!TryCaptureCameraView(camera, out var view)) return;
            if (cameras.TryGetValue(camera, out var state))
            {
                state.RenderedView = view;
                state.RenderedAt = now;
                state.HasRendered = true;
                return;
            }
            QueueCamera(new CameraObservation { Camera = camera, View = view, RenderedAt = now, HasRendered = true });
        }

        private void QueueCamera(CameraObservation observation)
        {
            for (int i = 0; i < pendingCameraCount; i++)
            {
                if (pendingCameras[i].Camera != observation.Camera) continue;
                if (observation.HasRendered) pendingCameras[i] = observation;
                return;
            }
            if (pendingCameraCount < pendingCameras.Length) pendingCameras[pendingCameraCount++] = observation;
        }

        internal void RefreshPendingBindings(float now)
        {
            if (growTextureBuffers)
            {
                int capacity = checked(textureCapacity * 2);
                demands.EnsureCapacity(capacity);
                requests.EnsureCapacity(capacity);
                releases.Capacity = capacity;
                textureCapacity = capacity;
                growTextureBuffers = false;
            }

            var main = BasisLocalCameraDriver.CameraInstance;
            if (main != null && !cameras.ContainsKey(main)) QueueCamera(new CameraObservation { Camera = main });
            expiredCameras.Clear();
            foreach (var entry in cameras)
            {
                var camera = entry.Key;
                var state = entry.Value;
                if (camera == null || (camera != main && (!state.HasRendered || now - state.RenderedAt > RenderedViewGraceSeconds)))
                    expiredCameras.Add(camera);
                else if (state.Controller == null)
                    camera.TryGetComponent(out state.Controller);
            }
            foreach (var camera in expiredCameras) cameras.Remove(camera);
            expiredCameras.Clear();

            for (int i = 0; i < pendingCameraCount; i++)
            {
                var pending = pendingCameras[i];
                var camera = pending.Camera;
                if (camera == null || cameras.ContainsKey(camera)) continue;
                camera.TryGetComponent<StreamingController>(out var controller);
                cameras.Add(camera, new CameraState
                {
                    Controller = controller, RenderedView = pending.View, RenderedAt = pending.RenderedAt, HasRendered = pending.HasRendered,
                });
            }
            views.Capacity = Mathf.Max(views.Capacity, cameras.Count);
            Array.Clear(pendingCameras, 0, pendingCameraCount);
            pendingCameraCount = 0;

            for (int i = 0; i < pendingShaderCount; i++)
            {
                var shader = pendingShaders[i];
                if (shader != null) shaderProperties.GetValue(shader, BuildTextureProperties);
            }
            Array.Clear(pendingShaders, 0, pendingShaderCount);
            pendingShaderCount = 0;

            for (int i = 0; i < pendingFilterCount; i++)
            {
                var state = pendingFilters[i];
                if (state.Renderer != null) state.Renderer.TryGetComponent(out state.Filter);
                state.CheckFilter = true;
            }
            Array.Clear(pendingFilters, 0, pendingFilterCount);
            pendingFilterCount = 0;
        }

        private void QueueShader(Shader shader)
        {
            incompleteScan = true;
            for (int i = 0; i < pendingShaderCount; i++)
                if (pendingShaders[i] == shader) return;
            if (pendingShaderCount < pendingShaders.Length) pendingShaders[pendingShaderCount++] = shader;
        }

        private void Collect(RendererState state)
        {
            var renderer = state.Renderer;
            if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy || !renderer.HasPropertyBlock()) return;
            Bounds bounds = renderer.bounds;
            int viewCount = views.Count;
            bool visible = false;
            for (int i = 0; i < viewCount; i++)
                if (Sees(views[i], renderer, bounds)) { visible = true; break; }
            if (!visible) return;

            float metric = 0f;
            renderer.GetSharedMaterials(materials);
            renderer.GetPropertyBlock(rendererBlock);
            int materialCount = materials.Count;
            for (int slot = 0; slot < materialCount; slot++)
            {
                var material = materials[slot];
                if (material == null) continue;
                var shader = material.shader;
                if (shader == null) continue;
                renderer.GetPropertyBlock(materialBlock, slot);
                // Unity selects the per-material block wholesale when it exists; it does not
                // merge missing properties from the renderer-wide block into that block.
                var block = SelectBlock(rendererBlock, materialBlock);
                if (block.isEmpty) continue;
                if (!shaderProperties.TryGetValue(shader, out var properties))
                {
                    QueueShader(shader);
                    continue;
                }
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
            int count = batch?.Count ?? 0;
            if (count == 0) return;
            batch.Calculate();
            int i = 0;
            while (i < count)
            {
                var texture = batch.Textures[i];
                int mip = batch.Mips[i++];
                while (i < count && ReferenceEquals(texture, batch.Textures[i]))
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
            bool hasDemand = demands.TryGetValue(texture, out int existing);
            if (hasDemand && mip >= existing) return;
            if ((!hasDemand && demands.Count >= textureCapacity)
                || (requests.Count >= textureCapacity && !requests.ContainsKey(texture)))
            {
                incompleteScan = true;
                growTextureBuffers = true;
                return;
            }
            demands[texture] = mip;
            ApplyRequest(texture, mip, upgradesOnly: true);
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

        private float GetUvMetric(RendererState state, Bounds bounds)
        {
            var renderer = state.Renderer;
            if (state.CheckFilter && state.Filter == null && pendingFilterCount < pendingFilters.Length)
            {
                state.CheckFilter = false;
                pendingFilters[pendingFilterCount++] = state;
            }
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
            if (refreshBindings != null)
            {
                StopCoroutine(refreshBindings);
                refreshBindings = null;
            }
            ReleaseAll();
            ClearCameraViews();
            ClearPendingRendererBindings();
            scanning = false;
        }

        private void ClearPendingRendererBindings()
        {
            Array.Clear(pendingShaders, 0, pendingShaderCount);
            pendingShaderCount = 0;
            for (int i = 0; i < pendingFilterCount; i++) pendingFilters[i].CheckFilter = true;
            Array.Clear(pendingFilters, 0, pendingFilterCount);
            pendingFilterCount = 0;
        }

        private void ClearCameraViews()
        {
            cameras.Clear();
            views.Clear();
            Array.Clear(pendingCameras, 0, pendingCameraCount);
            pendingCameraCount = 0;
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
