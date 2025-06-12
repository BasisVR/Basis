using Unity.Mathematics;

namespace Basis.Scripts.Common
{
    [System.Serializable]
    public struct BasisCalibratedOffsetData
    {
        public bool Use;
        public quaternion rotation;
        public float3 position;
    }
    [System.Serializable]
    public struct BasisCalibratedCoords
    {
        public quaternion rotation;
        public float3 position;
    }
}