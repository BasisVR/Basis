using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management.Devices;
using System;
using UnityEngine;

namespace Basis.Scripts.Device_Management
{
    public class BasisVisualTracker : MonoBehaviour
    {
        public BasisInput BasisInput;
        public Action TrackedSetup;
        public Quaternion ModelRotationOffset = Quaternion.identity;
        public bool HasEvents = false;

        public Vector3 ScaleOfModel = Vector3.one;

        public void Initialization(BasisInput basisInput)
        {
            if (basisInput != null)
            {
                BasisInput = basisInput;
                UpdateVisualSizeAndOffset();
                if (HasEvents == false)
                {
                    BasisLocalPlayer.Instance.OnLocalAvatarChanged += UpdateVisualSizeAndOffset;
                    BasisLocalPlayer.Instance.OnPlayersHeightChanged += UpdateVisualSizeAndOffset;
                    HasEvents = true;
                }
                TrackedSetup?.Invoke();
            }
        }
        public void OnDestroy()
        {
            if (HasEvents)
            {
                BasisLocalPlayer.Instance.OnLocalAvatarChanged -= UpdateVisualSizeAndOffset;
                BasisLocalPlayer.Instance.OnPlayersHeightChanged -= UpdateVisualSizeAndOffset;
                HasEvents = false;
            }
        }
        public void UpdateVisualSizeAndOffset()
        {
            gameObject.transform.localScale = ScaleOfModel * BasisLocalPlayer.Instance.CurrentHeight.SelectedAvatarToAvatarDefaultScale;
            gameObject.transform.SetLocalPositionAndRotation(Vector3.zero, ModelRotationOffset);
        }
    }
}
