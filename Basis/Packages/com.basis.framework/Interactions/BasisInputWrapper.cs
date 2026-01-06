using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using System;
using UnityEngine;
namespace Basis.Scripts.BasisSdk.Interactions
{
    [Serializable]
    public struct BasisInputWrapper
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="source"></param>
        /// <param name="state"></param>
        /// <param name="wrapper"></param>
        /// <returns>true: added and tracking, false: not added or tracking (default struct)</returns>
        public static bool TryNewTracking(BasisInput source, BasisInteractInputState state, out BasisInputWrapper wrapper)
        {
            wrapper = default;

            if (state == BasisInteractInputState.NotAdded)
            {
             //   BasisDebug.LogError($"Unable to create device for {source?.name ?? "UNKNOWN (null source)"} because state was NotAdded", BasisDebug.LogTag.Device);
                return false;
            }

            if (source == null)
            {
                BasisDebug.LogError("Source device was null", BasisDebug.LogTag.Device);
                return false;
            }

            if (!source.TryGetRole(out BasisBoneTrackedRole role))
            {
               // BasisDebug.LogError($"Source {source.name} did not provide a valid role", BasisDebug.LogTag.Device);
                return false;
            }

            if (!BasisLocalPlayer.Instance.LocalBoneDriver.FindBone(out BasisLocalBoneControl control, role))
            {
                BasisDebug.LogError($"Failed to find LocalBoneControl for role {role} from source {source.name}", BasisDebug.LogTag.Device);
                return false;
            }

            // If we got this far, everything is valid
            wrapper.Source = source;
            wrapper.BoneControl = control;
            wrapper.State = state;
            wrapper.Role = role;

            return true;
        }
        public BasisInput Source;
        public BasisLocalBoneControl BoneControl { get; set; }
        public BasisBoneTrackedRole Role { get; set; }
        [SerializeField]
        // TODO: this should not be editable in editor, useful for debugging for now tho
        private BasisInteractInputState State;
        public BasisInteractInputState GetState()
        {
            return State;
        }
        public bool TrySetState(BasisInteractInputState newState)
        {
            if (State == BasisInteractInputState.NotAdded)
            {
                return false;
            }
            State = newState;
            return true;
        }
        public readonly bool IsInput(BasisInput input)
        {
            if (input == null)
            {
                BasisDebug.LogError("Input Provided was null");
                return false;
            }

            return State != BasisInteractInputState.NotAdded && Source.UniqueDeviceIdentifier == input.UniqueDeviceIdentifier;
        }
    }
}
