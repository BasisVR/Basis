using UnityEngine;
using UnityEngine.UI;

[System.Serializable]
public class BasisDepthOfFieldInteractionHandler : MonoBehaviour
{
    [Header("References")]
    public BasisHandHeldCamera cameraController;
    public RectTransform focusCursor;
    public Toggle depthOfFieldToggle;

    [Header("Raycasting")]
    public float maxRaycastDistance = 1000f;
    private void Awake()
    {
        if (cameraController == null)
            BasisDebug.LogError("BasisDepthOfFieldInteractionHandler: cameraController must be assigned!");
        else if (cameraController.MetaData == null)
            BasisDebug.LogError("BasisDepthOfFieldInteractionHandler: cameraController.MetaData must be assigned!");
        else if (cameraController.MetaData.depthOfField == null)
            BasisDebug.LogError("BasisDepthOfFieldInteractionHandler: cameraController.MetaData.depthOfField must be assigned!");

        if (focusCursor == null)
            BasisDebug.LogError("BasisDepthOfFieldInteractionHandler: focusCursor must be assigned!");

        if (depthOfFieldToggle == null)
            BasisDebug.LogError("BasisDepthOfFieldInteractionHandler: depthOfFieldToggle must be assigned!");

        if (cameraController != null && cameraController.HandHeld == null)
            BasisDebug.LogError("BasisDepthOfFieldInteractionHandler: cameraController.HandHeld must be assigned!");
        else if (cameraController != null && cameraController.HandHeld != null)
        {
            if (cameraController.HandHeld.DepthFocusDistanceSlider == null)
                BasisDebug.LogError("BasisDepthOfFieldInteractionHandler: cameraController.HandHeld.DepthFocusDistanceSlider must be assigned!");
            if (cameraController.HandHeld.DOFFocusOutput == null)
                BasisDebug.LogError("BasisDepthOfFieldInteractionHandler: cameraController.HandHeld.DOFFocusOutput must be assigned!");
        }
        if (depthOfFieldToggle != null)
            depthOfFieldToggle.onValueChanged.AddListener(SetCursorVisibility);
    }
    public void SetDoFState(bool enabled)
    {
        cameraController.MetaData.depthOfField.active = enabled;
        depthOfFieldToggle.SetIsOnWithoutNotify(enabled);
        SetCursorVisibility(enabled);
    }

    private void SetCursorVisibility(bool enabled)
    {
        focusCursor.gameObject.SetActive(enabled);
        cameraController.MetaData.depthOfField.active = enabled;
    }

    public void ApplyFocusFromRay(Ray ray)
    {
        if (!Physics.Raycast(ray, out RaycastHit hit, maxRaycastDistance))
        {
            BasisDebug.Log("[DOF] Raycast missed");
            return;
        }

        if (hit.collider != null && hit.collider.transform.IsChildOf(cameraController.transform))
        {
            BasisDebug.Log("[DOF] Hit self — skipping");
            return;
        }

        float distance = Vector3.Distance(ray.origin, hit.point);
        cameraController.MetaData.depthOfField.focusDistance.value = distance;
        cameraController.HandHeld.DepthFocusDistanceSlider.SetValueWithoutNotify(distance);
        cameraController.HandHeld.DOFFocusOutput.SetText(distance.ToString("F2"));

        if (!focusCursor.gameObject.activeSelf)
            focusCursor.gameObject.SetActive(true);
        BasisDebug.Log($"[DOF] Focus distance set to {distance:F2} units (hit {hit.collider.name})");
    }
}