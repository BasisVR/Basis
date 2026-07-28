using UnityEngine;
namespace Basis.IK
{
    // Derives a limb bend-plane normal from a tracker's live rotation instead of a fixed body-frame axis.
    // A two-bone IK pole expressed as a POSITION degenerates when it lines up with the limb axis (the cross
    // product that builds the bend axis collapses and flips sign). A normal does not: it names the plane
    // directly, so it is flip-free by construction. The knee/elbow flexion axis is fixed in the limb's own
    // frame, so the right normal is the one that rides the limb tracker. Capture stores a known-good world
    // normal in the tracker's local frame at calibration; Resolve rebuilds it from the live tracker rotation.
    public static class BasisTrackerBendNormalCore
    {
        const float k_SqrEpsilon = 1e-8f;

        // Express worldNormalCalib in the tracker's local frame so it can be regenerated later. Returns
        // Vector3.zero when there is nothing usable to store (caller falls back to the body-frame normal).
        public static Vector3 CaptureLocalAxis(Quaternion trackerRotCalib, Vector3 worldNormalCalib)
        {
            if (worldNormalCalib.sqrMagnitude < k_SqrEpsilon)
            {
                return Vector3.zero;
            }

            return Quaternion.Inverse(trackerRotCalib) * worldNormalCalib.normalized;
        }

        // Rebuild the world bend normal from the live tracker rotation. At the calibration pose
        // (trackerRotLive == trackerRotCalib) this returns the captured world normal exactly, so it is a
        // no-op there; as the limb rotates the normal follows it. Falls back when no axis was captured.
        public static Vector3 ResolveWorldNormal(Quaternion trackerRotLive, Vector3 localAxis, Vector3 fallbackWorldNormal)
        {
            if (localAxis.sqrMagnitude < k_SqrEpsilon)
            {
                return fallbackWorldNormal;
            }

            Vector3 world = trackerRotLive * localAxis;
            return world.sqrMagnitude < k_SqrEpsilon ? fallbackWorldNormal : world.normalized;
        }
    }
}
