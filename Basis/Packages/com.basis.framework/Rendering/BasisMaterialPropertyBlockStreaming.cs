using System.Collections.Generic;
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
    [DefaultExecutionOrder(10000)]
    public partial class BasisMaterialPropertyBlockStreaming : MonoBehaviour
    {
        private static BasisMaterialPropertyBlockStreaming instance;
        private const float UpdateInterval = 0.1f;
        private const int RenderersPerFrame = 128;

        private sealed class RendererState
        {
            internal Renderer Renderer;
            internal Mesh Mesh;
            internal float UvMetric;
            internal readonly List<Material> Materials = new List<Material>();
            internal readonly Dictionary<Shader, TextureProperty[]> Properties = new Dictionary<Shader, TextureProperty[]>();
        }

        private struct TextureProperty
        {
            internal int Id;
            internal int ScaleOffsetId;
        }

        private sealed class View
        {
            internal Camera Camera;
            internal readonly Plane[] Planes = new Plane[6];
            internal float ProjectionArea;
            internal float Bias;
        }

        private struct Request
        {
            internal int Previous;
            internal int Applied;
        }

        private readonly List<RendererState> renderers = new List<RendererState>();
        private readonly HashSet<Renderer> registered = new HashSet<Renderer>();
        private readonly List<Renderer> discovery = new List<Renderer>();
        private readonly List<View> views = new List<View>();
        private MaterialPropertyBlock rendererBlock;
        private MaterialPropertyBlock materialBlock;
        private readonly Dictionary<Texture2D, int> demands = new Dictionary<Texture2D, int>();
        private readonly Dictionary<Texture2D, Request> requests = new Dictionary<Texture2D, Request>();
        private readonly List<Texture2D> releases = new List<Texture2D>();
        private Camera[] cameras = new Camera[8];
        private int viewCount;
        private int scanIndex;
        private int scanCount;
        private bool scanning;
        private float nextScan;

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
            foreach (var renderer in items)
            {
                if (renderer != null && instance.registered.Add(renderer))
                    instance.renderers.Add(new RendererState { Renderer = renderer });
            }
        }

        private void Awake()
        {
            rendererBlock = new MaterialPropertyBlock();
            materialBlock = new MaterialPropertyBlock();
            instance = this;
            ContentPoliceControl.OnBeforeContentActivated += RegisterRoot;
            SceneManager.sceneLoaded += SceneLoaded;
            // Includes existing scene roots and DontDestroyOnLoad objects. Later loads use hooks.
            RegisterRenderers(FindObjectsByType<Renderer>(FindObjectsInactive.Include));
        }

        private void RegisterRoot(GameObject root)
        {
            root.GetComponentsInChildren(true, discovery);
            RegisterRenderers(discovery);
            discovery.Clear();
        }

        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            foreach (var root in scene.GetRootGameObjects()) RegisterRoot(root);
        }

        private void LateUpdate()
        {
            if (!QualitySettings.streamingMipmapsActive)
            {
                ReleaseAll();
                scanning = false;
                return;
            }
            if (!scanning)
            {
                if (Time.unscaledTime < nextScan) return;
                nextScan = Time.unscaledTime + UpdateInterval;
                PrepareViews();
                demands.Clear();
                scanIndex = 0;
                scanCount = renderers.Count;
                scanning = true;
            }

            int budget = Mathf.Clamp(QualitySettings.streamingMipmapsRenderersPerFrame, 1, RenderersPerFrame);
            int end = Mathf.Min(scanIndex + budget, scanCount);
            while (scanIndex < end)
            {
                var state = renderers[scanIndex];
                if (state.Renderer == null)
                {
                    registered.Remove(state.Renderer);
                    renderers.RemoveAt(scanIndex);
                    scanCount--;
                    end--;
                    continue;
                }
                Collect(state);
                scanIndex++;
            }
            if (scanIndex < scanCount) return;
            ApplyRequests();
            scanning = false;
        }

        private void PrepareViews()
        {
            int count = Camera.allCamerasCount;
            if (cameras.Length < count) cameras = new Camera[count];
            count = Camera.GetAllCameras(cameras);
            viewCount = 0;
            for (int i = 0; i < count; i++)
            {
                var camera = cameras[i];
                if (camera == null || !camera.isActiveAndEnabled || camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.SceneView) continue;
                bool hasController = camera.TryGetComponent<StreamingController>(out var controller);
                if (hasController ? !controller.enabled : !QualitySettings.streamingMipmapsAddAllCameras) continue;
                if (camera.pixelHeight <= 0 || camera.pixelWidth <= 0) continue;
                if (views.Count == viewCount) views.Add(new View());
                var view = views[viewCount++];
                view.Camera = camera;
                view.Bias = hasController ? controller.streamingMipmapBias : 0f;
                float pixelsPerUnit = camera.orthographic
                    ? camera.pixelHeight / Mathf.Max(0.0001f, 2f * camera.orthographicSize)
                    : camera.pixelHeight / Mathf.Max(0.0001f, 2f * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f));
                view.ProjectionArea = pixelsPerUnit * pixelsPerUnit * Mathf.Max(1f, camera.aspect);
                GeometryUtility.CalculateFrustumPlanes(camera, view.Planes);
            }
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

            Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                : renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;
            if (state.Mesh != mesh)
            {
                state.Mesh = mesh;
                state.UvMetric = mesh != null ? mesh.GetUVDistributionMetric(0) : 0f;
            }
            float metric = state.UvMetric;
            Vector3 scale = renderer.transform.lossyScale;
            float x = Mathf.Abs(scale.x), y = Mathf.Abs(scale.y), z = Mathf.Abs(scale.z);
            metric *= Mathf.Max(x * y, Mathf.Max(x * z, y * z));
            // Procedural/non-mesh renderers may have no UV metric. Conservatively assume one
            // UV tile across their bounds; supported meshes use their imported metric above.
            if (!(metric > 0f) || float.IsInfinity(metric)) metric = Mathf.Max(bounds.size.sqrMagnitude, 0.0001f);

            renderer.GetSharedMaterials(state.Materials);
            renderer.GetPropertyBlock(rendererBlock);
            for (int slot = 0; slot < state.Materials.Count; slot++)
            {
                var material = state.Materials[slot];
                if (material == null || material.shader == null) continue;
                renderer.GetPropertyBlock(materialBlock, slot);
                // Unity selects the per-material block wholesale when it exists; it does not
                // merge missing properties from the renderer-wide block into that block.
                var block = SelectBlock(rendererBlock, materialBlock);
                if (block.isEmpty) continue;
                if (!state.Properties.TryGetValue(material.shader, out var properties))
                {
                    var found = new List<TextureProperty>();
                    var shader = material.shader;
                    for (int p = 0; p < shader.GetPropertyCount(); p++)
                        if (shader.GetPropertyType(p) == ShaderPropertyType.Texture)
                            found.Add(new TextureProperty { Id = shader.GetPropertyNameId(p), ScaleOffsetId = Shader.PropertyToID(shader.GetPropertyName(p) + "_ST") });
                    properties = found.ToArray();
                    state.Properties.Add(shader, properties);
                }
                foreach (var property in properties)
                {
                    var texture = block.GetTexture(property.Id) as Texture2D;
                    if (texture == null || !texture.streamingMipmaps) continue;
                    Vector2 uvScale = material.GetTextureScale(property.Id);
                    if (block.HasVector(property.ScaleOffsetId))
                    {
                        Vector4 st = block.GetVector(property.ScaleOffsetId);
                        uvScale = new Vector2(st.x, st.y);
                    }
                    float uvArea = Mathf.Max(Mathf.Abs(uvScale.x * uvScale.y), 0.000001f);
                    int mip = texture.mipmapCount - 1;
                    for (int v = 0; v < viewCount; v++)
                    {
                        var view = views[v];
                        if (!Sees(view, renderer, bounds)) continue;
                        float distanceSquared = view.Camera.orthographic ? 1f : bounds.SqrDistance(view.Camera.transform.position);
                        mip = Mathf.Min(mip, CalculateMip(texture.width, texture.height, distanceSquared, metric / uvArea, view.ProjectionArea, view.Bias, texture.mipmapCount));
                    }
                    // A texture may also be used by ordinary materials closer to a camera.
                    // Preserve that higher native demand while adding the block's demand.
                    mip = Mathf.Min(mip, texture.calculatedMipmapLevel);
                    if (!demands.TryGetValue(texture, out int existing) || mip < existing) demands[texture] = mip;
                }
            }
        }

        private static bool Sees(View view, Renderer renderer, Bounds bounds) => view.Camera != null
            && (view.Camera.cullingMask & (1 << renderer.gameObject.layer)) != 0
            && GeometryUtility.TestPlanesAABB(view.Planes, bounds);

        internal static MaterialPropertyBlock SelectBlock(MaterialPropertyBlock renderer, MaterialPropertyBlock material) => material.isEmpty ? renderer : material;

        // Derived from Unity's Mesh.GetUVDistributionMetric example: UV texel area divided by
        // projected world area, converted from an area ratio into a mip (linear) ratio.
        internal static int CalculateMip(int width, int height, float distanceSquared, float uvMetric, float projectionArea, float bias, int mipCount)
        {
            float ratio = (float)width * height * Mathf.Max(distanceSquared, 0.000001f)
                / Mathf.Max(uvMetric * projectionArea, 0.000001f);
            float mip = 0.5f * Mathf.Log(ratio, 2f) + bias;
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
            foreach (var demand in demands)
            {
                var texture = demand.Key;
                if (texture == null) continue;
                int current = texture.requestedMipmapLevel;
                if (!requests.TryGetValue(texture, out var request)) request.Previous = current;
                else if (current != request.Applied) request.Previous = current;
                int mip = request.Previous >= 0 ? Mathf.Min(request.Previous, demand.Value) : demand.Value;
                if (current != mip) texture.requestedMipmapLevel = mip;
                request.Applied = mip;
                requests[texture] = request;
            }
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
        }

        private void OnDisable() { ReleaseAll(); scanning = false; }

        private void OnDestroy()
        {
            ReleaseAll();
            ContentPoliceControl.OnBeforeContentActivated -= RegisterRoot;
            SceneManager.sceneLoaded -= SceneLoaded;
            if (instance == this) instance = null;
        }
    }
}
