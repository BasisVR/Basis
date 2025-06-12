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
        public List<BasisDeviceMatchSettings> BasisDevice = new List<BasisDeviceMatchSettings>();
        public BasisDeviceMatchSettings GetAssociatedDeviceMatchableNames(string nameToMatch, BasisBoneTrackedRole FallBackRole = BasisBoneTrackedRole.CenterEye, bool UseFallbackROle = false)
        {
            foreach (BasisDeviceMatchSettings DeviceEntry in BasisDevice)
            {
                string[] Matched = DeviceEntry.MatchableDeviceIdsLowered().ToArray();
                if (Matched.Contains(nameToMatch.ToLower()))
                {
                    return DeviceEntry;
                }
            }
            BasisDeviceMatchSettings Settings = new BasisDeviceMatchSettings
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
        public BasisDeviceMatchSettings GetAssociatedDeviceMatchableNamesNoCreate(string nameToMatch)
        {
            foreach (BasisDeviceMatchSettings deviceEntry in BasisDevice)
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
        public BasisDeviceMatchSettings GetAssociatedDeviceMatchableNamesNoCreate(string nameToMatch, BasisDeviceMatchSettings CheckAgainst)
        {
            foreach (BasisDeviceMatchSettings deviceEntry in BasisDevice)
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
