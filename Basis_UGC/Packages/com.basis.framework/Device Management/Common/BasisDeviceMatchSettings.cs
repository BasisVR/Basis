using Basis.Scripts.TransformBinders.BoneControl;
using System;
using System.Collections.Generic;
using UnityEngine;
namespace Basis.Scripts.Device_Management
{
    [Serializable]
    public class BasisDeviceMatchSettings
    {
        [Header("Identification")]
        public string DeviceID = string.Empty;
        public int VersionNumber = 1;

        [Header("Match with Ids")]
        [SerializeField]
        public string[] matchableDeviceIds = Array.Empty<string>();

        public IEnumerable<string> MatchableDeviceIdsLowered()
        {
            foreach (var id in matchableDeviceIds)
            {
                yield return id.ToLower();
            }
        }

        [Header("Raycast Support")]
        public bool HasRayCastSupport = false;

        [Header("Physical Device")]
        public bool CanDisplayPhysicalTracker = false;

        [Header("Raycast Visuals")]
        public bool HasRayCastVisual = false;
        public bool HasRayCastRadical = false;

        [Header("Raycast Offsets")]
        public Vector3 PositionRayCastOffset = Vector3.zero;
        public Vector3 RotationRaycastOffset = Vector3.zero;

        [Header("Tracked Role Override")]
        public bool HasTrackedRole = false;
        public BasisBoneTrackedRole TrackedRole;

        [Header("Interact Settings")]
        public bool HasInteractVisual = true;
    }
}
