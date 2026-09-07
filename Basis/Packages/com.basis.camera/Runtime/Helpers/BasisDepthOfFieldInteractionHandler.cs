using UnityEngine;
using UnityEngine.Rendering.Universal;
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
    /// Layers the focus ray is allowed to land on, before it is narrowed to the ones the capture
    /// camera renders. Defaults to Unity's DefaultRaycastLayers — everything but Ignore Raycast —
    /// which is what the untargeted overload used.
    /// </summary>
    public LayerMask focusLayers = DefaultRaycastLayers;

    /// <summary>
    /// Unity's own DefaultRaycastLayers — everything but Ignore Raycast. Named rather than written
    /// out again because the look-at pointer casts against the same set, and two copies of a mask
    /// drift apart the first time one of them is corrected.
    /// </summary>
    public const int DefaultRaycastLayers = ~(1 << 2);

    /// <summary>
    /// The requested layers narrowed to the ones the capture camera actually renders. UI,
    /// OverlayUI and HandHeldCameraUI all carry colliders and are all culled from every shot, so
    /// without this the open camera panel or the player's own menu — a hand's width in front of
    /// the lens and absent from the picture — is the nearest hit for most clicks and takes the
    /// focus. It also decides how far the subject picker may look: the world hit is what caps
    /// that search, so an invisible panel in the way makes every player behind it unpickable.
    /// A zero culling mask means there is no camera to read, so the request is left alone.
    /// </summary>
    public static int VisibleFocusLayers(int requested, int cullingMask) => cullingMask == 0 ? requested : requested & cullingMask;

    /// <summary>
    /// Metres of slop added to every bone capsule when hit-testing a player, so a click that lands
    /// just off a thin limb still counts. Zero tests the body exactly.
    /// </summary>
    public float subjectPadding = 0.05f;

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
    /// Whether the effect is switched on. The live effect is the state; the toggle only shows it.
    /// </summary>
    public bool IsDoFEnabled => cameraController != null && cameraController.MetaData != null
        && cameraController.MetaData.depthOfField != null && cameraController.MetaData.depthOfField.active;

    /// <summary>
    /// Enables/disables DoF and syncs UI + handheld mode when the toggle changes.
    /// </summary>
    /// <param name="enabled">Whether DoF should be active.</param>
    public void SetDoFState(bool enabled)
    {
        if (cameraController == null || cameraController.MetaData == null || cameraController.MetaData.depthOfField == null) return;

        // Turning DoF on while the stored blur style is Off would render nothing and read as
        // "the toggle does nothing", so promote it to a real mode on the way in.
        if (enabled && cameraController.MetaData.depthOfField.mode.value == DepthOfFieldMode.Off)
        {
            cameraController.MetaData.depthOfField.mode.overrideState = true;
            cameraController.MetaData.depthOfField.mode.value = DepthOfFieldMode.Bokeh;
        }

        cameraController.MetaData.depthOfField.active = enabled;
        SyncToggleFromState();
        SetCursorVisibility(enabled);
        cameraController.HandHeld?.SetDepthMode(cameraController.HandHeld.currentDepthMode);
    }

    /// <summary>
    /// Pushes the live on/off state onto the prop's toggle without re-driving anything. The
    /// settings panel, the blur-style dropdown, the mode presets and a settings load all write the
    /// effect directly, so the toggle has to follow the camera rather than assume it is the only
    /// writer — otherwise switching depth of field off from the panel leaves the toggle on the
    /// camera itself still reading on.
    /// </summary>
    public void SyncToggleFromState()
    {
        if (depthOfFieldToggle != null) depthOfFieldToggle.SetIsOnWithoutNotify(IsDoFEnabled);
    }

    /// <summary>
    /// Shows/hides the focus cursor and mirrors DoF active state for safety.
    /// </summary>
    /// <param name="enabled">Whether the cursor should be visible.</param>
    private void SetCursorVisibility(bool enabled)
    {
        if (focusCursor != null) focusCursor.gameObject.SetActive(enabled);
        cameraController.MetaData.depthOfField.active = enabled;
    }

    /// <summary>
    /// Picks what was clicked and pulls focus onto it. Players carry no colliders, so a plain
    /// raycast focuses on whatever is behind them; they are hit-tested against capsules fitted to
    /// their live skeleton instead, and only count when nothing solid stands between them and the
    /// lens. Anything else falls back to the world raycast, which skips the camera prop itself and
    /// every player's own colliders.
    /// </summary>
    /// <param name="ray">Ray from the preview/camera pixel into the world.</param>
    public void ApplyFocusFromRay(Ray ray)
    {
        if (cameraController == null) return;

        int layers = VisibleFocusLayers(focusLayers.value, cameraController.WorldCullingMask);
        bool hasWorld = BasisCameraSubjectPicker.TryRaycastWorld(ray, maxRaycastDistance, layers, cameraController, out RaycastHit worldHit, out float worldDistance);

        if (BasisCameraSubjectPicker.TryPickSubject(ray, maxRaycastDistance, hasWorld ? worldDistance : float.PositiveInfinity, subjectPadding, !cameraController.IsDetachedFromHand, out BasisCameraSubjectHit subject))
        {
            string who = subject.Player != null && !string.IsNullOrEmpty(subject.Player.DisplayName) ? subject.Player.DisplayName : "player";
            if (!subject.FromSkeleton) who += " (bounds)";
            RackFocusToPoint(subject.Point, who);
            return;
        }

        if (!hasWorld)
        {
            BasisDebug.Log("[DOF] Raycast missed");
            return;
        }

        RackFocusToPoint(worldHit.point, worldHit.collider != null ? worldHit.collider.name : "world");
    }

    private void RackFocusToPoint(Vector3 worldPoint, string label)
    {
        if (!cameraController.TryGetFocusDepth(worldPoint, out float depth))
        {
            BasisDebug.Log("[DOF] Hit is behind or inside the minimum focus distance — skipping");
            return;
        }

        cameraController.RackFocusTo(depth);

        if (focusCursor != null && !focusCursor.gameObject.activeSelf)
            focusCursor.gameObject.SetActive(true);

        BasisDebug.Log($"[DOF] Pulling focus to {depth:F2} units over {cameraController.focusRackSeconds:F2}s (hit {label})");
    }
}
