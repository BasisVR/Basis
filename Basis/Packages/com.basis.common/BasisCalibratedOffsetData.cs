using UnityEngine;

namespace Basis.Scripts.Common
{
    [System.Serializable]
    /// <summary>
    /// Lightweight struct for storing a rigid transform used for calibrated coordinate offsets.
    /// It is made up of a position Vector3 and a rotation Quaternion.
    /// Be sure to call the constructor instead of using the default struct initializer
    /// to avoid the Quaternion being set to all zeroes (which is invalid).
    /// </summary>
    public struct BasisCalibratedCoords
    {
        public Quaternion rotation;
        public Vector3 position;

        public static BasisCalibratedCoords Identity
        {
            get { return new BasisCalibratedCoords(Vector3.zero, Quaternion.identity); }
        }

        public BasisCalibratedCoords(Vector3 pos, Quaternion rot)
        {
            this.position = pos;
            this.rotation = rot;
        }

        public static Vector3 operator *(BasisCalibratedCoords basisTransform, Vector3 vec)
        {
            return basisTransform.position + (basisTransform.rotation * vec);
        }
    }
}
