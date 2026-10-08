using Unity.Scripting.LifecycleManagement;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Runtime diagnostic for the foot-IK target rotation. The correct foot orientation is the bone-sim
    /// WORLD foot (OutgoingWorldData) -- it's the animation/standing foot and is valid every frame, independent
    /// of the FBIK. So this logs, per foot per frame, how far the rendered foot is from that correct foot
    /// (the bug magnitude), and for each candidate target rotation how far (candidate*offset) lands from the
    /// correct foot -- i.e. which target, when fed, makes finalFoot = the correct foot. Measure on foot-IK-ON
    /// frames (stand still). Stop+Dump writes a CSV + a PASS/FAIL summary naming the target to feed.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisFootRotationDebug
    {
        public static bool Enabled;
        const int MaxRows = 8000;
        static readonly StringBuilder _sb = new StringBuilder();
        static int _rows;
        static bool _started;

        // target candidates: 0 OutGoingData(local), 1 OutgoingWorldData, 2 rOut, 3 procedural,
        //                     4 OutgoingWorldData*Inv(offset), 5 OutGoingData*Inv(offset)
        [NoAutoStaticsCleanup] static readonly string[] CandNames =
            { "OutGoingData(local)", "OutgoingWorldData", "rOut", "procedural(FootRotation)", "OutgoingWorldData*Inv(offset)", "OutGoingData*Inv(offset)" };
        static readonly double[] _refSum = new double[6];
        static int _refN;
        static float _worstOnVsCorrect;
        static double _onVsCorrectSum;
        static int _onFrames;
        static double _modelAngSum;
        static int _modelN;

        public static void Start()
        {
            _sb.Clear(); _rows = 0; _started = true; Enabled = true;
            for (int i = 0; i < 6; i++) _refSum[i] = 0;
            _refN = 0; _worstOnVsCorrect = 0f; _onVsCorrectSum = 0; _onFrames = 0; _modelAngSum = 0; _modelN = 0;
            _sb.Append("side,t,active,weight,actVsCorrect,act_toesUp,modelAng,offsetMag,");
            _sb.Append("outL_ref,outW_ref,rOut_ref,proc_ref,outWinv_ref,outLinv_ref\n");
        }

        static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
        static float ToesUp(Quaternion q) => 90f - Vector3.Angle(q * Vector3.forward, Vector3.up);

        public static void Record(string side, float t, float weight, bool active,
            Quaternion actual, Quaternion fed, Quaternion offset,
            Quaternion outLocal, Quaternion outWorld, Quaternion rOut, Quaternion procedural)
        {
            if (!Enabled || !_started || _rows >= MaxRows) return;
            Quaternion correct = outWorld;                       // bone-sim world foot = the correct orientation
            Quaternion invOff = Quaternion.Inverse(offset);
            Quaternion[] targets = { outLocal, outWorld, rOut, procedural, outWorld * invOff, outLocal * invOff };
            float[] refAng = new float[6];
            for (int i = 0; i < 6; i++) refAng[i] = Quaternion.Angle(targets[i] * offset, correct);

            float actVsCorrect = Quaternion.Angle(actual, correct);
            float modelAng = Quaternion.Angle(actual, fed * offset);
            float offsetMag = Quaternion.Angle(offset, Quaternion.identity);

            _sb.Append(side).Append(',').Append(F(t)).Append(',').Append(active ? 1 : 0).Append(',').Append(F(weight)).Append(',')
               .Append(F(actVsCorrect)).Append(',').Append(F(ToesUp(actual))).Append(',').Append(F(modelAng)).Append(',').Append(F(offsetMag)).Append(',')
               .Append(F(refAng[0])).Append(',').Append(F(refAng[1])).Append(',').Append(F(refAng[2])).Append(',')
               .Append(F(refAng[3])).Append(',').Append(F(refAng[4])).Append(',').Append(F(refAng[5])).Append('\n');
            _rows++;

            for (int i = 0; i < 6; i++) _refSum[i] += refAng[i];
            _refN++;
            if (active)
            {
                _onFrames++;
                _worstOnVsCorrect = Mathf.Max(_worstOnVsCorrect, actVsCorrect);
                _onVsCorrectSum += actVsCorrect;
                _modelAngSum += modelAng; _modelN++;
            }
        }

        public static void StopAndDump()
        {
            Enabled = false;
            if (!_started) { Debug.LogError("[FootRotDebug] never started -- run 'Foot Rotation Debug - Start' first"); return; }
            string path = Path.Combine(Application.persistentDataPath, "BasisFootRotationDebug.csv");
            try { File.WriteAllText(path, _sb.ToString()); }
            catch (System.Exception e) { Debug.LogError("[FootRotDebug] write failed: " + e.Message); _started = false; return; }

            var msg = new StringBuilder();
            if (_onFrames > 0)
            {
                bool pass = _worstOnVsCorrect < 15f;
                msg.Append($"[FootRotDebug] {(pass ? "PASS" : "FAIL")}: rendered foot vs correct (bone-sim) -- worst {_worstOnVsCorrect:F1}, avg {_onVsCorrectSum / _onFrames:F1} deg over {_onFrames} foot-IK-ON frames (correct < 15). ");
                msg.Append($"solve model (fed*offset vs rendered) avg {_modelAngSum / Mathf.Max(1, _modelN):F1} deg [~0 confirms finalFoot=target*offset, lag aside]. ");
            }
            else
            {
                msg.Append("[FootRotDebug] no foot-IK-ON frames -- STAND STILL with foot IK enabled so the ON path is recorded, then Stop+Dump again. ");
            }
            if (_refN > 0)
            {
                int best = 0; for (int i = 1; i < 6; i++) if (_refSum[i] < _refSum[best]) best = i;
                msg.Append($"To make finalFoot=correct, FEED '{CandNames[best]}' ((target*offset) lands {_refSum[best] / _refN:F1} deg from correct). all: ");
                for (int i = 0; i < 6; i++) msg.Append($"{CandNames[i]}={_refSum[i] / _refN:F1} ");
            }
            msg.Append($"| {_rows} rows -> {path}");
            if (_onFrames > 0 && _worstOnVsCorrect < 15f) Debug.Log(msg.ToString()); else Debug.LogError(msg.ToString());
            _started = false;
        }
    }
}
