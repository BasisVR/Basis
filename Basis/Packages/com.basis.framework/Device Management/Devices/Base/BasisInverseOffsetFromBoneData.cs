using UnityEngine;
namespace Basis.Scripts.Device_Management.Devices
{
    public class BasisInverseOffsetFromBoneData
    {
        public Vector3 TrackerPosition;
        public Quaternion TrackerRotation;

        public Quaternion InitialInverseTrackRotation;
        public Quaternion InitialControlRotation;
        public void ComputeScale()
        {

        }
    }
}
