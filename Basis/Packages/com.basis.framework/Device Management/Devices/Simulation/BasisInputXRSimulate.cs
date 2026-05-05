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
        /// turning this on will mean that the positions get scaled relative to the overriden height.
        /// </summary>
        public bool AccountForScale = false;
        /// <summary>
        /// Polls the simulated device pose (and optional jitter), updates scaled coordinates,
        /// and forwards values to the bound bone control when a role is assigned.
        /// </summary>
        public override void LateDoPollData()
        {
            if (AddSomeRandomizedInput)
            {
                Vector3 randomOffset = new Vector3(
                    UnityEngine.Random.Range(-MinMaxOffset, MinMaxOffset),
                    UnityEngine.Random.Range(-MinMaxOffset, MinMaxOffset),
                    UnityEngine.Random.Range(-MinMaxOffset, MinMaxOffset));

                float lerpAmt = LerpAmount * Time.deltaTime;
                Quaternion lerpRot = Quaternion.Lerp(FollowMovement.localRotation, UnityEngine.Random.rotation, lerpAmt);
                Vector3 newPos = Vector3.Lerp(FollowMovement.localPosition, FollowMovement.localPosition + randomOffset, lerpAmt);

                FollowMovement.SetLocalPositionAndRotation(newPos, lerpRot);
            }

            FollowMovement.GetLocalPositionAndRotation(out Vector3 localPos, out Quaternion localRot);

            // FollowMovement.localPos is placed in avatar-world coordinates (relative
            // to playerRoot, which carries no scale itself — the avatar transform
            // inside it carries ScaledToMatchValue). The classifier scores against
            // PlayerEyeHeight in playspace pre-scale, so we divide by the avatar's
            // transform scale to convert avatar-world → player-real-world for
            // UnscaledDeviceCoord. AvatarToPlayerRatioScaled was the wrong factor
            // — it's computed from unscaled metrics and stays at 1.0 regardless of
            // SelectedScale, so simulator output broke the moment a user changed
            // their avatar scale.
            float avatarScale = BasisHeightDriver.ScaledToMatchValue;
            if (avatarScale <= 0f) avatarScale = 1f;

            Vector3 unscaledPos = localPos / avatarScale;
            Quaternion unscaledRot = localRot;

            // Publish the unscaled pose so downstream consumers (constellation calibration,
            // gizmos, anything else reading UnscaledDeviceCoord) see real-world player-scale
            // data. Without this every sim'd tracker would classify against bloated heights
            // when the avatar is scaled larger than 1×.
            UnscaledDeviceCoord.position = unscaledPos;
            UnscaledDeviceCoord.rotation = unscaledRot;

            // ScaledDeviceCoord stays in avatar-world (the bone-IK consumers want world
            // placement). Equivalent to unscaledPos × avatarScale = localPos, so we just
            // pass through localPos with the rigid OffsetCoords transform applied.
            ScaledDeviceCoord.position = OffsetCoords.position + (OffsetCoords.rotation * localPos);
            ScaledDeviceCoord.rotation = OffsetCoords.rotation * unscaledRot;

            if (AccountForScale)
            {
                // Optional second scale layer for callers that want avatar-relative
                // positioning despite world placement. Off by default.
                ScaledDeviceCoord.position *= BasisHeightDriver.AvatarToPlayerRatioScaled;
            }

            if (hasRoleAssigned && Control.HasTracked != BasisHasTracked.HasNoTracker)
            {
                Control.IncomingData.position = ScaledDeviceCoord.position;
                Control.IncomingData.rotation = ScaledDeviceCoord.rotation;
                this.transform.name = Control.name;
                this.FollowMovement.name = $"{Control.name} Moveable transform";
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
