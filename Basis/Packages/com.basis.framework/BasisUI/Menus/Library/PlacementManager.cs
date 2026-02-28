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
        private const float triggerDownThreshold = 0.75f;
        private const float triggerUpThreshold = 0.20f;

        public static BasisInput PlacementInput;
        public static GameObject PlacementCube;
        public static string PlacementAddress = "Packages/com.basis.sdk/Prefabs/SpawnOutline.prefab";

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
            PlacementInput.BasisPointRaycaster.EnterPlacementMode(halfExtents);

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
                var op = Addressables.LoadAssetAsync<GameObject>(PlacementAddress);
                GameObject go = op.WaitForCompletion();
                PlacementCube = GameObject.Instantiate(go, BasisDeviceManagement.Instance.transform);
                PlacementCube.name = "Placement";
            }

            if (PlacementInput != null && PlacementInput.BasisPointRaycaster.TryGetPlacement(out var placement))
            {
                if (placement.HasHit)
                {
                    PlacementCube.gameObject.SetActive(true);
                    PlacementCube.transform.SetPositionAndRotation(placement.Center, placement.Rotation);

                    // placement.Extents is HALF-size; preview cube scale expects FULL size
                    PlacementCube.transform.localScale = placement.Extents * 2f;
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
    }
}
