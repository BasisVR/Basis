using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisKneeDeepCrouchTests
    {
        const float S = 1.50f;                 // standing head height
        const float R = 0.52f;                 // hips->head chain
        const float Thigh = 0.36f, Shin = 0.36f;
        const float HipHalf = 0.09f;
        static float SitBack(float dhat)
        {
            float x = dhat - BasisCrouchOffsetCore.depthDeadzone;
            if (x <= 0f) return 0f;
            return BasisCrouchOffsetCore.setbackSlope * x / (1f + BasisCrouchOffsetCore.setbackSat * x) * S;
        }
        struct Step
        {
            public float LateralDeg;   // knee off the sagittal plane, + = outward
            public float ForwardFrac;  // knee perp z-fraction, + = knee in front of the hip-ankle axis
            public float KneeStepMm;   // knee travel since the previous depth step
        }
        static Step[] Descend(bool sitBack)
        {
            var steps = new System.Collections.Generic.List<Step>();
            Vector3 prevKnee = default, prevFoot = default, prevOut = default;
            bool have = false;

            for (float dhat = 0f; dhat <= 0.501f; dhat += 0.0025f)
            {
                float depth = dhat * S;
                Vector3 head = new Vector3(0f, S - depth, 0f);
                float s = sitBack ? SitBack(dhat) : 0f, drop = Mathf.Sqrt(Mathf.Max(R * R - s * s, 0f));
                Vector3 hips = head + new Vector3(0f, -drop, -s), chest = hips + 0.55f * (head - hips);
                float leanDeg = Mathf.Atan2(s, drop) * Mathf.Rad2Deg;
                Quaternion pelvis = Quaternion.AngleAxis(Mathf.Clamp(leanDeg - 40f, 0f, 52f), Vector3.right);
                Vector3 hipsRight = pelvis * Vector3.right;

                // right leg (the left is its mirror; the probe showed them symmetric)
                Vector3 hipJ = hips + pelvis * new Vector3(HipHalf, -0.03f, 0f);
                Vector3 ankle = new Vector3(HipHalf, 0.07f, 0f);
                if (!have) { prevKnee = hipJ + new Vector3(0f, -Thigh, 0.01f); prevFoot = ankle; }

                BasisSwivelFrame fr = BasisSwivelHintCore.BuildFrame(hips + pelvis * new Vector3(-HipHalf, -0.03f, 0f), hips + pelvis * new Vector3(+HipHalf, -0.03f, 0f), hips, chest);
                BasisSwivelHintCore.LegHint(fr, hipJ, ankle, Thigh + Shin, false, out Vector3 hint, out float conf);
                float trust = BasisSwivelHintCore.LegModelTrust(conf);
                BasisLegSolveInput i = default;
                i.Root = hipJ;
                i.Mid = prevKnee;
                i.Tip = prevFoot;
                i.RootRotation = pelvis;
                i.MidRotation = Quaternion.identity;
                i.TargetPosition = ankle;
                i.TargetRotation = Quaternion.identity;
                i.TargetOffset = Quaternion.identity;
                i.HintPosition = hint;
                i.HintWeight = 1f;
                i.HintDistrust = 1f - trust;
                i.BendNormal = hipsRight;
                i.AnteriorNormal = hipsRight;
                BasisLegSolveCore.Solve(i, out BasisLegSolveResult r);

                Vector3 axis = (r.FootSolved - hipJ).normalized, d = r.KneeSolved - hipJ;
                Vector3 perp = d - axis * Vector3.Dot(d, axis);
                Step st = default;
                if (perp.magnitude > 1e-6f)
                {
                    Vector3 pn = perp.normalized;
                    st.LateralDeg = Mathf.Asin(Mathf.Clamp(Mathf.Abs(pn.x), 0f, 1f)) * Mathf.Rad2Deg * Mathf.Sign(pn.x);
                    st.ForwardFrac = pn.z;
                }
                st.KneeStepMm = have ? (r.KneeSolved - prevOut).magnitude * 1000f : 0f;
                steps.Add(st);

                prevOut = r.KneeSolved;
                prevKnee = r.KneeSolved;
                prevFoot = r.FootSolved;
                have = true;
            }
            return steps.ToArray();
        }
        [Test]
        public void StraightDownDeepCrouch_KneesStayForward_NotPinnedLateral()
        {
            // The reported bug's geometry: hips straight down, feet ending up under them. Pre-fix the
            // bottom of this descent pinned the knee at ~87 deg lateral.
            foreach (Step s in Descend(sitBack: false))
            {
                Assert.That(Mathf.Abs(s.LateralDeg), Is.LessThan(45f), $"knee swung {s.LateralDeg:F1} deg off sagittal during a straight-down crouch.");
            }
            Step bottom = Descend(sitBack: false)[^1];
            Assert.That(bottom.ForwardFrac, Is.GreaterThan(0.7f), $"at full depth the knee should point forward (sitting on heels), got z-fraction {bottom.ForwardFrac:F2}.");
        }
        [Test]
        public void SitBackDeepCrouch_KneesTrackForward()
        {
            foreach (Step s in Descend(sitBack: true))
            {
                Assert.That(Mathf.Abs(s.LateralDeg), Is.LessThan(30f), $"knee swung {s.LateralDeg:F1} deg off sagittal during a sit-back crouch.");
            }
        }
        [Test]
        public void Descents_HaveNoKneeClicks()
        {
            // Pre-fix the trust collapse/recovery clicked the knee 30-38 mm per 2.5 mm depth step.
            foreach (bool sitBack in new[] { true, false })
            {
                foreach (Step s in Descend(sitBack))
                {
                    Assert.That(s.KneeStepMm, Is.LessThan(15f), $"knee stepped {s.KneeStepMm:F1} mm on a 2.5 mm depth step (sitBack={sitBack}).");
                }
            }
        }
        [Test]
        public void Model_IsConfidentlyWrong_UnderTheHips_WhichIsWhyTheGuardExists()
        {
            // Anti-tautology: the raw polynomial at the sitting-on-heels pose (foot straight under the hip
            // at fractional reach) reports a healthy confidence while its swivel is nowhere near the true
            // knee-forward answer (~+90 for a right leg). If this ever starts passing sanely, the domain
            // guard band can be revisited.
            float swivel = BasisLegSwivelModel.SwivelRad(new float3(0f, -0.18f, 0.0f), out float conf) * Mathf.Rad2Deg;
            Assert.That(conf, Is.GreaterThan(0.5f),"the model no longer overstates confidence under the hips -- re-measure the guard band.");
            Assert.That(swivel, Is.LessThan(45f),"the model now predicts a sane forward knee under the hips -- re-measure the guard band.");
        }
        [Test]
        public void DomainGuard_IsIdentityInTheFittedDomain_AndMonotone()
        {
            Assert.That(BasisSwivelHintCore.LegDomainTrust(BasisSwivelHintCore.LegDomainReachHi), Is.EqualTo(1f));
            Assert.That(BasisSwivelHintCore.LegDomainTrust(1f), Is.EqualTo(1f));
            Assert.That(BasisSwivelHintCore.LegDomainTrust(BasisSwivelHintCore.LegDomainReachLo), Is.EqualTo(0f));
            float prev = -1f;
            for (float r = 0.2f; r <= 1f; r += 0.005f)
            {
                float t = BasisSwivelHintCore.LegDomainTrust(r);
                Assert.That(t, Is.GreaterThanOrEqualTo(prev), "domain trust must be monotone in reach.");
                prev = t;
            }
        }
    }
}
