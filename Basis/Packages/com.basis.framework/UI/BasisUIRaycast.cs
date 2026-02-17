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
using UnityEngine.UIElements;
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
        public ActiveStateOfHightlight HighlightState;
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
        public RaycastHit PhysicHit;
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
        public bool IgnoreReversedGraphics = true;
        public Vector3 highlightQuadInitalSize;
        public bool HasOnPlayersHeightChanged = false;

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
                LineRenderer.widthMultiplier = BasisPlayerInteract.interactLineWidth;
                LineRenderer.useWorldSpace = true;
                LineRenderer.textureMode = LineTextureMode.Tile;
                LineRenderer.applyActiveColorSpace = false;
                var g = new Gradient();
                g.SetKeys(
                    new[]
                    {
        new GradientColorKey(new Color(0.3019608f,0.09411766f,0.2980392f), 0f),
        new GradientColorKey(new Color(0.1058824f,0.1411765f,0.3137255f), 1f),
                    },
                    new[]
                    {
        new GradientAlphaKey(1.00f, 1),
        new GradientAlphaKey(1, 0),
                    }
                );

                LineRenderer.colorGradient = g;
            }
            if (basisInput.DeviceMatchSettings.HasRayCastRadical)
            {
                AsyncOperationHandle<GameObject> handle = Addressables.LoadAssetAsync<GameObject>(LoadUIRedicalAddress);
                GameObject InMemory = handle.WaitForCompletion();
                GameObject gameObject = GameObject.Instantiate(InMemory);
                gameObject.name = $"{DeviceName}_Redical";
                gameObject.transform.SetParent(BasisLocalPlayer.Instance.transform);
                highlightQuadInitalSize = gameObject.transform.localScale;
                highlightQuadInstance = gameObject;
                if (highlightQuadInstance.TryGetComponent(out Canvas Canvas))
                {
                    Canvas.worldCamera = BasisLocalCameraDriver.Instance.Camera;
                }
                highlightQuadInstance.gameObject.SetActive(false);
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
            float uiScale = BasisHeightDriver.PlayerToDefaultRatioScaledWithAvatarScale;
            if (LineRenderer != null)
            {
                float size = lineWidth * uiScale;
                LineRenderer.startWidth = size;
                LineRenderer.endWidth = size;
            }
            if (highlightQuadInstance != null)
            {
                highlightQuadInstance.transform.localScale = highlightQuadInitalSize * uiScale;
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

            bool hitCollider = BasisPointRaycaster.PhysicHitCount > 0;

            // NOTE: only first collider hit counted for UI
            // TODO: this should never be null at this point, right? yet it sometimes is with this! wtf!
            bool hitObject = hitCollider && BasisPointRaycaster.ClosestRayCastHit.transform != null;
            PhysicHit = BasisPointRaycaster.ClosestRayCastHit;

            bool hitCanvas = false;
            if (hitObject)
            {
                PhysicHit.transform.GetComponentsInChildren<Canvas>(false, Results);
                hitCanvas = Results.Count > 0;
            }

            if (!hitCanvas)
            {
                HandleNoHit();
                return;
            }

            HadRaycastUITarget = RaycastToUI();

            if (HadRaycastUITarget)
            {
                HandleDidHit();
            }
            else
            {
                HandleNoHit();
            }
        }

        private void HandleNoHit()
        {
            ResetRenderers();
            RaycastResult = new RaycastResult();
            PhysicHit = new RaycastHit();
        }

        bool ContainsLayer(LayerMask mask, int layer)
        {
            return (mask.value & (1 << layer)) != 0;
        }

        private void HandleDidHit()
        {
            WasCorrectLayer = ContainsLayer(UILayers, PhysicHit.transform.gameObject.layer);
            if (WasCorrectLayer)
            {
                UpdateRayCastResult();   // sets all RaycastResult data
                UpdateLineRenderer();    // updates the line renderer
                UpdateReticleRenderer(); // moves the Reticle renderer
            }
            else
            {
                ResetRenderers();
            }
        }

        private void UpdateRayCastResult()
        {
            var physicshit = PhysicHit.transform.gameObject;
            RaycastResult.gameObject = physicshit;
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
            FoundCanvas = physicshit.GetComponentInParent<Canvas>();
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
                const float endOffset = 0.01f; // tweak in meters (VR usually likes 0.005–0.02)

                Vector3 start = BasisPointRaycaster.ray.origin;
                Vector3 end = PhysicHit.point + PhysicHit.normal * endOffset;

                LineRenderer.SetPosition(0, start);
                LineRenderer.SetPosition(1, end);
            }
        }

        private void UpdateReticleRenderer()
        {
            if (HasRedicalRenderer)
            {
                if (PhysicHit.transform != null)
                {
                    if (BasisDeviceManagement.IsUserInDesktop() && BasisCursorManagement.ActiveLockState() != CursorLockMode.Locked)
                    {
                        if (HighlightState != ActiveStateOfHightlight.Off)
                        {
                            highlightQuadInstance.SetActive(false);
                            HighlightState = ActiveStateOfHightlight.Off;
                        }
                    }
                    else
                    {
                        if (HighlightState != ActiveStateOfHightlight.On)
                        {
                            highlightQuadInstance.SetActive(true);
                            HighlightState = ActiveStateOfHightlight.On;
                        }
                        highlightQuadInstance.transform.SetPositionAndRotation(PhysicHit.point, Quaternion.LookRotation(PhysicHit.normal));
                    }
                }
                else
                {
                    if (HighlightState != ActiveStateOfHightlight.Off)
                    {
                        highlightQuadInstance.SetActive(false);
                        HighlightState = ActiveStateOfHightlight.Off;
                    }
                }
            }
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
                highlightQuadInstance.SetActive(false);
                HighlightState = ActiveStateOfHightlight.Off;
            }
        }

        // NEW: priority helper so OverlayUI canvases always win
        private int GetCanvasPriority(Canvas canvas)
        {
            if (canvas == null)
                return 0;

            // Any canvas on the OverlayUI layer gets a huge priority bump
            if (canvas.gameObject.layer == OverlayUI)
                return 1000;

            // Normal canvases
            return 0;
        }

        public bool RaycastToUI()
        {
            // Sort canvases so OverlayUI always comes first,
            // then fall back to sortingOrder (higher first).
            Results.Sort((c1, c2) =>
            {
                int p1 = GetCanvasPriority(c1);
                int p2 = GetCanvasPriority(c2);

                int priorityCompare = p2.CompareTo(p1);
                if (priorityCompare != 0)
                    return priorityCompare;

                // Same priority class: use sortingOrder like before
                return c2.sortingOrder.CompareTo(c1.sortingOrder);
            });

            int Count = Results.Count;
            for (int Index = 0; Index < Count; Index++)
            {
                Canvas CurrentTopLevel = Results[Index];
                if (CurrentTopLevel != null)
                {
                    if (CurrentTopLevel.worldCamera == null)
                    {
                        CurrentTopLevel.worldCamera = BasisLocalCameraDriver.Instance.Camera;
                    }
                    SortedRaycastGraphics(CurrentTopLevel, CurrentTopLevel.worldCamera, ref SortedGraphics);
                    ProcessSortedHitsResults(CurrentTopLevel, true, SortedGraphics, SortedRays);
                    if (SortedGraphics.Count != 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public bool ProcessSortedHitsResults(Canvas canvas, bool hitSomething, List<BasisRaycastUIHitData> raycastHitDatums, List<RaycastResult> resultAppendList)
        {
            // Now that we have a list of sorted hits, process any extra settings and filters.
            foreach (var hitData in raycastHitDatums)
            {
                var validHit = true;

                if (hitData.graphic == null)
                {
                    continue;
                }
                var go = hitData.graphic.gameObject;
                if (IgnoreReversedGraphics)
                {
                    var forward = BasisPointRaycaster.ray.direction;
                    var goDirection = go.transform.rotation * Vector3.forward;
                    validHit = Vector3.Dot(forward, goDirection) > 0;
                }

                validHit &= hitData.distance < BasisPointRaycaster.MaxDistance;

                if (validHit)
                {
                    var trans = go.transform;
                    var transForward = trans.forward;
                    var castResult = new RaycastResult
                    {
                        gameObject = go,
                        module = BasisPointRaycaster,
                        distance = hitData.distance,
                        index = resultAppendList.Count,
                        depth = hitData.graphic.depth,
                        sortingLayer = canvas.sortingLayerID,
                        sortingOrder = canvas.sortingOrder,
                        worldPosition = hitData.worldHitPosition,
                        worldNormal = -transForward,
                        screenPosition = hitData.screenPosition,
                        displayIndex = hitData.displayIndex,
                    };
                    resultAppendList.Add(castResult);

                    hitSomething = true;
                }
            }

            return hitSomething;
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
            var graphics = GraphicRegistry.GetGraphicsForCanvas(canvas);

            results.Clear();
            for (int i = 0; i < graphics.Count; ++i)
            {
                var graphic = graphics[i];

                if (!ShouldTestGraphic(graphic, BasisPlayerInteract.Mask))
                    continue;

                var raycastPadding = graphic.raycastPadding;

                if (RayIntersectsRectTransform(graphic.rectTransform, raycastPadding, BasisPointRaycaster.ray, out var worldPos, out var distance))
                {
                    if (distance <= BasisPointRaycaster.MaxDistance)
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
