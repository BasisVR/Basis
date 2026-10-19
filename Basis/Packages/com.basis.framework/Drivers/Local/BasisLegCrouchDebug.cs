using Unity.Scripting.LifecycleManagement;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Runtime diagnostic for the leg/knee during crouch. Logs, per leg per frame, the leg-IK inputs (hip,
    /// foot target, knee hint) and the rendered knee, plus the "shoot up" signals: knee above the hip and the
    /// knee's per-frame position jump. Crouch up/down in VR, then Stop+Dump to see the exact inputs at the
    /// frame the leg flips -- crucially this includes the LIVE foot-driver knee hint, which the offline crouch
    /// sweep (synthetic hint) can't reproduce.
    /// </summary>
    [AutoStaticsCleanup]
    public static partial class BasisLegCrouchDebug
    {
        public static bool Enabled;
        const int MaxRows = 8000;
        static readonly StringBuilder _sb = new StringBuilder();
        static int _rows;
        static bool _started;
        static Vector3 _prevKneeL, _prevKneeR;
        static bool _hasPrevL, _hasPrevR;
        static float _worstAboveL, _worstAboveR, _worstJumpL, _worstJumpR;
        static int _shootups;

        public static void Start()
        {
            _sb.Clear(); _rows = 0; _started = true; Enabled = true;
            _hasPrevL = _hasPrevR = false;
            _worstAboveL = _worstAboveR = _worstJumpL = _worstJumpR = 0f; _shootups = 0;
            _sb.Append("side,t,active,legLen,hipX,hipY,hipZ,footTgtX,footTgtY,footTgtZ,hintX,hintY,hintZ,kneeX,kneeY,kneeZ,kneeAboveHipFrac,kneeJumpFrac\n");
        }

        static string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);

        public static void Record(string side, float t, bool active, float legLen,
            Vector3 hip, Vector3 footTarget, Vector3 hint, Vector3 knee)
        {
            if (!Enabled || !_started || _rows >= MaxRows || legLen < 1e-4f) return;
            float aboveFrac = (knee.y - hip.y) / legLen;
            bool isL = side == "L";
            Vector3 prev = isL ? _prevKneeL : _prevKneeR;
            bool hasPrev = isL ? _hasPrevL : _hasPrevR;
            float jumpFrac = hasPrev ? Vector3.Distance(knee, prev) / legLen : 0f;
            if (isL) { _prevKneeL = knee; _hasPrevL = true; } else { _prevKneeR = knee; _hasPrevR = true; }

            _sb.Append(side).Append(',').Append(F(t)).Append(',').Append(active ? 1 : 0).Append(',').Append(F(legLen)).Append(',')
               .Append(F(hip.x)).Append(',').Append(F(hip.y)).Append(',').Append(F(hip.z)).Append(',')
               .Append(F(footTarget.x)).Append(',').Append(F(footTarget.y)).Append(',').Append(F(footTarget.z)).Append(',')
               .Append(F(hint.x)).Append(',').Append(F(hint.y)).Append(',').Append(F(hint.z)).Append(',')
               .Append(F(knee.x)).Append(',').Append(F(knee.y)).Append(',').Append(F(knee.z)).Append(',')
               .Append(F(aboveFrac)).Append(',').Append(F(jumpFrac)).Append('\n');
            _rows++;

            if (active)
            {
                if (isL) { _worstAboveL = Mathf.Max(_worstAboveL, aboveFrac); _worstJumpL = Mathf.Max(_worstJumpL, jumpFrac); }
                else { _worstAboveR = Mathf.Max(_worstAboveR, aboveFrac); _worstJumpR = Mathf.Max(_worstJumpR, jumpFrac); }
                if (jumpFrac > 0.2f) _shootups++; // knee teleported >20% of leg length in one frame = a flip/shoot-up
            }
        }

        public static void StopAndDump()
        {
            Enabled = false;
            if (!_started) { Debug.LogError("[LegCrouchDebug] never started -- run 'Leg Crouch Debug - Start' first"); return; }
            string path = Path.Combine(Application.persistentDataPath, "BasisLegCrouchDebug.csv");
            try { File.WriteAllText(path, _sb.ToString()); }
            catch (System.Exception e) { Debug.LogError("[LegCrouchDebug] write failed: " + e.Message); _started = false; return; }
            bool pass = _shootups == 0;
            string msg = $"[LegCrouchDebug] {(pass ? "PASS" : "FAIL")}: {_shootups} knee shoot-ups (>20% leg/frame); worst knee jump L={_worstJumpL:F2} R={_worstJumpR:F2} frac/frame; worst knee-above-hip L={_worstAboveL:F2} R={_worstAboveR:F2} (frac of leg). {_rows} rows -> {path}";
            if (pass) Debug.Log(msg); else Debug.LogError(msg);
            _started = false;
        }
    }
}
