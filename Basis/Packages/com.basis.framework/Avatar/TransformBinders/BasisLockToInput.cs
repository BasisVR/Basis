using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.TransformBinders
{
    /// <summary>
    /// Locks this GameObject’s transform to a specific tracked input role.
    /// Useful for attaching objects (e.g. cameras or props) to controller or bone inputs.
    /// </summary>
    public class BasisLockToInput : MonoBehaviour
    {
        /// <summary>
        /// The tracked role this object should follow (e.g. head, hand, etc.).
        /// </summary>
        public BasisBoneTrackedRole TrackedRole;

        /// <summary>
        /// The resolved input device associated with <see cref="TrackedRole"/>.
        /// </summary>
        public BasisInput BasisInput = null;

        /// <summary>
        /// True if events have been subscribed to <see cref="BasisDeviceManagement.AllInputDevices"/>.
        /// </summary>
        public bool HasEvent = false;

        /// <summary>
        /// Unity lifecycle: ensures initialization occurs at startup.
        /// </summary>
        public void Awake()
        {
            Initialize();
        }

        /// <summary>
        /// Registers this component with the device management system and hooks into input list events.
        /// Also attempts to bind immediately to the specified <see cref="TrackedRole"/>.
        /// </summary>
        public void Initialize()
        {
            if (BasisDeviceManagement.Instance.BasisLockToInputs.Contains(this) == false)
            {
                BasisDeviceManagement.Instance.BasisLockToInputs.Add(this);
            }

            if (HasEvent == false)
            {
                BasisDeviceManagement.Instance.AllInputDevices.OnListChanged += FindRole;
                BasisDeviceManagement.Instance.AllInputDevices.OnListItemRemoved += ResetIfNeeded;
                HasEvent = true;
            }

            FindRole();
        }

        /// <summary>
        /// Unity lifecycle: unsubscribes from events when destroyed.
        /// </summary>
        public void OnDestroy()
        {
            if (HasEvent)
            {
                BasisDeviceManagement.Instance.AllInputDevices.OnListChanged -= FindRole;
                BasisDeviceManagement.Instance.AllInputDevices.OnListItemRemoved -= ResetIfNeeded;
                HasEvent = false;
            }
        }

        /// <summary>
        /// Resets this object to follow the local player root if its current input is lost or removed.
        /// </summary>
        /// <param name="input">The input that was removed.</param>
        private void ResetIfNeeded(BasisInput input)
        {
            if (BasisInput == null || BasisInput == input)
            {
                BasisDebug.Log("ReParenting Camera", BasisDebug.LogTag.Device);
                this.transform.parent = BasisLocalPlayer.Instance.transform;
            }
        }

        /// <summary>
        /// Attempts to find and bind this object to the transform of the input matching <see cref="TrackedRole"/>.
        /// Falls back to the local player root if not found.
        /// </summary>
        public void FindRole()
        {
            this.transform.parent = BasisLocalPlayer.Instance.transform;
            int count = BasisDeviceManagement.Instance.AllInputDevices.Count;

            BasisDebug.Log("finding Lock " + TrackedRole, BasisDebug.LogTag.Device);

            for (int Index = 0; Index < count; Index++)
            {
                BasisInput Input = BasisDeviceManagement.Instance.AllInputDevices[Index];
                if (Input != null)
                {
                    if (Input.TryGetRole(out BasisBoneTrackedRole role))
                    {
                        if (role == TrackedRole)
                        {
                            BasisInput = Input;
                            this.transform.parent = BasisInput.transform;
                            this.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
                            return;
                        }
                    }
                    else
                    {
                        BasisDebug.LogError("Missing Role " + role);
                    }
                }
                else
                {
                    // During application shutdown, destroyed objects may cause nulls — skip logging in play mode exit.
                    if (!Application.isPlaying) BasisDebug.LogError("There was a missing BasisInput at " + Index);
                }
            }
        }
    }
}
