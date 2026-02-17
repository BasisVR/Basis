using UnityEngine;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;

/// <summary>
/// VR-side helper that forwards controller "trigger over preview rect" interactions
/// to the desktop Depth of Field interactor. Runs after player movement each frame,
/// checks all inputs except the desktop center eye, and passes screen points that hit
/// the preview rectangle.
/// </summary>
public class BasisDepthOfFieldInteractorVR : MonoBehaviour
{
    /// <summary>
    /// Desktop DoF interactor that consumes screen-space interactions.
    /// </summary>
    public BasisDepthOfFieldInteractorDesktop BasisDOFInteractorDesktop;

    /// <summary>
    /// Camera used to convert controller world positions into screen coordinates
    /// for UI hit-testing (defaults to <see cref="Camera.main"/>).
    /// </summary>
    public Camera worldSpaceUICamera;

    /// <summary>
    /// The UI rectangle that acts as the interactive preview surface.
    /// </summary>
    public RectTransform previewRect;

    /// <summary>
    /// Trigger threshold required to count as an interaction (0..1).
    /// </summary>
    public float interactThreshold = 0.9f;

    /// <summary>
    /// Execution order priority when subscribing to <see cref="BasisLocalPlayer.AfterSimulateOnLate"/>.
    /// </summary>
    private const int UpdateOrder = 210; // After PlayerInteract (201)

    /// <summary>
    /// Unity start: ensures a camera is assigned (falls back to <see cref="Camera.main"/>).
    /// </summary>
    private void Start()
    {
        if (worldSpaceUICamera == null)
        {
            worldSpaceUICamera = Camera.main;
            if (worldSpaceUICamera == null)
                BasisDebug.LogWarning("No camera tagged MainCamera found. Assign worldSpaceUICamera manually.");
        }
    }

    /// <summary>
    /// Subscribes to the post-move update hook to poll inputs.
    /// </summary>
    private void OnEnable()
    {
        BasisLocalPlayer.AfterSimulateOnLate.AddAction(UpdateOrder, PollInputs);
    }

    /// <summary>
    /// Unsubscribes from the post-move update hook.
    /// </summary>
    private void OnDisable()
    {
        BasisLocalPlayer.AfterSimulateOnLate.RemoveAction(UpdateOrder, PollInputs);
    }

    /// <summary>
    /// Returns true if the given input represents the desktop CenterEye role (to be ignored here).
    /// </summary>
    private bool IsDesktopCenterEye(BasisInput input)
    {
        return input.TryGetRole(out BasisBoneTrackedRole role) && role == BasisBoneTrackedRole.CenterEye;
    }

    /// <summary>
    /// Runs after the local player's final move step. Scans inputs, checks trigger,
    /// projects to screen space, and forwards interactions within the preview rect.
    /// </summary>
    private void PollInputs()
    {
        if (!BasisLocalPlayer.PlayerReady || BasisDOFInteractorDesktop == null || worldSpaceUICamera == null || previewRect == null)
            return;

        var inputs = BasisDeviceManagement.Instance.AllInputDevices;
        int count = inputs.Count;
        for (int i = 0; i < count; i++)
        {
            var input = inputs[i];
            if (input == null) continue;
            if (IsDesktopCenterEye(input)) continue;

            if (input.CurrentInputState.Trigger < interactThreshold)
                continue;

            Vector3 worldPos = input.transform.position;
            Vector2 screenPos = worldSpaceUICamera.WorldToScreenPoint(worldPos);

            if (RectTransformUtility.RectangleContainsScreenPoint(previewRect, screenPos, worldSpaceUICamera))
            {
                BasisDOFInteractorDesktop.TryProcessInteraction(screenPos);
            }
        }
    }
}
