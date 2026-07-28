using UnityEngine;
namespace Basis.IK
{
    // Inputs for one leg's butterfly-knee solve. World space unless noted.
    public struct BasisButterflyKneeInput
    {
        public Vector3 HipPosition;     // leg root (the upper-leg joint, = two-bone solve Root)
        public Vector3 FootPosition;    // tracked ankle/foot position
        public Vector3 FootInstepDir;   // foot "up" (instep normal): FootRotation * Vector3.up. The sole faces -this.
        public Vector3 OutwardDir;      // unit lateral dir pointing AWAY from the body centerline for THIS leg
        public Vector3 DefaultBendDir;  // unit dir the knee points with no butterfly (sagittal; e.g. hips forward / belly)
        public Vector3 PlayerUp;        // world / playspace up
        public Vector3 TorsoFacingDir;  // hips forward (belly) dir; dot with PlayerUp gives the on-your-back factor
        public float UpperLength;       // thigh length
        public float LowerLength;       // shin length
        public float MaxOpenDeg;        // hip natural max open (abduction) clamp; <=0 falls back to DefaultMaxOpenDeg
        public float Strength;          // external enable / scale 0..1 (global setting * tracked confidence)
        public float SupineFloor;       // 0 = require laying-down; 1 = also allow upright (sitting cross-legged butterfly)
    }

    public struct BasisButterflyKneeResult
    {
        public Vector3 KneeHint;    // world pole position to feed the leg solver's knee hint
        public float HintWeight;    // 0..1 weight for that hint (0 = inactive, fall back to the default bend)
        public float OpenAngleDeg;  // resulting knee abduction angle (always <= MaxOpenDeg)
        public float Supine01;      // how much on-the-back (0..1)
        public float FootTilt01;    // how much the feet are tilted outward (0..1)
        public float PullIn01;      // how much the feet are pulled toward the hips (0..1)
    }

    // Lying on your back and letting your knees fall open -- the "butterfly" / cobbler pose -- with FOOT trackers
    // but no KNEE trackers. The tracked feet tilt outward (soles rotate to face each other, so the instep faces
    // outward) and pull in toward the hips; the knees should splay laterally. This steers the two-bone leg
    // solver's knee POLE outward by an angle that the user controls with foot tilt, amplified by how far the feet
    // are pulled in, and HARD-CLAMPED so the splay never exceeds the hip's natural max open (MaxOpenDeg).
    //
    // Pure + stream-free so it can be swept in edit-mode tests (see BasisButterflyKneeSweepTests). The live wiring
    // (BasisLocalRigDriver, no-knee-tracker branch) gathers the inputs and feeds the result through the existing
    // PositionLeftLowerLeg / EnableLeftLowerLeg knee-hint channel. Sibling of the crouch
    // knee-splay in BasisFootSimulateJob, but for the tracked-foot supine case the foot driver never runs.
    public static class BasisButterflyKneeCore
    {
        // Supine gate: on-back factor ramps over (hipsBelly . playerUp) in [Start, Full]. Upright ~0, flat-on-back ~1.
        public const float ReclineStartDot = 0.50f;
        public const float ReclineFullDot = 0.85f;
        // Foot outward tilt (degrees of instep-toward-outward lean) that maps to full strength.
        public const float FootTiltRefDeg = 55f;
        // Pull-in: hipFootDist / maxReach. >= Start = leg ~straight (no fold), <= Full = knee folded (full amplify).
        public const float PullInStartRatio = 0.97f;
        public const float PullInFullRatio = 0.60f;
        // A near-straight leg can still splay a little; floor keeps a slight effect before the knee folds.
        public const float PullInFloor = 0.20f;
        // engage (supine * tilt) at/above which the hint reaches full weight, so the solver realizes OpenAngleDeg
        // faithfully instead of double-attenuating it. Below this the pole fades in for a pop-free onset.
        public const float EngageFullThreshold = 0.30f;
        // Default hip max abduction if the caller passes MaxOpenDeg <= 0.
        public const float DefaultMaxOpenDeg = 60f;

        const float k_Epsilon = 1e-5f;

        public static void Solve(in BasisButterflyKneeInput i, out BasisButterflyKneeResult r)
        {
            r = default;

            float maxOpenDeg = i.MaxOpenDeg > k_Epsilon ? i.MaxOpenDeg : DefaultMaxOpenDeg;

            Vector3 hipToFoot = i.FootPosition - i.HipPosition;
            float dist = hipToFoot.magnitude;
            float maxReach = i.UpperLength + i.LowerLength;
            Vector3 axis = dist > k_Epsilon ? hipToFoot / dist : Vector3.zero;

            // ── Supine (on-your-back) factor ──
            Vector3 up = i.PlayerUp.sqrMagnitude > k_Epsilon ? i.PlayerUp.normalized : Vector3.up;
            Vector3 belly = i.TorsoFacingDir.sqrMagnitude > k_Epsilon ? i.TorsoFacingDir.normalized : Vector3.forward;
            float supine01 = Saturate(InvLerp(ReclineStartDot, ReclineFullDot, Vector3.Dot(belly, up)));

            // ── Foot outward tilt ──
            // The instep normal (foot "up") leans toward OutwardDir as the soles turn to face each other.
            // dot(instep, outward) = sin(tilt); convert to degrees and normalize against the reference tilt.
            Vector3 outward = i.OutwardDir.sqrMagnitude > k_Epsilon ? i.OutwardDir.normalized : Vector3.zero;
            Vector3 instep = i.FootInstepDir.sqrMagnitude > k_Epsilon ? i.FootInstepDir.normalized : Vector3.zero;
            float tiltSin = Mathf.Clamp(Vector3.Dot(instep, outward), -1f, 1f);
            float tiltDeg = Mathf.Asin(Mathf.Max(0f, tiltSin)) * Mathf.Rad2Deg;
            float footTilt01 = Saturate(tiltDeg / Mathf.Max(1f, FootTiltRefDeg));

            // ── Pull-in (foot pulled toward the hip) ──
            float reachRatio = maxReach > k_Epsilon ? dist / maxReach : 1f;
            float pullIn01 = Saturate(InvLerp(PullInStartRatio, PullInFullRatio, reachRatio)); // closer -> 1
            float amplify = Mathf.Lerp(PullInFloor, 1f, pullIn01);

            r.Supine01 = supine01;
            r.FootTilt01 = footTilt01;
            r.PullIn01 = pullIn01;

            float strength = Saturate(i.Strength);
            // Upright butterfly (sitting cross-legged): SupineFloor relaxes the on-your-back requirement. The
            // foot-tilt + pull-in signals still gate it, so flat-footed standing/walking can't false-engage.
            float supineGate = Mathf.Max(supine01, Saturate(i.SupineFloor));
            float engage = supineGate * footTilt01;         // (laying back OR upright-allowed) AND tilting the feet out
            if (engage <= k_Epsilon || strength <= k_Epsilon || axis == Vector3.zero)
            {
                r.HintWeight = 0f;
                r.OpenAngleDeg = 0f;
                r.KneeHint = BuildHint(i.HipPosition, i.FootPosition, i.DefaultBendDir, axis, i.UpperLength, 0f, outward);
                return;
            }

            // Open angle: how far the knee swings off the sagittal default toward outward. Tilt drives it,
            // pull-in amplifies it. Clamped to the hip's natural max-open by construction (openFrac in [0,1]).
            float openFrac = Saturate(engage * amplify);
            float openDeg = openFrac * maxOpenDeg;

            r.OpenAngleDeg = openDeg;
            // Weight gates the pole on/off; it saturates early so the solver applies OpenAngleDeg faithfully
            // across the engaged range rather than re-scaling it.
            r.HintWeight = strength * Saturate(engage / EngageFullThreshold);
            r.KneeHint = BuildHint(i.HipPosition, i.FootPosition, i.DefaultBendDir, axis, i.UpperLength, openDeg, outward);
        }

        // Pole at the knee midpoint, swung from the sagittal default toward outward by openDeg about the hip->foot axis.
        static Vector3 BuildHint(Vector3 hip, Vector3 foot, Vector3 defaultBendDir, Vector3 axis, float upperLen, float openDeg, Vector3 outward)
        {
            Vector3 mid = (hip + foot) * 0.5f;
            float radius = upperLen > k_Epsilon ? upperLen : 0.4f;

            // Degenerate leg (foot ~ hip): no axis to swing around, just offset along the default bend dir.
            if (axis.sqrMagnitude < k_Epsilon)
            {
                Vector3 d = defaultBendDir.sqrMagnitude > k_Epsilon ? defaultBendDir.normalized : Vector3.up;
                return mid + d * radius;
            }

            // Default bend dir, projected perpendicular to the leg axis (the plane the knee swings in).
            Vector3 defPerp = Vector3.ProjectOnPlane(defaultBendDir, axis);
            if (defPerp.sqrMagnitude < k_Epsilon)
            {
                // Default bend colinear with the leg: pick any stable perpendicular as the base direction.
                defPerp = Vector3.ProjectOnPlane(Vector3.forward, axis);
                if (defPerp.sqrMagnitude < k_Epsilon) defPerp = Vector3.ProjectOnPlane(Vector3.up, axis);
            }
            defPerp.Normalize();

            Vector3 outPerp = Vector3.ProjectOnPlane(outward, axis);
            if (outPerp.sqrMagnitude < k_Epsilon || openDeg <= k_Epsilon)
            {
                return mid + defPerp * radius;
            }
            outPerp.Normalize();

            Vector3 hintDir = Vector3.RotateTowards(defPerp, outPerp, openDeg * Mathf.Deg2Rad, 0f);
            if (hintDir.sqrMagnitude < k_Epsilon) hintDir = defPerp;
            else hintDir.Normalize();
            return mid + hintDir * radius;
        }

        static float InvLerp(float a, float b, float v) => Mathf.Approximately(a, b) ? (v >= b ? 1f : 0f) : (v - a) / (b - a);
        static float Saturate(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
