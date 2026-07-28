using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Basis.Scripts.Drivers
{
    /// <summary>
    /// Runtime diagnostic for the knee SWIVEL, per leg per frame. Built to answer one question: when one knee
    /// misbehaves and the other is fine, WHICH input differs? The solve path is mirror-symmetric (same
    /// kneeAnteriorRef, same captured bend normal, mirrored driver blocks, and an ODD anterior clamp), so a
    /// left/right asymmetry has to come from the DATA, and this is what reads the data.
    ///
    /// Rows are <see cref="Basis.IK.BasisLegDiagnostics"/> verbatim -- its own Header/ToRow, not a second copy
    /// of the field list, so the two cannot drift.
    ///
    /// The column that is NOT in the diagnostics struct is bendVsAnt: the angle between that leg's BendNormal
    /// (which rides the lower-leg TRACKER when FBIKTrackerBendNormal is on) and kneeAnteriorRef (which is always
    /// body-frame hips-right). Those two frames are what the anterior guard and the pole eases are measured
    /// against respectively, and the guard-first ordering in BasisLegSolveCore only keeps the near-colinear
    /// slerp well-conditioned while they agree to within ~90 deg -- measured 1.3x knee gain up to 90, then 316x
    /// at 120 and 471x at 180. A strap rotated on one shin, or a stale bend-normal capture, shows up here as a
    /// large bendVsAnt on that leg alone.
    ///
    /// Read the SUMMARY first; the CSV is for the frame the number goes bad.
    /// </summary>
    public static class BasisLegSwivelDebug
    {
        public static bool Enabled;
        const int MaxRows = 16000;
        static readonly StringBuilder _sb = new StringBuilder();
        static int _rows;
        static bool _started;

        struct Side
        {
            public int Frames;
            public float MaxAbsRaw;
            public float MaxAbsSmooth;
            public float MaxSmoothJump;      // the CLICK metric: worst per-frame change in the applied swivel
            public float MaxSmoothJumpAtT;
            public int GuardFrames;          // frames the anterior guard actually pulled the knee back
            public int PastSoftFrames;       // |raw| > 85: the guard's band
            public int PastDeepFrames;       // |raw| > 120: where the taper starts re-posing the knee
            public int UnseededFrames;
            public float CondSum;
            public float MaxBendVsAnt;
            public float BendVsAntSum;
            public float PrevSmooth;
            public bool HasPrev;
        }

        static Side _l, _r;

        public static void Start()
        {
            _sb.Clear();
            _rows = 0;
            _started = true;
            Enabled = true;
            _l = default;
            _r = default;
            _sb.Append("t,").Append(Basis.IK.BasisLegDiagnostics.Header).Append(",bendVsAnt\n");
        }

        static string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);

        public static void Record(string side, float t, in Basis.IK.BasisLegDiagnostics d, float bendVsAntDeg)
        {
            if (!Enabled || !_started) return;

            bool isL = side == "L";
            ref Side s = ref (isL ? ref _l : ref _r);

            s.Frames++;
            float absRaw = Mathf.Abs(d.RawSwivelDeg);
            float absSmooth = Mathf.Abs(d.SmoothSwivelDeg);
            if (absRaw > s.MaxAbsRaw) s.MaxAbsRaw = absRaw;
            if (absSmooth > s.MaxAbsSmooth) s.MaxAbsSmooth = absSmooth;
            if (absRaw > Basis.IK.BasisLegSolveCore.KneeAnteriorSoftDeg) s.PastSoftFrames++;
            if (absRaw > 120f) s.PastDeepFrames++;
            if (d.AnteriorGuardApplied > 0.5f) s.GuardFrames++;
            if (d.Seeded < 0.5f) s.UnseededFrames++;
            s.CondSum += d.Conditioning;
            s.BendVsAntSum += bendVsAntDeg;
            if (bendVsAntDeg > s.MaxBendVsAnt) s.MaxBendVsAnt = bendVsAntDeg;

            // DeltaAngle, not a raw subtraction: the swivel is an angle on a circle and a wrap must not read as
            // a 360 deg click. (That confusion is the whole reason this recorder exists.)
            if (s.HasPrev)
            {
                float jump = Mathf.Abs(Mathf.DeltaAngle(s.PrevSmooth, d.SmoothSwivelDeg));
                if (jump > s.MaxSmoothJump) { s.MaxSmoothJump = jump; s.MaxSmoothJumpAtT = t; }
            }
            s.PrevSmooth = d.SmoothSwivelDeg;
            s.HasPrev = true;

            if (_rows < MaxRows)
            {
                _sb.Append(F(t)).Append(',').Append(d.ToRow(side)).Append(',').Append(F(bendVsAntDeg)).Append('\n');
                _rows++;
            }
        }

        public static void StopAndDump()
        {
            Enabled = false;
            if (!_started)
            {
                Debug.LogError("[LegSwivelDebug] never started -- run 'Leg Swivel Debug - Start' first");
                return;
            }

            string path = Path.Combine(Application.persistentDataPath, "BasisLegSwivelDebug.csv");
            try { File.WriteAllText(path, _sb.ToString()); }
            catch (System.Exception e) { Debug.LogError("[LegSwivelDebug] write failed: " + e.Message); _started = false; return; }

            Debug.Log($"[LegSwivelDebug] {_rows} rows -> {path}\n{Summary()}");
            _started = false;
        }

        static string Summary()
        {
            var sb = new StringBuilder();
            sb.Append("            metric |        LEFT |       RIGHT | what it means\n");
            Row(sb, "frames", _l.Frames, _r.Frames, "");
            Row(sb, "max |raw swivel|", _l.MaxAbsRaw, _r.MaxAbsRaw, "deg off anterior the POLE asks for");
            Row(sb, "max |smooth swivel|", _l.MaxAbsSmooth, _r.MaxAbsSmooth, "deg off anterior actually applied");
            Row(sb, "frames past 85 deg", _l.PastSoftFrames, _r.PastSoftFrames, "guard band -- pole is near lateral");
            Row(sb, "frames past 120 deg", _l.PastDeepFrames, _r.PastDeepFrames, "taper re-poses the knee here");
            Row(sb, "guard fired frames", _l.GuardFrames, _r.GuardFrames, "guard actually pulled the knee back");
            Row(sb, "worst swivel jump", _l.MaxSmoothJump, _r.MaxSmoothJump, "deg/frame -- >20 is a CLICK");
            Row(sb, "  at t =", _l.MaxSmoothJumpAtT, _r.MaxSmoothJumpAtT, "grep the CSV at this timestamp");
            Row(sb, "mean conditioning", _l.Frames > 0 ? _l.CondSum / _l.Frames : 0f, _r.Frames > 0 ? _r.CondSum / _r.Frames : 0f, "knee lever arm; ~0.035 = standing");
            Row(sb, "unseeded frames", _l.UnseededFrames, _r.UnseededFrames, "smoother never seeded (hold defers it)");
            Row(sb, "mean bendVsAnt", _l.Frames > 0 ? _l.BendVsAntSum / _l.Frames : 0f, _r.Frames > 0 ? _r.BendVsAntSum / _r.Frames : 0f, "tracker bend plane vs body frame");
            Row(sb, "max bendVsAnt", _l.MaxBendVsAnt, _r.MaxBendVsAnt, "> 90 deg = pole eases go ill-conditioned");

            sb.Append('\n');
            sb.Append(Verdict());
            return sb.ToString();
        }

        static void Row(StringBuilder sb, string name, float l, float r, string note)
        {
            sb.Append(name.PadLeft(18)).Append(" | ").Append(l.ToString("F2").PadLeft(11))
              .Append(" | ").Append(r.ToString("F2").PadLeft(11)).Append(" | ").Append(note).Append('\n');
        }

        /// <summary>
        /// Names the asymmetry rather than leaving it to be eyeballed. Deliberately reports only what the
        /// numbers support -- an inconclusive run should say so, not pick the nearest story.
        /// </summary>
        static string Verdict()
        {
            if (_l.Frames == 0 || _r.Frames == 0)
            {
                return "[verdict] one leg logged no frames -- that leg's IK is disabled or its weight is 0.";
            }

            var sb = new StringBuilder();
            bool said = false;

            if (_l.MaxBendVsAnt > 90f || _r.MaxBendVsAnt > 90f)
            {
                sb.Append($"[verdict] BEND NORMAL DIVERGED past 90 deg (L {_l.MaxBendVsAnt:F0}, R {_r.MaxBendVsAnt:F0}). ")
                  .Append("Past 90 the near-colinear pole ease is handed a near-anti-parallel slerp and the knee gain ")
                  .Append("goes to hundreds. Confirm by turning FBIKTrackerBendNormal OFF -- both legs then use ")
                  .Append("body-frame hips-right and this cannot happen.\n");
                said = true;
            }

            float rawGap = Mathf.Abs(_l.MaxAbsRaw - _r.MaxAbsRaw);
            if (rawGap > 45f)
            {
                string bad = _l.MaxAbsRaw > _r.MaxAbsRaw ? "LEFT" : "RIGHT";
                sb.Append($"[verdict] the {bad} POLE is asking for a far larger swivel (L {_l.MaxAbsRaw:F0} vs R {_r.MaxAbsRaw:F0}). ")
                  .Append("The solve path is mirror-symmetric, so this is an INPUT asymmetry: that leg's hint tracker ")
                  .Append("position or its calibration reference, not the solver.\n");
                said = true;
            }

            if (_l.MaxSmoothJump > 20f || _r.MaxSmoothJump > 20f)
            {
                sb.Append($"[verdict] STILL CLICKING (worst L {_l.MaxSmoothJump:F0} deg, R {_r.MaxSmoothJump:F0} deg per frame). ")
                  .Append("A human knee tops out near 5.6 deg/frame at 90fps. Check the CSV at the timestamp above.\n");
                said = true;
            }

            if (_l.UnseededFrames > _l.Frames / 2 || _r.UnseededFrames > _r.Frames / 2)
            {
                sb.Append("[verdict] the smoother spent most of the run UNSEEDED, which is expected while standing ")
                  .Append("(the singularity hold defers seeding below HoldCondHi) -- it means the smoother is not ")
                  .Append("moving that knee at all, so blame the SOLVE, not the filter.\n");
                said = true;
            }

            if (!said)
            {
                sb.Append("[verdict] no asymmetry large enough to explain a one-sided artifact. Both poles are behaving; ")
                  .Append("look at shin roll (ShinRollDeg column) or somewhere outside the swivel path.\n");
            }

            return sb.ToString();
        }
    }
}
