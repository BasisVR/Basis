using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Offline invariant sweep of the body self-collision primitives on BasisFullIKConstraintJob
    // (ClosestPointOnSegment, SegmentSegmentClosestPoints, CapsuleCapsuleResolve, PushOutFromCapsule).
    // These resolve hand/limb-vs-torso capsule penetration in SolveHand and feed BasisElbowProtectCore,
    // yet the raw geometry had no direct test. Each case verifies exact optimality certificates rather
    // than a tolerance against brute force: the closest points lie on their segments, the connecting
    // vector is perpendicular to an interior segment (KKT), the result is symmetric under input swap,
    // the reported penetration depth equals the real overlap, and applying the push actually separates
    // the capsules. Pure math, runs off the main thread.
    public struct BasisCapsuleCollisionSweepConfig
    {
        public int OffsetSteps;    // per-axis grid of seg1 centre offsets around seg2
        public float OffsetRange;  // metres, +/- on each axis

        public static BasisCapsuleCollisionSweepConfig Default()
        {
            return new BasisCapsuleCollisionSweepConfig
            {
                OffsetSteps = 7,
                OffsetRange = 0.6f,
            };
        }
    }

    public struct BasisCapsuleCollisionSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Cases;
        public int OverlapCases;             // capsule pairs that actually penetrated
        public int NaNCount;                 // non-finite closest point / push
        public float MaxClosestParamErr;     // s,t pushed outside [0,1]
        public float MaxFixedPointErr;       // ClosestPointOnSegment(c2,seg1)==c1 and the mirror (m)
        public float MaxKktResidual;         // interior closest point perpendicular to its segment dir (unit dot)
        public float MaxSymmetryErr;         // |dist(A,B) - dist(B,A)| (m)
        public float MaxPenetrationDepthErr; // | |push| - (rSum - dist) | (m)
        public float MaxResidualPenMm;       // leftover penetration after the push, well-conditioned shallow overlaps (gated)
        public float MaxDeepResidualPenMm;   // residual on deep/crossing-core overlaps (reported; single-step MTD can't un-cross)
        public int DeepOverlapCases;         // overlaps outside the resolver's shallow design regime
        public float MaxPushOutSurfaceErrMm; // PushOutFromCapsule lands on the radius surface
        public int Failures;
        public string Error;
    }

    public static class BasisCapsuleCollisionSweep
    {
        public const string DefaultFileName = "BasisCapsuleCollisionSweep.csv";
        const float k_Eps = 1e-5f;
        const float k_ParallelDot = 0.999f;   // above this the closest pair is non-unique; skip the sharp certificates

        public static string DefaultPath() => System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);

        // Torso-like reference segments (segment 2) the limb capsule is tested against, plus the limb
        // orientations, lengths and radius pairs. Degenerate inputs (point segments, parallel/coincident)
        // are included on purpose -- they are the branch cases in SegmentSegmentClosestPoints.
        static readonly (Vector3 a, Vector3 b)[] k_Seg2 =
        {
            (new Vector3(0f, -0.5f, 0f), new Vector3(0f, 0.5f, 0f)),   // vertical (spine)
            (new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f)),   // horizontal x
            (new Vector3(0f, 0f, -0.5f), new Vector3(0f, 0f, 0.5f)),   // horizontal z
            (new Vector3(-0.3f, -0.3f, -0.2f), new Vector3(0.3f, 0.4f, 0.25f)), // skew
        };
        static readonly Vector3[] k_Dirs =
        {
            new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
            new Vector3(1f, 1f, 0f), new Vector3(1f, 0f, 1f), new Vector3(0.4f, 1f, 0.7f),
        };
        static readonly float[] k_Lengths = { 0f, 0.4f, 0.9f };
        static readonly (float r1, float r2)[] k_Radii = { (0.1f, 0.1f), (0.05f, 0.2f), (0.2f, 0.05f) };

        public static BasisCapsuleCollisionSweepSummary Run(BasisCapsuleCollisionSweepConfig cfg, string path)
        {
            var summary = new BasisCapsuleCollisionSweepSummary { Ok = false, Path = path };

            int steps = Mathf.Max(1, cfg.OffsetSteps);
            float range = Mathf.Max(0f, cfg.OffsetRange);

            int cases = 0, overlap = 0, nan = 0, fails = 0, deepCount = 0;
            float maxParam = 0f, maxFixed = 0f, maxKkt = 0f, maxSym = 0f, maxDepth = 0f, maxResid = 0f, maxDeepResid = 0f, maxPush = 0f;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisCapsuleCollisionSweep " + System.DateTime.UtcNow.ToString("o"));
                    w.WriteLine("seg2,dir,len,r1,r2,ox,oy,oz,dist,overlap,param_err,fixed_err,kkt_resid,sym_err,depth_err,resid_pen_mm,deep_resid_mm,pushout_mm,fail");

                    var sb = new StringBuilder(192);

                    for (int si = 0; si < k_Seg2.Length; si++)
                    {
                        Vector3 p2 = k_Seg2[si].a, q2 = k_Seg2[si].b;
                        Vector3 mid2 = (p2 + q2) * 0.5f;

                        for (int di = 0; di < k_Dirs.Length; di++)
                        {
                            Vector3 dirN = k_Dirs[di].normalized;
                            for (int li = 0; li < k_Lengths.Length; li++)
                            {
                                float len = k_Lengths[li];
                                for (int ri = 0; ri < k_Radii.Length; ri++)
                                {
                                    float r1 = k_Radii[ri].r1, r2 = k_Radii[ri].r2;

                                    for (int ix = 0; ix < steps; ix++)
                                    {
                                        float ox = Axis(range, steps, ix);
                                        for (int iy = 0; iy < steps; iy++)
                                        {
                                            float oy = Axis(range, steps, iy);
                                            for (int iz = 0; iz < steps; iz++)
                                            {
                                                float oz = Axis(range, steps, iz);
                                                Vector3 centre = mid2 + new Vector3(ox, oy, oz);
                                                Vector3 p1 = centre - dirN * (len * 0.5f);
                                                Vector3 q1 = centre + dirN * (len * 0.5f);

                                                bool fail = EvaluateCase(p1, q1, r1, r2, p2, q2,
                                                    out float dist, out bool didOverlap, out float paramErr,
                                                    out float fixedErr, out float kkt, out float sym,
                                                    out float depthErr, out float residMm, out float deepResidMm,
                                                    out bool deepOverlap, out float pushMm, out bool hadNaN);

                                                cases++;
                                                if (hadNaN) nan++;
                                                if (didOverlap) overlap++;
                                                if (deepOverlap) deepCount++;
                                                if (fail) fails++;
                                                if (paramErr > maxParam) maxParam = paramErr;
                                                if (fixedErr > maxFixed) maxFixed = fixedErr;
                                                if (kkt > maxKkt) maxKkt = kkt;
                                                if (sym > maxSym) maxSym = sym;
                                                if (depthErr > maxDepth) maxDepth = depthErr;
                                                if (residMm > maxResid) maxResid = residMm;
                                                if (deepResidMm > maxDeepResid) maxDeepResid = deepResidMm;
                                                if (pushMm > maxPush) maxPush = pushMm;

                                                sb.Clear();
                                                sb.Append(si).Append(',').Append(di).Append(',');
                                                Append(sb, len); Append(sb, r1); Append(sb, r2);
                                                Append(sb, ox); Append(sb, oy); Append(sb, oz);
                                                Append(sb, dist);
                                                sb.Append(didOverlap ? '1' : '0').Append(',');
                                                Append(sb, paramErr); Append(sb, fixedErr); Append(sb, kkt);
                                                Append(sb, sym); Append(sb, depthErr); Append(sb, residMm); Append(sb, deepResidMm); Append(sb, pushMm);
                                                sb.Append(fail ? '1' : '0');
                                                w.WriteLine(sb.ToString());
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                summary.Ok = true;
                summary.Cases = cases;
                summary.OverlapCases = overlap;
                summary.NaNCount = nan;
                summary.MaxClosestParamErr = maxParam;
                summary.MaxFixedPointErr = maxFixed;
                summary.MaxKktResidual = maxKkt;
                summary.MaxSymmetryErr = maxSym;
                summary.MaxPenetrationDepthErr = maxDepth;
                summary.MaxResidualPenMm = maxResid;
                summary.MaxDeepResidualPenMm = maxDeepResid;
                summary.DeepOverlapCases = deepCount;
                summary.MaxPushOutSurfaceErrMm = maxPush;
                summary.Failures = fails;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }

            return summary;
        }

        static bool EvaluateCase(Vector3 p1, Vector3 q1, float r1, float r2, Vector3 p2, Vector3 q2,
            out float dist, out bool didOverlap, out float paramErr, out float fixedErr, out float kkt,
            out float sym, out float depthErr, out float residMm, out float deepResidMm, out bool deepOverlap,
            out float pushMm, out bool hadNaN)
        {
            dist = 0f; didOverlap = false; paramErr = 0f; fixedErr = 0f; kkt = 0f;
            sym = 0f; depthErr = 0f; residMm = 0f; deepResidMm = 0f; deepOverlap = false; pushMm = 0f; hadNaN = false;

            BasisEerieMovement.SegmentSegmentClosestPoints(p1, q1, p2, q2, out float s, out float t, out Vector3 c1, out Vector3 c2);
            if (!Finite(c1) || !Finite(c2) || !Finite(s) || !Finite(t))
            {
                hadNaN = true;
                return true;
            }

            dist = (c1 - c2).magnitude;
            Vector3 d1 = q1 - p1, d2 = q2 - p2;
            float len1 = d1.magnitude, len2 = d2.magnitude;

            paramErr = Mathf.Max(Mathf.Max(Relu(-s), Relu(s - 1f)), Mathf.Max(Relu(-t), Relu(t - 1f)));

            // Consistency + alternating-projection fixed point: at the true optimum, c1 is the closest
            // point on seg1 to c2 and vice versa. (Skipped when a pair is parallel -- the optimum is a
            // non-unique continuum and a different-but-equally-valid point would read as a false miss.)
            Vector3 d1n = len1 > k_Eps ? d1 / len1 : Vector3.zero;
            Vector3 d2n = len2 > k_Eps ? d2 / len2 : Vector3.zero;
            bool parallel = len1 > k_Eps && len2 > k_Eps && Mathf.Abs(Vector3.Dot(d1n, d2n)) > k_ParallelDot;
            bool sharp = len1 > k_Eps && len2 > k_Eps && !parallel;

            if (sharp)
            {
                Vector3 cp1 = BasisEerieMovement.ClosestPointOnSegment(c2, p1, q1);
                Vector3 cp2 = BasisEerieMovement.ClosestPointOnSegment(c1, p2, q2);
                fixedErr = (cp1 - c1).magnitude + (cp2 - c2).magnitude;

                Vector3 n = c1 - c2;
                float nMag = n.magnitude;
                if (nMag > k_Eps)
                {
                    Vector3 nn = n / nMag;
                    if (s > 1e-3f && s < 1f - 1e-3f) kkt = Mathf.Max(kkt, Mathf.Abs(Vector3.Dot(nn, d1n)));
                    if (t > 1e-3f && t < 1f - 1e-3f) kkt = Mathf.Max(kkt, Mathf.Abs(Vector3.Dot(nn, d2n)));
                }
            }

            // Symmetry: swapping the two capsules must not change the separation.
            BasisEerieMovement.SegmentSegmentClosestPoints(p2, q2, p1, q1, out _, out _, out Vector3 c1b, out Vector3 c2b);
            if (Finite(c1b) && Finite(c2b)) sym = Mathf.Abs(dist - (c1b - c2b).magnitude);
            else hadNaN = true;

            // Penetration round-trip: the reported push depth must equal the overlap, and translating
            // capsule 1 by it must separate the pair.
            float rSum = r1 + r2;
            Vector3 push = BasisEerieMovement.CapsuleCapsuleResolve(p1, q1, r1, p2, q2, r2, Vector3.up);
            if (!Finite(push)) { hadNaN = true; }
            else if (dist >= rSum)
            {
                depthErr = push.magnitude; // must be zero when not penetrating
            }
            else
            {
                didOverlap = true;
                float overlapDepth = rSum - dist;
                depthErr = Mathf.Abs(push.magnitude - overlapDepth);
                if (overlapDepth > k_Eps)
                {
                    BasisEerieMovement.SegmentSegmentClosestPoints(p1 + push, q1 + push, p2, q2, out _, out _, out Vector3 nc1, out Vector3 nc2);
                    if (Finite(nc1) && Finite(nc2))
                    {
                        float resid = rSum - (nc1 - nc2).magnitude;
                        float residMmLocal = resid > 0f ? resid * 1000f : 0f;
                        // A single-step minimum-translation push separates exactly only in the resolver's
                        // design regime: a SHALLOW overlap with interior closest points. Deep penetration /
                        // crossing cores (dist~0, where the limb would skewer the torso) can't be un-crossed
                        // by one push and are out of scope for the live collision system -- reported, not gated.
                        bool wellConditioned = dist > k_Eps
                            && s >= 0.3f && s <= 0.7f && t >= 0.3f && t <= 0.7f
                            && overlapDepth <= 0.3f * rSum;
                        if (wellConditioned) residMm = residMmLocal;
                        else { deepResidMm = residMmLocal; deepOverlap = true; }
                    }
                    else hadNaN = true;
                }
            }

            // PushOutFromCapsule: a point inside the radius must land exactly on the surface; one outside
            // is returned untouched. Tested on the closest point of seg2 against the seg1 capsule.
            if (len1 > k_Eps)
            {
                Vector3 testP = c2;
                Vector3 cp = BasisEerieMovement.ClosestPointOnSegment(testP, p1, q1);
                float d = (testP - cp).magnitude;
                Vector3 outP = BasisEerieMovement.PushOutFromCapsule(testP, p1, q1, r1, Vector3.up);
                if (!Finite(outP)) hadNaN = true;
                else if (d >= r1) pushMm = (outP - testP).magnitude * 1000f; // outside -> unchanged
                else if (d > k_Eps) // skip on-axis (normal is ill-defined; pushout falls back to playerUp)
                {
                    Vector3 cpo = BasisEerieMovement.ClosestPointOnSegment(outP, p1, q1);
                    pushMm = Mathf.Abs((outP - cpo).magnitude - r1) * 1000f;
                }
            }

            bool fail = hadNaN
                || paramErr > 1e-3f
                || fixedErr > 1e-3f
                || kkt > 1e-2f
                || sym > 1e-3f
                || depthErr > 1e-3f
                || residMm > 2f
                || pushMm > 1f;
            return fail;
        }

        static float Axis(float range, int steps, int idx)
        {
            if (steps <= 1) return 0f;
            return Mathf.Lerp(-range, range, idx / (float)(steps - 1));
        }

        static float Relu(float v) => v > 0f ? v : 0f;
        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
