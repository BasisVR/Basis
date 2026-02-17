using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using System;
using UnityEngine;
using static BasisHeightDriver;

namespace Basis.Scripts.Device_Management
{
    /// <summary>
    /// Simple visual representation for a tracked input device.
    /// Scales to the local avatar’s current height and applies a model rotation offset.
    /// Subscribes to avatar/height change events to keep visuals consistent.
    /// </summary>
    public class BasisVisualTracker : MonoBehaviour
    {
        /// <summary>
        /// The input source this visual is representing (e.g., controller, tracker).
        /// </summary>
        public BasisInput BasisInput;

        /// <summary>
        /// Optional callback invoked after a successful <see cref="Initialization(BasisInput)"/>.
        /// </summary>
        public Action TrackedSetup;

        /// <summary>
        /// Local-space rotation applied to the visual model (e.g., to align mesh forward/up).
        /// </summary>
        public Quaternion ModelRotationOffset = Quaternion.identity;

        /// <summary>
        /// True once we’ve subscribed to <see cref="BasisLocalPlayer"/> events.
        /// Used to avoid duplicate subscriptions and to gate unsubscription on destroy.
        /// </summary>
        public bool HasEvents = false;

        /// <summary>
        /// Base scale for the model before applying avatar height scaling.
        /// </summary>
        public Vector3 ScaleOfModel = Vector3.one;

        /// <summary>
        /// Binds this visual to a <see cref="BasisInput"/>, adjusts size/offset,
        /// and subscribes to avatar/height change events.
        /// </summary>
        /// <param name="basisInput">The tracked input to visualize.</param>
        public void Initialization(BasisInput basisInput)
        {
            if (basisInput != null)
            {
                BasisInput = basisInput;

                OnPlayersHeightChangedNextFrame();

                if (HasEvents == false)
                {
                    BasisLocalPlayer.OnLocalAvatarChanged += OnPlayersHeightChangedNextFrame;
                    BasisLocalPlayer.OnPlayersHeightChangedNextFrame += OnPlayersHeightChangedNextFrame;
                    HasEvents = true;
                }

                TrackedSetup?.Invoke();
            }
        }

        /// <summary>
        /// Unity destroy hook: unsubscribes from avatar/height change events.
        /// </summary>
        public void OnDestroy()
        {
            if (HasEvents)
            {
                BasisLocalPlayer.OnLocalAvatarChanged -= OnPlayersHeightChangedNextFrame;
                BasisLocalPlayer.OnPlayersHeightChangedNextFrame -= OnPlayersHeightChangedNextFrame;
                HasEvents = false;
            }
        }
        public void OnPlayersHeightChangedNextFrame()
        {
            OnPlayersHeightChangedNextFrame(HeightModeChange.OnApplyHeightAndScale);
        }
        /// <summary>
        /// Applies avatar-relative scale and local offset/rotation to the visual.
        /// Called on initialization and whenever the local avatar/height changes.
        /// </summary>
        public void OnPlayersHeightChangedNextFrame(HeightModeChange Mode)
        {
            this.transform.localScale = ScaleOfModel * BasisHeightDriver.AvatarToDefaultRatioScaledWithAvatarScale;
            this.transform.SetLocalPositionAndRotation(Vector3.zero, ModelRotationOffset);
        }
    }
}
