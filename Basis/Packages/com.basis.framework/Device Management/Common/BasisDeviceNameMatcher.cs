using Basis.Scripts.TransformBinders.BoneControl;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Basis.Scripts.Device_Management
{
    /// <summary>
    /// Matches incoming device identifiers to a configured <see cref="DeviceSupportInformation"/>.
    /// Falls back to an auto-generated entry when a match is not found (unless using the *NoCreate* variants).
    /// </summary>
    [CreateAssetMenu(fileName = "BasisDeviceNameMatcher", menuName = "Basis/BasisDeviceNameMatcher", order = 1)]
    public class BasisDeviceNameMatcher : ScriptableObject
    {
        /// <summary>
        /// Registry of known devices and their capabilities/overrides.
        /// </summary>
        [SerializeField]
        public List<DeviceSupportInformation> BasisDevice = new List<DeviceSupportInformation>();

        /// <summary>
        /// Find a device config whose <see cref="DeviceSupportInformation.matchableDeviceIds"/> contains the provided name
        /// (case-insensitive). If not found, a new entry is created, added to <see cref="BasisDevice"/>, and returned.
        /// </summary>
        /// <param name="nameToMatch">Incoming device name or identifier to match.</param>
        /// <param name="FallBackRole">Role to set on the generated fallback entry (if created).</param>
        /// <param name="UseFallbackROle">If true, the generated fallback will claim a tracked role using <paramref name="FallBackRole"/>.</param>
        public DeviceSupportInformation GetAssociatedDeviceMatchableNames( string nameToMatch, BasisBoneTrackedRole FallBackRole = BasisBoneTrackedRole.CenterEye, bool UseFallbackROle = false)
        {
            if (string.IsNullOrEmpty(nameToMatch))
            {
                BasisDebug.LogError("[DeviceNameMatcher] nameToMatch was null or empty—returning generated fallback.");
                return CreateAndRegisterFallback(nameToMatch ?? "UnknownDevice", FallBackRole, UseFallbackROle);
            }

            string needle = nameToMatch.ToLowerInvariant();

            // Try find a match (case-insensitive) without extra allocations per item
            for (int i = 0; i < BasisDevice.Count; i++)
            {
                var entry = BasisDevice[i];
                if (entry == null || entry.matchableDeviceIds == null) continue;

                // Avoid ToArray() and LINQ here; scan once, compare lowered.
                for (int j = 0; j < entry.matchableDeviceIds.Length; j++)
                {
                    var id = entry.matchableDeviceIds[j];
                    if (!string.IsNullOrEmpty(id) && id.ToLowerInvariant() == needle)
                    {
                        if (UseFallbackROle)
                        {
                            entry.HasTrackedRole = UseFallbackROle;
                            entry.TrackedRole = FallBackRole;
                        }
                        return entry;
                    }
                }
            }
            // Not found -> create a sensible fallback and append to list
            BasisDebug.LogError($"[DeviceNameMatcher] Unable to find configuration for '{nameToMatch}'. Generating fallback entry.");
            return CreateAndRegisterFallback(nameToMatch, FallBackRole, UseFallbackROle);
        }

        /// <summary>
        /// Find a device config (no creation). Returns null if none found.
        /// </summary>
        public DeviceSupportInformation GetAssociatedDeviceMatchableNamesNoCreate(string nameToMatch)
        {
            if (string.IsNullOrEmpty(nameToMatch))
            {
                Debug.LogWarning("[DeviceNameMatcher] nameToMatch was null or empty.");
                return null;
            }

            string needle = nameToMatch.ToLowerInvariant();

            for (int i = 0; i < BasisDevice.Count; i++)
            {
                var entry = BasisDevice[i];
                if (entry == null || entry.matchableDeviceIds == null) continue;

                for (int j = 0; j < entry.matchableDeviceIds.Length; j++)
                {
                    var id = entry.matchableDeviceIds[j];
                    if (!string.IsNullOrEmpty(id) && id.ToLowerInvariant() == needle)
                        return entry;
                }
            }

            Debug.LogWarning($"Configuration for device not found: {nameToMatch}");
            return null;
        }

        /// <summary>
        /// Find a device config (no creation). Returns null if none found.
        /// The <paramref name="CheckAgainst"/> parameter is ignored (preserved for backward compatibility).
        /// </summary>
        public DeviceSupportInformation GetAssociatedDeviceMatchableNamesNoCreate(string nameToMatch, DeviceSupportInformation CheckAgainst)
        {
            // Keep the same behavior: identical to the single-arg overload.
            return GetAssociatedDeviceMatchableNamesNoCreate(nameToMatch);
        }

        // --- Helpers ---

        private DeviceSupportInformation CreateAndRegisterFallback(string nameToMatch, BasisBoneTrackedRole fallbackRole, bool useFallbackRole)
        {
            bool HasRayCastVisual = true;
            bool HasRayCastRadical = false;
            bool HasRayCastSupport = true;
            if (fallbackRole == BasisBoneTrackedRole.CenterEye && useFallbackRole)
            {
                HasRayCastVisual = false;
                HasRayCastRadical = false;
                HasRayCastSupport = false;
            }
            // Build a minimal, reasonable fallback entry
            var settings = new DeviceSupportInformation
            {
                VersionNumber = 1,
                DeviceID = nameToMatch,
                matchableDeviceIds = new[] { nameToMatch },
                HasRayCastVisual = HasRayCastVisual,
                HasRayCastRadical = HasRayCastRadical,
                CanDisplayPhysicalTracker = false,
                HasRayCastSupport = HasRayCastSupport,
                HasTrackedRole = useFallbackRole,
                TrackedRole = fallbackRole,
            };

            // Avoid accidental duplicates if someone calls us repeatedly with the same unknown name
            // (we still keep original behavior of adding, just ensure we don't add identical ref twice)
            if (!BasisDevice.Contains(settings))
            {
                BasisDevice.Add(settings);
            }
            return settings;
        }
    }
}
