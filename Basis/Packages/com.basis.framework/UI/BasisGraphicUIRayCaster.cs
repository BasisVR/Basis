using Basis.Scripts.BasisSdk.Helpers;
using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Scripts.UI
{
    /// <summary>
    /// Provides a raycasting utility for Basis UI canvases.
    /// Ensures the canvas has colliders for interaction and attaches the appropriate camera.
    /// </summary>
    public class BasisGraphicUIRayCaster : MonoBehaviour
    {
        /// <summary>
        /// The canvas this raycaster operates on.
        /// </summary>
        public Canvas Canvas;

        /// <summary>
        /// Unity OnEnable hook. Ensures the canvas has a collider and binds the correct world camera.
        /// </summary>
        public void OnEnable()
        {
            AddCanvasCollider();
            if (Canvas.worldCamera == null)
            {
                if (BasisLocalCameraDriver.HasInstance)
                {
                    Canvas.worldCamera = BasisLocalCameraDriver.Instance.Camera;
                }
                else
                {
                    BasisLocalCameraDriver.InstanceExists += InstanceExists;
                }
            }
        }

        /// <summary>
        /// Unity OnDisable hook. Unsubscribes from camera driver events.
        /// </summary>
        public void OnDisable()
        {
            BasisLocalCameraDriver.InstanceExists -= InstanceExists;
        }

        /// <summary>
        /// Callback for when a <see cref="BasisLocalCameraDriver"/> instance becomes available.
        /// </summary>
        private void InstanceExists()
        {
            if (Canvas != null)
            {
                Canvas.worldCamera = BasisLocalCameraDriver.Instance.Camera;
            }
        }

        /// <summary>
        /// Ensures a <see cref="BoxCollider"/> is sized to match a UI element's <see cref="RectTransform"/>.
        /// </summary>
        /// <param name="handlerGameObject">The GameObject containing a RectTransform.</param>
        public static void SetBoxColliderToRectTransform(GameObject handlerGameObject)
        {
            BoxCollider boxCollider = BasisHelpers.GetOrAddComponent<BoxCollider>(handlerGameObject);

            if (handlerGameObject.TryGetComponent<RectTransform>(out RectTransform rectTransform))
            {
                Vector2 size = rectTransform.rect.size;
                Vector3 newSize = new Vector3(size.x, size.y, AppropriateSize(rectTransform));
                boxCollider.size = newSize;

                Vector2 pivot = rectTransform.pivot;
                Vector3 newCenter = new Vector3(
                    (0.5f - pivot.x) * size.x,
                    (0.5f - pivot.y) * size.y,
                    0);

                boxCollider.center = newCenter;
            }
            else
            {
                Debug.LogWarning("No RectTransform found on " + handlerGameObject.name);
            }
        }

        /// <summary>
        /// Determines an appropriate collider depth size based on attached components.
        /// </summary>
        /// <param name="RectTransform">The RectTransform to evaluate.</param>
        /// <returns>Depth size for the collider.</returns>
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

        /// <summary>
        /// Ensures the canvas has a <see cref="BoxCollider"/> sized to its RectTransform.
        /// </summary>
        public void AddCanvasCollider()
        {
            if (Canvas.TryGetComponent<RectTransform>(out RectTransform canvasRectTransform))
            {
                BoxCollider canvasCollider = BasisHelpers.GetOrAddComponent<BoxCollider>(Canvas.gameObject);
                Vector2 canvasSize = canvasRectTransform.sizeDelta;
                Vector3 canvasNewSize = new Vector3(canvasSize.x, canvasSize.y, 0.1f);
                canvasCollider.size = canvasNewSize;

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
