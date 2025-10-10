using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.Device_Management
{
    /// <summary>
    /// Defines support capabilities and metadata for a specific tracked device.
    /// Used to describe identification, matching IDs, raycast support,
    /// role overrides, and interaction visual settings.
    /// </summary>
    [Serializable]
    public class DeviceSupportInformation
    {
        // ───────────────────────────────────────────────
        // Identification
        // ───────────────────────────────────────────────

        /// <summary>
        /// Unique ID string for the device.
        /// </summary>
        [Header("Identification")]
        public string DeviceID = string.Empty;

        /// <summary>
        /// Version number for compatibility tracking.
        /// </summary>
        public int VersionNumber = 1;

        // ───────────────────────────────────────────────
        // Matchable IDs
        // ───────────────────────────────────────────────

        /// <summary>
        /// List of device IDs (case insensitive) that this definition should match against.
        /// </summary>
        [Header("Match with Ids")]
        [SerializeField]
        public string[] matchableDeviceIds = Array.Empty<string>();

        /// <summary>
        /// Returns all <see cref="matchableDeviceIds"/> as lowercase strings
        /// for case-insensitive comparisons.
        /// </summary>
        public IEnumerable<string> MatchableDeviceIdsLowered()
        {
            foreach (var id in matchableDeviceIds)
            {
                yield return id.ToLower();
            }
        }

        // ───────────────────────────────────────────────
        // Raycast Support
        // ───────────────────────────────────────────────

        /// <summary>
        /// True if this device supports raycasting (e.g., laser pointer).
        /// </summary>
        [Header("Raycast Support")]
        public bool HasRayCastSupport = false;

        // ───────────────────────────────────────────────
        // Physical Tracker Visualization
        // ───────────────────────────────────────────────

        /// <summary>
        /// Whether this device can display a representation of the physical tracker in-world.
        /// </summary>
        [Header("Physical Device")]
        public bool CanDisplayPhysicalTracker = false;

        // ───────────────────────────────────────────────
        // Raycast Visuals
        // ───────────────────────────────────────────────

        /// <summary>
        /// True if a raycast line visual should be displayed.
        /// </summary>
        [Header("Raycast Visuals")]
        public bool HasRayCastVisual = false;

        /// <summary>
        /// True if a radial (circular) raycast indicator should be displayed.
        /// </summary>
        public bool HasRayCastRadical = false;

        // ───────────────────────────────────────────────
        // Role Override
        // ───────────────────────────────────────────────

        /// <summary>
        /// True if this device should be bound to a specific tracked role instead of inferred.
        /// </summary>
        [Header("Tracked Role Override")]
        public bool HasTrackedRole = false;

        /// <summary>
        /// The role this device should assume if <see cref="HasTrackedRole"/> is true.
        /// </summary>
        public BasisBoneTrackedRole TrackedRole;

        // ───────────────────────────────────────────────
        // Interaction
        // ───────────────────────────────────────────────

        /// <summary>
        /// Whether interaction visuals (like highlights) are supported for this device.
        /// </summary>
        [Header("Interact Settings")]
        public bool HasInteractVisual = true;
    }
}
