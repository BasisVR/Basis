using System;
using System.Threading.Tasks;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace Basis.BasisUI
{
    /// <summary>
    /// Used by the LibraryProvider.cs when a item is desired to spawn with a raycast placement
    /// </summary>
    public static class PlacementManager
    {

        #region CalculateLocalRenderBounds, TransformBoundsAABB

        /// <summary>
        /// Calculates bounds of all child renderers in PARENT LOCAL SPACE (pivot-relative).
        /// This is stable even if the object is moved/rotated in the world before measuring.
        /// </summary>
        public static Bounds CalculateLocalRenderBounds(GameObject parent)
        {
            var renderers = parent.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
                return new Bounds(Vector3.zero, Vector3.zero);

            Matrix4x4 parentWorldToLocal = parent.transform.worldToLocalMatrix;

            bool hasAny = false;
            Bounds accum = default;

            foreach (var r in renderers)
            {
                if (r == null) continue;

                Bounds srcLocal;

                if (r is SkinnedMeshRenderer smr)
                {
                    // In smr local space
                    srcLocal = smr.localBounds;
                }
                else if (r is MeshRenderer mr)
                {
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;

                    // In mesh local space (same as MeshFilter transform local space)
                    srcLocal = mf.sharedMesh.bounds;
                }
                else
                {
                    continue; // ignore other renderer types for now
                }

                // Map from renderer local -> world -> parent local
                Matrix4x4 toParentLocal = parentWorldToLocal * r.transform.localToWorldMatrix;

                // Transform bounds center and extents to new AABB in parent local space
                Bounds transformed = TransformBoundsAABB(srcLocal, toParentLocal);

                if (!hasAny)
                {
                    accum = transformed;
                    hasAny = true;
                }
                else
                {
                    accum.Encapsulate(transformed.min);
                    accum.Encapsulate(transformed.max);
                }
            }

            if (!hasAny)
                return new Bounds(Vector3.zero, Vector3.zero);

            if (accum.extents == Vector3.zero)
                accum = new Bounds(accum.center, new Vector3(0.1f, 0.1f, 0.1f));

            return accum;
        }

        private static Bounds TransformBoundsAABB(Bounds b, Matrix4x4 m)
        {
            // Standard affine bounds transform:
            Vector3 c = m.MultiplyPoint3x4(b.center);

            Vector3 ex = m.MultiplyVector(new Vector3(b.extents.x, 0f, 0f));
            Vector3 ey = m.MultiplyVector(new Vector3(0f, b.extents.y, 0f));
            Vector3 ez = m.MultiplyVector(new Vector3(0f, 0f, b.extents.z));

            Vector3 e = new Vector3(
                Mathf.Abs(ex.x) + Mathf.Abs(ey.x) + Mathf.Abs(ez.x),
                Mathf.Abs(ex.y) + Mathf.Abs(ey.y) + Mathf.Abs(ez.y),
                Mathf.Abs(ex.z) + Mathf.Abs(ey.z) + Mathf.Abs(ez.z)
            );

            return new Bounds(c, e * 2f);
        }

        #endregion

        public static string SpawnOutlineAddress = "Packages/com.basis.sdk/Prefabs/SpawnOutline.prefab";

        private static readonly int OutlineColorID = Shader.PropertyToID("_OutlineColor");
        private static readonly int OutlineThickness = Shader.PropertyToID("_OutlineThickness");
        private static readonly int OutlineCornerScale = Shader.PropertyToID("_OutlineCornerScale");
        private static readonly int OutlineDotScale = Shader.PropertyToID("_CenterDotScale");

        private static void SetOutlineColor(GameObject target, Color color)
        {
            foreach (Renderer r in target.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material mat in r.materials)
                {
                    if (mat.HasProperty(OutlineColorID))
                    {
                        mat.SetColor(OutlineColorID, color);
                    }
                }
            }
        }

        private static void UpdateOutlineScale(GameObject targetOutlineGameObject)
        {
            float scale = targetOutlineGameObject.transform.localScale.magnitude / 10;
            foreach (Renderer r in targetOutlineGameObject.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material mat in r.materials)
                {
                    if (mat.HasProperty(OutlineCornerScale))
                    {
                        mat.SetFloat(OutlineCornerScale, scale);
                    }

                    if (mat.HasProperty(OutlineDotScale))
                    {
                        mat.SetFloat(OutlineDotScale, scale / 2);
                    }

                    if (mat.HasProperty(OutlineThickness))
                    {
                        mat.SetFloat(OutlineThickness, scale / 5);
                    }
                }
            }
        }

        #region PLACEMENT
        private const float triggerDownThreshold = 0.75f;
        private const float triggerUpThreshold = 0.20f;

        public static BasisInput PlacementInput;
        private static GameObject PlacementCube;

        private static Action _triggerHandler;
        private static TaskCompletionSource<(Vector3 pos, Quaternion rot, Vector3 scale)> _tcs;
        private static bool _wasDown = false;

        // NEW: store placement metadata for finalize step
        private static Vector3 _localBoundsCenter;
        private static Vector3 _halfExtents;

        public static async Task<(Vector3 pos, Quaternion rot, Vector3 scale)> BeginPlacement(
            BasisInput input,
            Vector3 halfExtents,
            Vector3 localBoundsCenter)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            PlacementInput = input;

            // Store for finalize step
            _localBoundsCenter = localBoundsCenter;
            _halfExtents = halfExtents;

            // Enter placement mode on the raycaster (expects HALF-extents)
            PlacementInput.BasisPointRaycaster.EnterPlacementMode(halfExtents, localBoundsCenter);

            BasisLocalPlayer.AfterSimulateOnLate.AddAction(122, VisualDrive);

            _tcs = new TaskCompletionSource<(Vector3, Quaternion, Vector3)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _wasDown = false;

            _triggerHandler = PlacementOnTriggerChanged;
            try { PlacementInput.CurrentInputState.OnTriggerChanged += _triggerHandler; } catch { }

            try { return await _tcs.Task; }
            finally { CancelPlacement(); }
        }

        public static void CancelPlacement()
        {
            try { if (_triggerHandler != null && PlacementInput?.CurrentInputState != null) PlacementInput.CurrentInputState.OnTriggerChanged -= _triggerHandler; } catch { }
            try { PlacementInput?.BasisPointRaycaster?.ExitPlacementMode(); } catch { }
            try { BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(122, VisualDrive); } catch { }
            try { Addressables.ReleaseInstance(PlacementCube); } catch { }
            try { if (PlacementCube != null) GameObject.Destroy(PlacementCube); } catch { }

            try
            {
                if (_tcs != null && !_tcs.Task.IsCompleted)
                    _tcs.TrySetCanceled();
            }
            catch { }

            _tcs = null;
            _triggerHandler = null;
            _wasDown = false;

            // NEW: clear stored data
            _localBoundsCenter = default;
            _halfExtents = default;

            PlacementInput = null;
        }

        public static void VisualDrive()
        {
            if (BasisMainMenu.Instance != null)
            {
                CancelPlacement();
                return;
            }

            if (PlacementCube == null)
            {
                var op = Addressables.LoadAssetAsync<GameObject>(SpawnOutlineAddress);
                GameObject go = op.WaitForCompletion();
                PlacementCube = GameObject.Instantiate(go, BasisDeviceManagement.Instance.transform);
                PlacementCube.name = "Placement Outline";

                SetOutlineColor(PlacementCube, Color.purple);
            }

            if (PlacementInput != null && PlacementInput.BasisPointRaycaster.TryGetPlacement(out var placement))
            {
                if (placement.HasHit)
                {
                    PlacementCube.gameObject.SetActive(true);
                    PlacementCube.transform.SetPositionAndRotation(placement.Center, placement.Rotation);

                    // placement.Extents is HALF-size; preview cube scale expects FULL size
                    // TODO: this needs to account for the scale of the object
                    PlacementCube.transform.localScale = placement.Extents * 2f;

                    // update its selection
                    UpdateOutlineScale(PlacementCube);
                }
                else
                {
                    PlacementCube.gameObject.SetActive(false);
                }
            }
        }

        private static void PlacementOnTriggerChanged()
        {
            try
            {
                if (PlacementInput?.CurrentInputState == null || _tcs == null) return;

                float t = PlacementInput.CurrentInputState.Trigger;

                if (!_wasDown && t >= triggerDownThreshold)
                {
                    _wasDown = true;

                    if (PlacementInput.BasisPointRaycaster.TryGetPlacement(out var placement))
                    {
                        // Place the object so the BOTTOM of its bounds touches the hit point.
                        // local bottom = localCenter - up * halfHeight
                        Vector3 localBottom = _localBoundsCenter - Vector3.up * _halfExtents.y;

                        // Solve for pivot position such that pivot + rot*localBottom == hit.point
                        Vector3 spawnPos = placement.Hit.point - (placement.Rotation * localBottom);

                        _tcs.TrySetResult((spawnPos, placement.Rotation, Vector3.one));
                        CancelPlacement();
                        return;
                    }

                    // if (PlacementInput.BasisPointRaycaster.TryGetPlacement(out var placement))
                    // {
                    //     // placement.Center is already the world-space pivot position with bottom
                    //     // touching the surface, and placement.Rotation is already surface-aligned.
                    //     // ComputePlacementOBB handles all the offset math — just use it directly.
                    //     _tcs.TrySetResult((placement.Center, placement.Rotation, Vector3.one));
                    //     CancelPlacement();
                    //     return;
                    // }

                    Transform tr = PlacementInput.transform;
                    Vector3 fallbackPos = tr.position + tr.forward * 0.75f;
                    Quaternion fallbackRot = Quaternion.LookRotation(tr.forward, Vector3.up);
                    _tcs.TrySetResult((fallbackPos, fallbackRot, Vector3.one));
                    CancelPlacement();
                    return;
                }

                if (_wasDown && t <= triggerUpThreshold)
                    _wasDown = false;
            }
            catch (Exception ex)
            {
                BasisDebug.LogError(ex);
                try { if (_tcs != null && !_tcs.Task.IsCompleted) _tcs.TrySetException(ex); } catch { }
                CancelPlacement();
            }
        }

        #endregion

        #region SELECTION

        private static BasisRuntimeSpawnRegistry.SpawnInstance selectedInstance;
        public static BasisRuntimeSpawnRegistry.SpawnInstance ActiveInstance { get => selectedInstance; }
        private static GameObject selectedGameObjectRef;
        private static GameObject selectionGameObjectRef;

        /// <summary>
        /// Syncs the selection outline's transform to match the selected object's current world pose and bounds.
        /// </summary>
        private static void SyncOutlineTransform(BasisBundleConnector connector)
        {
            Vector3 worldCenter = selectedGameObjectRef.transform.TransformPoint(connector.Bounds.center);
            selectionGameObjectRef.transform.SetPositionAndRotation(worldCenter, selectedGameObjectRef.transform.rotation);
            selectionGameObjectRef.transform.localScale = new Vector3( selectedGameObjectRef.transform.localScale.x * connector.Bounds.size.x, selectedGameObjectRef.transform.localScale.y * connector.Bounds.size.y, selectedGameObjectRef.transform.localScale.z * connector.Bounds.size.z  );
            UpdateOutlineScale(selectionGameObjectRef);
        }

        public static void SetActiveSelection(BasisRuntimeSpawnRegistry.SpawnInstance spawnInstance)
        {
            if (spawnInstance == null) return;

            if (!BasisRuntimeSpawnRegistry.SpawnedGameobjects.TryGetValue(spawnInstance.LoadedNetID, out GameObject go) || go == null)
            {
                BasisDebug.LogError($"PlacementManager.SetActiveSelection: was unable to find the spawned object instance for spawnInstance.LoadedNetID = {spawnInstance.LoadedNetID} or asset {spawnInstance.Url}");
                return;
            }

            BasisBundleConnector connector = spawnInstance.bundleConnector;
            if (connector == null)
            {
                BasisDebug.LogWarning($"PlacementManager.SetActiveSelection: bundleConnector is null for {spawnInstance.Url}");
                return;
            }

            selectedInstance = spawnInstance;
            selectedGameObjectRef = go;

            // Compute bounds at runtime if the connector still has default (zero-size) bounds
            if (connector.Bounds.size == new BasisBounds().size)
            {
                Bounds unitybounds = CalculateLocalRenderBounds(selectedGameObjectRef);
                connector.Bounds = new BasisBounds(unitybounds.center, unitybounds.size);
            }

            // Create the outline visual if it doesn't exist yet
            if (selectionGameObjectRef == null)
            {
                var op = Addressables.LoadAssetAsync<GameObject>(SpawnOutlineAddress);
                GameObject prefab = op.WaitForCompletion();
                selectionGameObjectRef = GameObject.Instantiate(prefab, BasisDeviceManagement.Instance.transform);
                selectionGameObjectRef.name = "Selection Outline";

                SetOutlineColor(selectionGameObjectRef, Color.cyan);
                BasisLocalPlayer.AfterSimulateOnLate.AddAction(122, UpdateSelectionOutline);
            }

            SyncOutlineTransform(connector);
        }

        public static void UpdateSelectionOutline()
        {
            if (selectedInstance == null)
                return;

            // selected target was destroyed externally; self-heal so the outline doesn't linger
            if (selectedGameObjectRef == null)
            {
                RemoveActiveSelection();
                return;
            }

            if (selectionGameObjectRef == null) return;

            BasisBundleConnector connector = selectedInstance.bundleConnector;
            if (connector == null) return;

            SyncOutlineTransform(connector);
        }

        public static void RemoveSelectionSpawnInstanceID(BasisRuntimeSpawnRegistry.SpawnInstance spawnInstance)
        {
            if (selectedInstance == null || spawnInstance == null) return;

            if (spawnInstance.LoadedNetID == selectedInstance.LoadedNetID)
            {
                RemoveActiveSelection();
            }
        }

        public static void RemoveActiveSelection()
        {
            try { BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(122, UpdateSelectionOutline); } catch { }

            if (selectionGameObjectRef != null)
            {
                GameObject.Destroy(selectionGameObjectRef);
                selectionGameObjectRef = null;
            }

            selectedInstance = null;
            selectedGameObjectRef = null;
        }

        #endregion

    }
}
