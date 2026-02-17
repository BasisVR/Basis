using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Drivers;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.TransformBinders
{
    /// <summary>
    /// Locks this GameObject’s transform to follow a tracked bone of the local player.
    /// Typically used for attaching scene objects to avatar bones (e.g. head, hands).
    /// </summary>
    public class BasisLockToBone : MonoBehaviour
    {
        /// <summary>
        /// The local player that owns the tracked skeleton.
        /// </summary>
        public BasisLocalPlayer BasisLocalPlayer;

        /// <summary>
        /// The local bone driver used to query and simulate bone transforms.
        /// </summary>
        public BasisLocalBoneDriver CharacterTransformDriver;

        /// <summary>
        /// The resolved bone control for the specified <see cref="Role"/>.
        /// </summary>
        public BasisLocalBoneControl BoneControl;

        /// <summary>
        /// The bone role this object should follow (defaults to <see cref="BasisBoneTrackedRole.Head"/>).
        /// </summary>
        public BasisBoneTrackedRole Role = BasisBoneTrackedRole.Head;

        /// <summary>
        /// True if the local player has a valid bone driver.
        /// </summary>
        public bool hasCharacterTransformDriver = false;

        /// <summary>
        /// True if the specified <see cref="Role"/> was successfully resolved to a bone.
        /// </summary>
        public bool HasBoneControl = false;

        /// <summary>
        /// True if this component has subscribed to player events.
        /// </summary>
        public bool HasEvent = false;

        /// <summary>
        /// Initializes this component with the given local player.
        /// Finds the bone driver and attempts to resolve the <see cref="Role"/>.
        /// Subscribes to the <see cref="BasisLocalPlayer.AfterSimulateOnRender"/> event for updates.
        /// </summary>
        /// <param name="LocalPlayer">The local player to bind against.</param>
        public void Initialize(BasisLocalPlayer LocalPlayer)
        {
            if (LocalPlayer != null)
            {
                BasisLocalPlayer = LocalPlayer;
                CharacterTransformDriver = LocalPlayer.LocalBoneDriver;

                if (CharacterTransformDriver == null)
                {
                    hasCharacterTransformDriver = false;
                    BasisDebug.LogError("Missing CharacterTransformDriver");
                }
                else
                {
                    hasCharacterTransformDriver = true;
                    HasBoneControl = CharacterTransformDriver.FindBone(out BoneControl, Role);
                }
            }
            else
            {
                BasisDebug.LogError("Missing LocalPlayer");
            }

            if (HasEvent == false)
            {
                BasisLocalPlayer.AfterSimulateOnRender.AddAction(99, Simulation);
                HasEvent = true;
            }
        }

        /// <summary>
        /// Unity lifecycle: unsubscribes from the player’s event callbacks when destroyed.
        /// </summary>
        public void OnDestroy()
        {
            if (CharacterTransformDriver != null)
            {
                if (HasEvent)
                {
                    BasisLocalPlayer.AfterSimulateOnRender.RemoveAction(99, Simulation);
                    HasEvent = false;
                }
            }
        }

        /// <summary>
        /// Called after the local player’s final movement step each frame.
        /// Updates this transform to match the tracked bone if available.
        /// </summary>
        void Simulation()
        {
            if (hasCharacterTransformDriver && HasBoneControl)
            {
                transform.SetPositionAndRotation(
                    BoneControl.OutgoingWorldData.position,
                    BoneControl.OutgoingWorldData.rotation
                );
            }
        }
    }
}
