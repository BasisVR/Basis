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
    private void OnEnable()
    {
        if (depthOfFieldToggle != null)
            depthOfFieldToggle.onValueChanged.AddListener(SetCursorVisibility);
    }
    public void SetDoFState(bool enabled)
    {
        if (cameraController?.MetaData?.depthOfField != null)
            cameraController.MetaData.depthOfField.active = enabled;

        if (depthOfFieldToggle != null)
            depthOfFieldToggle.SetIsOnWithoutNotify(enabled);
    }

    private void SetCursorVisibility(bool enabled)
    {
        if (focusCursor != null)
            focusCursor.gameObject.SetActive(enabled);
        if (cameraController?.MetaData?.depthOfField != null)
            cameraController.MetaData.depthOfField.active = enabled;
    }

    public void ApplyFocusFromRay(Ray ray)
    {
        if (cameraController?.MetaData?.depthOfField == null)
        {
            BasisDebug.LogWarning("[DOF] Cannot set focus: camera or depth of field is null");
            return;
        }

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
        cameraController.HandHeld?.DepthFocusDistanceSlider?.SetValueWithoutNotify(distance);
        cameraController.HandHeld?.DOFFocusOutput?.SetText(distance.ToString("F2"));

        focusCursor?.gameObject.SetActive(true);
        BasisDebug.Log($"[DOF] Focus distance set to {distance:F2} units (hit {hit.collider.name})");
    }
}
