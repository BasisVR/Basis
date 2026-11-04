using System.Globalization;
using System.IO;
using System.Text;
using Basis.Scripts.BasisSdk.Players;
using UnityEngine;
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
    StreamWriter writer;
    readonly StringBuilder csvBuilder = new StringBuilder(256);
    bool hasPrev;
    Vector3 prevLeft, prevRight;
    bool prevLeftPlanted, prevRightPlanted;
    int sinceFlush;
    Transform leftBone, rightBone;
    Vector3 prevLBone, prevRBone;
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

        ResolvedLogPath = string.IsNullOrEmpty(LogPathOverride) ? Path.Combine(Application.persistentDataPath, "BasisFootIK.csv") : LogPathOverride;

        try
        {
            string dir = Path.GetDirectoryName(ResolvedLogPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            writer = new StreamWriter(ResolvedLogPath, false, Encoding.UTF8);
            writer.WriteLine("time_s," + "left_planted,left_step_prog,left_step_timer,left_fx,left_fy,left_fz,left_fyaw,left_slide_mm,left_plant_x,left_plant_z," + "left_ideal_x,left_ideal_y,left_ideal_z,left_tgt_x,left_tgt_y,left_tgt_z," + "right_planted,right_step_prog,right_step_timer,right_fx,right_fy,right_fz,right_fyaw,right_slide_mm,right_plant_x,right_plant_z," + "right_ideal_x,right_ideal_y,right_ideal_z,right_tgt_x,right_tgt_y,right_tgt_z," + "stance_width_m,ground_hit,ground_up,hips_up," + "lbone_x,lbone_y,lbone_z,lbone_jump_mm,rbone_x,rbone_y,rbone_z,rbone_jump_mm");

            SnapshotsWritten = 0;
            TotalPlantedSlideM = 0f;
            LeftSteps = 0;
            RightSteps = 0;
            hasPrev = false;
            sinceFlush = 0;
            IsLogging = true;
        }
        catch (System.Exception e)
        {
            BasisDebug.LogError("[FootIKDiagnostics] start failed: " + e.Message);
            writer = null;
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
            writer?.Flush();
            writer?.Dispose();
        }
        catch { }
        writer = null;
    }
    public void Flush()
    {
        writer?.Flush();
    }
    void LateUpdate()
    {
        if (!IsLogging || writer == null)
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
        if (hasPrev)
        {
            if (lp && prevLeftPlanted) leftSlide = HorizDist(lf, prevLeft);
            if (rp && prevRightPlanted) rightSlide = HorizDist(rf, prevRight);
            TotalPlantedSlideM += leftSlide + rightSlide;
            if (lp && !prevLeftPlanted) LeftSteps++;
            if (rp && !prevRightPlanted) RightSteps++;
        }

        float t = Time.realtimeSinceStartup;
        csvBuilder.Clear();
        Append(csvBuilder, t);
        WriteFoot(csvBuilder, lp, foot.LeftStepProgress, foot.LeftStepTimer, lf, foot.LeftIdealPos, foot.LeftStepTarget, foot.LeftFootRotation, leftSlide, foot.LeftPlantedPos);
        WriteFoot(csvBuilder, rp, foot.RightStepProgress, foot.RightStepTimer, rf, foot.RightIdealPos, foot.RightStepTarget, foot.RightFootRotation, rightSlide, foot.RightPlantedPos);
        Append(csvBuilder, HorizDist(lf, rf));
        csvBuilder.Append(foot.LastGroundHit ? '1' : '0').Append(',');
        Append(csvBuilder, foot.LastGroundUp);
        Append(csvBuilder, foot.HipsUp);

        ResolveBones(player);
        Vector3 lb = leftBone != null ? leftBone.position : Vector3.zero;
        Vector3 rb = rightBone != null ? rightBone.position : Vector3.zero;
        float lbJump = (hasPrev && leftBone != null) ? (lb - prevLBone).magnitude * 1000f : 0f;
        float rbJump = (hasPrev && rightBone != null) ? (rb - prevRBone).magnitude * 1000f : 0f;
        Append(csvBuilder, lb.x); Append(csvBuilder, lb.y); Append(csvBuilder, lb.z); Append(csvBuilder, lbJump);
        Append(csvBuilder, rb.x); Append(csvBuilder, rb.y); Append(csvBuilder, rb.z); AppendLast(csvBuilder, rbJump);
        writer.WriteLine(csvBuilder.ToString());

        SnapshotsWritten++;
        prevLeft = lf; prevRight = rf;
        prevLeftPlanted = lp; prevRightPlanted = rp;
        prevLBone = lb; prevRBone = rb;
        hasPrev = true;

        if (++sinceFlush >= 120)
        {
            writer.Flush();
            sinceFlush = 0;
        }
    }
    void WriteFoot(StringBuilder sb, bool planted, float stepProg, float stepTimer, Vector3 foot, Vector3 ideal, Vector3 tgt, Quaternion rot, float slide, Vector3 plant)
    {
        sb.Append(planted ? '1' : '0').Append(',');
        Append(sb, stepProg);
        Append(sb, stepTimer);
        Append(sb, foot.x); Append(sb, foot.y); Append(sb, foot.z);
        Append(sb, rot.eulerAngles.y);
        Append(sb, slide * 1000f);
        Append(sb, plant.x); Append(sb, plant.z);
        Append(sb, ideal.x); Append(sb, ideal.y); Append(sb, ideal.z);
        Append(sb, tgt.x); Append(sb, tgt.y); Append(sb, tgt.z);
    }
    void ResolveBones(BasisLocalPlayer player)
    {
        if (leftBone != null && rightBone != null) return;
        var anim = player != null && player.BasisAvatar != null ? player.BasisAvatar.Animator : null;
        if (anim == null || !anim.isHuman) return;
        leftBone = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
        rightBone = anim.GetBoneTransform(HumanBodyBones.RightFoot);
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
