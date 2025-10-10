using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Desktop-side Depth of Field interactor. Listens for mouse clicks,
/// checks whether the click is inside the preview rect, updates a focus cursor,
/// and (if DoF is enabled) casts a ray into the capture camera to set focus.
/// </summary>
public class BasisDepthOfFieldInteractorDesktop : MonoBehaviour
{
    /// <summary>
    /// Controller that owns the capture camera and DoF handler.
    /// </summary>
    public BasisHandHeldCamera cameraController;

    /// <summary>
    /// UI rectangle that displays the camera preview.
    /// </summary>
    public RectTransform previewRect;

    /// <summary>
    /// Optional UI element that shows the current focus point within the preview.
    /// </summary>
    public RectTransform focusCursor;

    /// <summary>
    /// Camera used for screen-space to UI-space conversions (defaults to <see cref="Camera.main"/>).
    /// </summary>
    public Camera worldSpaceUICamera;

    /// <summary>
    /// Input action bound to the desktop left mouse button.
    /// </summary>
    private InputAction clickAction;

    /// <summary>
    /// Subscribes input and enables click listening.
    /// </summary>
    private void OnEnable()
    {
        clickAction = new InputAction(type: InputActionType.Button, binding: "<Mouse>/leftButton");
        clickAction.performed += OnClick;
        clickAction.Enable();
    }

    /// <summary>
    /// Unsubscribes input on disable.
    /// </summary>
    private void OnDisable()
    {
        clickAction.Disable();
        clickAction.performed -= OnClick;
    }

    /// <summary>
    /// Ensures a camera is assigned (falls back to <see cref="Camera.main"/>).
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
    /// Mouse click handler: forwards to <see cref="TryProcessInteraction(Vector2)"/>.
    /// </summary>
    /// <param name="context">Input System callback context.</param>
    private void OnClick(InputAction.CallbackContext context)
    {
        Vector2 screenPos = Mouse.current.position.ReadValue();
        TryProcessInteraction(screenPos);
    }

    /// <summary>
    /// Converts a local point within <paramref name="rect"/> into normalized UV (0..1) coordinates.
    /// </summary>
    /// <param name="localPos">Local-space point (anchored) inside the rect.</param>
    /// <param name="rect">The rect to convert within.</param>
    /// <returns>UV coordinates in the range [0,1]x[0,1].</returns>
    private Vector2 CalculateUV(Vector2 localPos, RectTransform rect)
    {
        Vector2 size = rect.rect.size;
        Vector2 pivot = rect.pivot;
        return new Vector2(
            Mathf.Clamp01((localPos.x + size.x * pivot.x) / size.x),
            Mathf.Clamp01((localPos.y + size.y * pivot.y) / size.y)
        );
    }

    /// <summary>
    /// Attempts to process a screen-space interaction for DoF focusing.
    /// If within the preview rect, moves the focus cursor and, when DoF is enabled,
    /// computes a ray through the capture camera to set the focus.
    /// </summary>
    /// <param name="screenPos">Screen-space position (pixels).</param>
    public void TryProcessInteraction(Vector2 screenPos)
    {
        if (cameraController == null || previewRect == null || worldSpaceUICamera == null) return;

        // Must be inside the preview rect
        if (!RectTransformUtility.RectangleContainsScreenPoint(previewRect, screenPos, worldSpaceUICamera))
            return;

        // Check if DoF is enabled via the handler's toggle (if present)
        bool depthOfFieldEnabled =
            cameraController.BasisDOFInteractionHandler?.depthOfFieldToggle != null &&
            cameraController.BasisDOFInteractionHandler.depthOfFieldToggle.isOn;

        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(previewRect, screenPos, worldSpaceUICamera, out Vector2 localPos))
        {
            // Move the UI focus cursor
            if (focusCursor != null)
                focusCursor.anchoredPosition = localPos;

            if (!depthOfFieldEnabled) return;

            // Convert local point to UV in the preview rect
            Vector2 uv = CalculateUV(localPos, previewRect);

            // Map UV to pixels in the capture camera's render texture
            RenderTexture rt = cameraController.captureCamera.targetTexture;
            if (rt == null)
            {
                BasisDebug.LogWarning("[Click] RenderTexture is null.");
                return;
            }

            Vector2 pixelPos = new Vector2(uv.x * rt.width, uv.y * rt.height);

            // Raycast through the capture camera at that pixel and apply focus
            Ray ray = cameraController.captureCamera.ScreenPointToRay(pixelPos);
            cameraController.BasisDOFInteractionHandler?.ApplyFocusFromRay(ray);
        }
    }
}
