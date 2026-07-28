using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Offline sweep of BasisHipHingeCore over a grid of forward-lean angles, azimuths, onset thresholds
    // and caps. Verifies the pelvis pitch only engages past StartDeg, never exceeds MaxAddDeg, rotates by
    // exactly the reported AddDeg about a horizontal (player-up-perpendicular) axis, grows monotonically
    // with lean, and is a no-op when disabled. Same math as the live ApplyHipHinge.
    public struct BasisHipHingeSweepConfig
    {
        public float HeadToHips;   // m, lever length head sits from hips
        public int LeanSteps;      // forward lean samples 0..MaxLeanDeg
        public float MaxLeanDeg;
        public int AzimuthSteps;   // lean directions around player-up

        public static BasisHipHingeSweepConfig Default()
        {
            return new BasisHipHingeSweepConfig
            {
                HeadToHips = 0.6f,
                LeanSteps = 41,
                MaxLeanDeg = 85f,
                AzimuthSteps = 6,
            };
        }
    }

    public struct BasisHipHingeSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Cases;
        public int EngagedCases;
        public int NaNCount;
        public float MaxAngleMatchErrDeg; // |angle(in,out) - AddDeg|
        public float MaxOverAddDeg;       // AddDeg past MaxAddDeg
        public float MaxAxisDotUp;        // hinge axis vs player-up (should be ~0)
        public int MonotonicViolations;   // AddDeg dropped as lean grew
        public int DisabledMoves;         // MaxAddDeg<=0 or below onset yet rotation changed
        public int Failures;
        public string Error;
    }

    public static class BasisHipHingeSweep
    {
        public const string DefaultFileName = "BasisHipHingeSweep.csv";
        static readonly float[] k_Start = { 10f, 20f, 35f };
        static readonly float[] k_MaxAdd = { 0f, 5f, 15f, 30f };

        public static string DefaultPath() => System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisHipHingeSweepSummary Run(BasisHipHingeSweepConfig cfg, string path)
        {
            var summary = new BasisHipHingeSweepSummary { Ok = false, Path = path };

            Vector3 hips = Vector3.zero;
            Quaternion hipsRot = Quaternion.identity;
            Vector3 up = Vector3.up;
            float H = Mathf.Max(0.05f, cfg.HeadToHips);
            int ls = Mathf.Max(2, cfg.LeanSteps);
            int az = Mathf.Max(1, cfg.AzimuthSteps);

            int cases = 0, engaged = 0, nan = 0, monoViol = 0, disabledMoves = 0, fails = 0;
            float mAngle = 0f, mOver = 0f, mAxis = 0f;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisHipHingeSweep " + System.DateTime.UtcNow.ToString("o") + " H=" + F(H));
                    w.WriteLine("start,maxadd,azimuth,lean,applied,add_deg,angle_match,over_add,axis_dot_up,mono_viol,fail");
                    var sb = new StringBuilder(160);

                    for (int sti = 0; sti < k_Start.Length; sti++)
                    {
                        float start = k_Start[sti];
                        for (int mi = 0; mi < k_MaxAdd.Length; mi++)
                        {
                            float maxAdd = k_MaxAdd[mi];
                            for (int ai = 0; ai < az; ai++)
                            {
                                float phi = az <= 1 ? 0f : (ai / (float)az) * 360f;
                                Vector3 horizDir = new Vector3(Mathf.Cos(phi * Mathf.Deg2Rad), 0f, Mathf.Sin(phi * Mathf.Deg2Rad));

                                float prevAdd = 0f;
                                bool havePrev = false;
                                for (int li = 0; li < ls; li++)
                                {
                                    float lean = Mathf.Lerp(0f, cfg.MaxLeanDeg, li / (float)(ls - 1));
                                    Vector3 head = hips + (up * Mathf.Cos(lean * Mathf.Deg2Rad) + horizDir * Mathf.Sin(lean * Mathf.Deg2Rad)) * H;

                                    BasisHipHingeInput input;
                                    input.HeadPos = head;
                                    input.HipsPos = hips;
                                    input.HipsRot = hipsRot;
                                    input.PlayerUp = up;
                                    input.StartDeg = start;
                                    input.MaxAddDeg = maxAdd;
                                    BasisHipHingeCore.Solve(input, out BasisHipHingeResult res);

                                    bool hadNaN = !FiniteQ(res.HipsRot) || float.IsNaN(res.AddDeg) || float.IsInfinity(res.AddDeg);
                                    float angleMatch = 0f, over = 0f, axisDotUp = 0f;
                                    bool monoBad = false, disabledMove = false;

                                    if (!hadNaN)
                                    {
                                        float ang = Quaternion.Angle(hipsRot, res.HipsRot);
                                        angleMatch = Mathf.Abs(ang - res.AddDeg);
                                        over = Relu(res.AddDeg - maxAdd);

                                        if (res.Applied && ang > 1e-3f)
                                        {
                                            Quaternion delta = res.HipsRot * Quaternion.Inverse(hipsRot);
                                            delta.ToAngleAxis(out float dAng, out Vector3 dAxis);
                                            if (dAng > 1e-3f && dAng < 359.999f) axisDotUp = Mathf.Abs(Vector3.Dot(dAxis.normalized, up));
                                        }

                                        if ((maxAdd <= 0f || lean <= start) && ang > 1e-3f) disabledMove = true;

                                        if (havePrev && res.AddDeg < prevAdd - 1e-3f) monoBad = true;
                                        prevAdd = res.AddDeg;
                                        havePrev = true;
                                    }

                                    cases++;
                                    if (hadNaN) nan++;
                                    if (res.Applied) engaged++;
                                    if (monoBad) monoViol++;
                                    if (disabledMove) disabledMoves++;
                                    if (angleMatch > mAngle) mAngle = angleMatch;
                                    if (over > mOver) mOver = over;
                                    if (axisDotUp > mAxis) mAxis = axisDotUp;

                                    bool fail = hadNaN || angleMatch > 0.05f || over > 0.05f || axisDotUp > 1e-2f || monoBad || disabledMove;
                                    if (fail) fails++;

                                    sb.Clear();
                                    Append(sb, start); Append(sb, maxAdd); Append(sb, phi); Append(sb, lean);
                                    sb.Append(res.Applied ? '1' : '0').Append(',');
                                    Append(sb, res.AddDeg); Append(sb, angleMatch); Append(sb, over); Append(sb, axisDotUp);
                                    sb.Append(monoBad ? '1' : '0').Append(',');
                                    sb.Append(fail ? '1' : '0');
                                    w.WriteLine(sb.ToString());
                                }
                            }
                        }
                    }
                }

                summary.Ok = true;
                summary.Cases = cases;
                summary.EngagedCases = engaged;
                summary.NaNCount = nan;
                summary.MaxAngleMatchErrDeg = mAngle;
                summary.MaxOverAddDeg = mOver;
                summary.MaxAxisDotUp = mAxis;
                summary.MonotonicViolations = monoViol;
                summary.DisabledMoves = disabledMoves;
                summary.Failures = fails;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }

            return summary;
        }

        static float Relu(float v) => v > 0f ? v : 0f;
        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        static bool FiniteQ(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w);

        static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }

        static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
