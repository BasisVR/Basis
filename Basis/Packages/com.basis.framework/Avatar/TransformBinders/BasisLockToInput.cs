using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;

namespace Basis.Scripts.TransformBinders
{
    public class BasisLockToInput : MonoBehaviour
    {
        public BasisBoneTrackedRole TrackedRole;
        public BasisInput BasisInput = null;
        public bool HasEvent = false;
        public void Awake()
        {
            Initialize();
        }
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
        public void OnDestroy()
        {
            if (HasEvent)
            {
                BasisDeviceManagement.Instance.AllInputDevices.OnListChanged -= FindRole;
                BasisDeviceManagement.Instance.AllInputDevices.OnListItemRemoved -= ResetIfNeeded;
                HasEvent = false;
            }
        }
        private void ResetIfNeeded(BasisInput input)
        {
            if (BasisInput == null || BasisInput == input)
            {
                BasisDebug.Log("ReParenting Camera", BasisDebug.LogTag.Device);
                this.transform.parent = BasisLocalPlayer.Instance.transform;
            }
        }

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
                            this.transform.localScale = Vector3.one;
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
                    // when application is exiting, objects will be destroyed naturally, don't error log during this process
                    if (!Application.isPlaying) BasisDebug.LogError("There was a missing BasisInput at " + Index);
                }
            }
        }
    }
}
