using System.Collections.Generic;
using System.Text;
using Basis.IK.Mocap;
using NUnit.Framework;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    using BasisMotionClip = Basis.IK.Mocap.BasisMotionClip;
    public sealed class BasisPelvisPostureModelTests
    {
        // ==========================================================================================
        // SAFETY. These are the ones that would ruin someone's session, so they are checked hardest.
        // ==========================================================================================
        [Test]
        public void StandingStill_MovesThePelvisExactlyZero()
        {
            for (float lean = 0f; lean <= 0.8f; lean += 0.05f)
            {
                Assert.AreEqual(0f, BasisPelvisPostureModel.PelvisDrop(0f, lean),"a head at standing height must not move the pelvis by ANY amount, at any lean");
            }
        }
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
        [Test]
        public void ANaNInput_MovesThePelvisNotAtAll()
        {
            Assert.AreEqual(0f, BasisPelvisPostureModel.PelvisDrop(float.NaN, 0.2f));
            Assert.AreEqual(0f, BasisPelvisPostureModel.PelvisDrop(0.3f, float.NaN));
            Assert.AreEqual(0f, BasisPelvisPostureModel.PelvisDrop(float.NaN, float.NaN));
            Assert.AreEqual(0f, BasisPelvisPostureModel.PelvisDrop(-1f, 0.2f), "a head ABOVE standing is not this model's business");
        }
        [Test]
        public void OutsideTheFitDomain_TheModelSaturatesInsteadOfExtrapolating()
        {
            Assert.AreEqual(BasisPelvisPostureModel.Coupling(BasisPelvisPostureModel.MaxDrop, 0.3f), BasisPelvisPostureModel.Coupling(5f, 0.3f), 1e-6f,"an absurd head drop must clamp to the domain edge");
            Assert.AreEqual(BasisPelvisPostureModel.Coupling(0.3f, BasisPelvisPostureModel.MaxLean), BasisPelvisPostureModel.Coupling(0.3f, 5f), 1e-6f,"an absurd lean must clamp to the domain edge");
        }
        // ==========================================================================================
        // BEHAVIOUR. Does it actually tell the two postures apart?
        // ==========================================================================================
        [Test]
        public void TheSameHeadDrop_MovesThePelvisDifferently_DependingOnTheLean()
        {
            const float drop = 0.35f;   // head 35% of body height below standing -- a real bend either way

            float squat = BasisPelvisPostureModel.Coupling(drop, 0.05f);   // straight down: a squat
            float bend = BasisPelvisPostureModel.Coupling(drop, 0.50f);    // way out front: a waist-bend

            Assert.Greater(squat, 0.75f, $"dropping straight down is a SQUAT -- the pelvis must ride the head down (got k={squat:F2}, " +"real humans measure 0.78-0.99)");
            Assert.Less(bend, 0.25f, $"dropping while leaning far forward is a WAIST-BEND -- the pelvis must stay high (got k={bend:F2}, " +"real humans measure 0.02-0.14)");
            Assert.Greater(squat - bend, 0.5f, "if the model cannot separate these two by a wide margin it has learned nothing, and the " +"single constant it replaces would do just as well");
        }
        // ==========================================================================================
        // ACCURACY. Against real humans, out of sample, versus the law that ships today.
        // ==========================================================================================
        [Test]
        public void ThePostureModel_BeatsTheShippedSaturationLaw_OnRealHumans()
        {
            List<BasisMotionClip> clips = BasisPostureCorpusTests.LoadPostureCorpus();

            // The shipped law, verbatim (BasisVirtualSpineCore + BasisSettingsDefaults).
            const float maxDropM = 0.30f, strength = 0.85f;
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
                    float dropM = s.HeadDrop * standHeadY, soft = maxDropM * (1f - Mathf.Exp(-dropM / maxDropM));
                    float rig = Mathf.Lerp(dropM, soft, strength), truth = s.HipsDrop * standHeadY;
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
            float rigMean = (float)(rigErr / n) * 100f, modelMean = (float)(modelErr / n) * 100f;

            report.AppendLine(new string('-', 52));
            report.AppendLine($"MEAN pelvis-height error:  rig {rigMean:F1} cm   MODEL {modelMean:F1} cm");
            report.AppendLine($"WORST frame:               rig {rigWorst * 100f:F1} cm   MODEL {modelWorst * 100f:F1} cm");
            report.AppendLine();
            report.AppendLine("Every centimetre in that gap is a centimetre the spine and neck were being asked to");
            report.AppendLine("find by ROTATING, because the head is welded to the HMD and cannot be negotiated with.");
            Debug.Log(report.ToString());

            Assert.Less(modelMean, rigMean, $"the posture model ({modelMean:F1} cm) must beat the shipped saturation law ({rigMean:F1} cm) " +"on real humans, or there is no case for shipping it");
            Assert.Less(modelMean, 0.75f * rigMean, "and it must beat it by a margin that survives the corpus's own fidelity limits -- a few " +"percent would not be worth the risk of touching every player's pelvis");
        }
    }
}
