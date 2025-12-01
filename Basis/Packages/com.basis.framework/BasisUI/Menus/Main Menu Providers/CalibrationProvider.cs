using System;
using System.Collections.Generic;
using Basis.Scripts.Avatar;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;

namespace Basis.BasisUI
{
    public class CalibrationProvider : BasisMenuActionProvider<BasisMainMenu>
    {

        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new CalibrationProvider());
        }

        public override string Title => "Calibrate";
        public override string IconAddress => AddressableAssets.Sprites.Calibrate;
        public override int Order => 10;

        private Dictionary<BasisInput, Action> _triggerDelegates = new();


        public override void RunAction()
        {
            BasisLocalPlayer.Instance.LocalAvatarDriver.PutAvatarIntoTPose();

            foreach (BasisInput device in BasisDeviceManagement.Instance.AllInputDevices)
            {
                Action triggerDelegate = () => OnTriggerChanged(device);
                _triggerDelegates[device] = triggerDelegate;
                device.CurrentInputState.OnTriggerChanged += triggerDelegate;
            }
        }

        private void OnTriggerChanged(BasisInput device)
        {
            if (device.CurrentInputState.Trigger >= 0.9f)
            {
                foreach (KeyValuePair<BasisInput, Action> entry in _triggerDelegates)
                {
                    entry.Key.CurrentInputState.OnTriggerChanged -= entry.Value;
                }

                _triggerDelegates.Clear();
                BasisAvatarIKStageCalibration.FullBodyCalibration();
            }
        }

        public override void OnButtonCreated(PanelButton button)
        {
            base.OnButtonCreated(button);
            BasisDeviceManagement.OnBootModeChanged += BootModeChanged;
            BoundButton.OnInstanceReleased += () => BasisDeviceManagement.OnBootModeChanged -= BootModeChanged;
            CheckUserForVR();
        }

        private void BootModeChanged(string _) => CheckUserForVR();

        private void CheckUserForVR()
        {
            BoundButton.gameObject.SetActive(!BasisDeviceManagement.IsUserInDesktop());
        }
    }
}
