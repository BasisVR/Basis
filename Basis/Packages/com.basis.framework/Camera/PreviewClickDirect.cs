using UnityEngine;
using UnityEngine.InputSystem;

public class PreviewClickDirect : MonoBehaviour
{
    public BasisHandHeldCamera cameraController;
    public RectTransform previewRect;
    public RectTransform focusCursor;
    public Camera worldSpaceUICamera; // Optional assignment

    private InputAction clickAction;

    private void OnEnable()
    {
        clickAction = new InputAction(type: InputActionType.Button, binding: "<Mouse>/leftButton");
        clickAction.performed += OnClick;
        clickAction.Enable();
    }

    private void OnDisable()
    {
        clickAction.Disable();
        clickAction.performed -= OnClick;
    }

    private void Start()
    {
        // Automatically assign MainCamera if not set
        if (worldSpaceUICamera == null)
        {
            worldSpaceUICamera = Camera.main;
            if (worldSpaceUICamera == null)
                Debug.LogWarning("No camera tagged MainCamera found. Assign worldSpaceUICamera manually.");
        }
    }

    private void OnClick(InputAction.CallbackContext context)
    {
        if (cameraController.HandHeld.depthIsActiveButton == null || !cameraController.HandHeld.depthIsActiveButton.isOn)
            return;

        if (previewRect == null || worldSpaceUICamera == null || cameraController == null)
            return;

        Vector2 screenPos = Mouse.current.position.ReadValue();
        // Ensure we haven't clicked on other UI elements
        if (!RectTransformUtility.RectangleContainsScreenPoint(previewRect, screenPos, worldSpaceUICamera)) { return; }
        // Only respond if clicked on the previewRect
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(previewRect, screenPos, worldSpaceUICamera, out Vector2 localPos))
        {
            // Move focus cursor to local position
            if (focusCursor != null)
                focusCursor.anchoredPosition = localPos;

            // Calculate UV from localPos
            Vector2 size = previewRect.rect.size;
            Vector2 pivot = previewRect.pivot;

            Vector2 uv = new Vector2(
                (localPos.x + size.x * pivot.x) / size.x,
                (localPos.y + size.y * pivot.y) / size.y
            );

            // Clamp UV to [0, 1]
            uv = Vector2.Max(Vector2.zero, Vector2.Min(Vector2.one, uv));

            // Convert to pixel coords
            RenderTexture rt = cameraController.captureCamera.targetTexture;
            if (rt == null)
            {
                Debug.LogWarning("[Click] RenderTexture is null.");
                return;
            }

            Vector2 pixelPos = new Vector2(
                uv.x * rt.width,
                uv.y * rt.height
            );

            // Generate the ray from captureCamera using pixelPos
            Ray ray = cameraController.captureCamera.ScreenPointToRay(pixelPos);

            cameraController.SetFocusFromRay(ray);
        }
    }
}
