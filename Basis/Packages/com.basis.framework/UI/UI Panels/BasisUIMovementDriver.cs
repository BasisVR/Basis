using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using System.Collections;
using UnityEngine;

namespace Basis.Scripts.UI.UI_Panels
{
    public class BasisUIMovementDriver : MonoBehaviour
    {
        public Vector3 WorldOffset = new Vector3(0, 0, 0.5f);
        public bool hasLocalCreationEvent = false;
        public Vector3 Position;
        public Quaternion Rotation;
        private Vector3 InitalScale;
        public void OnEnable()
        {
            InitalScale = transform.localScale;
            if (BasisLocalPlayer.Instance != null)
            {
                LocalPlayerGenerated();
            }
            else
            {
                if (hasLocalCreationEvent == false)
                {
                    BasisLocalPlayer.OnLocalPlayerCreated += LocalPlayerGenerated;
                    hasLocalCreationEvent = true;
                }
            }
        }
        public void LocalPlayerGenerated()
        {
            BasisLocalPlayer.Instance.OnPlayersHeightChanged += StartWaitAndSetUILocation;
            SetUILocation();
        }
        public void OnDisable()
        {
            BasisLocalPlayer.Instance.OnPlayersHeightChanged -= StartWaitAndSetUILocation;
            if (hasLocalCreationEvent)
            {
                BasisLocalPlayer.OnLocalPlayerCreated -= LocalPlayerGenerated;
                hasLocalCreationEvent = false;
            }
        }
        public void StartWaitAndSetUILocation()
        {
            StartCoroutine(DelaySetUI());
        }
        private IEnumerator DelaySetUI() // Waits until end of frame to set position, to ensure all other data has been updated
        {
            yield return null;
            SetUILocation();
        }
        public void SetUILocation()
        {
            // Get the current position and rotation from the BasisLocalCameraDriver
            BasisLocalCameraDriver.GetPositionAndRotation(out Position, out Rotation);
            if(BasisLocalPlayer.Instance == null)
            {
                return;
            }
            if (BasisLocalPlayer.Instance.LocalCameraDriver == null)
            {
                return;
            }
            // Log the current scale for debugging purposes
            BasisDebug.Log("Scale was " + BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerToDefaultScale);

            // Extract the yaw (rotation around the vertical axis) and ignore pitch and roll
            Vector3 eulerRotation = Rotation.eulerAngles;
            eulerRotation.z = 0f; // Remove roll

            // Create a new quaternion with the adjusted rotation
            Quaternion horizontalRotation = Quaternion.Euler(eulerRotation);

            Vector3 adjustedOffset = new Vector3(WorldOffset.x, 0, WorldOffset.z) * BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerToDefaultScale;
            Vector3 targetPosition = Position + (horizontalRotation * adjustedOffset);

            // Set the position and the adjusted horizontal rotation
            transform.SetPositionAndRotation(targetPosition, horizontalRotation);
            transform.localScale = InitalScale * BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerToDefaultScale;
        }

    }
}
