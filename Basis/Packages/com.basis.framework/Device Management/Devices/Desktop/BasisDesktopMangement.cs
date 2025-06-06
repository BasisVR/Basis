using Basis.Scripts.BasisSdk.Players;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Device_Management.Devices.Desktop
{
    [Serializable]
    public class BasisDesktopManagement : BasisBaseTypeManagement
    {
        public BasisAvatarEyeInput BasisAvatarEyeInput;
        public override void BeginLoadSDK()
        {
            if (BasisAvatarEyeInput == null)
            {
                BasisDeviceManagement.Instance.SetCameraRenderState(false);
                BasisDeviceManagement.Instance.CurrentMode = BasisDeviceManagement.Desktop;
                GameObject gameObject = new GameObject("Desktop Eye");
                if (BasisLocalPlayer.Instance != null)
                {
                    gameObject.transform.parent = BasisLocalPlayer.Instance.transform;
                }
                BasisAvatarEyeInput = gameObject.AddComponent<BasisAvatarEyeInput>();
                BasisAvatarEyeInput.Initialize("Desktop Eye", nameof(BasisDesktopManagement));
                BasisDeviceManagement.Instance.TryAdd(BasisAvatarEyeInput);
            }
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public override void StartSDK()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
        }
        public override void StopSDK()
        {
            if (BasisDeviceManagement.Instance.TryFindBasisBaseTypeManagement("SimulateXR", out List<BasisBaseTypeManagement> Matched))
            {
                foreach (var m in Matched)
                {
                    m.StopSDK();
                }
            }
            BasisDeviceManagement.Instance.RemoveDevicesFrom(nameof(BasisDesktopManagement), "Desktop Eye");
        }

        public override string Type()
        {
            return BasisDeviceManagement.Desktop;
        }
    }
}
