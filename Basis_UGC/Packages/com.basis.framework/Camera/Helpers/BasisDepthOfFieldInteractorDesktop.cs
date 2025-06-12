using UnityEngine;
using UnityEngine.InputSystem;

public class BasisDepthOfFieldInteractorDesktop : MonoBehaviour
{
    public BasisHandHeldCamera cameraController;
    public RectTransform previewRect;
    public RectTransform focusCursor;
    public Camera worldSpaceUICamera;

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
        if (worldSpaceUICamera == null)
        {
            worldSpaceUICamera = Camera.main;
            if (worldSpaceUICamera == null)
                BasisDebug.LogWarning("No camera tagged MainCamera found. Assign worldSpaceUICamera manually.");
        }
    }

    private void OnClick(InputAction.CallbackContext context)
    {
        Vector2 screenPos = Mouse.current.position.ReadValue();
        TryProcessInteraction(screenPos);
    }
    private Vector2 CalculateUV(Vector2 localPos, RectTransform rect)
    {
        Vector2 size = rect.rect.size;
        Vector2 pivot = rect.pivot;
        return new Vector2(
            Mathf.Clamp01((localPos.x + size.x * pivot.x) / size.x),
            Mathf.Clamp01((localPos.y + size.y * pivot.y) / size.y)
        );
    }
    public void TryProcessInteraction(Vector2 screenPos)
    {
        if (cameraController == null || previewRect == null || worldSpaceUICamera == null) return;

        if (!RectTransformUtility.RectangleContainsScreenPoint(previewRect, screenPos, worldSpaceUICamera))
            return;
        bool depthOfFieldEnabled = cameraController.BasisDOFInteractionHandler?.depthOfFieldToggle != null &&
                      cameraController.BasisDOFInteractionHandler.depthOfFieldToggle.isOn;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(previewRect, screenPos, worldSpaceUICamera, out Vector2 localPos))
        {
            if (focusCursor != null)
                focusCursor.anchoredPosition = localPos;

            if (!depthOfFieldEnabled) return;

            Vector2 uv = CalculateUV(localPos, previewRect);

            RenderTexture rt = cameraController.captureCamera.targetTexture;
            if (rt == null)
            {
                BasisDebug.LogWarning("[Click] RenderTexture is null.");
                return;
            }

            Vector2 pixelPos = new Vector2(uv.x * rt.width, uv.y * rt.height);

            Ray ray = cameraController.captureCamera.ScreenPointToRay(pixelPos);
            cameraController.BasisDOFInteractionHandler?.ApplyFocusFromRay(ray);
        }
    }
}
