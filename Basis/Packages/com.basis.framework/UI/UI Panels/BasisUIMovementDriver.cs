using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
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

        public void Start()
        {
            InitalScale = transform.localScale;
            if (BasisLocalPlayer.Instance != null)
            {
                LocalPlayerGenerated();
            }
            else
            {
                if (!hasLocalCreationEvent)
                {
                    BasisLocalPlayer.OnLocalPlayerCreated += LocalPlayerGenerated;
                    hasLocalCreationEvent = true;
                }
            }
            CurrentMaxDistanceInVRBeforeSnap = MaxDistanceInVRBeforeSnap;
        }
        public void OnDestroy()
        {
            DeInitalize();
        }

        public void DeInitalize()
        {
            if (SnapToPlayOnDistance)
            {
                BasisLocalPlayer.Instance.AfterFinalMove.RemoveAction(120, UpdateUIFollow);
            }

            BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= SetUILocation;

            if (hasLocalCreationEvent)
            {
                BasisLocalPlayer.OnLocalPlayerCreated -= LocalPlayerGenerated;
                hasLocalCreationEvent = false;
            }
        }
        public void LocalPlayerGenerated()
        {
            if (SnapToPlayOnDistance)
            {
                BasisLocalPlayer.Instance.AfterFinalMove.AddAction(120, UpdateUIFollow);
            }
            BasisLocalPlayer.OnPlayersHeightChangedNextFrame += SetUILocation;
            SetUILocation();
        }
        public void UpdateUIFollow()
        {
            if (!BasisDeviceManagement.IsUserInDesktop())
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
            BasisLocalCameraDriver.GetPositionAndRotation(out Position, out Rotation);

            if (BasisLocalPlayer.Instance == null)
            {
                return;
            }

            Vector3 eulerRotation = Rotation.eulerAngles;
            eulerRotation.z = 0f;

            float Scale = BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerToDefaultScale;
            Quaternion horizontalRotation = Quaternion.Euler(eulerRotation);
            Vector3 adjustedOffset = new Vector3(WorldOffset.x, 0, WorldOffset.z) * Scale;
            targetPosition = Position + (horizontalRotation * adjustedOffset);

            transform.SetPositionAndRotation(targetPosition, horizontalRotation);
            transform.localScale = InitalScale * Scale;

            CurrentMaxDistanceInVRBeforeSnap = MaxDistanceInVRBeforeSnap * BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerToDefaultScale;
        }
    }
}
