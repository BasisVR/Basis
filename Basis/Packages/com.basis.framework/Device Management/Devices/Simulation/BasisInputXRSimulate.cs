using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Simulation
{
    public class BasisInputXRSimulate : BasisInput
    {
        public Transform FollowMovement;
        public bool AddSomeRandomizedInput = false;
        public float MinMaxOffset = 0.0001f;
        public float LerpAmount = 0.1f;
        public override void DoPollData()
        {
            if (AddSomeRandomizedInput)
            {
                Vector3 randomOffset = new Vector3(UnityEngine.Random.Range(-MinMaxOffset, MinMaxOffset), UnityEngine.Random.Range(-MinMaxOffset, MinMaxOffset), UnityEngine.Random.Range(-MinMaxOffset, MinMaxOffset));

                float LerpAmounts = LerpAmount * Time.deltaTime;
                Quaternion LerpRotation = Quaternion.Lerp(FollowMovement.localRotation, UnityEngine.Random.rotation, LerpAmounts);
                Vector3 newPosition = Vector3.Lerp(FollowMovement.localPosition, FollowMovement.localPosition + randomOffset, LerpAmounts);

                FollowMovement.SetLocalPositionAndRotation(newPosition, LerpRotation);
            }
            FollowMovement.GetLocalPositionAndRotation(out Vector3 VOut, out Quaternion QOut);
            LocalRawPosition = VOut;
          Quaternion LocalRawRotation = QOut;

            float SPTDS = BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerToDefaultScale;

            LocalRawPosition /= SPTDS;

            DeviceFinalPosition = LocalRawPosition * SPTDS;
            DevuceFinalRotation = LocalRawRotation;
            if (hasRoleAssigned)
            {
                if (Control.HasTracked != BasisHasTracked.HasNoTracker)
                {
                    // Apply position offset using math.mul for quaternion-vector multiplication
                    Control.IncomingData.position = DeviceFinalPosition;

                    // Apply rotation offset using math.mul for quaternion multiplication
                    Control.IncomingData.rotation = DevuceFinalRotation;
                }
            }
            UpdatePlayerControl();
        }
        public new void OnDestroy()
        {
            if (FollowMovement != null)
            {
                GameObject.Destroy(FollowMovement.gameObject);
            }
            base.OnDestroy();
        }

        public override void ShowTrackedVisual()
        {
            if (BasisVisualTracker == null && LoadedDeviceRequest == null)
            {
                BasisDeviceMatchSettings Match = BasisDeviceManagement.Instance.BasisDeviceNameMatcher.GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);
                if (Match.CanDisplayPhysicalTracker)
                {
                    LoadModelWithKey(Match.DeviceID);
                }
                else
                {
                    if (UseFallbackModel())
                    {
                        LoadModelWithKey(FallbackDeviceID);
                    }
                }
            }
        }
        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
        {
            BasisDebug.LogError("Simulate Does not Support haptics!");
        }
    }
}
