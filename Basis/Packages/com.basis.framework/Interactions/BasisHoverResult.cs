using UnityEngine;
using UnityEngine.LowLevelPhysics;
namespace Basis.Scripts.BasisSdk.Interactions
{
    [System.Serializable]
    public struct BasisHoverResult
    {
        [SerializeField]
        public Collider collider;
        [SerializeField]
        public float distanceToCenter;
        [SerializeField]
        public Vector3 closestPointToCenter;
        public BasisHoverResult(Collider collider, Vector3 worldPos)
        {
            this.collider = collider;
            switch (collider.GeometryHolder.Type)
            {
                case GeometryType.Sphere:
                case GeometryType.Capsule:
                case GeometryType.Box:
                case GeometryType.ConvexMesh:
                    // Physics.ClosestPoint can only be used with a BoxCollider, SphereCollider, CapsuleCollider and a convex MeshCollider
                    closestPointToCenter = collider.ClosestPoint(worldPos);
                    distanceToCenter = Vector3.Distance(closestPointToCenter, worldPos);
                    break;
                case GeometryType.TriangleMesh:
                case GeometryType.Terrain:
                case GeometryType.Invalid:
                default:
                    closestPointToCenter = collider.ClosestPointOnBounds(worldPos);
                    distanceToCenter = Vector3.Distance(closestPointToCenter, worldPos);
                    break;
            }
        }
    }
}
