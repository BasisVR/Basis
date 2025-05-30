using UnityEngine;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine.InputSystem;
using Basis.Scripts.TransformBinders.BoneControl;

public class PreviewClickInteractor : MonoBehaviour
{
    public PreviewClickDirect previewClick;
    public Camera worldSpaceUICamera;
    public RectTransform previewRect;
    public float interactThreshold = 0.9f;

    private const int UpdateOrder = 210; // After PlayerInteract (201)

    private void Start()
    {
        // Automatically assign MainCamera if not set
        if (worldSpaceUICamera == null)
        {
            worldSpaceUICamera = Camera.main;
            if (worldSpaceUICamera == null)
                BasisDebug.LogWarning("No camera tagged MainCamera found. Assign worldSpaceUICamera manually.");
        }
    }

    private void OnEnable()
    {
        BasisLocalPlayer.Instance.AfterFinalMove.AddAction(UpdateOrder, PollInputs);
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
        if (!BasisLocalPlayer.PlayerReady || previewClick == null || worldSpaceUICamera == null || previewRect == null)
            return;

        var inputs = BasisDeviceManagement.Instance.AllInputDevices;
        int count = inputs.Count;
        for (int i = 0; i < count; i++)
        {
            var input = inputs[i];
            if (input == null) continue;
            if (IsDesktopCenterEye(input)) continue;

            if (input.InputState.Trigger < interactThreshold)
                continue;

            Vector3 worldPos = input.transform.position;
            Vector2 screenPos = worldSpaceUICamera.WorldToScreenPoint(worldPos);

            if (RectTransformUtility.RectangleContainsScreenPoint(previewRect, screenPos, worldSpaceUICamera))
            {
                previewClick.TryInteraction(screenPos);
            }
        }
    }
}
