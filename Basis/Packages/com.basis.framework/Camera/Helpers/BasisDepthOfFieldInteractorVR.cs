using UnityEngine;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine.InputSystem;
using Basis.Scripts.TransformBinders.BoneControl;

public class BasisDepthOfFieldInteractorVR : MonoBehaviour
{
    public BasisDepthOfFieldInteractorDesktop BasisDOFInteractorDesktop;
    public Camera worldSpaceUICamera;
    public RectTransform previewRect;
    public float interactThreshold = 0.9f;

    private const int UpdateOrder = 210; // After PlayerInteract (201)

    private void Start()
    {
        if (worldSpaceUICamera == null)
        {
            worldSpaceUICamera = Camera.main;
            if (worldSpaceUICamera == null)
                BasisDebug.LogWarning("No camera tagged MainCamera found. Assign worldSpaceUICamera manually.");
        }
    }

    private void OnEnable()
    {
        if (BasisLocalPlayer.Instance != null)
            BasisLocalPlayer.Instance.AfterFinalMove.AddAction(UpdateOrder, PollInputs);
        else
            BasisDebug.LogWarning("BasisLocalPlayer.Instance is null in OnEnable().");
    }

    private void OnDisable()
    {
        BasisLocalPlayer.Instance.AfterFinalMove.RemoveAction(UpdateOrder, PollInputs);
    }
    private bool IsDesktopCenterEye(BasisInput input)
    {
        return input.TryGetRole(out BasisBoneTrackedRole role) && role == BasisBoneTrackedRole.CenterEye;
    }

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
