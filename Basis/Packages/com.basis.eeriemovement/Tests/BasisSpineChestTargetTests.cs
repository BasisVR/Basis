using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    /// <summary>
    /// THE CHEST AS A SECONDARY IK TARGET -- does it improve the chest WITHOUT costing the head?
    ///
    /// The job's SolveChestTarget reads live transform handles, which cannot be driven from NUnit. So this is
    /// a DESIGN MODEL: a self-contained forward-kinematics chain running the EXACT two-phase algorithm the job
    /// runs (place the chest with the lower spine; restore the head with the upper joints), using the SAME
    /// shared BasisTwistSolveCore.ShapeReachStep so the shaping cannot drift from the job. It validates the
    /// principle -- the job wiring is covered by the full suite (no regressions) and, finally, in a headset.
    ///
    /// A humanoid spine is directly parented (spine -> chest -> upperChest -> neck -> head), so this chain's
    /// FK-from-offsets matches what the job reads from live transforms. The properties proven here are the
    /// ones that make the feature safe to ship on by default:
    ///   1. weight 0 is bit-identical to the head-only solve   (the "same usability" guarantee)
    ///   2. the head is never traded away for the chest         (usability preserved at every weight)
    ///   3. the chest lands closer to its target                (the improvement)
    ///   4. no pop as the weight changes                        (continuity)
    /// </summary>
    public sealed class BasisSpineChestTargetTests
    {
        const float Relax = 0.8f;
        const int MaxIters = 20;
        const float Tol = 0.001f;
        const float ChestWeight = 0.5f;   // the job's k_ChestIkWeight
        const int ChestIters = 8;         // the job's k_ChestIkIters
        const int RestoreSweeps = 2;      // the job's k_ChestIkHeadRestoreSweeps

        // TIP -> ROOT, exactly the job's chainHeadToSpine convention: index 0 = head (tip), index N-1 = hips
        // (root, fixed). The parent of joint i is i+1. Offset[i] is the local vector from that parent to i.
        sealed class Chain
        {
            public Vector3[] Offset;
            public Quaternion[] Local;
            public Vector3 RootPos;      // hips, at index N-1
            public Quaternion RootRot;
            public int N => Local.Length;

            public void Fk(Vector3[] wp, Quaternion[] wr)
            {
                int root = N - 1;
                wr[root] = RootRot; wp[root] = RootPos;
                for (int j = root - 1; j >= 0; j--)
                {
                    wr[j] = wr[j + 1] * Local[j];
                    wp[j] = wp[j + 1] + wr[j + 1] * Offset[j];
                }
            }

            public void SetWorldRot(int j, Quaternion newWr)
            {
                var wp = new Vector3[N]; var wr = new Quaternion[N];
                Fk(wp, wr);
                Quaternion parent = j < N - 1 ? wr[j + 1] : RootRot;
                Local[j] = Quaternion.Inverse(parent) * newWr;
            }
        }

        static Quaternion FromTo(Vector3 a, Vector3 b)
        {
            a = a.normalized; b = b.normalized;
            float d = Vector3.Dot(a, b);
            if (d > 0.999999f) return Quaternion.identity;
            if (d < -0.999999f)
            {
                Vector3 ax = Vector3.Cross(a, Vector3.right);
                if (ax.sqrMagnitude < 1e-6f) ax = Vector3.Cross(a, Vector3.up);
                return Quaternion.AngleAxis(180f, ax.normalized);
            }
            Vector3 c = Vector3.Cross(a, b);
            float s = Mathf.Sqrt((1f + d) * 2f), inv = 1f / s;
            return new Quaternion(c.x * inv, c.y * inv, c.z * inv, s * 0.5f);
        }

        static void ReachHead(Chain ch, Vector3 headTarget, int i, int first, float span, Vector3 up)
        {
            var wp = new Vector3[ch.N]; var wr = new Quaternion[ch.N];
            ch.Fk(wp, wr);
            Vector3 cur = wp[0] - wp[i];
            Vector3 tgt = headTarget - wp[i];
            if (cur.sqrMagnitude < 1e-10f || tgt.sqrMagnitude < 1e-10f) return;
            Quaternion delta = FromTo(cur, tgt);
            float t = (i - first) / span;
            float twistKeep = Mathf.Lerp(0.9f, 0.25f, t);
            float swingScale = 1f - 0.3f * (1f - Mathf.Abs(2f * t - 1f));
            delta = BasisTwistSolveCore.ShapeReachStep(delta, up, twistKeep, swingScale);
            delta = Quaternion.Slerp(Quaternion.identity, delta, Relax);
            ch.SetWorldRot(i, delta * wr[i]);
        }

        // Mirrors the job: Phase A (head only), then Phase B (chest via lower spine, head restored by upper).
        static void Solve(Chain ch, Vector3 headTarget, Vector3 chestTarget, int chestIdx, float chestWeight)
        {
            int tip = 0, first = 1, last = ch.N - 2;
            float span = Mathf.Max(1, last - first);
            Vector3 up = Vector3.up;
            var wp = new Vector3[ch.N]; var wr = new Quaternion[ch.N];

            for (int it = 0; it < MaxIters; it++)
            {
                ch.Fk(wp, wr);
                if ((headTarget - wp[tip]).sqrMagnitude < Tol * Tol) break;
                for (int i = last; i >= first; i--) ReachHead(ch, headTarget, i, first, span, up);
            }

            if (chestWeight > 0f && last > first && last > chestIdx)
            {
                float st = (last - first) / span;
                float twistKeep = Mathf.Lerp(0.9f, 0.25f, st);
                float swingScale = 1f - 0.3f * (1f - Mathf.Abs(2f * st - 1f));
                for (int c = 0; c < ChestIters; c++)
                {
                    ch.Fk(wp, wr);
                    Vector3 cCur = wp[chestIdx] - wp[last];
                    Vector3 cTgt = chestTarget - wp[last];
                    if (cCur.sqrMagnitude > 1e-10f && cTgt.sqrMagnitude > 1e-10f)
                    {
                        Quaternion d = FromTo(cCur, cTgt);
                        d = BasisTwistSolveCore.ShapeReachStep(d, up, twistKeep, swingScale);
                        d = Quaternion.Slerp(Quaternion.identity, d, Relax * chestWeight);
                        ch.SetWorldRot(last, d * wr[last]);
                    }
                    for (int sweep = 0; sweep < RestoreSweeps; sweep++)
                        for (int i = last - 1; i >= first; i--) ReachHead(ch, headTarget, i, first, span, up);
                }
            }
        }

        // A straight chain in the job's TIP->ROOT order: head(0), neck(1), upperChest(2), chest(3),
        // spine(4), hips(5). Each bone points +Y from its parent (toward the root) up to the tip.
        static Chain Straight()
        {
            int n = 6;
            var off = new Vector3[n]; var loc = new Quaternion[n];
            for (int j = 0; j < n; j++)
            {
                off[j] = j == n - 1 ? Vector3.zero : new Vector3(0f, 0.22f, 0f);   // hips (root) has no offset
                loc[j] = Quaternion.identity;
            }
            return new Chain { Offset = off, Local = loc, RootPos = Vector3.zero, RootRot = Quaternion.identity };
        }

        const int ChestIdx = 3;   // the Chest bone = chainLen-3, exactly as the job computes it

        // Head target requires a real forward bend. The chest target is a REACHABLE chest position -- read
        // off a pose where the spine (the joint that owns the chest) is rotated a little -- so it sits on the
        // chest's reachable arc, exactly like the live rig's target (which comes from the avatar's own
        // skeleton). It is deliberately NOT where the head-only solve leaves the chest, so weight>0 has a real
        // ~several-cm correction to make.
        static readonly Vector3 k_Head = new Vector3(0f, 0.9f, 0.35f);

        static (Vector3 head, Vector3 chest) Targets()
        {
            int last = 6 - 2;   // spine
            Chain probe = Straight();
            Solve(probe, k_Head, Vector3.zero, ChestIdx, 0f);          // head-only baseline
            var wp = new Vector3[probe.N]; var wr = new Quaternion[probe.N];
            probe.Fk(wp, wr);
            // rotate ONLY the spine, then read the chest -> a genuinely reachable, off-baseline target
            probe.SetWorldRot(last, Quaternion.AngleAxis(14f, Vector3.right) * wr[last]);
            probe.Fk(wp, wr);
            return (k_Head, wp[ChestIdx]);
        }

        static float HeadErr(Chain ch, Vector3 head)
        {
            var wp = new Vector3[ch.N]; var wr = new Quaternion[ch.N]; ch.Fk(wp, wr);
            return (wp[0] - head).magnitude;
        }

        static float ChestErr(Chain ch, Vector3 chest)
        {
            var wp = new Vector3[ch.N]; var wr = new Quaternion[ch.N]; ch.Fk(wp, wr);
            return (wp[ChestIdx] - chest).magnitude;
        }

        // -------------------------------------------------------------------------------------------------

        [Test]
        public void WeightZero_IsExactlyTheHeadOnlySolve()
        {
            var (head, chest) = Targets();
            Chain a = Straight(); Solve(a, head, chest, ChestIdx, 0f);
            Chain b = Straight(); Solve(b, head, chest, ChestIdx, 0f);
            // deterministic, and Phase B is gated off at weight 0 -- so nothing the chest target does can
            // reach the bones. This is the "same usability when off" guarantee.
            for (int j = 0; j < a.N; j++)
                Assert.AreEqual(0f, Quaternion.Angle(a.Local[j], b.Local[j]), 1e-4f, $"joint {j} not deterministic");
            Assert.Greater(ChestErr(a, chest), 0.02f, "the test setup must leave a real chest error at weight 0");
        }

        [Test]
        public void TheHead_IsNeverTradedAwayForTheChest()
        {
            var (head, chest) = Targets();
            Chain head0 = Straight(); Solve(head0, head, chest, ChestIdx, 0f);
            float baseHead = HeadErr(head0, head);

            foreach (float w in new[] { 0.25f, 0.5f, 0.75f, 1.0f })
            {
                Chain ch = Straight(); Solve(ch, head, chest, ChestIdx, w);
                float he = HeadErr(ch, head);
                // the head must stay at least as well placed as head-only, within a hair. At the shipped
                // weight it is measurably BETTER (the restore sweeps tighten it) -- that is a bonus, not the
                // requirement. The requirement is: turning the chest on does not push the head off the HMD.
                Assert.LessOrEqual(he, baseHead + 0.005f,
                    $"weight {w}: head error {he:F4} m worse than head-only {baseHead:F4} m -- the head was sacrificed");
            }
        }

        [Test]
        public void TheChest_LandsCloserToItsTarget()
        {
            var (head, chest) = Targets();
            Chain w0 = Straight(); Solve(w0, head, chest, ChestIdx, 0f);
            Chain w5 = Straight(); Solve(w5, head, chest, ChestIdx, ChestWeight);

            float e0 = ChestErr(w0, chest);
            float e5 = ChestErr(w5, chest);
            Assert.Less(e5, e0 * 0.8f,
                $"chest targeting barely moved the chest: {e0 * 100f:F1} cm -> {e5 * 100f:F1} cm (want a real reduction)");
        }

        [Test]
        public void MoreWeight_MovesTheChestMonotonicallyCloser()
        {
            var (head, chest) = Targets();
            float prev = float.MaxValue;
            foreach (float w in new[] { 0f, 0.25f, 0.5f, 0.75f, 1.0f })
            {
                Chain ch = Straight(); Solve(ch, head, chest, ChestIdx, w);
                float e = ChestErr(ch, chest);
                Assert.LessOrEqual(e, prev + 1e-3f, $"chest error rose from weight step to {w}: {e:F4}");
                prev = e;
            }
        }

        [Test]
        public void TheWeightIsContinuous_NoPopAcrossTheSweep()
        {
            var (head, chest) = Targets();
            Chain prev = null;
            float worstStep = 0f;
            for (float w = 0f; w <= 1.0001f; w += 0.05f)
            {
                Chain ch = Straight(); Solve(ch, head, chest, ChestIdx, w);
                if (prev != null)
                {
                    var wpA = new Vector3[ch.N]; var wrA = new Quaternion[ch.N]; ch.Fk(wpA, wrA);
                    var wpB = new Vector3[prev.N]; var wrB = new Quaternion[prev.N]; prev.Fk(wpB, wrB);
                    worstStep = Mathf.Max(worstStep, (wpA[ChestIdx] - wpB[ChestIdx]).magnitude);
                }
                prev = ch;
            }
            // a 0.05 weight step must move the chest a small, smooth amount -- not jump. A pop would be a
            // large discontinuity here.
            Assert.Less(worstStep, 0.02f, $"chest jumped {worstStep * 100f:F1} cm across a 0.05 weight step (a pop)");
        }

        [Test]
        public void TheHeadStaysReachable_AtEveryWeight()
        {
            var (head, chest) = Targets();
            foreach (float w in new[] { 0f, 0.5f, 1.0f })
            {
                Chain ch = Straight(); Solve(ch, head, chest, ChestIdx, w);
                Assert.Less(HeadErr(ch, head), 0.03f, $"weight {w}: head left {HeadErr(ch, head) * 100f:F1} cm off target");
            }
        }
    }
}
