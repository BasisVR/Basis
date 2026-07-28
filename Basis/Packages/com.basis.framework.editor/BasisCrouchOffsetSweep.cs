using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // Offline sweep of BasisCrouchOffsetCore over crouch depth x facing x factor x body size. Verifies the
    // hips slide back by exactly the corpus curve (EvaluateSetback -- the live math, not a mirrored copy),
    // land on the rest-length sphere once engaged, leak nothing laterally, grow monotonically with depth,
    // and never move while standing, below the deadzone, or disabled. Same math as the live
    // ApplyCrouchBodyOffset.
    public struct BasisCrouchOffsetSweepConfig
    {
        public int DepthSteps;     // crouch samples from above-standing to past-full
        public int YawSteps;       // hips facing directions

        public static BasisCrouchOffsetSweepConfig Default()
        {
            return new BasisCrouchOffsetSweepConfig { DepthSteps = 41, YawSteps = 8 };
        }
    }

    public struct BasisCrouchOffsetSweepSummary
    {
        public bool Ok;
        public string Path;
        public int Cases;
        public int AppliedCases;
        public int NaNCount;
        public float MaxMagErrM;          // backward slide vs EvaluateSetback
        public float MaxSphereErrM;       // | |head->hips| - rest | once the vertical takeover is complete
        public float MaxLateralLeakM;     // slide component perpendicular to hips-back (should be ~0)
        public int StandingMoves;         // moved while setback==0 / disabled
        public int MonotonicViolations;   // setback shrank as depth grew
        public int Failures;
        public string Error;
    }

    public static class BasisCrouchOffsetSweep
    {
        public const string DefaultFileName = "BasisCrouchOffsetSweep.csv";
        // Rest spine lengths with proportionate standing heights (rest ~0.34*S, a typical humanoid), so the
        // grid doubles as a scale-invariance check.
        static readonly float[] k_Rest = { 0.4f, 0.55f, 0.7f };
        static readonly float[] k_Factor = { 0f, 0.2f, 0.5f, 1f };

        public static string DefaultPath() => System.IO.Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisCrouchOffsetSweepSummary Run(BasisCrouchOffsetSweepConfig cfg, string path)
        {
            var summary = new BasisCrouchOffsetSweepSummary { Ok = false, Path = path };

            Vector3 up = Vector3.up;
            int ds = Mathf.Max(2, cfg.DepthSteps);
            int ys = Mathf.Max(1, cfg.YawSteps);

            int cases = 0, applied = 0, nan = 0, standingMoves = 0, monoViol = 0, fails = 0;
            float mMag = 0f, mSphere = 0f, mLat = 0f;

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisCrouchOffsetSweep " + System.DateTime.UtcNow.ToString("o"));
                    w.WriteLine("rest,standH,factor,yaw,depth,applied,setback,mag_err,sphere_err,lateral,mono_viol,fail");
                    var sb = new StringBuilder(160);

                    foreach (float rest in k_Rest)
                    {
                        float standH = rest / 0.34f;
                        foreach (float factor in k_Factor)
                        {
                            for (int yi = 0; yi < ys; yi++)
                            {
                                float yaw = ys <= 1 ? 0f : (yi / (float)ys) * 360f;
                                Quaternion hipsRot = Quaternion.Euler(0f, yaw, 0f);
                                Vector3 fwd = hipsRot * Vector3.forward;
                                fwd -= up * Vector3.Dot(fwd, up);
                                Vector3 backDir = -fwd.normalized;

                                float prevSetback = 0f; bool havePrev = false;
                                for (int di = 0; di < ds; di++)
                                {
                                    float depth = Mathf.Lerp(-0.1f * standH, 0.7f * standH, di / (float)(ds - 1));
                                    float clamped = Mathf.Max(depth, 0f);
                                    Vector3 head = new Vector3(0f, standH - clamped, 0f);

                                    BasisCrouchOffsetInput input;
                                    input.HeadTargetPos = head;
                                    input.HipsPos = head - up * rest; // as the LockHead stage leaves them
                                    input.HipsRot = hipsRot;
                                    input.Bind = Quaternion.identity;
                                    input.PlayerUp = up;
                                    input.Factor = factor;
                                    input.RestDist = rest;
                                    input.CrouchDepth = clamped;
                                    input.StandingHeadHeight = standH;
                                    input.Fade = 1f;
                                    BasisCrouchOffsetCore.Solve(input, out BasisCrouchOffsetResult res);

                                    bool hadNaN = !Finite(res.HipsPos) || float.IsNaN(res.SetbackMeters);
                                    float expected = BasisCrouchOffsetCore.EvaluateSetback(clamped, standH, factor, 1f, rest);

                                    Vector3 fromHead = res.HipsPos - head;
                                    Vector3 horiz = fromHead - up * Vector3.Dot(fromHead, up);
                                    float back = Vector3.Dot(horiz, backDir);
                                    float lateral = (horiz - backDir * back).magnitude;

                                    float magErr = Mathf.Abs(back - expected);
                                    // sphere check only once the vertical blend has fully engaged
                                    bool sphereDue = expected > BasisCrouchOffsetCore.k_VerticalEngageFrac * rest + 1e-4f;
                                    float sphereErr = sphereDue ? Mathf.Abs(fromHead.magnitude - rest) : 0f;
                                    bool standingMove = expected <= 1e-6f && (res.HipsPos - input.HipsPos).magnitude > 1e-6f;
                                    bool monoBad = havePrev && res.SetbackMeters < prevSetback - 1e-5f;
                                    prevSetback = res.SetbackMeters; havePrev = true;

                                    cases++;
                                    if (hadNaN) nan++;
                                    if (res.Applied) applied++;
                                    if (standingMove) standingMoves++;
                                    if (monoBad) monoViol++;
                                    if (magErr > mMag) mMag = magErr;
                                    if (sphereErr > mSphere) mSphere = sphereErr;
                                    if (lateral > mLat) mLat = lateral;

                                    bool fail = hadNaN || magErr > 1e-4f || sphereErr > 1e-4f || lateral > 1e-4f || standingMove || monoBad;
                                    if (fail) fails++;

                                    sb.Clear();
                                    Append(sb, rest); Append(sb, standH); Append(sb, factor); Append(sb, yaw); Append(sb, clamped);
                                    sb.Append(res.Applied ? '1' : '0').Append(',');
                                    Append(sb, res.SetbackMeters); Append(sb, magErr); Append(sb, sphereErr); Append(sb, lateral);
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
                summary.AppliedCases = applied;
                summary.NaNCount = nan;
                summary.MaxMagErrM = mMag;
                summary.MaxSphereErrM = mSphere;
                summary.MaxLateralLeakM = mLat;
                summary.StandingMoves = standingMoves;
                summary.MonotonicViolations = monoViol;
                summary.Failures = fails;
            }
            catch (System.Exception e)
            {
                summary.Ok = false;
                summary.Error = e.Message;
            }

            return summary;
        }

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
