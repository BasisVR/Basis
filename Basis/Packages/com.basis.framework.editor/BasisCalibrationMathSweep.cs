using System.Globalization;
using System.IO;
using System.Text;
using Basis.Scripts.Drivers;
using UnityEngine;

namespace Basis.IK.Debugging
{
    // Offline sweep of the calibration OFFSET / ROTATION / HEIGHT math that the live calibration
    // runs (BasisCalibrationMath, BasisAnimationRiggingHelper.CalibratedRotationOffset,
    // BasisAvatarScaleModifier) — the plumbing that turns a captured tracker pose into a bone pose
    // and a player/avatar height into a device scale. Pure math, edit mode, asserts round-trip /
    // no-leak invariants. One CSV row per case.

    public struct BasisCalibrationMathSweepConfig
    {
        public int CasesPerSection;
        public static BasisCalibrationMathSweepConfig Default() => new BasisCalibrationMathSweepConfig { CasesPerSection = 4000 };
    }

    public struct BasisCalibrationMathSummary
    {
        public bool Ok;
        public string Path;
        public string Error;
        public int Cases;
        public int Failures;

        // Inverse-offset capture↔apply round-trip (BasisCalibrationMath).
        public float MaxOffsetPosErr;       // metres: apply(capture()) reproduces the bone
        public float MaxOffsetRotErrDeg;
        public float MaxRigidFollowErr;     // metres: tracker moves by Δ (same rot) → bone moves by exactly Δ

        // Device-scale round-trip (BasisCalibrationMath.ScaleDeviceCoord).
        public float MaxScalePosErr;        // metres: forward then inverse recovers the unscaled position

        // Per-effector rotation calibration / avatar-swap (BasisAnimationRiggingHelper).
        public float MaxRotCalErrDeg;       // boneOutgoing*offset must land on the avatar bind frame (no spawn-orientation leak)

        // Avatar scale modifier (BasisAvatarScaleModifier).
        public int ScaleModifierMismatches; // sanitization / FinalScale = DuringCalibration*ApplyScale

        // Feel height (BasisCalibrationMath.ComputeDeviceScale): a correctly measured denominator lands the
        // viewpoint on the avatar eye; an under-bridged eye reference renders too tall by exactly E/(E-shortfall).
        public float MaxFeelHeightErr;      // metres: |trueEye*DeviceScale - avatarEye| for a well-measured/nudged denominator
        public float MaxFeelFactorErr;      // unitless: predicted vs observed too-tall ratio for an under-bridged eye reference

        public float WorstSummary;
    }

    public static class BasisCalibrationMathSweep
    {
        public const string DefaultFileName = "BasisCalibrationMathSweep.csv";
        public static string DefaultPath() => Path.Combine(Application.persistentDataPath, DefaultFileName);

        public static BasisCalibrationMathSummary Run(BasisCalibrationMathSweepConfig cfg, string path)
        {
            var s = new BasisCalibrationMathSummary { Ok = false, Path = path };
            int cases = Mathf.Max(1, cfg.CasesPerSection);

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.WriteLine("# BasisCalibrationMathSweep " + System.DateTime.UtcNow.ToString("o"));
                    w.WriteLine("section,case,errA,errB,detail,pass");
                    var sb = new StringBuilder(160);

                    // 1) Inverse-offset capture↔apply round-trip + rigid follow.
                    for (int i = 0; i < cases; i++)
                    {
                        var rng = new System.Random(1000 + i);
                        Vector3 trackerPos = RandVec(rng, 2f);
                        Quaternion trackerRot = RandRot(rng);
                        Vector3 bonePos = RandVec(rng, 2f);
                        Quaternion boneRot = RandRot(rng);

                        BasisCalibrationMath.ComputeInverseOffset(trackerPos, trackerRot, bonePos, boneRot, out Vector3 offPos, out Quaternion offRot);
                        BasisCalibrationMath.ApplyInverseOffset(trackerPos, trackerRot, offPos, offRot, out Vector3 bp, out Quaternion br);

                        float posErr = Vector3.Distance(bp, bonePos);
                        float rotErr = Quaternion.Angle(br, boneRot);

                        Vector3 delta = RandVec(rng, 0.5f);
                        BasisCalibrationMath.ApplyInverseOffset(trackerPos + delta, trackerRot, offPos, offRot, out Vector3 bp2, out _);
                        float followErr = Vector3.Distance(bp2 - bp, delta);

                        s.MaxOffsetPosErr = Mathf.Max(s.MaxOffsetPosErr, posErr);
                        s.MaxOffsetRotErrDeg = Mathf.Max(s.MaxOffsetRotErrDeg, rotErr);
                        s.MaxRigidFollowErr = Mathf.Max(s.MaxRigidFollowErr, followErr);
                        bool pass = posErr < 1e-3f && rotErr < 0.1f && followErr < 1e-3f;
                        if (!pass) s.Failures++;
                        s.Cases++;
                        if (!pass || i < 3) WriteRow(w, sb, "offset", i, posErr, rotErr, $"follow={F(followErr)}", pass);
                    }

                    // 2) Device-scale round-trip (scale → unscale recovers the input).
                    for (int i = 0; i < cases; i++)
                    {
                        var rng = new System.Random(2000 + i);
                        Vector3 unscaled = RandVec(rng, 2f);
                        Quaternion unscaledRot = RandRot(rng);
                        float scale = Mathf.Lerp(0.3f, 3f, (float)rng.NextDouble());
                        Vector3 offsetPos = RandVec(rng, 1f);
                        Quaternion offsetRot = RandRot(rng);

                        BasisCalibrationMath.ScaleDeviceCoord(unscaled, unscaledRot, scale, offsetPos, offsetRot, out Vector3 sp, out Quaternion sr);
                        Vector3 rec = (Quaternion.Inverse(offsetRot) * (sp - offsetPos)) / scale;
                        float posErr = Vector3.Distance(rec, unscaled);
                        float rotErr = Quaternion.Angle(sr, offsetRot * unscaledRot);

                        s.MaxScalePosErr = Mathf.Max(s.MaxScalePosErr, posErr);
                        bool pass = posErr < 1e-3f && rotErr < 0.1f;
                        if (!pass) s.Failures++;
                        s.Cases++;
                        if (!pass || i < 3) WriteRow(w, sb, "scale", i, posErr, rotErr, $"s={F(scale)}", pass);
                    }

                    // 3) Per-effector rotation calibration (production = aligned pure-world): captured with the
                    //    avatar root aligned to the bone-sim parent, Inverse(boneOutWorld)*avatarBone must both
                    //    reproduce the bone AND be root-independent (#531 "no orientation leak across avatar swap").
                    for (int i = 0; i < cases; i++)
                    {
                        var rng = new System.Random(3000 + i);
                        Quaternion root = RandRot(rng);        // shared capture frame: avatar root aligned to bone-sim parent
                        Quaternion boneLocal = RandRot(rng);   // bone-sim outgoing within that frame
                        Quaternion avatarLocal = RandRot(rng); // avatar bind bone within that frame
                        Quaternion boneOutWorld = root * boneLocal;
                        Quaternion avatarBone = root * avatarLocal;

                        Quaternion offset = BasisAnimationRiggingHelper.CalibratedRotationOffset(boneOutWorld, avatarBone);
                        float reproduceErr = Quaternion.Angle(boneOutWorld * offset, avatarBone);
                        float leakDeg = Quaternion.Angle(offset, Quaternion.Inverse(boneLocal) * avatarLocal);
                        float errDeg = Mathf.Max(reproduceErr, leakDeg);

                        s.MaxRotCalErrDeg = Mathf.Max(s.MaxRotCalErrDeg, errDeg);
                        bool pass = errDeg < 0.1f;
                        if (!pass) s.Failures++;
                        s.Cases++;
                        if (!pass || i < 3) WriteRow(w, sb, "rotcal", i, errDeg, 0f, "aligned", pass);
                    }

                    // 4) Avatar scale modifier: ReInitialize + override sanitization + FinalScale.
                    s.ScaleModifierMismatches = RunScaleModifier(w, sb);
                    s.Cases += 9;
                    s.Failures += s.ScaleModifierMismatches;

                    // 5) Feel height (BasisCalibrationMath.ComputeDeviceScale): the DeviceScale denominator must
                    //    equal the player's true standing eye height E, else the avatar renders too tall/short.
                    //    A correctly measured denominator lands the viewpoint (trueEye * DeviceScale) on the
                    //    avatar eye; an under-bridged eye reference (OpenVR HMD pose-origin gap g not fully
                    //    covered by CenterEyeVerticalOffset o) renders too tall by E/(E-shortfall) and is
                    //    cancelled by an additive correction == shortfall.
                    for (int i = 0; i < cases; i++)
                    {
                        var rng = new System.Random(6000 + i);
                        float E = Mathf.Lerp(1.35f, 1.95f, (float)rng.NextDouble());  // true standing eye height
                        float A = Mathf.Lerp(1.25f, 1.80f, (float)rng.NextDouble());  // avatar authored eye height
                        float u = Mathf.Lerp(0.6f, 2.0f, (float)rng.NextDouble());    // custom avatar scale
                        float g = Mathf.Lerp(0f, 0.18f, (float)rng.NextDouble());     // device-origin -> eye gap
                        float o = g * (float)rng.NextDouble();                        // captured (under-)bridge
                        float shortfall = g - o;
                        float avatarEye = A * u;

                        float dsGood = BasisCalibrationMath.ComputeDeviceScale(A, u, E, 0f, 0f);
                        float goodErr = Mathf.Abs(E * dsGood - avatarEye);

                        float dsBias = BasisCalibrationMath.ComputeDeviceScale(A, u, E - g, o, 0f);
                        float tallFactor = (E * dsBias) / avatarEye;
                        float factorErr = Mathf.Abs(tallFactor - E / (E - shortfall));

                        float dsNudged = BasisCalibrationMath.ComputeDeviceScale(A, u, E - g, o, shortfall);
                        float nudgeErr = Mathf.Abs(E * dsNudged - avatarEye);

                        s.MaxFeelHeightErr = Mathf.Max(s.MaxFeelHeightErr, Mathf.Max(goodErr, nudgeErr));
                        s.MaxFeelFactorErr = Mathf.Max(s.MaxFeelFactorErr, factorErr);
                        bool tallOk = shortfall < 1e-4f || E * dsBias > avatarEye - 1e-4f;
                        bool pass = goodErr < 1e-3f && nudgeErr < 1e-3f && factorErr < 1e-3f && tallOk;
                        if (!pass) s.Failures++;
                        s.Cases++;
                        if (!pass || i < 3) WriteRow(w, sb, "feelheight", i, goodErr, factorErr, $"short={F(shortfall)} tall={F(tallFactor)}", pass);
                    }
                }

                s.WorstSummary = Mathf.Max(Mathf.Max(s.MaxOffsetPosErr, s.MaxScalePosErr), Mathf.Max(s.MaxRigidFollowErr, s.MaxFeelHeightErr));
                s.Ok = true;
            }
            catch (System.Exception e)
            {
                s.Ok = false;
                s.Error = e.Message;
            }
            return s;
        }

        private static int RunScaleModifier(StreamWriter w, StringBuilder sb)
        {
            int mismatches = 0;
            var mod = new BasisAvatarScaleModifier();

            mod.ReInitialize(null);
            bool reinitOk = Approximately(mod.FinalScale, Vector3.one) && Mathf.Approximately(mod.ApplyScale, 1f);
            if (!reinitOk) mismatches++;
            WriteRow(w, sb, "scalemod", -1, 0f, 0f, "reinit(null)->one", reinitOk);

            var calib = new Vector3(1.1f, 1.1f, 1.1f);
            float[] scales = { 0.5f, 1f, 1.5f, 2f, float.NaN, float.PositiveInfinity, -1f, 0f };
            for (int i = 0; i < scales.Length; i++)
            {
                mod.DuringCalibrationScale = calib;
                mod.SetAvatarheightOverride(scales[i]);
                float expectedApply = (!float.IsNaN(scales[i]) && !float.IsInfinity(scales[i]) && scales[i] > 0f) ? scales[i] : 1f;
                Vector3 expectedFinal = calib * expectedApply;
                bool ok = Mathf.Approximately(mod.ApplyScale, expectedApply) && Approximately(mod.FinalScale, expectedFinal);
                if (!ok) mismatches++;
                WriteRow(w, sb, "scalemod", i, Mathf.Abs(mod.ApplyScale - expectedApply), 0f, $"in={F(scales[i])} apply={F(mod.ApplyScale)}", ok);
            }
            return mismatches;
        }

        private static bool Approximately(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;

        private static Quaternion RandRot(System.Random r) =>
            Quaternion.Euler((float)(r.NextDouble() * 360.0 - 180.0), (float)(r.NextDouble() * 360.0 - 180.0), (float)(r.NextDouble() * 360.0 - 180.0));

        private static Vector3 RandVec(System.Random r, float m) =>
            new Vector3((float)(r.NextDouble() * 2.0 - 1.0), (float)(r.NextDouble() * 2.0 - 1.0), (float)(r.NextDouble() * 2.0 - 1.0)) * m;

        private static void WriteRow(StreamWriter w, StringBuilder sb, string section, int caseIdx, float errA, float errB, string detail, bool pass)
        {
            sb.Clear();
            sb.Append(section).Append(',').Append(caseIdx).Append(',').Append(F(errA)).Append(',').Append(F(errB)).Append(',').Append(detail).Append(',').Append(pass ? 1 : 0);
            w.WriteLine(sb.ToString());
        }

        private static string F(float v)
        {
            if (float.IsNaN(v)) return "nan";
            if (float.IsInfinity(v)) return "inf";
            return v.ToString("0.#######", CultureInfo.InvariantCulture);
        }
    }
}
