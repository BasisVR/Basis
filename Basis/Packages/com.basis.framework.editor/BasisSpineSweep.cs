using System.Globalization;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEngine;

namespace Basis.IK.Debugging
{
    // Offline sweep of the virtual-spine solve helpers (BasisVirtualSpineCore: ComputeChainPlacement,
    // ComputeHipsPosition, ExtractYawBurst, YawDegrees) that synthesize the torso between head and hips.
    // Pure math, edit mode. Asserts the chest/spine sit on the neck→hips segment at the right fractions
    // (no inversion), hips drop the spine length below the neck, and the yaw extraction strips pitch/roll.

    public struct BasisSpineSweepConfig
    {
        public int Cases;
        public static BasisSpineSweepConfig Default() => new BasisSpineSweepConfig { Cases = 5000 };
    }

    public struct BasisSpineSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int Cases;
        public float MaxChainFracErr;     // chest/spine off their neck→hips fraction (collinearity + fraction)
        public int ChainMonotonicFails;   // tChest<tSpine must put chest closer to the neck than spine
        public float MaxHipsYErr;          // hips Y must sit exactly the spine length below the neck
        public float MaxHipsXZErr;         // hips XZ must equal desiredXZ + the pelvic forward bias
        public float MaxHipsFreezeErr;     // freeze branch: hips == tposeHips + identity-yaw bias
        public float MaxYawFlatErr;        // extracted yaw must be pitch/roll-free (forward stays horizontal)
        public float MaxYawIdempotentErr;  // ExtractYaw(ExtractYaw(q)) == ExtractYaw(q)
        public float MaxYawDegErr;         // YawDegrees recovers the applied yaw angle
    }

    public static class BasisSpineSweep
    {
        public const string DefaultFileName = "BasisSpineSweep.csv";
        public static string DefaultPath() => Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisSpineSummary Run(BasisSpineSweepConfig cfg, string path)
        {
            var s = new BasisSpineSummary { Ok = false, Path = path };
            int cases = Mathf.Max(1, cfg.Cases);

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisSpineSweep " + System.DateTime.UtcNow.ToString("o"));
                    w.WriteLine("case,chain_frac_err,mono_ok,hips_y_err,hips_xz_err,freeze_err,yaw_flat,yaw_idem,yaw_deg_err");
                    var sb = new StringBuilder(160);
                    float3 up = new float3(0f, 1f, 0f);

                    for (int i = 0; i < cases; i++)
                    {
                        var rng = new System.Random(8000 + i);

                        // --- chain placement ---
                        float3 neck = RandVec(rng, 1.5f);
                        float3 hips = RandVec(rng, 1.5f);
                        float tChest = Mathf.Lerp(0.1f, 0.45f, (float)rng.NextDouble());
                        float tSpine = Mathf.Lerp(0.55f, 0.9f, (float)rng.NextDouble());
                        quaternion neckYaw = RandYaw(rng);
                        quaternion hipsYaw = RandYaw(rng);
                        BasisVirtualSpineCore.ComputeChainPlacement(neck, hips, tChest, tSpine, neckYaw, hipsYaw,
                            out float3 chest, out float3 spine, out _, out _);

                        float segLen = math.distance(neck, hips);
                        float chainFracErr = 0f;
                        bool mono = true;
                        if (segLen > 1e-4f)
                        {
                            chainFracErr = Mathf.Max(
                                Mathf.Abs(math.distance(neck, chest) / segLen - tChest),
                                Mathf.Abs(math.distance(neck, spine) / segLen - tSpine));
                            mono = math.distance(neck, chest) <= math.distance(neck, spine) + 1e-4f;
                        }

                        // --- hips position (non-freeze) ---
                        float lenTotal = Mathf.Lerp(0.3f, 0.7f, (float)rng.NextDouble());
                        quaternion headYaw = RandYaw(rng);
                        float biasScale = Mathf.Lerp(-0.1f, 0.1f, (float)rng.NextDouble());
                        float3 desiredXZ = RandVec(rng, 1f);
                        float3 tposeHips = RandVec(rng, 1f);
                        BasisVirtualSpineCore.ComputeHipsPosition(neck, neck, float3.zero, up, lenTotal, headYaw, biasScale, desiredXZ, false, tposeHips, 0f, 0f, 0f, false, 0f, 0f, out float3 hipsOut);
                        float3 fwdBias = math.mul(headYaw, new float3(0f, 0f, 1f)) * biasScale;
                        float hipsYErr = Mathf.Abs(hipsOut.y - (neck.y - lenTotal));
                        float hipsXZErr = Mathf.Sqrt(Sq(hipsOut.x - (desiredXZ.x + fwdBias.x)) + Sq(hipsOut.z - (desiredXZ.z + fwdBias.z)));

                        // --- hips position (freeze) ---
                        BasisVirtualSpineCore.ComputeHipsPosition(neck, neck, float3.zero, up, lenTotal, headYaw, biasScale, desiredXZ, true, tposeHips, 0f, 0f, 0f, false, 0f, 0f, out float3 hipsFreeze);
                        float3 freezeExpect = tposeHips + new float3(0f, 0f, 1f) * biasScale;
                        float freezeErr = math.distance(hipsFreeze, freezeExpect);

                        // --- yaw extraction ---
                        quaternion q = RandRot(rng);
                        BasisVirtualSpineCore.ExtractYawBurst(q, out quaternion y1);
                        BasisVirtualSpineCore.ExtractYawBurst(y1, out quaternion y2);
                        float yawFlat = Mathf.Abs(math.mul(y1, new float3(0f, 0f, 1f)).y);
                        float yawIdem = QuatAngle(y1, y2);

                        float yawDeg = Mathf.Lerp(-170f, 170f, (float)rng.NextDouble());
                        quaternion pureYaw = quaternion.AxisAngle(up, math.radians(yawDeg));
                        BasisVirtualSpineCore.YawDegrees(pureYaw, out float yawDegOut);
                        float yawDegErr = Mathf.Abs(Mathf.DeltaAngle(yawDegOut, yawDeg));

                        s.MaxChainFracErr = Mathf.Max(s.MaxChainFracErr, chainFracErr);
                        if (!mono) s.ChainMonotonicFails++;
                        s.MaxHipsYErr = Mathf.Max(s.MaxHipsYErr, hipsYErr);
                        s.MaxHipsXZErr = Mathf.Max(s.MaxHipsXZErr, hipsXZErr);
                        s.MaxHipsFreezeErr = Mathf.Max(s.MaxHipsFreezeErr, freezeErr);
                        s.MaxYawFlatErr = Mathf.Max(s.MaxYawFlatErr, yawFlat);
                        s.MaxYawIdempotentErr = Mathf.Max(s.MaxYawIdempotentErr, yawIdem);
                        s.MaxYawDegErr = Mathf.Max(s.MaxYawDegErr, yawDegErr);
                        s.Cases++;

                        bool fail = chainFracErr > 1e-3f || !mono || hipsYErr > 1e-3f || hipsXZErr > 1e-3f || freezeErr > 1e-3f || yawFlat > 1e-3f || yawIdem > 0.1f || yawDegErr > 0.1f;
                        if (fail || i < 3)
                        {
                            sb.Clear();
                            sb.Append(i).Append(',').Append(F(chainFracErr)).Append(',').Append(mono ? 1 : 0).Append(',')
                              .Append(F(hipsYErr)).Append(',').Append(F(hipsXZErr)).Append(',').Append(F(freezeErr)).Append(',')
                              .Append(F(yawFlat)).Append(',').Append(F(yawIdem)).Append(',').Append(F(yawDegErr));
                            w.WriteLine(sb.ToString());
                        }
                    }
                }
                s.Ok = true;
            }
            catch (System.Exception e)
            {
                s.Ok = false;
                s.Error = e.Message;
            }
            return s;
        }

        private static float Sq(float v) => v * v;

        private static float QuatAngle(quaternion a, quaternion b)
        {
            float d = math.abs(math.dot(a.value, b.value));
            return math.degrees(2f * math.acos(math.min(1f, d)));
        }

        private static quaternion RandRot(System.Random r)
        {
            var uq = UnityEngine.Quaternion.Euler((float)(r.NextDouble() * 360.0 - 180.0), (float)(r.NextDouble() * 360.0 - 180.0), (float)(r.NextDouble() * 360.0 - 180.0));
            return new quaternion(uq.x, uq.y, uq.z, uq.w);
        }

        private static quaternion RandYaw(System.Random r) => quaternion.AxisAngle(new float3(0f, 1f, 0f), (float)(r.NextDouble() * 2.0 * math.PI_DBL - math.PI_DBL));

        private static float3 RandVec(System.Random r, float m) =>
            new float3((float)(r.NextDouble() * 2.0 - 1.0), (float)(r.NextDouble() * 2.0 - 1.0), (float)(r.NextDouble() * 2.0 - 1.0)) * m;

        private static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
