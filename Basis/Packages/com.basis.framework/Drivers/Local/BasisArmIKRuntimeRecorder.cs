using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    // Runtime per-frame capture of the LIVE arm IK, to chase jitter the offline sweeps can't reproduce.
    // Call RequestCapture(n) (e.g. an editor button) in play mode, then move/hold the arm; the rig driver
    // calls Sample() each frame AFTER the IK graph solves, and after n frames it flushes a CSV to
    // persistentDataPath/ArmIKRuntime. Records the SOLVED bones (upper-arm / elbow / hand) plus the IK
    // inputs (hand target, elbow hint, hint-on) so the diff shows exactly what moves when the elbow
    // jitters while the hand is held rock-solid. Cheap no-op (one bool) when not capturing.
    [AutoStaticsCleanup]
    public static partial class BasisArmIKRuntimeRecorder
    {
        public static bool Active { get; private set; }
        static readonly List<string> _rows = new List<string>(8192);
        static int _remaining;
        static int _frame;

        public static void RequestCapture(int frames)
        {
            _rows.Clear();
            _rows.Add("frame,side,sh_x,sh_y,sh_z,el_x,el_y,el_z,hand_x,hand_y,hand_z,tgt_x,tgt_y,tgt_z,hint_x,hint_y,hint_z,has_hint,swivel_deg");
            _remaining = Mathf.Max(1, frames);
            _frame = 0;
            Active = true;
            Debug.Log($"[ArmIKRuntime] capturing {_remaining} frames -- move/hold the arm now.");
        }

        public static void Sample(
            Transform lShoulder, Transform lElbow, Transform lHand,
            Transform rShoulder, Transform rElbow, Transform rHand,
            Vector3 lTarget, Vector3 rTarget, Vector3 lHint, Vector3 rHint,
            bool lHasHint, bool rHasHint)
        {
            if (!Active) return;
            Row("L", lShoulder, lElbow, lHand, lTarget, lHint, lHasHint);
            Row("R", rShoulder, rElbow, rHand, rTarget, rHint, rHasHint);
            _frame++;
            if (--_remaining <= 0) Flush();
        }

        static void Row(string side, Transform sh, Transform el, Transform hand, Vector3 tgt, Vector3 hint, bool hasHint)
        {
            if (sh == null || el == null || hand == null) return;
            Vector3 s = sh.position, e = el.position, h = hand.position;
            float swivel = Swivel(s, h, e);
            var sb = new StringBuilder(176);
            sb.Append(_frame).Append(',').Append(side).Append(',');
            App(sb, s); App(sb, e); App(sb, h); App(sb, tgt); App(sb, hint);
            sb.Append(hasHint ? '1' : '0').Append(',').Append(F(swivel));
            _rows.Add(sb.ToString());
        }

        // Signed elbow swivel around the shoulder->hand axis, measured from straight-down (matches the sweeps).
        static float Swivel(Vector3 shoulder, Vector3 hand, Vector3 elbow)
        {
            Vector3 axis = hand - shoulder;
            if (axis.sqrMagnitude < 1e-8f) return float.NaN;
            axis.Normalize();
            Vector3 refVec = Vector3.ProjectOnPlane(Vector3.down, axis);
            Vector3 pole = Vector3.ProjectOnPlane(elbow - shoulder, axis);
            if (refVec.sqrMagnitude < 1e-8f || pole.sqrMagnitude < 1e-8f) return float.NaN;
            return Vector3.SignedAngle(refVec, pole, axis);
        }

        static void Flush()
        {
            Active = false;
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "ArmIKRuntime");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "armik_runtime_" +
                    System.DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
                File.WriteAllLines(file, _rows);
                Debug.Log($"[ArmIKRuntime] wrote {_rows.Count - 1} rows to {file}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[ArmIKRuntime] flush failed: " + ex);
            }
            _rows.Clear();
        }

        static void App(StringBuilder sb, Vector3 v)
        {
            sb.Append(F(v.x)).Append(',').Append(F(v.y)).Append(',').Append(F(v.z)).Append(',');
        }

        static string F(float v) => float.IsNaN(v) ? "nan" : v.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
