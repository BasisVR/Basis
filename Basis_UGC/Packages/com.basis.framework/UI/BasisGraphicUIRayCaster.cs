using Basis.Scripts.BasisSdk.Helpers;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Scripts.UI
{
public class BasisGraphicUIRayCaster : MonoBehaviour
{
    public Canvas Canvas;
    public void OnEnable()
    {
        AddCanvasCollider();
    }
    public static void SetBoxColliderToRectTransform(GameObject handlerGameObject)
    {
        BoxCollider boxCollider = BasisHelpers.GetOrAddComponent<BoxCollider>(handlerGameObject);

        if (handlerGameObject.TryGetComponent<RectTransform>(out RectTransform rectTransform))
        {
            // Calculate the size and center based on the RectTransform's rect, which accounts for anchors
            Vector2 size = rectTransform.rect.size;
            

            Vector3 newSize = new Vector3(size.x, size.y, AppropriateSize(rectTransform)); // Assuming a depth of 1 for the collider
            boxCollider.size = newSize;

            // Calculate the center of the BoxCollider based on the RectTransform's pivot
            Vector2 pivot = rectTransform.pivot;
            Vector3 newCenter = new Vector3(
                (0.5f - pivot.x) * size.x,
                (0.5f - pivot.y) * size.y,
                0); // Centered along the z-axis

            boxCollider.center = newCenter;
        }
        else
        {
            Debug.LogWarning("No RectTransform found on " + handlerGameObject.name);
        }
    }
    public static float AppropriateSize(RectTransform RectTransform)
    {
        if (RectTransform.TryGetComponent(out ScrollRect Rect))
        {
            return 0.5f;
        }
        if (RectTransform.TryGetComponent(out Canvas Canvas))
        {
            return 0.5f;
        }
        return 1;
    }
    public void AddCanvasCollider()
    {
        if (Canvas.TryGetComponent<RectTransform>(out RectTransform canvasRectTransform))
        {
            BoxCollider canvasCollider = BasisHelpers.GetOrAddComponent<BoxCollider>(Canvas.gameObject);
            Vector2 canvasSize = canvasRectTransform.sizeDelta;
            Vector3 canvasNewSize = new Vector3(canvasSize.x, canvasSize.y, 0.1f); // Assuming a depth of 1 for the collider
            canvasCollider.size = canvasNewSize;

            // Set the collider's position to the center of the canvas
            canvasCollider.center = new Vector3(
                canvasRectTransform.rect.width * (0.5f - canvasRectTransform.pivot.x),
                canvasRectTransform.rect.height * (0.5f - canvasRectTransform.pivot.y),
                0);
        }
        else
        {
            Debug.LogWarning("No RectTransform found on the Canvas.");
        }
    }
}
}