using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Simulation
{
    /// <summary>
    /// Simulated XR input device that follows a provided transform and (optionally)
    /// jitters its motion for test scenarios. Populates the base <see cref="BasisInput"/>
    /// fields so the rest of the pipeline (bone control, visuals, etc.) behaves as if
    /// a real device were present.
    /// </summary>
    public class BasisInputXRSimulate : BasisInput
    {
        /// <summary>
        /// Transform whose local pose is sampled each frame to drive this simulated device.
        /// Typically created and moved by a simulation controller.
        /// </summary>
        public Transform FollowMovement;

        /// <summary>
        /// When true, applies small random offsets and rotations to <see cref="FollowMovement"/>
        /// each frame to emulate noisy tracking.
        /// </summary>
        public bool AddSomeRandomizedInput = false;

        /// <summary>
        /// Maximum +/- range (in meters) for random positional jitter when
        /// <see cref="AddSomeRandomizedInput"/> is enabled.
        /// </summary>
        public float MinMaxOffset = 0.0001f;

        /// <summary>
        /// Lerp factor used to blend toward random poses when
        /// <see cref="AddSomeRandomizedInput"/> is enabled. Multiplied by <c>Time.deltaTime</c>.
        /// </summary>
        public float LerpAmount = 0.1f;

        /// <summary>
        /// Polls the simulated device pose (and optional jitter), updates scaled coordinates,
        /// and forwards values to the bound bone control when a role is assigned.
        /// </summary>
        public override void DoPollData()
        {
            if (AddSomeRandomizedInput)
            {
                Vector3 randomOffset = new Vector3(
                    UnityEngine.Random.Range(-MinMaxOffset, MinMaxOffset),
                    UnityEngine.Random.Range(-MinMaxOffset, MinMaxOffset),
                    UnityEngine.Random.Range(-MinMaxOffset, MinMaxOffset));

                float LerpAmounts = LerpAmount * Time.deltaTime;
                Quaternion LerpRotation = Quaternion.Lerp(FollowMovement.localRotation, UnityEngine.Random.rotation, LerpAmounts);
                Vector3 newPosition = Vector3.Lerp(FollowMovement.localPosition, FollowMovement.localPosition + randomOffset, LerpAmounts);

                FollowMovement.SetLocalPositionAndRotation(newPosition, LerpRotation);
            }

            FollowMovement.GetLocalPositionAndRotation(out Vector3 VOut, out Quaternion QOut);
            ScaledDeviceCoord.position = VOut;
            Quaternion LocalRawRotation = QOut;

            float SPTDS = BasisLocalPlayer.Instance.CurrentHeight.SelectedPlayerToDefaultScale;

            // Normalize to player default scale and restore (keeps internal math consistent)
            ScaledDeviceCoord.position /= SPTDS;
            ScaledDeviceCoord.position = OffsetCoords.position + (ScaledDeviceCoord.position * SPTDS);
            ScaledDeviceCoord.rotation = LocalRawRotation;

            if (hasRoleAssigned)
            {
                if (Control.HasTracked != BasisHasTracked.HasNoTracker)
                {
                    // Apply pose to incoming data for the bone control
                    Control.IncomingData.position = ScaledDeviceCoord.position;
                    Control.IncomingData.rotation = ScaledDeviceCoord.rotation;
                    this.transform.name = Control.name;
                    this.FollowMovement.name = $"{Control.name} Moveable transform";
                }
            }

            ComputeRaycastDirection(ScaledDeviceCoord.position, ScaledDeviceCoord.rotation, Quaternion.identity);
            UpdateInputEvents();
        }

        /// <summary>
        /// Unity destroy hook: cleans up the spawned follow transform (if any) and then
        /// defers to base destruction.
        /// </summary>
        public new void OnDestroy()
        {
            if (FollowMovement != null)
            {
                GameObject.Destroy(FollowMovement.gameObject);
            }
            base.OnDestroy();
        }

        /// <summary>
        /// Attempts to show a visual model for the simulated tracker based on device support info.
        /// Falls back to a default model when no specific physical model is available.
        /// </summary>
        public override void ShowTrackedVisual()
        {
            if (BasisVisualTracker == null)
            {
                DeviceSupportInformation Match =
                    BasisDeviceManagement.Instance.BasisDeviceNameMatcher
                        .GetAssociatedDeviceMatchableNames(CommonDeviceIdentifier);

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

        /// <summary>
        /// No-op for simulation: haptics are not supported on the simulated device.
        /// </summary>
        public override void PlayHaptic(float duration = 0.25F, float amplitude = 0.5F, float frequency = 0.5F)
        {
            // Simulated device does not support haptics.
        }

        /// <summary>
        /// Plays a sound effect using the default base implementation (for debug/feedback).
        /// </summary>
        public override void PlaySoundEffect(string SoundEffectName, float Volume)
        {
            PlaySoundEffectDefaultImplementation(SoundEffectName, Volume);
        }
    }
}
