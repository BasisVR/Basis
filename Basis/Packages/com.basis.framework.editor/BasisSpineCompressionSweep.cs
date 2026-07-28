using System.Globalization;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEngine;
using Basis.IK;

namespace Basis.IK.Debugging
{
    // End-to-end sweep for the virtual-spine HIPS COMPRESSION fix (BasisVirtualSpineCore.ComputeHipsPosition)
    // composed with the leg solve (BasisLegSolveCore). The rigid model drops the pelvis the full head drop, so a
    // deep forward lean (touch toes) or a sit buckles the knees and buries the seated hips. Compression saturates
    // the pelvis' downward travel so the spine shortens instead. This drives a head height ramp from standing down
    // to a deep crouch and asserts, with compression ON vs OFF:
    //   * the pelvis sinks far less than rigid (the seated-hips-too-low fix),
    //   * the planted-foot leg stays markedly straighter at the knee (the touch-toes-folds-knees fix),
    //   * at/above standing the pose is byte-for-byte the rigid pose (no idle posture change),
    //   * the compressed hips height is continuous over the ramp (no pops).

    public struct BasisSpineCompressionSweepConfig
    {
        public int HeadDropSteps;
        public float MaxHeadDropM;
        public float CompressionStrength;
        public float MaxDropM;
        // Standing rig geometry (metres). Foot is planted on the floor with the leg fully extended at standing.
        public float StandingHipsY;
        public float SpineLen;
        public float ThighLen;
        public float ShinLen;
        // Head drop past which compression is expected to be doing real work (used by the repro/knee gates).
        public float EngageDropM;

        public static BasisSpineCompressionSweepConfig Default() => new BasisSpineCompressionSweepConfig
        {
            HeadDropSteps = 64,
            MaxHeadDropM = 0.8f,
            CompressionStrength = 0.85f,
            MaxDropM = 0.3f,
            StandingHipsY = 0.92f,
            SpineLen = 0.55f,
            ThighLen = 0.46f,
            ShinLen = 0.46f,
            EngageDropM = 0.2f,
        };
    }

    public struct BasisSpineCompressionSweepSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int Cases;
        public int NanCount;

        public float MaxPelvisSinkM;          // worst compressed hips drop below standing (seated bound)
        public float MaxPelvisSinkRigidM;     // worst rigid drop, for context
        public float MinKneeStraighterDeg;    // min (kneeOn - kneeOff) over engaged steps; >0 means compression helps
        public float MaxKneeStraighterDeg;
        public float KneeAtDeepLeanOnDeg;      // interior knee angle at the deepest lean, compression on (higher = straighter)
        public float KneeAtDeepLeanOffDeg;     // ...rigid, for context
        public float AboveStandingMaxDevM;     // |compressed - rigid| where head >= standing (must be ~0)
        public float MaxStepDiscontinuityM;    // largest jump in compressed hips Y between adjacent steps
    }

    public static class BasisSpineCompressionSweep
    {
        public const string DefaultFileName = "BasisSpineCompressionSweep.csv";
        public static string DefaultPath() => Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisSpineCompressionSweepSummary Run(BasisSpineCompressionSweepConfig cfg, string path)
        {
            var s = new BasisSpineCompressionSweepSummary { Ok = false, Path = path };
            int steps = Mathf.Max(4, cfg.HeadDropSteps);
            float standingHipsY = cfg.StandingHipsY;
            float lenTotal = cfg.SpineLen;
            float restNeckY = standingHipsY + lenTotal;
            float3 up = new float3(0f, 1f, 0f);
            quaternion yaw = quaternion.identity;
            float3 tposeHips = new float3(0f, standingHipsY, 0f);
            Vector3 footTarget = new Vector3(0f, 0f, 0f);   // planted under standing hips
            Vector3 bendNormal = new Vector3(0f, 0f, 1f);   // knees bend forward

            float prevOn = float.NaN;

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisSpineCompressionSweep " + System.DateTime.UtcNow.ToString("o")
                        + " strength=" + F(cfg.CompressionStrength) + " maxDrop=" + F(cfg.MaxDropM));
                    w.WriteLine("headDrop,hipsOn,hipsOff,sinkOn,kneeOn,kneeOff,kneeDelta");
                    var sb = new StringBuilder(128);

                    for (int i = 0; i < steps; i++)
                    {
                        // Start slightly ABOVE standing (negative drop) so the no-op region is exercised too.
                        float headDrop = Mathf.Lerp(-0.1f, cfg.MaxHeadDropM, i / (float)(steps - 1));
                        float3 neck = new float3(0f, restNeckY - headDrop, 0f);

                        // usePostureModel:false -- this sweep exists to characterise the LEGACY saturation law,
                        // so it keeps calling it. The posture model that now ships by default is covered by
                        // BasisPelvisPostureModelTests, against real humans rather than against a synthetic sweep.
                        BasisVirtualSpineCore.ComputeHipsPosition(neck, neck, float3.zero, up, lenTotal, yaw, 0f, float3.zero,
                            false, tposeHips, standingHipsY, 0f, 0f, false, cfg.CompressionStrength, cfg.MaxDropM, out float3 hipsOn);
                        BasisVirtualSpineCore.ComputeHipsPosition(neck, neck, float3.zero, up, lenTotal, yaw, 0f, float3.zero,
                            false, tposeHips, standingHipsY, 0f, 0f, false, 0f, 0f, out float3 hipsOff);

                        if (IsNan(hipsOn.y) || IsNan(hipsOff.y)) s.NanCount++;

                        float sinkOn = standingHipsY - hipsOn.y;
                        float sinkOff = standingHipsY - hipsOff.y;
                        float kneeOn = SolveKnee(hipsOn.y, cfg.ThighLen, cfg.ShinLen, footTarget, bendNormal);
                        float kneeOff = SolveKnee(hipsOff.y, cfg.ThighLen, cfg.ShinLen, footTarget, bendNormal);
                        float kneeDelta = kneeOn - kneeOff;

                        if (sinkOn > s.MaxPelvisSinkM) s.MaxPelvisSinkM = sinkOn;
                        if (sinkOff > s.MaxPelvisSinkRigidM) s.MaxPelvisSinkRigidM = sinkOff;

                        // No-op region: head at/above standing must leave the rigid pose untouched.
                        if (headDrop <= 0f)
                        {
                            float dev = Mathf.Abs(hipsOn.y - hipsOff.y);
                            if (dev > s.AboveStandingMaxDevM) s.AboveStandingMaxDevM = dev;
                        }

                        // Engaged region: compression should keep the knee straighter than rigid.
                        if (headDrop >= cfg.EngageDropM)
                        {
                            if (s.Cases == 0 || kneeDelta < s.MinKneeStraighterDeg) s.MinKneeStraighterDeg = kneeDelta;
                            if (kneeDelta > s.MaxKneeStraighterDeg) s.MaxKneeStraighterDeg = kneeDelta;
                            s.Cases++;
                        }

                        if (!float.IsNaN(prevOn))
                        {
                            float jump = Mathf.Abs(hipsOn.y - prevOn);
                            if (jump > s.MaxStepDiscontinuityM) s.MaxStepDiscontinuityM = jump;
                        }
                        prevOn = hipsOn.y;

                        if (i == steps - 1)
                        {
                            s.KneeAtDeepLeanOnDeg = kneeOn;
                            s.KneeAtDeepLeanOffDeg = kneeOff;
                        }

                        sb.Clear();
                        sb.Append(F(headDrop)).Append(',').Append(F(hipsOn.y)).Append(',').Append(F(hipsOff.y)).Append(',')
                          .Append(F(sinkOn)).Append(',').Append(F(kneeOn)).Append(',').Append(F(kneeOff)).Append(',').Append(F(kneeDelta));
                        w.WriteLine(sb.ToString());
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

        // Planted-foot leg: hip root at hipsY directly above the foot, leg fully extended at standing. Returns the
        // interior knee angle (deg) the leg solver lands on; lower = more folded.
        static float SolveKnee(float hipsY, float thigh, float shin, Vector3 footTarget, Vector3 bendNormal)
        {
            Vector3 root = new Vector3(0f, hipsY, 0f);
            Vector3 mid = new Vector3(0f, hipsY - thigh, 0f);
            Vector3 tip = new Vector3(0f, hipsY - thigh - shin, 0f);
            BasisLegSolveInput input = default;
            input.Root = root;
            input.Mid = mid;
            input.Tip = tip;
            input.RootRotation = Quaternion.identity;
            input.MidRotation = Quaternion.identity;
            input.TargetPosition = footTarget;
            input.TargetRotation = Quaternion.identity;
            input.TargetOffset = Quaternion.identity;
            input.HintPosition = new Vector3(0f, hipsY - thigh, 0.5f); // knee pole forward
            input.HintWeight = 1f;
            input.BendNormal = bendNormal;
            BasisLegSolveCore.Solve(input, out BasisLegSolveResult r);
            return r.KneeAngleDeg;
        }

        static bool IsNan(float v) => float.IsNaN(v) || float.IsInfinity(v);

        static string F(float v) => float.IsNaN(v) ? "nan" : v.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
