using Basis.BasisUI;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.UI;
public partial class BasisHandHeldCamera
{
    private const float SpawnForwardOffset = 0.5f;
    private Canvas onPropUICanvas;
    private GraphicRaycaster onPropUIRaycaster;
    private Collider onPropUICollider;
    private Renderer[] cameraBodyRenderers;
    private bool onPropUIHidden, cameraHidden, dismissed;
    public bool IsCameraHidden => cameraHidden;
    public bool IsClosedHidden => dismissed && cameraHidden;
    public void CloseToHidden()
    {
        dismissed = true;
        SetCameraHidden(true);
    }
    public void SetCameraHidden(bool hidden)
    {
        if (cameraHidden == hidden) return;
        cameraHidden = hidden;

        if (!hidden) dismissed = false;

        if (cameraBodyRenderers != null)
        {
            for (int Index = 0; Index < cameraBodyRenderers.Length; Index++)
            {
                if (cameraBodyRenderers[Index] != null) cameraBodyRenderers[Index].enabled = !hidden;
            }
        }

        UpdateRenderGate();
        UpdateOnPropUIVisibility();
        UpdateHiddenInputLocks();
    }
    public void RevealAsFreshSpawn()
    {
        SetCameraHidden(false);
        ClearModifiers();
        PinSpace = CameraPinSpace.HandHeld;
        AcquireCursorLock();
    }
    public void TeleportInFrontOfPlayer()
    {
        if (!BasisLocalCameraDriver.HasInstance || captureCamera == null) return;

        SetCameraHidden(false);

        Vector3 headPos = BasisLocalCameraDriver.HeadPosition, forward = BasisLocalCameraDriver.HeadForward();
        forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;

        PlaceWorldPinned(headPos + forward * SpawnForwardOffset, Quaternion.LookRotation(forward, Vector3.up));
    }
    public void SetOnPropUIHidden(bool hidden)
    {
        if (onPropUIHidden == hidden || onPropUICanvas == null) return;
        onPropUIHidden = hidden;
        onPropUICanvas.enabled = !hidden;
        if (onPropUIRaycaster != null) onPropUIRaycaster.enabled = !hidden;
        if (onPropUICollider != null) onPropUICollider.enabled = !hidden;
    }
    private void CacheOnPropUI()
    {
        cameraBodyRenderers = GetComponentsInChildren<Renderer>(true);

        onPropUICanvas = GetComponentInChildren<Canvas>(true);
        if (onPropUICanvas == null) return;
        onPropUICanvas.TryGetComponent(out onPropUIRaycaster);
        onPropUICanvas.TryGetComponent(out onPropUICollider);
    }
    private void UpdateHiddenInputLocks()
    {
        if (!BasisDeviceManagement.IsUserInDesktop() || IsFlying) return;

        if (cameraHidden)
        {
            ReleasePlayerLocks();
            ReleaseCursorLock();
            return;
        }

        AcquireCursorLock();
    }
    private void UpdateOnPropUIVisibility() => SetOnPropUIHidden(cameraHidden || (BasisDeviceManagement.IsUserInDesktop() && BasisMainMenu.Instance != null));
}
