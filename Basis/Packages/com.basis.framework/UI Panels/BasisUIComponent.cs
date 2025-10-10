using Basis.Scripts.Drivers;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Scripts.UI.UI_Panels
{
    /// <summary>
    /// Provides a UI component wrapper that binds Unity UI elements (Canvas, Scaler, Raycaster)
    /// to the local player's camera driver, ensuring proper world camera assignment.
    /// </summary>
    public class BasisUIComponent : MonoBehaviour
    {
        /// <summary>
        /// The Unity Canvas associated with this UI.
        /// </summary>
        public Canvas Canvas;

        /// <summary>
        /// The CanvasScaler controlling resolution and scaling settings.
        /// </summary>
        public CanvasScaler CanvasScaler;

        /// <summary>
        /// Custom raycaster handling graphic UI interactions.
        /// </summary>
        public BasisGraphicUIRayCaster GraphicUIRayCaster;

        /// <summary>
        /// Unity OnEnable hook. Initializes the UI and subscribes to camera driver events.
        /// </summary>
        public void OnEnable()
        {
            Initalize();
            BasisLocalCameraDriver.InstanceExists += Initalize;
        }

        /// <summary>
        /// Unity OnDisable hook. Unsubscribes from camera driver events.
        /// </summary>
        public void OnDisable()
        {
            BasisLocalCameraDriver.InstanceExists -= Initalize;
        }

        /// <summary>
        /// Initializes the UI component by assigning the camera from the local camera driver
        /// to the Canvas world camera.
        /// </summary>
        public void Initalize()
        {
            if (BasisLocalCameraDriver.Instance != null)
            {
                Canvas.worldCamera = BasisLocalCameraDriver.Instance.Camera;
            }
        }
    }
}
