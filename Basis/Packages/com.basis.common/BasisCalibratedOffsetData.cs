using UnityEngine;

namespace Basis.Scripts.Common
{
    [System.Serializable]
    public struct BasisCalibratedCoords
    {
        public Quaternion rotation;
        public Vector3 position;
        public BasisCalibratedCoords(Vector3 pos, Quaternion rot) : this()
        {
            this.position = pos;
            this.rotation = rot;
        }
    }
}
