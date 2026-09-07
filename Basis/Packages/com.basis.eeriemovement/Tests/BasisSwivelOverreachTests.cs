using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;
namespace Basis.Tests.IK
{
    public class BasisSwivelOverreachTests
    {
        static float3[] Directions()
        {
            var list = new List<float3>();
            for (int elev = -75; elev <= 75; elev += 15)
                for (int az = 0; az < 360; az += 30)
                {
                    float e = elev * Mathf.Deg2Rad, a = az * Mathf.Deg2Rad;
                    list.Add(math.normalize(new float3(math.cos(e) * math.sin(a), math.sin(e), math.cos(e) * math.cos(a))));
                }
            return list.ToArray();
        }
        // Worst per-step swivel change (deg) in the three reach regions, over all directions.
        static (float inReach, float boundary, float beyond) WorstStep(Func<float3, float> swivel)
        {
            const int N = 131;
            const float r0 = 0.3f, r1 = 1.6f;
            float inR = 0f, bnd = 0f, bey = 0f;
            foreach (float3 d in Directions())
            {
                float prev = 0f;
                for (int i = 0; i < N; i++)
                {
                    float r = r0 + (r1 - r0) * i / (N - 1), phi = swivel(r * d);
                    if (i > 0)
                    {
                        float step = Mathf.Abs(Mathf.DeltaAngle(prev * Mathf.Rad2Deg, phi * Mathf.Rad2Deg));
                        float rmid = r - 0.5f * (r1 - r0) / (N - 1);
                        if (rmid < 0.9f) inR = Mathf.Max(inR, step);
                        else if (rmid <= 1.1f) bnd = Mathf.Max(bnd, step);
                        else bey = Mathf.Max(bey, step);
                    }
                    prev = phi;
                }
            }
            return (inR, bnd, bey);
        }
        [Test]
        public void NeuralPoleModels_AreBoundedAndSmooth_PastReach()
        {
            var models = new (string name, bool neural, Func<float3, float> fn)[]
            {
                ("arm  poly  (BasisArmSwivelModel)",       false, t => BasisArmSwivelModel.SwivelRad(t)),
                ("arm  neural(BasisArmSwivelNeuralModel)", true,  t => BasisArmSwivelNeuralModel.SwivelRad(t)),
                ("knee poly  (BasisLegSwivelModel)",       false, t => BasisLegSwivelModel.SwivelRad(t)),
                ("knee neural(BasisLegSwivelNeuralModel)", true,  t => BasisLegSwivelNeuralModel.SwivelRad(t)),
            };

            var w = new Dictionary<string, (float inR, float bnd, float bey)>();
            var report = new StringBuilder();
            report.AppendLine("Worst single-step swivel change (deg) per ~2-3 mm hand step, swept 0.3->1.6 limb-lengths:");
            report.AppendLine($"  {"model",-40} {"in-reach<0.9",13} {"boundary.9-1.1",15} {"beyond>1.1",12}");
            foreach (var m in models)
            {
                var r = WorstStep(m.fn);
                w[m.name] = r;
                report.AppendLine($"  {m.name,-40} {r.inReach,13:F1} {r.boundary,15:F1} {r.beyond,12:F1}");
            }
            Debug.Log(report.ToString());

            // (1) STRUCTURAL: past full reach the swivel must FREEZE along a radial (the clamp). Holds for the
            //     polynomials too (they clamp), so assert it everywhere -- a broken clamp is a shipping bug.
            foreach (var m in models)
                Assert.Less(w[m.name].bey, 0.05f, $"{m.name}: swivel not frozen past reach -- domain clamp broken?");

            // (2) BOUNDED: a pole model may never flip a quarter turn in a single 2-3 mm step. Gate the NEURAL
            //     models (the ones this work ships); the unclamped polynomial is a known offender and is here
            //     only as the reference that motivates the clamp.
            foreach (var m in models)
                if (m.neural)
                {
                    Assert.Less(w[m.name].inR, 90f, $"{m.name}: unbounded in-reach step -- a flip");
                    Assert.Less(w[m.name].bnd, 90f, $"{m.name}: unbounded boundary step -- a flip");
                }

            // (3) NO REGRESSION vs the polynomial it replaces: the neural pole must be at least as smooth at the
            //     reach boundary and in-reach. Measured ~10x smoother; the +1 deg margin is slack, not headroom.
            Assert.LessOrEqual(w["arm  neural(BasisArmSwivelNeuralModel)"].inR, w["arm  poly  (BasisArmSwivelModel)"].inR + 1f, "arm neural rougher in-reach than the poly");
            Assert.LessOrEqual(w["arm  neural(BasisArmSwivelNeuralModel)"].bnd, w["arm  poly  (BasisArmSwivelModel)"].bnd + 1f, "arm neural rougher at the boundary than the poly");
            Assert.LessOrEqual(w["knee neural(BasisLegSwivelNeuralModel)"].inR, w["knee poly  (BasisLegSwivelModel)"].inR + 1f, "knee neural rougher in-reach than the poly");
            Assert.LessOrEqual(w["knee neural(BasisLegSwivelNeuralModel)"].bnd, w["knee poly  (BasisLegSwivelModel)"].bnd + 1f, "knee neural rougher at the boundary than the poly");
        }
    }
}
