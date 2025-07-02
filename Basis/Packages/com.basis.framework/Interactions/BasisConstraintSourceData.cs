using System;
using UnityEngine;
namespace Basis.Scripts.BasisSdk.Interactions
{
    [Serializable]
    public struct BasisConstraintSourceData
    {
        public Vector3 position;
        public Quaternion rotation;

        public Vector3 positionOffset;
        public Quaternion rotationOffset;
        [Range(0, 1)]
        public float weight;
    }
}
