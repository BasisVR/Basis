using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevelPhysics;

[System.Serializable]
public class HoverSphere
{
    public bool enabled = true;
    public bool OnlySortClosest = true;
    public Vector3 WorldPosition;
    [SerializeField]
    public float Radius;

    /// <summary>
    /// Maximum expected number of colliders this is ever expected to see.
    /// Using a max result value less than that will cause undefined behavior. 
    /// </summary>
    [SerializeField]
    public int MaxResults;
    [SerializeField]
    public LayerMask LayerMask;
    public QueryTriggerInteraction QueryTrigger = QueryTriggerInteraction.UseGlobal;

    /// <summary>
    /// Unsorted list of colliders in this sphere
    /// </summary>
    public Collider[] HitResults;
    public int ResultCount = 0;

    /// <summary>
    /// List of hits sorted by distance.
    /// Values of hits are updated per CheckSphere call.
    /// </summary>
    [SerializeField]
    public HoverResult[] Results;

    [System.Serializable]
    public struct HoverResult
    {
        [SerializeField]
        public Collider collider;
        [SerializeField]
        public float distanceToCenter;
        [SerializeField]
        public Vector3 closestPointToCenter;

        public HoverResult(Collider collider, Vector3 worldPos)
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

    public HoverSphere(Vector3 position, float radius, int maxResults, LayerMask layerMask, bool startEnabled,bool onlySortClosest)
    {
        WorldPosition = position;
        Radius = radius;
        MaxResults = maxResults;
        HitResults = new Collider[MaxResults];
        Results = new HoverResult[MaxResults];
        LayerMask = layerMask;
        enabled = startEnabled;
        OnlySortClosest = onlySortClosest;
        AllocateArrays();
    }

    private void AllocateArrays()
    {
        if (MaxResults <= 0) MaxResults = 1; // Prevent zero-size arrays

        HitResults = new Collider[MaxResults];
        Results = new HoverResult[MaxResults];
    }

    public void PollSystem(Vector3 updatedWorldPosition)
    {
        if (!enabled)
        {
            ResultCount = 0;
            return;
        }

        WorldPosition = updatedWorldPosition;

        if (HitResults == null || HitResults.Length != MaxResults)
            AllocateArrays();

        ResultCount = Physics.OverlapSphereNonAlloc(WorldPosition, Radius, HitResults, LayerMask, QueryTrigger);
        ProcessHits();
    }

    private void ProcessHits()
    {
        if (OnlySortClosest)
        {
            HoverResult closestResult = default;
            float closestDistance = float.MaxValue;

            for (int Index = 0; Index < ResultCount; Index++)
            {
                Collider col = HitResults[Index];
                if (col == null) continue;

                float distance = Vector3.Distance(col.transform.position, WorldPosition);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestResult = new HoverResult(col, WorldPosition);
                }
            }

            if (ResultCount > 0 && closestResult.collider != null)
            {
                Results[0] = closestResult;
                ResultCount = 1; // update count to reflect only 1 result
            }
            else
            {
                ResultCount = 0;
            }

            return;
        }

        for (int Index = 0; Index < ResultCount; Index++)
        {
            Collider col = HitResults[Index];
            Results[Index] = col != null ? new HoverResult(col, WorldPosition) : default;
        }
        Array.Sort(Results, 0, ResultCount, HoverResultComparer.Instance);
    }

    public HoverResult? ClosestResult()
    {
        return (ResultCount > 0 && MaxResults > 0) ? Results[0] : (HoverResult?)null;
    }

    /// <summary>
    /// Efficient singleton comparer for sorting HoverResults by distance.
    /// </summary>
    private class HoverResultComparer : IComparer<HoverResult>
    {
        public static readonly HoverResultComparer Instance = new HoverResultComparer();

        public int Compare(HoverResult a, HoverResult b)
        {
            return a.distanceToCenter.CompareTo(b.distanceToCenter);
        }
    }
}
