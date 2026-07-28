using System.Globalization;
using System.IO;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;

// Per-frame CSV recorder for the procedural foot-placement system. Attach in play mode and
// move around; logs each foot's plant/step phase, positions, knee hint, and — the headline
// metric — planted-foot slide (XZ drift while planted = skating). Mirrors the media-player
// diagnostics recorder pattern.
public class BasisFootIKDiagnostics : MonoBehaviour
{
    public bool AutoStart = true;
    public string LogPathOverride = "";

    public bool IsLogging { get; private set; }
    public long SnapshotsWritten { get; private set; }
    public float TotalPlantedSlideM { get; private set; }
    public int LeftSteps { get; private set; }
    public int RightSteps { get; private set; }
    public string ResolvedLogPath { get; private set; }

    StreamWriter _writer;
    readonly StringBuilder _sb = new StringBuilder(256);
    bool _hasPrev;
    Vector3 _prevLeft, _prevRight;
    bool _prevLeftPlanted, _prevRightPlanted;
    int _sinceFlush;
    Transform _leftBone, _rightBone;
    Vector3 _prevLBone, _prevRBone;

    void OnEnable()
    {
        if (AutoStart)
        {
            StartLogging();
        }
    }

    void OnDisable()
    {
        StopLogging();
    }

    public void StartLogging()
    {
        if (IsLogging)
        {
            return;
        }

        ResolvedLogPath = string.IsNullOrEmpty(LogPathOverride)
            ? Path.Combine(Application.persistentDataPath, "BasisFootIK.csv")
            : LogPathOverride;

        try
        {
            string dir = Path.GetDirectoryName(ResolvedLogPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _writer = new StreamWriter(ResolvedLogPath, false, Encoding.UTF8);
            _writer.WriteLine("time_s," +
                "left_planted,left_step_prog,left_step_timer,left_fx,left_fy,left_fz,left_fyaw,left_slide_mm,left_plant_x,left_plant_z," +
                "left_knee_x,left_knee_y,left_knee_z,left_ideal_x,left_ideal_y,left_ideal_z,left_tgt_x,left_tgt_y,left_tgt_z," +
                "right_planted,right_step_prog,right_step_timer,right_fx,right_fy,right_fz,right_fyaw,right_slide_mm,right_plant_x,right_plant_z," +
                "right_knee_x,right_knee_y,right_knee_z,right_ideal_x,right_ideal_y,right_ideal_z,right_tgt_x,right_tgt_y,right_tgt_z," +
                "stance_width_m,ground_hit,ground_up,hips_up," +
                "lbone_x,lbone_y,lbone_z,lbone_jump_mm,rbone_x,rbone_y,rbone_z,rbone_jump_mm");

            SnapshotsWritten = 0;
            TotalPlantedSlideM = 0f;
            LeftSteps = 0;
            RightSteps = 0;
            _hasPrev = false;
            _sinceFlush = 0;
            IsLogging = true;
        }
        catch (System.Exception e)
        {
            BasisDebug.LogError("[FootIKDiagnostics] start failed: " + e.Message);
            _writer = null;
            IsLogging = false;
        }
    }

    public void StopLogging()
    {
        if (!IsLogging)
        {
            return;
        }

        IsLogging = false;
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch { }
        _writer = null;
    }

    public void Flush()
    {
        _writer?.Flush();
    }

    void LateUpdate()
    {
        if (!IsLogging || _writer == null)
        {
            return;
        }

        var player = BasisLocalPlayer.Instance;
        var foot = player != null ? player.BasisLocalFootDriver : null;
        if (foot == null || !foot.IsInitialized)
        {
            return;
        }

        Vector3 lf = foot.LeftFootPosition, rf = foot.RightFootPosition;
        bool lp = foot.LeftIsPlanted, rp = foot.RightIsPlanted;

        float leftSlide = 0f, rightSlide = 0f;
        if (_hasPrev)
        {
            if (lp && _prevLeftPlanted) leftSlide = HorizDist(lf, _prevLeft);
            if (rp && _prevRightPlanted) rightSlide = HorizDist(rf, _prevRight);
            TotalPlantedSlideM += leftSlide + rightSlide;
            if (lp && !_prevLeftPlanted) LeftSteps++;   // step just completed (stepping -> planted)
            if (rp && !_prevRightPlanted) RightSteps++;
        }

        float t = Time.realtimeSinceStartup;
        _sb.Clear();
        Append(_sb, t);
        WriteFoot(_sb, lp, foot.LeftStepProgress, foot.LeftStepTimer, lf, foot.LeftKneeHint, foot.LeftIdealPos, foot.LeftStepTarget, foot.LeftFootRotation, leftSlide, foot.LeftPlantedPos);
        WriteFoot(_sb, rp, foot.RightStepProgress, foot.RightStepTimer, rf, foot.RightKneeHint, foot.RightIdealPos, foot.RightStepTarget, foot.RightFootRotation, rightSlide, foot.RightPlantedPos);
        Append(_sb, HorizDist(lf, rf));
        _sb.Append(foot.LastGroundHit ? '1' : '0').Append(',');
        Append(_sb, foot.LastGroundUp);
        Append(_sb, foot.HipsUp);

        // Final visible foot bones (post blend + leg IK) — the actual rendered foot. Per-frame
        // jump of these is the visible 1-frame glitch the driver-state columns can't show.
        ResolveBones(player);
        Vector3 lb = _leftBone != null ? _leftBone.position : Vector3.zero;
        Vector3 rb = _rightBone != null ? _rightBone.position : Vector3.zero;
        float lbJump = (_hasPrev && _leftBone != null) ? (lb - _prevLBone).magnitude * 1000f : 0f;
        float rbJump = (_hasPrev && _rightBone != null) ? (rb - _prevRBone).magnitude * 1000f : 0f;
        Append(_sb, lb.x); Append(_sb, lb.y); Append(_sb, lb.z); Append(_sb, lbJump);
        Append(_sb, rb.x); Append(_sb, rb.y); Append(_sb, rb.z); AppendLast(_sb, rbJump);
        _writer.WriteLine(_sb.ToString());

        SnapshotsWritten++;
        _prevLeft = lf; _prevRight = rf;
        _prevLeftPlanted = lp; _prevRightPlanted = rp;
        _prevLBone = lb; _prevRBone = rb;
        _hasPrev = true;

        if (++_sinceFlush >= 120)
        {
            _writer.Flush();
            _sinceFlush = 0;
        }
    }

    void WriteFoot(StringBuilder sb, bool planted, float stepProg, float stepTimer, Vector3 foot, Vector3 knee, Vector3 ideal, Vector3 tgt, Quaternion rot, float slide, Vector3 plant)
    {
        sb.Append(planted ? '1' : '0').Append(',');
        Append(sb, stepProg);
        Append(sb, stepTimer);
        Append(sb, foot.x); Append(sb, foot.y); Append(sb, foot.z);
        Append(sb, rot.eulerAngles.y);
        Append(sb, slide * 1000f);
        Append(sb, plant.x); Append(sb, plant.z);
        Append(sb, knee.x); Append(sb, knee.y); Append(sb, knee.z);
        Append(sb, ideal.x); Append(sb, ideal.y); Append(sb, ideal.z);
        Append(sb, tgt.x); Append(sb, tgt.y); Append(sb, tgt.z);
    }

    void ResolveBones(BasisLocalPlayer player)
    {
        if (_leftBone != null && _rightBone != null) return;
        var anim = player != null && player.BasisAvatar != null ? player.BasisAvatar.Animator : null;
        if (anim == null || !anim.isHuman) return;
        _leftBone = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
        _rightBone = anim.GetBoneTransform(HumanBodyBones.RightFoot);
    }

    static float HorizDist(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    static void Append(StringBuilder sb, float v) { sb.Append(F(v)).Append(','); }
    static void AppendLast(StringBuilder sb, float v) { sb.Append(F(v)); }

    static string F(float v)
    {
        if (float.IsNaN(v)) return "nan";
        return v.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
