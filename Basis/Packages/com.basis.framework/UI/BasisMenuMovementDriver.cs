using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections;
using UnityEngine;
namespace Basis.Scripts.UI.UI_Panels
{
    public class BasisMenuMovementDriver : MonoBehaviour
    {
        private BasisLocalPlayer LocalPlayer;
        public Vector3 menuPosOffset;
        public Vector3 menuRotOffset;

        public float FallBackMultiplier = 0.35f;
        private enum MenuType
        {
            Hand,
            Floating
        }
        [SerializeField, Tooltip("Controls whether menu will be attached to hand or float in front of player")]
        private MenuType menuType = MenuType.Floating;
        #region Menu Generation Events
        private bool hasGeneratedAction = false;
        private void OnEnable()
        {
            LocalPlayer = BasisLocalPlayer.Instance;
            if (BasisLocalPlayer.Instance != null)
            {
                OnLocalPlayerGenerated();
                StartCoroutine(DelaySetUI());
            }
            else
            {
                if (hasGeneratedAction == false)
                {
                    BasisLocalPlayer.OnLocalPlayerCreated += OnLocalPlayerGenerated;
                    hasGeneratedAction = true;
                }
            }
            LocalPlayer.AfterFinalMove.AddAction(105, UpdateUI);
        }
        private void OnDisable()
        {
            if (hasGeneratedAction)
            {
                BasisLocalPlayer.OnLocalPlayerCreated -= OnLocalPlayerGenerated;
                hasGeneratedAction = false;
            }

            BasisLocalPlayer.OnLocalAvatarChanged -= UpdateDelayedSetUI;
            BasisLocalPlayer.OnPlayersHeightChanged -= UpdateDelayedSetUI;

            LocalPlayer.AfterFinalMove.RemoveAction(101, UpdateUI);
        }
        #endregion
        #region Player Change Callbacks
        public BasisBoneControl hand;
        private void OnLocalPlayerGenerated()
        {
            BasisLocalPlayer.OnLocalAvatarChanged += UpdateDelayedSetUI;
            BasisLocalPlayer.OnPlayersHeightChanged += UpdateDelayedSetUI;
            if (LocalPlayer.LocalBoneDriver.FindBone(out hand, BasisBoneTrackedRole.LeftHand))
            {
            }
        }
        private void UpdateDelayedSetUI()
        {
            StartCoroutine(DelaySetUI());
        }
        private void UpdateDelayedSetUI(bool state)
        {
            if (state)
            {
                StartCoroutine(DelaySetUI());
            }
        }
        #endregion
        #region UI Position Updating
        private IEnumerator DelaySetUI() // Waits until end of frame to set position, to ensure all other data has been updated
        {
            yield return null;
            SetUIPositionAndRotation();
        }
        private void UpdateUI()
        {
            if (menuType != MenuType.Hand || BasisDeviceManagement.IsUserInDesktop())
            {
                return;
            }

            SetUIPositionAndRotation();
        }
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 newPos;
        public void SetUIPositionAndRotation()
        {
            switch (menuType)
            {
                case MenuType.Floating:
                    // Get the camera's position and rotation
                    BasisLocalCameraDriver.GetPositionAndRotation(out position, out rotation);

                    // Get the camera's forward direction
                    Vector3 newPos = BasisLocalCameraDriver.Forward();

                    // Project the forward direction onto the plane defined by the player's up direction
                    Vector3 projectedPos = Vector3.ProjectOnPlane(newPos, LocalPlayer.transform.up).normalized;

                    // Calculate the base new position by considering the player's position, scale, and offset
                    newPos = LocalPlayer.transform.position + (projectedPos * (0.5f * LocalPlayer.CurrentHeight.SelectedPlayerToDefaultScale));

                    // Transform the relative offsets by the rotation to apply them correctly in world space
                    Vector3 rotatedOffsets = rotation * menuPosOffset;

                    // Add the rotated offsets to the new position
                    newPos += rotatedOffsets;

                    // Set the position and rotation, including the menu rotation offset
                    transform.SetPositionAndRotation(newPos, Quaternion.LookRotation(projectedPos) * Quaternion.Euler(menuRotOffset));
                    break;
                case MenuType.Hand:
                    // Check if the user is in desktop mode
                    if (!BasisDeviceManagement.IsUserInDesktop())
                    {
                        position = hand.OutgoingWorldData.position;
                        rotation = hand.OutgoingWorldData.rotation;
                        // Set new position and rotation
                        transform.SetPositionAndRotation(position + (menuPosOffset * LocalPlayer.CurrentHeight.SelectedPlayerToDefaultScale), rotation * Quaternion.Euler(menuRotOffset));
                    }
                    else
                    {
                        // Fallback to camera position and rotation
                        BasisLocalCameraDriver.GetPositionAndRotation(out position, out rotation);
                        newPos = position + (BasisLocalCameraDriver.Forward() * FallBackMultiplier);

                        // Set new position and rotation
                        transform.SetPositionAndRotation(newPos, rotation);
                    }
                    break;
            }
        }
        #endregion
    }
}
