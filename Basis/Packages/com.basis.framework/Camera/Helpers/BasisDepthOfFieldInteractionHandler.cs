using UnityEngine;
using UnityEngine.UI;


/// <summary>
/// Handles Depth of Field (DoF) interaction and UI for a handheld camera.
/// Toggles DoF on/off, shows a focus cursor, and sets focus distance from raycasts.
/// </summary>
[System.Serializable]
public class BasisDepthOfFieldInteractionHandler : MonoBehaviour
{
    [Header("References")]
    /// <summary>
    /// Controller that owns the capture camera and DoF metadata/controls.
    /// </summary>
    public BasisHandHeldCamera cameraController;

    /// <summary>
    /// UI element shown at the current focus point within the preview.
    /// </summary>
    public RectTransform focusCursor;

    /// <summary>
    /// Toggle controlling whether DoF is active.
    /// </summary>
    public Toggle depthOfFieldToggle;

    [Header("Raycasting")]
    /// <summary>
    /// Maximum raycast distance when determining focus target.
    /// </summary>
    public float maxRaycastDistance = 1000f;

    /// <summary>
    /// Validates references and wires up the DoF toggle listener.
    /// </summary>
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
            depthOfFieldToggle.onValueChanged.AddListener(SetDoFState);
    }

    /// <summary>
    /// Enables/disables DoF and syncs UI + handheld mode when the toggle changes.
    /// </summary>
    /// <param name="enabled">Whether DoF should be active.</param>
    public void SetDoFState(bool enabled)
    {
        cameraController.MetaData.depthOfField.active = enabled;
        depthOfFieldToggle.SetIsOnWithoutNotify(enabled);
        SetCursorVisibility(enabled);
        cameraController.HandHeld?.SetDepthMode(cameraController.HandHeld.currentDepthMode);
    }

    /// <summary>
    /// Shows/hides the focus cursor and mirrors DoF active state for safety.
    /// </summary>
    /// <param name="enabled">Whether the cursor should be visible.</param>
    private void SetCursorVisibility(bool enabled)
    {
        focusCursor.gameObject.SetActive(enabled);
        cameraController.MetaData.depthOfField.active = enabled;
    }

    /// <summary>
    /// Casts a ray into the scene and, on hit, sets the DoF focus distance.
    /// Ignores self-hits (objects parented under the camera controller).
    /// </summary>
    /// <param name="ray">Ray from the preview/camera pixel into the world.</param>
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

        // Reflect value into handheld UI (without feedback loops)
        cameraController.HandHeld.DepthFocusDistanceSlider.SetValueWithoutNotify(distance);
        cameraController.HandHeld.DOFFocusOutput.SetText(distance.ToString("F2"));

        if (!focusCursor.gameObject.activeSelf)
            focusCursor.gameObject.SetActive(true);

        BasisDebug.Log($"[DOF] Focus distance set to {distance:F2} units (hit {hit.collider.name})");
    }
}
