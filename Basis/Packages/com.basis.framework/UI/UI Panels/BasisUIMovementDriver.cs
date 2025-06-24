using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using System;
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
        public bool SnapToPlayOnDistance = false;
        public float MaxDistanceInVRBeforeSnap = 4;
        public float CurrentMaxDistanceInVRBeforeSnap;
        public float CurrentDistanceToVR;
        public Vector3 targetPosition;
        public Coroutine Runtime;
        public void Start()
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
            CurrentMaxDistanceInVRBeforeSnap = MaxDistanceInVRBeforeSnap;
        }
        public void DeInitalize()
        {
            if (SnapToPlayOnDistance)
            {
                BasisLocalPlayer.Instance.AfterFinalMove.RemoveAction(120, UpdateUIFollow);
            }
            BasisLocalPlayer.Instance.OnPlayersHeightChanged -= OnPlayersHeightChanged;
            if (hasLocalCreationEvent)
            {
                BasisLocalPlayer.OnLocalPlayerCreated -= LocalPlayerGenerated;
                hasLocalCreationEvent = false;
            }
            if(Runtime != null)
            {
                StopCoroutine(Runtime);
            }
        }
        public void LocalPlayerGenerated()
        {
            if (SnapToPlayOnDistance)
            {
                BasisLocalPlayer.Instance.AfterFinalMove.AddAction(120, UpdateUIFollow);
            }
            BasisLocalPlayer.Instance.OnPlayersHeightChanged += OnPlayersHeightChanged;
            SetUILocation();
        }
        public void OnPlayersHeightChanged()
        {
            CurrentMaxDistanceInVRBeforeSnap = MaxDistanceInVRBeforeSnap * BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerToDefaultScale;
            SetUILocation();
            Runtime = StartCoroutine(DelaySetUI());
        }

        private IEnumerator DelaySetUI()
        { 
             yield return null;
            SetUILocation();
        }

        public void UpdateUIFollow()
        {

            if (BasisDeviceManagement.IsUserInDesktop() == false)
            {
                BasisLocalCameraDriver.GetPositionAndRotation(out Position, out Rotation);
                CurrentDistanceToVR = Vector3.Distance(Position, targetPosition);
                if (CurrentDistanceToVR > CurrentMaxDistanceInVRBeforeSnap)
                {
                    SetUILocation();
                }
            }
            else
            {
                SetUILocation();
            }
        }
        public void SetUILocation()
        {
            // Get the current position and rotation from the BasisLocalCameraDriver
            BasisLocalCameraDriver.GetPositionAndRotation(out Position, out Rotation);
            if(BasisLocalPlayer.Instance == null)
            {
                return;
            }
            // Extract the yaw (rotation around the vertical axis) and ignore pitch and roll
            Vector3 eulerRotation = Rotation.eulerAngles;
            eulerRotation.z = 0f; // Remove roll

            float Scale = BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerToDefaultScale;
            // Create a new quaternion with the adjusted rotation
            Quaternion horizontalRotation = Quaternion.Euler(eulerRotation);

            Vector3 adjustedOffset = new Vector3(WorldOffset.x, 0, WorldOffset.z) * Scale;
            targetPosition = Position + (horizontalRotation * adjustedOffset);

            // Set the position and the adjusted horizontal rotation
            transform.SetPositionAndRotation(targetPosition, horizontalRotation);
            transform.localScale = InitalScale * Scale;
        }

    }
}
