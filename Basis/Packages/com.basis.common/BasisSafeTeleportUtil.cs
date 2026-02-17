using UnityEngine;

public static class BasisSafeTeleportUtil
{
    /// <summary>
    /// Try to find a safe standing position near a target point.
    /// - Snaps to ground if possible
    /// - Ensures capsule doesn't overlap blockers
    /// - Searches nearby if blocked
    /// </summary>
    public static bool TryFindSafeStandingPosition(
        Vector3 target,
        float capsuleRadius,
        float capsuleHeight,
        float skin,
        LayerMask groundMask,
        LayerMask blockingMask,
        float maxDownProbe,
        float maxUpProbe,
        out Vector3 safePos)
    {
        // The capsule endpoints in local-up terms (y axis)
        float usableHeight = Mathf.Max(capsuleHeight, capsuleRadius * 2f + 0.001f);
        float half = usableHeight * 0.5f;

        // We want feet on ground, so compute capsule center from feet point.
        // If we place the capsule center at (feet + up * half), the bottom sphere touches the ground.
        bool TryAt(Vector3 feetPoint, out Vector3 resolvedFeet)
        {
            // 1) Ground snap: probe down from slightly above to find ground
            Vector3 probeStart = feetPoint + Vector3.up * maxUpProbe;
            if (Physics.SphereCast(
                    probeStart,
                    capsuleRadius * 0.9f,
                    Vector3.down,
                    out RaycastHit hit,
                    maxUpProbe + maxDownProbe,
                    groundMask,
                    QueryTriggerInteraction.Ignore))
            {
                resolvedFeet = hit.point;
            }
            else
            {
                // No ground found; keep original feet point (might be a platform-less exit)
                resolvedFeet = feetPoint;
            }

            // 2) Compute capsule points for overlap test (slightly shrunk by skin)
            float r = Mathf.Max(0.001f, capsuleRadius - skin);
            float innerHalf = Mathf.Max(r, half - skin);

            Vector3 center = resolvedFeet + Vector3.up * innerHalf;
            Vector3 p1 = center + Vector3.up * (innerHalf - r);
            Vector3 p2 = center - Vector3.up * (innerHalf - r);

            // 3) Overlap check against blocking geometry
            bool blocked = Physics.CheckCapsule(
                p1, p2, r,
                blockingMask,
                QueryTriggerInteraction.Ignore);

            return !blocked;
        }

        // First try the target directly (treat target as "feet" or near-feet)
        if (TryAt(target, out safePos))
            return true;

        // Search nearby offsets (simple spiral-ish rings)
        // Tune these distances to match your game scale.
        float[] rings = { 0.15f, 0.30f, 0.45f, 0.60f, 0.80f };
        int anglesPerRing = 10;

        for (int ri = 0; ri < rings.Length; ri++)
        {
            float r = rings[ri];
            for (int a = 0; a < anglesPerRing; a++)
            {
                float t = (a / (float)anglesPerRing) * Mathf.PI * 2f;
                Vector3 offset = new Vector3(Mathf.Cos(t), 0f, Mathf.Sin(t)) * r;

                if (TryAt(target + offset, out safePos))
                    return true;
            }
        }

        safePos = target;
        return false;
    }
}
