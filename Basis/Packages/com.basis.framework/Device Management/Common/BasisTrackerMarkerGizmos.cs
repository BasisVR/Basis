using System.Collections.Generic;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Basis.Scripts.Device_Management
{
    /// <summary>
    /// The player-facing tracker "marker" balls — the generic placeholder visual for tracked
    /// devices (the Markers tracker-visuals mode, and the fallback whenever a runtime device
    /// model is unavailable). Drawn through the batched gizmo backend instead of a FallbackSphere
    /// GameObject per device: each registered input gets a solid, lit, depth-tested ball using
    /// the FallbackSphere prefab's own material, scaled with avatar height exactly like the old
    /// BasisVisualTracker (Ø 0.05 × height ratio). The per-frame tick recomputes the pose the
    /// device transform will render with this frame (ScaledDeviceCoord + playspace flip, applied
    /// later in AfterSimulateOnRender) so the ball never trails the avatar. Ticked from
    /// BasisEventDriver just before the gizmo submission; gizmo ids are lazily re-created after
    /// the debug-gizmo master teardown wipes the manager.
    /// </summary>
    public static class BasisTrackerMarkerGizmos
    {
        // Matches FallbackSphere's BasisVisualTracker.ScaleOfModel.
        private const float MarkerDiameter = 0.05f;

        private class Entry
        {
            public BasisInput Input;
            public int GizmoId = -1;
        }

        private static readonly List<Entry> _markers = new List<Entry>();
        private static AsyncOperationHandle<GameObject> _materialSourceHandle;
        private static bool _hooked;

        /// <summary>True while this input has a marker ball registered.</summary>
        public static bool IsShowing(BasisInput input)
        {
            int count = _markers.Count;
            for (int i = 0; i < count; i++)
            {
                if (_markers[i].Input == input)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Registers a marker ball for this input. Idempotent.</summary>
        public static void Show(BasisInput input)
        {
            if (input == null || IsShowing(input))
            {
                return;
            }
            EnsureMasterHook();
            _markers.Add(new Entry { Input = input });
        }

        /// <summary>Removes this input's marker ball.</summary>
        public static void Hide(BasisInput input)
        {
            if (input == null)
            {
                return;
            }
            int count = _markers.Count;
            for (int i = 0; i < count; i++)
            {
                if (_markers[i].Input == input)
                {
                    DestroyEntryGizmo(_markers[i]);
                    _markers.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Per-frame update, called right before BasisGizmoManager.Render so the balls carry
        /// this frame's latched device poses. Also sweeps out entries whose input was destroyed
        /// (the old GameObject marker died with its device automatically — mirror that).
        /// </summary>
        public static void Tick()
        {
            int count = _markers.Count;
            if (count == 0)
            {
                return;
            }
            EnsureMaterial();
            float diameter = MarkerDiameter * BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            Vector3 size = Vector3.one * diameter;
            for (int i = count - 1; i >= 0; i--)
            {
                Entry entry = _markers[i];
                BasisInput input = entry.Input;
                if (input == null)
                {
                    DestroyEntryGizmo(entry);
                    _markers.RemoveAt(i);
                    continue;
                }

                // The device transform is written from ScaledDeviceCoord by ApplyFinalMovement
                // during AfterSimulateOnRender — after this tick. Recompute the same world pose
                // here so the ball rides this frame's pose instead of last frame's transform.
                Vector3 localPosition = input.ScaledDeviceCoord.position;
                Quaternion localRotation = input.ScaledDeviceCoord.rotation;
                BasisLocalPlayspaceMover.ApplyFlipToLocalPose(ref localPosition, ref localRotation);
                Transform parent = input.transform.parent;
                Vector3 worldPosition = parent != null ? parent.TransformPoint(localPosition) : localPosition;

                if (entry.GizmoId <= 0 || !BasisGizmoManager.Exists(entry.GizmoId))
                {
                    BasisGizmoManager.CreateSolidSphereGizmo($"TrackerMarker_{input.CommonDeviceIdentifier}", out entry.GizmoId, worldPosition, diameter);
                }
                else
                {
                    BasisGizmoManager.UpdateSphereGizmo(entry.GizmoId, worldPosition, size);
                }
            }
        }

        private static void DestroyEntryGizmo(Entry entry)
        {
            if (entry.GizmoId > 0 && BasisGizmoManager.Exists(entry.GizmoId))
            {
                BasisGizmoManager.DestroyGizmo(entry.GizmoId);
            }
            entry.GizmoId = -1;
        }

        /// <summary>
        /// Points the gizmo backend's solid-sphere material at the FallbackSphere prefab's
        /// material, so the balls keep the exact look the marker prefab had (and keep tracking
        /// any future art change to it). Loads the prefab asset once — never instantiated.
        /// </summary>
        private static void EnsureMaterial()
        {
            if (BasisGizmoManager.SolidSphereMaterial != null)
            {
                return;
            }
            if (!_materialSourceHandle.IsValid())
            {
                _materialSourceHandle = Addressables.LoadAssetAsync<GameObject>(BasisInput.FallbackDeviceID);
            }
            GameObject prefab = _materialSourceHandle.WaitForCompletion();
            if (prefab != null && prefab.TryGetComponent(out MeshRenderer meshRenderer) && meshRenderer.sharedMaterial != null)
            {
                BasisGizmoManager.SolidSphereMaterial = meshRenderer.sharedMaterial;
            }
            else
            {
                BasisDebug.LogError("FallbackSphere prefab or its material missing — tracker marker balls cannot render.", BasisDebug.LogTag.Device);
            }
        }

        private static void EnsureMasterHook()
        {
            if (_hooked)
            {
                return;
            }
            BasisGizmoManager.OnUseGizmosChanged += OnMasterToggleChanged;
            _hooked = true;
        }

        // The debug-gizmo master teardown wipes the manager's slots — forget stale ids so the
        // next Tick re-creates the balls.
        private static void OnMasterToggleChanged(bool state)
        {
            if (state)
            {
                return;
            }
            int count = _markers.Count;
            for (int i = 0; i < count; i++)
            {
                _markers[i].GizmoId = -1;
            }
        }
    }
}
