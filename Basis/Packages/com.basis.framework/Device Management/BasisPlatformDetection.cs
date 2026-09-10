using System;
using Basis.Scripts.Common;
using UnityEngine;

namespace Basis.Scripts.Device_Management
{
    public enum BasisPlatformCondition
    {
        MobileGpu,
        Android,
        IOS,
        Windows,
        Linux,
        MacOS,
        Editor,
        Headless,
        Proton,
        VR,
        Desktop,
        OpenVR,
        OpenXR,
        SimulateXR,
        HeadsetWorn,
    }

    public static class BasisPlatformDetection
    {
        public static readonly string[] ConditionNames = Enum.GetNames(typeof(BasisPlatformCondition));
        private static readonly BasisPlatformCondition[] ConditionValues = (BasisPlatformCondition[])Enum.GetValues(typeof(BasisPlatformCondition));
        private static Action changed;
        private static bool hooked;

        public static event Action OnChanged
        {
            add { Hook(); changed += value; }
            remove { changed -= value; }
        }

        public static string CurrentMode => BasisDeviceManagement.StaticCurrentMode;
        public static string Platform => Application.platform.ToString();
        public static bool IsVR => BasisDeviceManagement.IsCurrentModeVR();
        public static bool IsDesktop => BasisDeviceManagement.IsUserInDesktop();
        public static bool IsMobileGpu => BasisGpuDetection.IsMobileGpu;
        public static bool IsHeadsetWorn => BasisHMDPresence.IsPresent;

        public static bool IsDetected(BasisPlatformCondition condition)
        {
            RuntimePlatform platform = Application.platform;
            switch (condition)
            {
                case BasisPlatformCondition.MobileGpu: return BasisGpuDetection.IsMobileGpu;
                case BasisPlatformCondition.Android: return platform == RuntimePlatform.Android;
                case BasisPlatformCondition.IOS: return platform == RuntimePlatform.IPhonePlayer;
                case BasisPlatformCondition.Windows: return platform == RuntimePlatform.WindowsPlayer || platform == RuntimePlatform.WindowsEditor;
                case BasisPlatformCondition.Linux: return platform == RuntimePlatform.LinuxPlayer || platform == RuntimePlatform.LinuxEditor;
                case BasisPlatformCondition.MacOS: return platform == RuntimePlatform.OSXPlayer || platform == RuntimePlatform.OSXEditor;
                case BasisPlatformCondition.Editor: return Application.isEditor;
                case BasisPlatformCondition.Headless: return Application.isBatchMode || IsMode(BasisConstants.Headless);
                case BasisPlatformCondition.Proton: return BasisProtonDetection.IsWine;
                case BasisPlatformCondition.VR: return IsVR;
                case BasisPlatformCondition.Desktop: return IsDesktop;
                case BasisPlatformCondition.OpenVR: return IsMode(BasisConstants.OpenVRLoader);
                case BasisPlatformCondition.OpenXR: return IsMode(BasisConstants.OpenXRLoader);
                case BasisPlatformCondition.SimulateXR: return IsMode(BasisConstants.SimulateXR);
                case BasisPlatformCondition.HeadsetWorn: return BasisHMDPresence.IsPresent;
            }
            return false;
        }

        public static bool IsStatic(BasisPlatformCondition condition)
        {
            switch (condition)
            {
                case BasisPlatformCondition.VR:
                case BasisPlatformCondition.Desktop:
                case BasisPlatformCondition.OpenVR:
                case BasisPlatformCondition.OpenXR:
                case BasisPlatformCondition.SimulateXR:
                case BasisPlatformCondition.HeadsetWorn:
                    return false;
            }
            return true;
        }

        public static bool TryParse(string name, out BasisPlatformCondition condition)
        {
            for (int i = 0; i < ConditionNames.Length; i++)
            {
                if (string.Equals(ConditionNames[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    condition = ConditionValues[i];
                    return true;
                }
            }
            condition = default;
            return false;
        }

        private static bool IsMode(string mode) => string.Equals(CurrentMode, mode, StringComparison.Ordinal);

        private static void Hook()
        {
            if (hooked) return;
            hooked = true;
            BasisDeviceManagement.OnBootModeChanged += ModeChanged;
            BasisHMDPresence.OnPresenceChanged += PresenceChanged;
        }

        private static void ModeChanged(string mode) => changed?.Invoke();

        private static void PresenceChanged(bool present) => changed?.Invoke();
    }
}
