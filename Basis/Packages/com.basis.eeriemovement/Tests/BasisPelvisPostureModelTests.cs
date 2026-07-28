using System.Collections.Generic;
using System.Text;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;

namespace Basis.Tests.IK
{
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;

    /// <summary>
    /// The pelvis posture model, against the humans it claims to imitate -- and against the law it replaces.
    ///
    /// The safety properties come first, because this model moves EVERY local player's pelvis on EVERY frame.
    /// A wrong elbow is ugly; a wrong pelvis is the whole avatar. So the things that must never happen are
    /// pinned as identities before anything about accuracy is discussed.
    /// </summary>
    public sealed class BasisPelvisPostureModelTests
    {
        // ==========================================================================================
        // SAFETY. These are the ones that would ruin someone's session, so they are checked hardest.
        // ==========================================================================================

        /// <summary>
        /// ⭐ THE ONE THAT MATTERS MOST. A user who is STANDING STILL has a pelvis that has not moved.
        /// Exactly zero -- not "small", not "within tolerance". The model is factored as k * drop precisely
        /// so this is an algebraic identity and not something a 3rd-order polynomial has to be trusted with.
        /// </summary>
        [Test]
        public void StandingStill_MovesThePelvisExactlyZero()
        {
            for (float lean = 0f; lean <= 0.8f; lean += 0.05f)
            {
                Assert.AreEqual(0f, BasisPelvisPostureModel.PelvisDrop(0f, lean),
                    "a head at standing height must not move the pelvis by ANY amount, at any lean");
            }
        }

        /// <summary>The pelvis never RISES because the head fell, and never falls FURTHER than the head did.
        /// Either would be a spine doing something no spine does, and both are reachable by a polynomial that
        /// is allowed to run free -- raw k spans -3.09..+1.23 across the domain. The clamp is load-bearing.</summary>
        [Test]
        public void ThePelvis_NeverRises_AndNeverOutrunsTheHead()
        {
            for (float d = 0f; d <= 1.2f; d += 0.01f)
            {
                for (float f = 0f; f <= 1.2f; f += 0.02f)
                {
                    float drop = BasisPelvisPostureModel.PelvisDrop(d, f);
                    Assert.GreaterOrEqual(drop, 0f, $"pelvis must not RISE when the head drops (d={d:F2} f={f:F2})");
                    Assert.LessOrEqual(drop, d + 1e-5f, $"pelvis must not drop FURTHER than the head (d={d:F2} f={f:F2})");

                    float k = BasisPelvisPostureModel.Coupling(d, f);
                    Assert.IsTrue(k >= 0f && k <= 1f, $"the coupling must stay in [0,1] (d={d:F2} f={f:F2} k={k})");
                }
            }
        }

        /// <summary>A NaN head position must move the pelvis by nothing. A NaN transform in Unity PERSISTS --
        /// the avatar does not recover when good data returns -- so this is not a tidiness check.</summary>
        [Test]
        public void ANaNInput_MovesThePelvisNotAtAll()
        {
            Assert.AreEqual(0f, BasisPelvisPostureModel.PelvisDrop(float.NaN, 0.2f));
            Assert.AreEqual(0f, BasisPelvisPostureModel.PelvisDrop(0.3f, float.NaN));
            Assert.AreEqual(0f, BasisPelvisPostureModel.PelvisDrop(float.NaN, float.NaN));
            Assert.AreEqual(0f, BasisPelvisPostureModel.PelvisDrop(-1f, 0.2f), "a head ABOVE standing is not this model's business");
        }

        /// <summary>Extrapolation is refused, not attempted: a 3rd-order polynomial outside its fit box is a
        /// random number generator. Anything past the domain saturates at the domain edge.</summary>
        [Test]
        public void OutsideTheFitDomain_TheModelSaturatesInsteadOfExtrapolating()
        {
            Assert.AreEqual(BasisPelvisPostureModel.Coupling(BasisPelvisPostureModel.MaxDrop, 0.3f),
                            BasisPelvisPostureModel.Coupling(5f, 0.3f), 1e-6f,
                            "an absurd head drop must clamp to the domain edge");
            Assert.AreEqual(BasisPelvisPostureModel.Coupling(0.3f, BasisPelvisPostureModel.MaxLean),
                            BasisPelvisPostureModel.Coupling(0.3f, 5f), 1e-6f,
                            "an absurd lean must clamp to the domain edge");
        }

        // ==========================================================================================
        // BEHAVIOUR. Does it actually tell the two postures apart?
        // ==========================================================================================

        /// <summary>
        /// THE WHOLE POINT, in one assertion. Same head drop, different lean -- and the pelvis must do two
        /// completely different things. A squat drops it; a waist-bend does not. The old law could not
        /// express this at all, because it never saw the lean.
        /// </summary>
        [Test]
        public void TheSameHeadDrop_MovesThePelvisDifferently_DependingOnTheLean()
        {
            const float drop = 0.35f;   // head 35% of body height below standing -- a real bend either way

            float squat = BasisPelvisPostureModel.Coupling(drop, 0.05f);   // straight down: a squat
            float bend = BasisPelvisPostureModel.Coupling(drop, 0.50f);    // way out front: a waist-bend

            Assert.Greater(squat, 0.75f,
                $"dropping straight down is a SQUAT -- the pelvis must ride the head down (got k={squat:F2}, " +
                "real humans measure 0.78-0.99)");
            Assert.Less(bend, 0.25f,
                $"dropping while leaning far forward is a WAIST-BEND -- the pelvis must stay high (got k={bend:F2}, " +
                "real humans measure 0.02-0.14)");
            Assert.Greater(squat - bend, 0.5f,
                "if the model cannot separate these two by a wide margin it has learned nothing, and the " +
                "single constant it replaces would do just as well");
        }

        // ==========================================================================================
        // ACCURACY. Against real humans, out of sample, versus the law that ships today.
        // ==========================================================================================

        /// <summary>
        /// ⭐ THE A/B. The model and the shipped saturation law, run over every posture clip, scored against
        /// the pelvis a real human actually had. This is the number that justifies the change.
        /// </summary>
        [Test]
        public void ThePostureModel_BeatsTheShippedSaturationLaw_OnRealHumans()
        {
            List<BasisMotionClip> clips = BasisPostureCorpusTests.LoadPostureCorpus();

            // The shipped law, verbatim (BasisVirtualSpineCore + BasisSettingsDefaults).
            const float k_MaxDropM = 0.30f, k_Strength = 0.85f;

            double modelErr = 0, rigErr = 0;
            int n = 0;
            float modelWorst = 0f, rigWorst = 0f;
            var report = new StringBuilder();
            report.AppendLine("PELVIS HEIGHT vs A REAL HUMAN'S -- the fitted model against the law that ships.");
            report.AppendLine($"{"clip",-11} {"frames",7} {"rig cm",9} {"model cm",9}  better?");
            report.AppendLine(new string('-', 52));

            foreach (BasisMotionClip c in clips)
            {
                BasisPostureFeatures.StandingReference(c, out float standHeadY, out float standHipsY);
                if (!(standHeadY > 0.1f)) continue;

                double cm = 0, cr = 0;
                int cn = 0;
                for (int f = 0; f < c.FrameCount; f++)
                {
                    BasisPostureSample s = BasisPostureFeatures.Extract(c, f, standHeadY, standHipsY);
                    if (!s.Valid || s.HeadDrop < 0f) continue;

                    // The model, exactly as the driver calls it. Everything in metres via standHeadY.
                    float model = BasisPelvisPostureModel.PelvisDrop(s.HeadDrop, s.HeadFwd) * standHeadY;

                    // The rig, exactly as the driver calls it. Its input is the RIGID pelvis drop, which is
                    // the head/neck drop -- so it is handed the same head drop, in metres.
                    float dropM = s.HeadDrop * standHeadY;
                    float soft = k_MaxDropM * (1f - Mathf.Exp(-dropM / k_MaxDropM));
                    float rig = Mathf.Lerp(dropM, soft, k_Strength);

                    float truth = s.HipsDrop * standHeadY;
                    float em = Mathf.Abs(model - truth), er = Mathf.Abs(rig - truth);
                    cm += em; cr += er; cn++;
                    modelWorst = Mathf.Max(modelWorst, em);
                    rigWorst = Mathf.Max(rigWorst, er);
                }

                if (cn == 0) continue;
                modelErr += cm; rigErr += cr; n += cn;
                float mc = (float)(cm / cn) * 100f, rc = (float)(cr / cn) * 100f;
                report.AppendLine($"{c.Name,-11} {cn,7} {rc,9:F1} {mc,9:F1}  {(mc < rc ? "MODEL" : "rig")}");
            }

            Assert.Greater(n, 1000, "not enough frames to conclude anything");
            float rigMean = (float)(rigErr / n) * 100f;
            float modelMean = (float)(modelErr / n) * 100f;

            report.AppendLine(new string('-', 52));
            report.AppendLine($"MEAN pelvis-height error:  rig {rigMean:F1} cm   MODEL {modelMean:F1} cm");
            report.AppendLine($"WORST frame:               rig {rigWorst * 100f:F1} cm   MODEL {modelWorst * 100f:F1} cm");
            report.AppendLine();
            report.AppendLine("Every centimetre in that gap is a centimetre the spine and neck were being asked to");
            report.AppendLine("find by ROTATING, because the head is welded to the HMD and cannot be negotiated with.");
            Debug.Log(report.ToString());

            Assert.Less(modelMean, rigMean,
                $"the posture model ({modelMean:F1} cm) must beat the shipped saturation law ({rigMean:F1} cm) " +
                "on real humans, or there is no case for shipping it");
            Assert.Less(modelMean, 0.75f * rigMean,
                "and it must beat it by a margin that survives the corpus's own fidelity limits -- a few " +
                "percent would not be worth the risk of touching every player's pelvis");
        }
    }
}
