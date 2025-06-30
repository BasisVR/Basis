using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Basis.Scripts.Device_Management
{
    [CreateAssetMenu(fileName = "BasisDeviceNameMatcher", menuName = "Basis/BasisDeviceNameMatcher", order = 1)]
    public class BasisDeviceNameMatcher : ScriptableObject
    {
        [SerializeField]
        public List<DeviceSupportInformation> BasisDevice = new List<DeviceSupportInformation>();
        public DeviceSupportInformation GetAssociatedDeviceMatchableNames(string nameToMatch, BasisBoneTrackedRole FallBackRole = BasisBoneTrackedRole.CenterEye, bool UseFallbackROle = false)
        {
            foreach (DeviceSupportInformation DeviceEntry in BasisDevice)
            {
                string[] Matched = DeviceEntry.MatchableDeviceIdsLowered().ToArray();
                if (Matched.Contains(nameToMatch.ToLower()))
                {
                    return DeviceEntry;
                }
            }
            DeviceSupportInformation Settings = new DeviceSupportInformation
            {
                VersionNumber = 1,
                DeviceID = nameToMatch,
                matchableDeviceIds = new string[] { nameToMatch },
                HasRayCastVisual = true,
                HasRayCastRadical = true,
                CanDisplayPhysicalTracker = false,
                HasRayCastSupport = true,
                HasTrackedRole = UseFallbackROle,
                TrackedRole = FallBackRole,
            };
            BasisDeviceManagement.Instance.BasisDeviceNameMatcher.BasisDevice.Add(Settings);
            BasisDebug.LogError("Unable to find Configuration for device Generating " + nameToMatch);
            return Settings;
        }
        public DeviceSupportInformation GetAssociatedDeviceMatchableNamesNoCreate(string nameToMatch)
        {
            foreach (DeviceSupportInformation deviceEntry in BasisDevice)
            {
                string[] matched = deviceEntry.MatchableDeviceIdsLowered().ToArray();
                if (matched.Contains(nameToMatch.ToLower()))
                {
                    return deviceEntry;
                }
            }

            // No matching device found, return null instead of creating or saving
            Debug.LogWarning("Configuration for device not found: " + nameToMatch);
            return null;
        }
        public DeviceSupportInformation GetAssociatedDeviceMatchableNamesNoCreate(string nameToMatch, DeviceSupportInformation CheckAgainst)
        {
            foreach (DeviceSupportInformation deviceEntry in BasisDevice)
            {
                string[] matched = deviceEntry.MatchableDeviceIdsLowered().ToArray();
                if (matched.Contains(nameToMatch.ToLower()))
                {
                    return deviceEntry;
                }
            }

            // No matching device found, return null instead of creating or saving
            Debug.LogWarning("Configuration for device not found: " + nameToMatch);
            return null;
        }
    }
}
