using System;
using System.Runtime.CompilerServices;
using UnityEngine;
/// <summary>
/// Axis-aligned bounding box (AABB), Unity Bounds-like: stored as center + extents.
/// </summary>
[Serializable]
public struct BasisBounds
{
    [SerializeField]
    public Vector3 m_Center;
    [SerializeField]
    public Vector3 m_Extents; // half-size

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BasisBounds(Vector3 center, Vector3 size)
    {
        m_Center = center;
        m_Extents = size * 0.5f;
    }

    public Vector3 center
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => m_Center;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => m_Center = value;
    }

    /// <summary> Total size (always 2*extents). </summary>
    public Vector3 size
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => m_Extents * 2f;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => m_Extents = value * 0.5f;
    }

    /// <summary> Half-size. </summary>
    public Vector3 extents
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => m_Extents;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => m_Extents = value;
    }

    /// <summary> center - extents </summary>
    public Vector3 min
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => m_Center - m_Extents;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => SetMinMax(value, max);
    }

    /// <summary> center + extents </summary>
    public Vector3 max
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly get => m_Center + m_Extents;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => SetMinMax(min, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetMinMax(Vector3 min, Vector3 max)
    {
        m_Extents = (max - min) * 0.5f;
        m_Center = min + m_Extents;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encapsulate(Vector3 point)
    {
        var newMin = Vector3.Min(min, point);
        var newMax = Vector3.Max(max, point);
        SetMinMax(newMin, newMax);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encapsulate(BasisBounds bounds)
    {
        Encapsulate(bounds.min);
        Encapsulate(bounds.max);
    }

    /// <summary>
    /// Expand by amount on each side (Unity semantics: extents += amount/2).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Expand(float amount)
    {
        float half = amount * 0.5f;
        m_Extents += new Vector3(half, half, half);
    }

    /// <summary>
    /// Expand by amount on each axis (Unity semantics: extents += amount/2 per component).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Expand(Vector3 amount)
    {
        m_Extents += amount * 0.5f;
    }

    /// <summary>
    /// AABB-AABB overlap test (inclusive on faces, like Unity).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Intersects(BasisBounds other)
    {
        Vector3 aMin = min;
        Vector3 aMax = max;
        Vector3 bMin = other.min;
        Vector3 bMax = other.max;

        return aMin.x <= bMax.x && aMax.x >= bMin.x
            && aMin.y <= bMax.y && aMax.y >= bMin.y
            && aMin.z <= bMax.z && aMax.z >= bMin.z;
    }

    /// <summary>
    /// Point containment (inclusive on faces, typical Bounds behavior).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Contains(Vector3 point)
    {
        Vector3 aMin = min;
        Vector3 aMax = max;
        return point.x >= aMin.x && point.x <= aMax.x
            && point.y >= aMin.y && point.y <= aMax.y
            && point.z >= aMin.z && point.z <= aMax.z;
    }

    /// <summary>
    /// Closest point on or inside the AABB to an arbitrary point.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector3 ClosestPoint(Vector3 point)
    {
        Vector3 aMin = min;
        Vector3 aMax = max;

        // clamp each axis
        float x = point.x < aMin.x ? aMin.x : (point.x > aMax.x ? aMax.x : point.x);
        float y = point.y < aMin.y ? aMin.y : (point.y > aMax.y ? aMax.y : point.y);
        float z = point.z < aMin.z ? aMin.z : (point.z > aMax.z ? aMax.z : point.z);

        return new Vector3(x, y, z);
    }

    /// <summary>
    /// Squared distance from point to AABB (0 if inside).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly float SqrDistance(Vector3 point)
    {
        Vector3 aMin = min;
        Vector3 aMax = max;

        float dx = (point.x < aMin.x) ? (aMin.x - point.x) : (point.x > aMax.x ? (point.x - aMax.x) : 0f);
        float dy = (point.y < aMin.y) ? (aMin.y - point.y) : (point.y > aMax.y ? (point.y - aMax.y) : 0f);
        float dz = (point.z < aMin.z) ? (aMin.z - point.z) : (point.z > aMax.z ? (point.z - aMax.z) : 0f);

        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>
    /// Ray-AABB intersection test. Returns true if the ray hits; distance is nearest hit t >= 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool IntersectRay(Ray ray) => IntersectRay(ray, out _);

    /// <summary>
    /// Ray-AABB intersection test. 'distance' matches Unity's style: the param along the ray direction.
    /// </summary>
    public readonly bool IntersectRay(Ray ray, out float distance)
    {
        // Slab method. Works with negative directions.
        Vector3 aMin = min;
        Vector3 aMax = max;

        float tMin = 0f;
        float tMax = float.PositiveInfinity;

        if (!Slab(ray.origin.x, ray.direction.x, aMin.x, aMax.x, ref tMin, ref tMax) ||
            !Slab(ray.origin.y, ray.direction.y, aMin.y, aMax.y, ref tMin, ref tMax) ||
            !Slab(ray.origin.z, ray.direction.z, aMin.z, aMax.z, ref tMin, ref tMax))
        {
            distance = 0f;
            return false;
        }

        distance = tMin; // nearest hit
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Slab(float origin, float dir, float min, float max, ref float tMin, ref float tMax)
    {
        const float EPS = 1e-8f;

        if (Mathf.Abs(dir) < EPS)
        {
            // Ray parallel to slab: must be within the slab to intersect.
            return origin >= min && origin <= max;
        }

        float inv = 1f / dir;
        float t1 = (min - origin) * inv;
        float t2 = (max - origin) * inv;

        if (t1 > t2) (t1, t2) = (t2, t1);

        if (t1 > tMin) tMin = t1;
        if (t2 < tMax) tMax = t2;

        return tMax >= tMin && tMax >= 0f;
    }
}
