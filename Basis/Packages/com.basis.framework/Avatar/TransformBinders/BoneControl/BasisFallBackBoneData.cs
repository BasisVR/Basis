using System.Collections.Generic;
using UnityEngine;

namespace Basis.Scripts.TransformBinders.BoneControl
{
    /// <summary>
    /// ScriptableObject container that maps <see cref="BasisBoneTrackedRole"/> values
    /// to fallback bone definitions (<see cref="BasisFallBackBone"/>).
    /// Provides a lookup mechanism to gracefully fall back when live tracking data
    /// is unavailable.
    /// </summary>
    [System.Serializable]
    [CreateAssetMenu(fileName = "Data", menuName = "ScriptableObjects/FallBackBoneData", order = 1)]
    public class BasisFallBackBoneData : ScriptableObject
    {
        /// <summary>
        /// List of fallback bone definitions (positions and metadata).
        /// Each entry corresponds by index to <see cref="BoneTrackedRoles"/>.
        /// </summary>
        [SerializeField]
        public List<BasisFallBackBone> FallBackPercentage = new List<BasisFallBackBone>();

        /// <summary>
        /// List of tracked roles (e.g., head, hand, foot).
        /// The index order must match <see cref="FallBackPercentage"/>.
        /// </summary>
        [SerializeField]
        public List<BasisBoneTrackedRole> BoneTrackedRoles = new List<BasisBoneTrackedRole>();

        /// <summary>
        /// Attempts to find a fallback bone definition for the given tracked role.
        /// </summary>
        /// <param name="control">
        /// When successful, contains the <see cref="BasisFallBackBone"/> associated
        /// with the requested role; otherwise null.
        /// </param>
        /// <param name="Role">The tracked role to look up.</param>
        /// <returns>
        /// True if a matching fallback bone was found; false otherwise.
        /// </returns>
        public bool FindBone(out BasisFallBackBone control, BasisBoneTrackedRole Role)
        {
            int Index = BoneTrackedRoles.IndexOf(Role);
            if (FallBackPercentage.Count > Index && Index != -1)
            {
                control = FallBackPercentage[Index];
                return true;
            }
            control = null;
            return false;
        }
    }
}
