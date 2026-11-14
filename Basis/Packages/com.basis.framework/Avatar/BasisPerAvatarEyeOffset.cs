using Unity.Scripting.LifecycleManagement;
using Basis.Scripts.BasisSdk;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Settings;
using Basis.Scripts.TransformBinders.BoneControl;
using UnityEngine;
[AutoStaticsCleanup]
public static partial class BasisPerAvatarEyeOffset
{
    public const float MinHeight = -0.25f, MaxHeight = 0.25f, MinForward = -0.15f, MaxForward = 0.15f;
    public static Vector2 Current { get; private set; } = Vector2.zero;
    static BasisAvatar appliedTo;
    static Vector2 appliedOffset;
    static string KeyFor(string avatarId, string axis)
    {
        uint hash = 2166136261u;
        for (int i = 0; i < avatarId.Length; i++)
        {
            hash ^= avatarId[i];
            hash *= 16777619u;
        }
        return $"avatareye{axis}::{hash:X8}";
    }
    static Vector2 Load(string avatarId)
    {
        string h = KeyFor(avatarId, "h"), f = KeyFor(avatarId, "f");
        float height = BasisSettingsSystem.HasSaveData(h) ? BasisSettingsSystem.LoadFloat(h, 0f) : 0f;
        float forward = BasisSettingsSystem.HasSaveData(f) ? BasisSettingsSystem.LoadFloat(f, 0f) : 0f;
        return Sanitize(new Vector2(height, forward));
    }
    static Vector2 Sanitize(Vector2 offset)
    {
        float h = float.IsNaN(offset.x) || float.IsInfinity(offset.x) ? 0f : Mathf.Clamp(offset.x, MinHeight, MaxHeight);
        float f = float.IsNaN(offset.y) || float.IsInfinity(offset.y) ? 0f : Mathf.Clamp(offset.y, MinForward, MaxForward);
        return new Vector2(h, f);
    }
    public static bool CanEditCurrent()
    {
        BasisLocalPlayer player = BasisLocalPlayer.Instance;
        return player != null && player.BasisAvatar != null && ReferenceEquals(appliedTo, player.BasisAvatar);
    }
    public static void ApplyOnLoad(BasisAvatar avatar, bool isFallback)
    {
        appliedTo = null;
        appliedOffset = Vector2.zero;
        Current = Vector2.zero;
        string avatarId = BasisLocalPlayer.CurrentAvatarUniqueID;
        if (avatar == null || isFallback || string.IsNullOrEmpty(avatarId))
        {
            return;
        }
        Current = Load(avatarId);
        avatar.AvatarEyePosition += Current;
        appliedTo = avatar;
        appliedOffset = Current;
        if (Current != Vector2.zero)
        {
            BasisDebug.Log($"Per-avatar eye offset: height {Current.x * 100f:F1} cm, forward {Current.y * 100f:F1} cm", BasisDebug.LogTag.Avatar);
        }
    }
    public static void SetForCurrentAvatar(Vector2 offset)
    {
        string avatarId = BasisLocalPlayer.CurrentAvatarUniqueID;
        BasisLocalPlayer player = BasisLocalPlayer.Instance;
        if (string.IsNullOrEmpty(avatarId) || !CanEditCurrent())
        {
            return;
        }
        offset = Sanitize(offset);
        Current = offset;
        BasisSettingsSystem.SaveFloat(KeyFor(avatarId, "h"), offset.x);
        BasisSettingsSystem.SaveFloat(KeyFor(avatarId, "f"), offset.y);
        Vector2 delta = offset - appliedOffset;
        if (delta == Vector2.zero)
        {
            return;
        }
        appliedOffset = offset;
        player.BasisAvatar.AvatarEyePosition += delta;
        Vector3 shift = new Vector3(0f, delta.x, delta.y);
        var boneDriver = player.LocalBoneDriver;
        if (boneDriver != null && boneDriver.FindBone(out BasisLocalBoneControl eye, BasisBoneTrackedRole.CenterEye))
        {
            eye.SetTposeLocal(eye.TposeLocal.position + shift, eye.TposeLocal.rotation);
            if (boneDriver.FindBone(out BasisLocalBoneControl head, BasisBoneTrackedRole.Head) && head.TargetIndex == eye.Index)
            {
                head.Offset -= (Unity.Mathematics.float3)shift;
            }
        }
        BasisHeightDriver.CaptureAvatarHeightDuringTpose();
        BasisHeightDriver.ApplyScaleAndHeight();
    }
    public static void ClearForCurrentAvatar() => SetForCurrentAvatar(Vector2.zero);
}
