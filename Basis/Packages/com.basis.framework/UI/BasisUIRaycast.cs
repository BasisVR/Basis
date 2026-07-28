using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.EventSystems;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;
using static BasisHeightDriver;

namespace Basis.Scripts.UI
{
    public class BasisUIRaycast
    {
        public BasisPointRaycaster BasisPointRaycaster;
        static LayerMask OverlayUI;
        static LayerMask UILayer;
        public static LayerMask UILayers;
        public Material lineMaterial;
        public float lineWidth = 0.01f;
        public LineRenderer LineRenderer;
        public static string LoadMaterialAddress = "Assets/UI/Material/RayCastMaterial.mat";
        public static string LoadUIRedicalAddress = "Assets/UI/Prefabs/highlightQuad.prefab";
        public GameObject highlightQuadInstance;
        private ActiveStateOfHightlight _highlightState;
        public ActiveStateOfHightlight HighlightState
        {
            get => _highlightState;
            set
            {
                if (_highlightState == value) return;
                _highlightState = value;
                highlightQuadInstance?.SetActive(value == ActiveStateOfHightlight.On);
            }
        }

        public enum ActiveStateOfHightlight
        {
            On,
            Off,
            NA
        }
        public BasisInput BasisInput;
        private string DeviceName;
        public bool HasLineRenderer = false;
        public bool HasRedicalRenderer = false;

        public bool CachedLinerRenderState = false;
        private float _nextPointerDebugTime;
        public RaycastHit PhysicHit;
        public bool DidPhysicHit = false;
        public Collider HitCollider;
        public Canvas FoundCanvas;
        public RaycastResult RaycastResult = new RaycastResult();

        public BasisPointerEventData CurrentEventData;
        public bool HadRaycastUITarget = false;
        public bool WasCorrectLayer = false;
        static readonly Vector3[] s_Corners = new Vector3[4];
        [SerializeField]
        public List<BasisRaycastUIHitData> SortedGraphics = new List<BasisRaycastUIHitData>();
        [SerializeField]
        public List<RaycastResult> SortedRays = new List<RaycastResult>();
        public List<Canvas> Results = new List<Canvas>();
        private readonly List<int> _uiHitOrder = new List<int>(16);
        // Per-hit layer/transform resolved once per frame — RaycastHit.collider/.transform are
        // re-resolving getters, and the hit sort would otherwise re-read them O(n^2).
        private int[] _hitLayers = System.Array.Empty<int>();
        private Transform[] _hitTransforms = System.Array.Empty<Transform>();
        // Canvas hierarchy under a hit collider is near-static; cache the walk and re-walk rarely.
        private struct CanvasCacheEntry { public Canvas[] Canvases; public int Frame; }
        private readonly Dictionary<Transform, CanvasCacheEntry> _canvasCache = new Dictionary<Transform, CanvasCacheEntry>();
        private const int CanvasCacheRevalidateFrames = 30;
        public bool IgnoreReversedGraphics = true;
        public Vector3 highlightQuadInitialSize;
        public bool HasOnPlayersHeightChanged = false;
        public BasisCursorType ActiveCursorType = BasisCursorType.Default;
        public Renderer ReticleRenderer;

        public BasisUIToolkitPanel HitToolkitPanel;
        public bool HadToolkitPanelTarget = false;
        public Vector2 ToolkitPanelPosition;
        public Vector3 ToolkitSurfacePoint;
        public readonly BasisUIToolkitPointer ToolkitPointer = new BasisUIToolkitPointer();
        private struct ToolkitPanelCacheEntry { public BasisUIToolkitPanel Panel; public int Frame; }
        private readonly Dictionary<Transform, ToolkitPanelCacheEntry> _toolkitPanelCache = new Dictionary<Transform, ToolkitPanelCacheEntry>();

        public void Initialize(BasisInput basisInput, BasisPointRaycaster pointRaycaster)
        {

            OverlayUI = LayerMask.NameToLayer("OverlayUI");
            UILayer = LayerMask.NameToLayer("UI");
            UILayers = LayerMask.GetMask("UI", "OverlayUI");
            CurrentEventData = new BasisPointerEventData(EventSystem.current);
            BasisInput = basisInput;
            BasisPointRaycaster = pointRaycaster;
            DeviceName = BasisInput.DeviceMatchSettings.DeviceID;
            ApplyStaticDataToRaycastResult();

            HasLineRenderer = false;
            HasRedicalRenderer = false;
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnPlayersHeightChanged;
            // Create the ray with the adjusted starting position and direction
            if (basisInput.DeviceMatchSettings.HasRayCastVisual)
            {
                // Add a Line Renderer component to the GameObject
                LineRenderer = BasisHelpers.GetOrAddComponent<LineRenderer>(BasisPointRaycaster.gameObject);
                LineRenderer.enabled = false;
                AsyncOperationHandle<Material> handle = Addressables.LoadAssetAsync<Material>(LoadMaterialAddress);
                Material InMemory = handle.WaitForCompletion();

                lineMaterial = InMemory;
                // Set the Line Renderer properties
                LineRenderer.material = lineMaterial;

                HasOnPlayersHeightChanged = true;
                // Set the number of points in the Line Renderer
                LineRenderer.positionCount = 2;
                HasLineRenderer = true;
                LineRenderer.enabled = HasLineRenderer;
                LineRenderer.numCapVertices = 32;
                LineRenderer.numCornerVertices = 32;
                LineRenderer.gameObject.layer = UILayer;

                LineRenderer.useWorldSpace = true;
                LineRenderer.textureMode = LineTextureMode.Tile;
                LineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                LineRenderer.startWidth = 0.1f;
                LineRenderer.endWidth = 0.1f;
                LineRenderer.useWorldSpace = true;
                LineRenderer.textureMode = LineTextureMode.Tile;
                LineRenderer.applyActiveColorSpace = false;
                LineRenderer.sortingLayerID = 0;
                LineRenderer.sortingOrder = short.MaxValue;
                BasisRaycastLineCustomization.StyleUiLine(LineRenderer);
            }
            if (basisInput.DeviceMatchSettings.HasRayCastRadical)
            {
                AsyncOperationHandle<GameObject> handle = Addressables.LoadAssetAsync<GameObject>(LoadUIRedicalAddress);
                GameObject InMemory = handle.WaitForCompletion();
                GameObject gameObject = GameObject.Instantiate(InMemory);
                gameObject.name = $"{DeviceName}_Redical";
                gameObject.transform.SetParent(BasisLocalPlayer.Instance.transform);
                highlightQuadInitialSize = gameObject.transform.localScale;
                highlightQuadInstance = gameObject;
                if (highlightQuadInstance.TryGetComponent(out Canvas Canvas))
                {
                    Canvas.worldCamera = BasisLocalCameraDriver.Instance.Camera;
                }
                ReticleRenderer = highlightQuadInstance.GetComponentInChildren<Renderer>();
                HighlightState = ActiveStateOfHightlight.NA;
                HasRedicalRenderer = true;
            }
            OnPlayersHeightChanged();

            CachedLinerRenderState = HasLineRenderer;
        }

        public void OnDeInitialize()
        {
            if (HasOnPlayersHeightChanged)
            {
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnPlayersHeightChanged;
            }
        }
        public void OnPlayersHeightChanged()
        {
            OnPlayersHeightChanged(HeightModeChange.OnTpose);
        }

        public void OnPlayersHeightChanged(HeightModeChange Mode)
        {
            float uiScale = BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            if (LineRenderer != null)
            {
                float size = lineWidth * uiScale;
                LineRenderer.startWidth = size;
                LineRenderer.endWidth = size;
            }
            if (highlightQuadInstance != null)
            {
                highlightQuadInstance.transform.localScale = highlightQuadInitialSize * uiScale;
            }
        }

        public void ApplyStaticDataToRaycastResult()
        {
            RaycastResult.displayIndex = 0;
            RaycastResult.index = 0;
            RaycastResult.depth = 0;
            RaycastResult.module = BasisPointRaycaster;
        }

        public void HandleUIRaycast()
        {
            SortedGraphics.Clear();
            SortedRays.Clear();
            HadRaycastUITarget = false;
            HadToolkitPanelTarget = false;
            HitToolkitPanel = null;

            int hitCount = BasisPointRaycaster.PhysicHitCount;
            if (hitCount == 0)
            {
                HandleNoHit();
                return;
            }

            // walk all forward physics hits, OverlayUI layer first, then ascending distance.
            // the first collider whose canvas hierarchy actually produces a UI graphic at the
            // current ray position wins.
            // this fixes issues like the menu being unresponsive when the player opens the photo camera
            var hits = BasisPointRaycaster.PhysicHits;
            if (_hitLayers.Length < hitCount)
            {
                _hitLayers = new int[hitCount];
                _hitTransforms = new Transform[hitCount];
            }
            _uiHitOrder.Clear();
            for (int i = 0; i < hitCount; i++)
            {
                // Resolve collider/layer/transform once; the sort and candidate loop reuse these.
                Collider c = hits[i].collider;
                if (c != null)
                {
                    _hitLayers[i] = c.gameObject.layer;
                    _hitTransforms[i] = hits[i].transform;
                    _uiHitOrder.Add(i);
                }
            }

            int orderCount = _uiHitOrder.Count;
            for (int i = 1; i < orderCount; i++)
            {
                int currentIndex = _uiHitOrder[i];
                int insertIndex = i - 1;

                while (insertIndex >= 0 && CompareUiHitOrder(_hitLayers, hits, currentIndex, _uiHitOrder[insertIndex]) < 0)
                {
                    _uiHitOrder[insertIndex + 1] = _uiHitOrder[insertIndex];
                    insertIndex--;
                }

                _uiHitOrder[insertIndex + 1] = currentIndex;
            }

            for (int idx = 0; idx < orderCount; idx++)
            {
                int hitIndex = _uiHitOrder[idx];
                Transform candidateTransform = _hitTransforms[hitIndex];
                if (candidateTransform == null)
                {
                    continue;
                }

                GetActiveCanvases(candidateTransform, Results);
                if (Results.Count != 0)
                {
                    SortedGraphics.Clear();
                    SortedRays.Clear();
                    PhysicHit = hits[hitIndex];
                    DidPhysicHit = true;
                    HitCollider = PhysicHit.collider;

                    if (RaycastToUI())
                    {
                        HadRaycastUITarget = true;
                        HandleDidHit();
                        return;
                    }
                }

                // UI Toolkit panels are invisible to the canvas walk above: they have no Graphic
                // and no entry in GraphicRegistry. They carry their own collider, so they arrive
                // here as an ordinary physics hit and inherit the OverlayUI/distance ordering.
                BasisUIToolkitPanel toolkitPanel = GetToolkitPanel(candidateTransform);
                if (toolkitPanel != null)
                {
                    PhysicHit = hits[hitIndex];
                    DidPhysicHit = true;
                    HitCollider = PhysicHit.collider;

                    if (RaycastToToolkitPanel(toolkitPanel))
                    {
                        HadToolkitPanelTarget = true;
                        HandleDidHit();
                        return;
                    }
                }
            }

            // no collider with a hittable UI graphic this frame....
            HandleNoHit();
        }

        // Re-walks the canvas hierarchy at most every N frames, then re-filters to active canvases
        // each frame. Structural changes are picked up within N frames; toggles take effect at once.
        private void GetActiveCanvases(Transform root, List<Canvas> output)
        {
            int frame = Time.frameCount;
            if (!_canvasCache.TryGetValue(root, out CanvasCacheEntry entry) || frame - entry.Frame >= CanvasCacheRevalidateFrames)
            {
                entry.Canvases = root.GetComponentsInChildren<Canvas>(true);
                entry.Frame = frame;
                _canvasCache[root] = entry;
            }

            output.Clear();
            Canvas[] canvases = entry.Canvases;
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas c = canvases[i];
                if (c != null && c.gameObject.activeInHierarchy)
                {
                    output.Add(c);
                }
            }
        }

        private BasisUIToolkitPanel GetToolkitPanel(Transform root)
        {
            int frame = Time.frameCount;
            if (!_toolkitPanelCache.TryGetValue(root, out ToolkitPanelCacheEntry entry) || frame - entry.Frame >= CanvasCacheRevalidateFrames)
            {
                entry.Panel = root.GetComponentInParent<BasisUIToolkitPanel>(true);
                entry.Frame = frame;
                _toolkitPanelCache[root] = entry;
            }

            BasisUIToolkitPanel panel = entry.Panel;
            if (panel == null || !panel.isActiveAndEnabled)
            {
                return null;
            }
            return panel;
        }

        private bool RaycastToToolkitPanel(BasisUIToolkitPanel panel)
        {
            if (!panel.TryGetPanelPosition(BasisPointRaycaster.ray, true, out Vector2 panelPosition, out Vector3 worldPosition, out float distance))
            {
                return false;
            }

            if (distance > BasisPointRaycaster.EffectiveMaxDistance)
            {
                return false;
            }

            HitToolkitPanel = panel;
            ToolkitPanelPosition = panelPosition;
            ToolkitSurfacePoint = worldPosition;
            return true;
        }

        private static int CompareUiHitOrder(int[] layers, RaycastHit[] hits, int leftIndex, int rightIndex)
        {
            bool leftIsOverlay = layers[leftIndex] == OverlayUI;
            bool rightIsOverlay = layers[rightIndex] == OverlayUI;

            if (leftIsOverlay != rightIsOverlay)
            {
                return leftIsOverlay ? -1 : 1;
            }

            return hits[leftIndex].distance.CompareTo(hits[rightIndex].distance);
        }

        private void HandleNoHit()
        {
            if (!TryRenderCapturedDrag())
            {
                ResetRenderers();
            }
            ResetCursorType();
            RaycastResult = new RaycastResult();
            PhysicHit = new RaycastHit();
            DidPhysicHit = false;
            HitCollider = null;
        }

        private bool TryRenderCapturedDrag()
        {
            BasisPointerEventData eventData = CurrentEventData;
            if (eventData == null || !eventData.WasLastDown || eventData.pointerDrag == null)
            {
                return false;
            }
            Transform planeTransform = eventData.DragPlaneTransform;
            if (planeTransform == null)
            {
                return false;
            }
            Plane plane = new Plane(planeTransform.forward, planeTransform.position);
            if (!plane.Raycast(BasisPointRaycaster.ray, out float enter))
            {
                return false;
            }

            Vector3 point = BasisPointRaycaster.ray.GetPoint(enter);
            Vector3 normal = -planeTransform.forward;

            if (HasLineRenderer)
            {
                if (!CachedLinerRenderState)
                {
                    LineRenderer.enabled = true;
                    CachedLinerRenderState = true;
                }
                float endOffset = BasisPlayerInteract.AvatarScaledRange(0.01f);
                Vector3 lineEnd = point + normal * endOffset;
                LineRenderer.SetPosition(0, BasisPlayerInteract.RayVisualStart(BasisPointRaycaster.ray.origin, lineEnd));
                LineRenderer.SetPosition(1, lineEnd);
            }

            if (HasRedicalRenderer)
            {
                bool show = !(BasisDeviceManagement.IsUserInDesktop() && BasisCursorManagement.ActiveLockState() != CursorLockMode.Locked);
                if (show)
                {
                    HighlightState = ActiveStateOfHightlight.On;
                    highlightQuadInstance.transform.SetPositionAndRotation(point, Quaternion.LookRotation(normal));
                }
                else
                {
                    HighlightState = ActiveStateOfHightlight.Off;
                }
            }

            return true;
        }

        bool ContainsLayer(LayerMask mask, int layer)
        {
            return (mask.value & (1 << layer)) != 0;
        }

        public static bool IsUILayer(int layer)
        {
            // Panels can enable before any raycaster has initialised the shared mask.
            if (UILayers.value == 0)
            {
                UILayers = LayerMask.GetMask("UI", "OverlayUI");
            }
            return (UILayers.value & (1 << layer)) != 0;
        }

        private void HandleDidHit()
        {
            WasCorrectLayer = ContainsLayer(UILayers, HitCollider.gameObject.layer);
            if (WasCorrectLayer)
            {
                UpdateRayCastResult();   // sets all RaycastResult data
                UpdateLineRenderer();    // updates the line renderer
                UpdateReticleRenderer(); // moves the Reticle renderer
                UpdateCursorType();      // detects cursor hints from hovered UI
            }
            else
            {
                if (!TryRenderCapturedDrag())
                {
                    ResetRenderers();
                }
                ResetCursorType();
            }
        }

        private void UpdateRayCastResult()
        {
            RaycastResult.gameObject = HitCollider.gameObject;
            RaycastResult.distance = PhysicHit.distance;
            if (BasisPointRaycaster.UseWorldPosition)
            {
                BasisPointRaycaster.ScreenPoint = BasisLocalCameraDriver.Instance.Camera.WorldToScreenPoint(BasisPointRaycaster.transform.position, Camera.MonoOrStereoscopicEye.Mono);
            }
            else
            {
                // we assign screenpoint manually example in BasisLocalCameraDriver
            }
            RaycastResult.screenPosition = BasisPointRaycaster.ScreenPoint;
            FoundCanvas = HitCollider.GetComponentInParent<Canvas>();
            if (FoundCanvas != null)
            {
                RaycastResult.sortingLayer = FoundCanvas.sortingLayerID;
                RaycastResult.sortingOrder = FoundCanvas.sortingOrder;
            }
            RaycastResult.worldPosition = BasisPointRaycaster.ray.origin + BasisPointRaycaster.ray.direction * PhysicHit.distance;
            RaycastResult.worldNormal = PhysicHit.normal;
        }

        private void UpdateLineRenderer()
        {
            if (HasLineRenderer && !CachedLinerRenderState)
            {
                LineRenderer.enabled = true;
                CachedLinerRenderState = true;
            }
            else if (!HasLineRenderer && CachedLinerRenderState)
            {
                LineRenderer.enabled = false;
                CachedLinerRenderState = false;
            }

            if (HasLineRenderer)
            {
                // Defensive: the line must be immune to ANY transform in its hierarchy. useWorldSpace
                // makes positions absolute, but normalize the renderer's lossyScale too (worlds/systems
                // that scale the player hierarchy otherwise scale the rendered width/geometry) and
                // re-assert world space in case anything flipped it.
                if (LineRenderer.useWorldSpace == false)
                {
                    LineRenderer.useWorldSpace = true;
                }
                Vector3 lossy = LineRenderer.transform.lossyScale;
                if (Mathf.Abs(lossy.x - 1f) > 1e-3f || Mathf.Abs(lossy.y - 1f) > 1e-3f || Mathf.Abs(lossy.z - 1f) > 1e-3f)
                {
                    Vector3 local = LineRenderer.transform.localScale;
                    LineRenderer.transform.localScale = new Vector3(
                        lossy.x > 1e-6f ? local.x / lossy.x : 1f,
                        lossy.y > 1e-6f ? local.y / lossy.y : 1f,
                        lossy.z > 1e-6f ? local.z / lossy.z : 1f);
                }

                // World-space standoff so the line tip doesn't z-fight the panel — MUST scale with the
                // avatar: a constant 0.01 m is a full body-relative metre of "line stops short of the
                // canvas" at 0.01 avatar scale (field report: "the distance from the canvas").
                float endOffset = BasisPlayerInteract.AvatarScaledRange(0.01f);

                Vector3 end = GetVisualSurfacePoint() + PhysicHit.normal * endOffset;
                Vector3 start = BasisPlayerInteract.RayVisualStart(BasisPointRaycaster.ray.origin, end);

                LineRenderer.SetPosition(0, start);
                LineRenderer.SetPosition(1, end);
            }

            // Pointer-chain diagnostic (tick EnableDebug on this hand's BasisPointRaycaster): one line
            // per second with every stage of the pointer, so a scale/alignment mismatch between what is
            // FELT (hand), COMPUTED (ray/hit) and DRAWN (line/reticle/interact line) reads directly off
            // the log. All values world-space.
            if (BasisPointRaycaster.EnableDebug && Time.unscaledTime >= _nextPointerDebugTime)
            {
                _nextPointerDebugTime = Time.unscaledTime + 1f;
                var input = BasisPointRaycaster.BasisInput;
                Vector3 handWorld = input != null ? input.transform.position : Vector3.zero;
                string interactLine = input != null && input.InteractionLineRenderer != null && input.InteractionLineRenderer.enabled
                    ? $"interactLine {input.InteractionLineRenderer.GetPosition(0)}->{input.InteractionLineRenderer.GetPosition(1)}"
                    : "interactLine off";
                // bounds.center is where Unity is ACTUALLY rendering the line — if it disagrees with
                // the midpoint of the written positions, the mismatch is in rendering space, and the
                // ratio names the culprit factor.
                Vector3 writtenMid = (LineRenderer.GetPosition(0) + LineRenderer.GetPosition(1)) * 0.5f;
                BasisDebug.Log(
                    $"PointerChain dev={input?.UniqueDeviceIdentifier} scale={BasisHeightDriver.DeviceScale:F3} " +
                    $"handVisual={handWorld} rayOrigin={BasisPointRaycaster.ray.origin} hit={PhysicHit.point} " +
                    $"uiLine {LineRenderer.GetPosition(0)}->{LineRenderer.GetPosition(1)} worldSpace={LineRenderer.useWorldSpace} " +
                    $"renderedCenter={LineRenderer.bounds.center} writtenMid={writtenMid} lineLossy={LineRenderer.transform.lossyScale} " +
                    $"playerLossy={(BasisLocalPlayer.Instance != null ? BasisLocalPlayer.Instance.transform.lossyScale.ToString() : "n/a")} " +
                    $"reticle={(HasRedicalRenderer && highlightQuadInstance != null ? highlightQuadInstance.transform.position.ToString() : "n/a")} {interactLine}",
                    BasisDebug.LogTag.Input);
            }
        }

        /// <summary>
        /// The physics hit lands on the COLLIDER face, which sits half the collider depth in front of
        /// the visual canvas plane (unit-scale world canvases: several real centimetres — the "distance
        /// from the canvas"). Snap the visual contact point along the ray onto the canvas plane so the
        /// line tip and reticle touch the panel the user actually sees, whatever the collider depth
        /// convention. Falls back to the raw hit when no canvas is resolved or the geometry is odd.
        /// </summary>
        private Vector3 GetVisualSurfacePoint()
        {
            // Panel hits already resolve on the panel plane, not the collider face.
            if (HadToolkitPanelTarget && HitToolkitPanel != null)
            {
                return ToolkitSurfacePoint;
            }

            Vector3 point = PhysicHit.point;
            if (FoundCanvas == null)
            {
                return point;
            }
            Transform canvasTransform = FoundCanvas.transform;
            Plane plane = new Plane(canvasTransform.forward, canvasTransform.position);
            if (plane.Raycast(BasisPointRaycaster.ray, out float enter))
            {
                Vector3 snapped = BasisPointRaycaster.ray.GetPoint(enter);
                // Cap the correction: it should only ever bridge the collider-face standoff, never
                // fling the tip on a grazing-angle intersection.
                if ((snapped - point).sqrMagnitude <= 0.25f)
                {
                    return snapped;
                }
            }
            return point;
        }

        private void UpdateReticleRenderer()
        {
            if (!HasRedicalRenderer)
            {
                return;
            }

            // Hide on desktop while the cursor is unlocked (free mouse uses the OS cursor, not this reticle).
            bool show = DidPhysicHit && !(BasisDeviceManagement.IsUserInDesktop() && BasisCursorManagement.ActiveLockState() != CursorLockMode.Locked);

            if (show)
            {
                HighlightState = ActiveStateOfHightlight.On;
                highlightQuadInstance.transform.SetPositionAndRotation(GetVisualSurfacePoint(), Quaternion.LookRotation(PhysicHit.normal));
            }
            else
            {
                HighlightState = ActiveStateOfHightlight.Off;
            }
        }

        private static readonly int ReticleColorID = Shader.PropertyToID("_Color");
        private static readonly int ReticleMainTexID = Shader.PropertyToID("_MainTex");

        private void UpdateCursorType()
        {
            BasisCursorType newType = BasisCursorType.Default;
            Texture2D customTex = null;

            if (HadToolkitPanelTarget)
            {
                newType = BasisCursorType.Pointer;
            }
            else if (SortedGraphics.Count > 0 && SortedGraphics[0].graphic != null)
            {
                var graphic = SortedGraphics[0].graphic;
                if (graphic.TryGetComponent(out BasisCursorHint hint))
                {
                    newType = hint.CursorType;
                    customTex = hint.CustomTexture;
                }
                else if (graphic.GetComponentInParent<BasisCursorHint>() is BasisCursorHint parentHint && parentHint != null)
                {
                    newType = parentHint.CursorType;
                    customTex = parentHint.CustomTexture;
                }
                else
                {
                    newType = BasisCursorType.Pointer;
                }
            }

            if (ActiveCursorType != newType)
            {
                ActiveCursorType = newType;
                ApplyCursorVisual(newType, customTex);
            }

            BasisCursorManagement.SetCursorType(newType, customTex);
        }

        private void ResetCursorType()
        {
            if (ActiveCursorType != BasisCursorType.Default)
            {
                ActiveCursorType = BasisCursorType.Default;
                ApplyCursorVisual(BasisCursorType.Default);
                BasisCursorManagement.SetCursorType(BasisCursorType.Default);
            }
        }

        private void ApplyCursorVisual(BasisCursorType cursorType, Texture2D customTexture = null)
        {
            if (!HasRedicalRenderer || ReticleRenderer == null)
            {
                return;
            }

            Color color;
            switch (cursorType)
            {
                case BasisCursorType.Pointer:
                    color = new Color(0.3f, 0.6f, 1f, 0.8f);
                    break;
                case BasisCursorType.Text:
                    color = new Color(1f, 1f, 1f, 0.9f);
                    break;
                case BasisCursorType.Grab:
                    color = new Color(0.4f, 1f, 0.4f, 0.8f);
                    break;
                case BasisCursorType.Grabbing:
                    color = new Color(1f, 0.8f, 0.2f, 0.9f);
                    break;
                case BasisCursorType.NotAllowed:
                    color = new Color(1f, 0.3f, 0.3f, 0.9f);
                    break;
                case BasisCursorType.Move:
                    color = new Color(0.6f, 0.8f, 1f, 0.8f);
                    break;
                default:
                    color = Color.white;
                    break;
            }

            MaterialPropertyBlock block = new MaterialPropertyBlock();
            ReticleRenderer.GetPropertyBlock(block);
            block.SetColor(ReticleColorID, color);

            if (cursorType == BasisCursorType.Custom && customTexture != null)
            {
                block.SetTexture(ReticleMainTexID, customTexture);
            }

            ReticleRenderer.SetPropertyBlock(block);
        }

        private void ResetRenderers()
        {
            if (CachedLinerRenderState && HasLineRenderer)
            {
                LineRenderer.enabled = false;
                CachedLinerRenderState = false;
            }

            if (HasRedicalRenderer)
            {
                HighlightState = ActiveStateOfHightlight.Off;
            }
        }

        // NEW: priority helper so OverlayUI canvases always win
        private static int GetCanvasPriority(Canvas canvas)
        {
            if (canvas == null)
                return 0;

            // Any canvas on the OverlayUI layer gets a huge priority bump
            if (canvas.gameObject.layer == OverlayUI)
                return 1000;

            // Normal canvases
            return 0;
        }

        // Per-canvas scratch for RaycastToUI, so accumulation into SortedGraphics survives
        // SortedRaycastGraphics' Clear. Not readonly: SortedRaycastGraphics takes it by ref.
        private List<BasisRaycastUIHitData> CanvasScratchGraphics = new List<BasisRaycastUIHitData>();

        /// <summary>
        /// Sort order: OverlayUI always first, then sortingOrder (higher first). A shared
        /// IComparer because this sort runs every frame the ray is over UI — a capturing
        /// lambda here cost a delegate plus Sort's comparer wrapper per call.
        /// </summary>
        private sealed class CanvasPriorityComparer : IComparer<Canvas>
        {
            public static readonly CanvasPriorityComparer Instance = new CanvasPriorityComparer();
            public int Compare(Canvas c1, Canvas c2)
            {
                int priorityCompare = GetCanvasPriority(c2).CompareTo(GetCanvasPriority(c1));
                if (priorityCompare != 0)
                    return priorityCompare;

                // Same priority class: use sortingOrder like before
                return c2.sortingOrder.CompareTo(c1.sortingOrder);
            }
        }

        public bool RaycastToUI()
        {
            Results.Sort(CanvasPriorityComparer.Instance);

            // Accumulate hits across EVERY candidate canvas instead of returning at the
            // first canvas that has any. Graphics register to the nearest Canvas up their
            // parent chain, so a nested isolation canvas owns its children's graphics —
            // the old first-canvas-wins walk let the root menu canvas shadow every nested
            // one, which made controls inside an IsolateAsCanvas group unclickable.
            // OverlayUI still beats normal canvases: once a higher priority class has a
            // hit, lower classes stop being tested.
            int Count = Results.Count;
            int hitPriorityClass = int.MinValue;
            for (int Index = 0; Index < Count; Index++)
            {
                Canvas CurrentTopLevel = Results[Index];
                if (CurrentTopLevel == null)
                {
                    continue;
                }
                int priorityClass = GetCanvasPriority(CurrentTopLevel);
                if (SortedGraphics.Count != 0 && priorityClass < hitPriorityClass)
                {
                    break;
                }
                if (CurrentTopLevel.worldCamera == null)
                {
                    CurrentTopLevel.worldCamera = BasisLocalCameraDriver.Instance.Camera;
                }
                SortedRaycastGraphics(CurrentTopLevel, CurrentTopLevel.worldCamera, ref CanvasScratchGraphics);
                if (CanvasScratchGraphics.Count == 0)
                {
                    continue;
                }
                AppendValidHits(CurrentTopLevel, CanvasScratchGraphics, SortedGraphics, SortedRays);
                if (SortedGraphics.Count != 0)
                {
                    hitPriorityClass = priorityClass > hitPriorityClass ? priorityClass : hitPriorityClass;
                }
            }

            if (SortedGraphics.Count == 0)
            {
                return false;
            }
            SortCombinedHits();
            return true;
        }

        /// <summary>
        /// Validates one canvas's ray-intersecting graphics and appends the survivors as an
        /// ALIGNED pair into the combined hit lists — the press pipeline consumes index 0 of
        /// both, so they must describe the same graphic. (The old split — every graphic in one
        /// list, only valid ones in the other — could disagree after filtering.)
        /// </summary>
        private void AppendValidHits(Canvas canvas, List<BasisRaycastUIHitData> canvasHits, List<BasisRaycastUIHitData> hitDataOut, List<RaycastResult> rayResultsOut)
        {
            int count = canvasHits.Count;
            for (int i = 0; i < count; i++)
            {
                BasisRaycastUIHitData hitData = canvasHits[i];
                if (hitData.graphic == null)
                {
                    continue;
                }
                var go = hitData.graphic.gameObject;
                bool validHit = true;
                if (IgnoreReversedGraphics)
                {
                    var forward = BasisPointRaycaster.ray.direction;
                    var goDirection = go.transform.rotation * Vector3.forward;
                    validHit = Vector3.Dot(forward, goDirection) > 0;
                }

                validHit &= hitData.distance < BasisPointRaycaster.EffectiveMaxDistance;
                if (!validHit)
                {
                    continue;
                }

                var castResult = new RaycastResult
                {
                    gameObject = go,
                    module = BasisPointRaycaster,
                    distance = hitData.distance,
                    index = rayResultsOut.Count,
                    depth = hitData.graphic.depth,
                    sortingLayer = canvas.sortingLayerID,
                    sortingOrder = canvas.sortingOrder,
                    worldPosition = hitData.worldHitPosition,
                    worldNormal = -go.transform.forward,
                    screenPosition = hitData.screenPosition,
                    displayIndex = hitData.displayIndex,
                };
                rayResultsOut.Add(castResult);
                hitDataOut.Add(hitData);
            }
        }

        /// <summary>
        /// Lockstep-sorts the combined hit lists so index 0 is the visually topmost graphic
        /// across every canvas tested. Within one canvas that is plain depth order; across
        /// canvases a higher sortingOrder wins, then a DESCENDANT canvas beats its ancestor
        /// (a nested canvas renders inline on top of the ancestor content behind it), then
        /// ray distance breaks ties between unrelated canvases.
        /// </summary>
        private void SortCombinedHits()
        {
            int count = SortedGraphics.Count;
            if (count <= 1)
            {
                return;
            }
            for (int i = 1; i < count; i++)
            {
                BasisRaycastUIHitData g = SortedGraphics[i];
                RaycastResult r = SortedRays[i];
                int j = i - 1;
                while (j >= 0 && CompareCombinedHits(SortedGraphics[j], g) > 0)
                {
                    SortedGraphics[j + 1] = SortedGraphics[j];
                    SortedRays[j + 1] = SortedRays[j];
                    j--;
                }
                SortedGraphics[j + 1] = g;
                SortedRays[j + 1] = r;
            }
        }

        private static int CompareCombinedHits(in BasisRaycastUIHitData a, in BasisRaycastUIHitData b)
        {
            Canvas canvasA = a.graphic.canvas;
            Canvas canvasB = b.graphic.canvas;
            if (canvasA == canvasB)
            {
                int depthCompare = b.graphic.depth.CompareTo(a.graphic.depth);
                if (depthCompare != 0)
                {
                    return depthCompare;
                }
                return a.distance.CompareTo(b.distance);
            }

            int orderCompare = canvasB.sortingOrder.CompareTo(canvasA.sortingOrder);
            if (orderCompare != 0)
            {
                return orderCompare;
            }

            Transform transformA = canvasA.transform;
            Transform transformB = canvasB.transform;
            if (transformA.IsChildOf(transformB))
            {
                return -1;
            }
            if (transformB.IsChildOf(transformA))
            {
                return 1;
            }

            return a.distance.CompareTo(b.distance);
        }

        public void Sort<T>(IList<T> hits, Comparison<T> comparer) where T : struct => Sort(hits, comparer, hits.Count);

        public static void Sort<T>(IList<T> hits, Comparison<T> comparer, int count) where T : struct
        {
            if (count <= 1)
                return;

            bool fullPass;
            do
            {
                fullPass = true;
                for (var i = 1; i < count; ++i)
                {
                    var result = comparer(hits[i - 1], hits[i]);
                    if (result > 0)
                    {
                        (hits[i - 1], hits[i]) = (hits[i], hits[i - 1]);
                        fullPass = false;
                    }
                }
            } while (fullPass == false);
        }

        public void SortedRaycastGraphics(Canvas canvas, Camera eventCamera, ref List<BasisRaycastUIHitData> results)
        {
            var graphics = GraphicRegistry.GetRaycastableGraphicsForCanvas(canvas);

            results.Clear();
            for (int i = 0; i < graphics.Count; ++i)
            {
                var graphic = graphics[i];

                if (!ShouldTestGraphic(graphic, BasisPlayerInteract.Mask))
                    continue;

                var raycastPadding = graphic.raycastPadding;

                if (RayIntersectsRectTransform(graphic.rectTransform, raycastPadding, BasisPointRaycaster.ray, out var worldPos, out var distance))
                {
                    if (distance <= BasisPointRaycaster.EffectiveMaxDistance)
                    {
                        Vector2 screenPos = eventCamera.WorldToScreenPoint(worldPos);
                        // mask/image intersection - See Unity docs on eventAlphaThreshold for when this does anything
                        if (graphic.Raycast(screenPos, eventCamera))
                        {
                            results.Add(new BasisRaycastUIHitData(graphic, worldPos, screenPos, distance, eventCamera.targetDisplay));
                        }
                    }
                }
            }

            Sort(results, (a, b) => b.graphic.depth.CompareTo(a.graphic.depth));
        }

        public bool ShouldTestGraphic(Graphic graphic, LayerMask layerMask)
        {
            // -1 means it hasn't been processed by the canvas, which means it isn't actually drawn
            if (graphic.depth == -1 || !graphic.raycastTarget || graphic.canvasRenderer.cull)
                return false;

            if (((1 << graphic.gameObject.layer) & layerMask) == 0)
                return false;

            return true;
        }

        public bool SphereIntersectsRectTransform(RectTransform transform, Vector4 raycastPadding, Vector3 from, out Vector3 worldPosition, out float distance)
        {
            var plane = GetRectTransformPlane(transform, raycastPadding, s_Corners);
            var closestPoint = plane.ClosestPointOnPlane(from);
            var ray = new Ray(from, closestPoint - from);
            return RayIntersectsRectTransform(ray, plane, out worldPosition, out distance);
        }

        public bool RayIntersectsRectTransform(RectTransform transform, Vector4 raycastPadding, Ray ray, out Vector3 worldPosition, out float distance)
        {
            var plane = GetRectTransformPlane(transform, raycastPadding, s_Corners);
            return RayIntersectsRectTransform(ray, plane, out worldPosition, out distance);
        }

        public bool RayIntersectsRectTransform(Ray ray, Plane plane, out Vector3 worldPosition, out float distance)
        {
            if (plane.Raycast(ray, out var enter))
            {
                var intersection = ray.GetPoint(enter);

                var bottomEdge = s_Corners[3] - s_Corners[0];
                var leftEdge = s_Corners[1] - s_Corners[0];
                var bottomDot = Vector3.Dot(intersection - s_Corners[0], bottomEdge);
                var leftDot = Vector3.Dot(intersection - s_Corners[0], leftEdge);

                // If the intersection is right of the left edge and above the bottom edge.
                if (leftDot >= 0f && bottomDot >= 0f)
                {
                    var topEdge = s_Corners[1] - s_Corners[2];
                    var rightEdge = s_Corners[3] - s_Corners[2];
                    var topDot = Vector3.Dot(intersection - s_Corners[2], topEdge);
                    var rightDot = Vector3.Dot(intersection - s_Corners[2], rightEdge);

                    // If the intersection is left of the right edge, and below the top edge
                    if (topDot >= 0f && rightDot >= 0f)
                    {
                        worldPosition = intersection;
                        distance = enter;
                        return true;
                    }
                }
            }

            worldPosition = Vector3.zero;
            distance = 0f;
            return false;
        }

        public Plane GetRectTransformPlane(RectTransform transform, Vector4 raycastPadding, Vector3[] fourCornersArray)
        {
            GetRectTransformWorldCorners(transform, raycastPadding, fourCornersArray);
            return new Plane(fourCornersArray[0], fourCornersArray[1], fourCornersArray[2]);
        }

        // This method is similar to RecTransform.GetWorldCorners, but with support for the raycastPadding offset.
        public void GetRectTransformWorldCorners(RectTransform transform, Vector4 offset, Vector3[] fourCornersArray)
        {
            if (fourCornersArray == null || fourCornersArray.Length < 4)
            {
                BasisDebug.LogError("Calling GetRectTransformWorldCorners with an array that is null or has less than 4 elements.");
                return;
            }

            // GraphicRaycaster.Raycast uses RectTransformUtility.RectangleContainsScreenPoint instead,
            // which redirects to PointInRectangle defined in RectTransformUtil.cpp. However, that method
            // uses the Camera to convert from the given screen point to a ray, but this class uses
            // the ray from the Ray Interactor that feeds the event data.
            // Offset calculation for raycastPadding from PointInRectangle method, which replaces RectTransform.GetLocalCorners.
            var rect = transform.rect;
            var x0 = rect.x + offset.x;
            var y0 = rect.y + offset.y;
            var x1 = rect.xMax - offset.z;
            var y1 = rect.yMax - offset.w;
            fourCornersArray[0] = new Vector3(x0, y0, 0f);
            fourCornersArray[1] = new Vector3(x0, y1, 0f);
            fourCornersArray[2] = new Vector3(x1, y1, 0f);
            fourCornersArray[3] = new Vector3(x1, y0, 0f);

            // Transform the local corners to world space, which is from RectTransform.GetWorldCorners.
            var localToWorldMatrix = transform.localToWorldMatrix;
            for (var index = 0; index < 4; ++index)
            {
                fourCornersArray[index] = localToWorldMatrix.MultiplyPoint(fourCornersArray[index]);
            }
        }
    }
}
