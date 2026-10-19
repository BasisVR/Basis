using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    // Runtime per-frame capture of the LIVE double-hip (linked tracker pair) rotation, to localize the
    // "funky snap / double multiplication" the offline sweeps can't reproduce. Call RequestCapture(n) from an
    // editor button in play mode, then turn/move with the linked pair on; BasisVirtualMidpointInput.Sample()
    // feeds one row per frame and after n frames it flushes a CSV to persistentDataPath/PairingRotation.
    //
    // The key columns are the angle each STAGE has rotated since capture started:
    //   ang_a / ang_b  - each physical partner (the ground truth: a single tracker would move by this)
    //   ang_fused      - the published virtual rotation (fusion output)
    //   ang_bone       - the actual driven hip bone (HipsControl world out)
    // A correct pair tracks 1:1, so ang_fused ~= ang_a ~= ang_b and ang_bone ~= ang_a. The failure shows as a
    // SLOPE: ang_fused ~= 2*ang_a means the fusion doubles; ang_fused ~= ang_a but ang_bone ~= 2*ang_a means a
    // downstream (offset/calibration) double; a step in ang_fused while wA/wB/t jump = a confidence-blend snap.
    [AutoStaticsCleanup]
    public static partial class BasisPairingRotationRecorder
    {
        public static bool Active { get; private set; }
        static readonly List<string> _rows = new List<string>(8192);
        static int _remaining;
        static int _frame;
        static bool _hasRef;
        static Quaternion _refA, _refB, _refFused, _refScaled, _refBone;

        public static void RequestCapture(int frames)
        {
            _rows.Clear();
            _rows.Add("frame,ang_a,ang_b,ang_fused,ang_scaled,ang_bone,ratio_fused_a,ratio_bone_a,wA,wB,t");
            _remaining = Mathf.Max(1, frames);
            _frame = 0;
            _hasRef = false;
            Active = true;
            Debug.Log($"[PairingRotation] capturing {_remaining} frames -- turn/lean with the linked pair now.");
        }

        // Called from BasisVirtualMidpointInput.LateDoPollData each frame (no-op when not capturing). The bone
        // is read here from the live HipsControl so we don't depend on the virtual's Control binding.
        public static void Sample(Quaternion aRot, Quaternion bRot, Quaternion fusedRot, Quaternion scaledRot, float wA, float wB, float t)
        {
            if (!Active) return;

            Quaternion boneRot = BasisLocalBoneDriver.HipsControl != null
                ? BasisLocalBoneDriver.HipsControl.OutgoingWorldData.rotation
                : Quaternion.identity;

            if (!_hasRef)
            {
                _refA = aRot; _refB = bRot; _refFused = fusedRot; _refScaled = scaledRot; _refBone = boneRot;
                _hasRef = true;
            }

            float angA = Quaternion.Angle(_refA, aRot);
            float angB = Quaternion.Angle(_refB, bRot);
            float angFused = Quaternion.Angle(_refFused, fusedRot);
            float angScaled = Quaternion.Angle(_refScaled, scaledRot);
            float angBone = Quaternion.Angle(_refBone, boneRot);
            // Ratio vs the partner ground truth -- ~1 is correct, ~2 is the double. Guarded near zero motion.
            float ratioFused = angA > 1f ? angFused / angA : float.NaN;
            float ratioBone = angA > 1f ? angBone / angA : float.NaN;

            var sb = new StringBuilder(128);
            sb.Append(_frame).Append(',');
            sb.Append(F(angA)).Append(',').Append(F(angB)).Append(',').Append(F(angFused)).Append(',')
              .Append(F(angScaled)).Append(',').Append(F(angBone)).Append(',')
              .Append(F(ratioFused)).Append(',').Append(F(ratioBone)).Append(',')
              .Append(F(wA)).Append(',').Append(F(wB)).Append(',').Append(F(t));
            _rows.Add(sb.ToString());

            _frame++;
            if (--_remaining <= 0) Flush();
        }

        static void Flush()
        {
            Active = false;
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "PairingRotation");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "pairing_rotation_" +
                    System.DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
                File.WriteAllLines(file, _rows);
                Debug.Log($"[PairingRotation] wrote {_rows.Count - 1} rows to {file}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[PairingRotation] flush failed: " + ex);
            }
            _rows.Clear();
        }

        static string F(float v) => float.IsNaN(v) ? "nan" : v.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
