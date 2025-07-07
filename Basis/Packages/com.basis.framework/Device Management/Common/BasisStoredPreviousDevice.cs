using Basis.Scripts.Common;
using Basis.Scripts.TransformBinders.BoneControl;

namespace Basis.Scripts.Device_Management
{
    [System.Serializable]
    public class BasisStoredPreviousDevice
    {
        public BasisCalibratedCoords InverseOffsetFromBone;
        public BasisBoneTrackedRole trackedRole;
        public bool hasRoleAssigned = false;
        public string SubSystem;
        public string UniqueID;
    }
}
