using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
[AutoStaticsCleanup]
public static partial class BasisEyePositionGizmo
{
    public static bool Show;
    const float AxisLength = 0.08f, AxisWidth = 0.003f, BallSize = 0.012f, LegWidth = 0.002f;
    static readonly Color AxisY = new Color(0.3f, 1f, 0.3f, 1f), AxisZ = new Color(0.3f, 0.5f, 1f, 1f), Ball = new Color(1f, 0.9f, 0.4f, 1f), Authored = new Color(0.6f, 0.6f, 0.65f, 1f);
    static int y, z, ball, authoredBall, leg;
    public static void SetShow(bool show)
    {
        Show = show;
        if (!show)
        {
            Shutdown();
        }
        SMModuleDebugOptions.RefreshGizmoGate();
    }
    public static void Tick(float scale)
    {
        if (!Show)
        {
            Shutdown();
            return;
        }
        BasisLocalPlayer player = BasisLocalPlayer.Instance;
        if (player == null || player.BasisAvatar == null || player.LocalBoneDriver == null || !player.LocalBoneDriver.FindBone(out BasisLocalBoneControl eye, BasisBoneTrackedRole.CenterEye) || eye == null)
        {
            Shutdown();
            return;
        }
        if (scale <= 0f) scale = 1f;
        BasisCalibratedCoords driven = eye.OutgoingWorldData;
        Vector3 p = driven.position;
        Quaternion r = driven.rotation;
        float len = AxisLength * scale, width = AxisWidth * scale;
        EnsureLine(ref y, p, p + r * Vector3.up * len, width, AxisY);
        EnsureLine(ref z, p, p + r * Vector3.forward * len, width, AxisZ);
        EnsureSphere(ref ball, p, BallSize * scale, Ball);
        Vector2 offset = BasisPerAvatarEyeOffset.Current;
        Vector3 authored = p - r * new Vector3(0f, offset.x, offset.y) * scale;
        EnsureSphere(ref authoredBall, authored, BallSize * 0.75f * scale, Authored);
        EnsureLine(ref leg, authored, p, LegWidth * scale, Authored);
    }
    static void EnsureSphere(ref int id, Vector3 position, float size, Color color)
    {
        if (id <= 0 || !BasisGizmoManager.Exists(id))
        {
            id = BasisGizmoManager.CreateSphereGizmo("EyePosition", out int created, position, size, color) ? created : 0;
            return;
        }
        BasisGizmoManager.UpdateSphereGizmo(id, position, Vector3.one * size);
    }
    static void EnsureLine(ref int id, Vector3 start, Vector3 end, float width, Color color)
    {
        if (id <= 0 || !BasisGizmoManager.Exists(id))
        {
            id = BasisGizmoManager.CreateLineGizmo("EyePositionAxis", out int created, start, end, width, color) ? created : 0;
            return;
        }
        BasisGizmoManager.UpdateLineGizmo(id, start, end);
    }
    static void Destroy(ref int id)
    {
        if (id > 0 && BasisGizmoManager.Exists(id)) BasisGizmoManager.DestroyGizmo(id);
        id = 0;
    }
    public static void Shutdown()
    {
        Destroy(ref y);
        Destroy(ref z);
        Destroy(ref ball);
        Destroy(ref authoredBall);
        Destroy(ref leg);
    }
}
