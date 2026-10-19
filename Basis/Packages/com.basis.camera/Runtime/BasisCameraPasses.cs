using System;
using System.IO;
using System.Text;
using Basis;
using Basis.BasisUI;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
public enum BasisCameraPassOutput
{
    Picture = 0,
    Depth = 1,
    Normals = 2,
    Mask = 3,
    Motion = 4,
    QuadView = 5,
}
public enum BasisCameraPass
{
    Depth = 0,
    Normals = 1,
    Mask = 2,
    Motion = 3,
}
[Flags]
public enum BasisCameraPassSet
{
    None = 0,
    Depth = 1,
    Normals = 2,
    Mask = 4,
    Motion = 8,
}
[NoAutoStaticsCleanup]
public static class BasisCameraPasses
{
    public const int Count = 4;
    public const float DefaultDepthRange = 10f, MinDepthRange = 0.5f, MaxDepthRange = 100f;
    public const string ShaderResource = "BasisCameraPasses", MaskShaderResource = "BasisCameraPassMask", ComposeShaderResource = "BasisCameraPassCompose";
    public const BasisCameraPassSet QuadSet = BasisCameraPassSet.Depth | BasisCameraPassSet.Normals | BasisCameraPassSet.Mask;
    public const BasisCameraPassSet AllSet = QuadSet | BasisCameraPassSet.Motion;
    public const BasisCameraPassSet DefaultSeparateSet = QuadSet;
    public static readonly string[] OutputKeys = { "camera.passes.output.picture", "camera.passes.output.depth", "camera.passes.output.normals", "camera.passes.output.mask", "camera.passes.output.motion", "camera.passes.output.quad" };
    public static readonly string[] PassKeys = { "camera.passes.depth", "camera.passes.normals", "camera.passes.mask", "camera.passes.motion" };
    public static readonly string[] Suffixes = { "Depth", "Normals", "Mask", "Motion" };
    public static readonly BasisCameraPass[] QuadPasses = { BasisCameraPass.Depth, BasisCameraPass.Normals, BasisCameraPass.Mask };
    public static BasisCameraPassOutput SanitizeOutput(int output) => output >= 0 && output < OutputKeys.Length ? (BasisCameraPassOutput)output : BasisCameraPassOutput.Picture;
    public static float SanitizeDepthRange(float range) => float.IsNaN(range) || range <= 0f ? DefaultDepthRange : Mathf.Clamp(range, MinDepthRange, MaxDepthRange);
    public static BasisCameraPassSet Flag(BasisCameraPass pass) => (BasisCameraPassSet)(1 << (int)pass);
    public static bool Contains(BasisCameraPassSet set, BasisCameraPass pass) => (set & Flag(pass)) != 0;
    public static BasisCameraPassSet SetOf(bool depth, bool normals, bool mask, bool motion) => (depth ? BasisCameraPassSet.Depth : 0) | (normals ? BasisCameraPassSet.Normals : 0) | (mask ? BasisCameraPassSet.Mask : 0) | (motion ? BasisCameraPassSet.Motion : 0);
    public static bool TryGetPass(BasisCameraPassOutput output, out BasisCameraPass pass)
    {
        switch (output)
        {
            case BasisCameraPassOutput.Depth:
                pass = BasisCameraPass.Depth;
                return true;
            case BasisCameraPassOutput.Normals:
                pass = BasisCameraPass.Normals;
                return true;
            case BasisCameraPassOutput.Mask:
                pass = BasisCameraPass.Mask;
                return true;
            case BasisCameraPassOutput.Motion:
                pass = BasisCameraPass.Motion;
                return true;
            default:
                pass = BasisCameraPass.Depth;
                return false;
        }
    }
    public static BasisCameraPassSet For(BasisCameraPassOutput output)
    {
        if (output == BasisCameraPassOutput.QuadView) return QuadSet;
        return TryGetPass(output, out BasisCameraPass pass) ? Flag(pass) : BasisCameraPassSet.None;
    }
    public static ScriptableRenderPassInput InputFor(BasisCameraPassSet set)
    {
        ScriptableRenderPassInput input = ScriptableRenderPassInput.None;
        if ((set & (BasisCameraPassSet.Depth | BasisCameraPassSet.Normals | BasisCameraPassSet.Mask)) != 0) input |= ScriptableRenderPassInput.Depth;
        if ((set & BasisCameraPassSet.Normals) != 0) input |= ScriptableRenderPassInput.Normal;
        if ((set & BasisCameraPassSet.Motion) != 0) input |= ScriptableRenderPassInput.Motion;
        return input;
    }
    public static string SenderName(string baseName, BasisCameraPass pass) => (string.IsNullOrWhiteSpace(baseName) ? BasisVideoOutputSettings.DefaultSenderName : baseName) + " " + Suffixes[(int)pass];
    public static string FileName(string fileName, BasisCameraPass pass) => Path.GetFileNameWithoutExtension(fileName) + "_" + Suffixes[(int)pass] + Path.GetExtension(fileName);
    public static float DepthDisplay(float eyeDepth, float range) => 1f - Mathf.Clamp01(eyeDepth / SanitizeDepthRange(range));
    public static Color NormalDisplay(Vector3 viewNormal) => new Color(viewNormal.x * 0.5f + 0.5f, viewNormal.y * 0.5f + 0.5f, viewNormal.z * 0.5f + 0.5f, 1f);
    public static Matrix4x4 ViewRotation(Quaternion cameraRotation) => Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.Rotate(Quaternion.Inverse(cameraRotation));
    public static BasisCameraPassOutput QuadrantAt(Vector2 uv)
    {
        if (uv.y >= 0.5f) return uv.x < 0.5f ? BasisCameraPassOutput.Picture : BasisCameraPassOutput.Depth;
        return uv.x < 0.5f ? BasisCameraPassOutput.Normals : BasisCameraPassOutput.Mask;
    }
    public static Vector2 QuadrantUV(Vector2 uv) => new Vector2(uv.x < 0.5f ? uv.x * 2f : uv.x * 2f - 1f, uv.y < 0.5f ? uv.y * 2f : uv.y * 2f - 1f);
    public static RenderTextureFormat TargetFormat(bool data) => data ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32;
    public static bool SameSize(RenderTexture target, RenderTexture reference) => target != null && reference != null && target.width == reference.width && target.height == reference.height;
    public static bool Matches(RenderTexture target, RenderTexture reference, bool data) => SameSize(target, reference) && target.format == TargetFormat(data) && target.sRGB == !data && target.IsCreated();
    public static string Describe(BasisCameraPassSet set)
    {
        StringBuilder builder = new StringBuilder();
        for (int Index = 0; Index < Count; Index++)
        {
            if (!Contains(set, (BasisCameraPass)Index)) continue;
            if (builder.Length > 0) builder.Append(", ");
            builder.Append(BasisLocalization.Get(PassKeys[Index]));
        }
        return builder.Length > 0 ? builder.ToString() : BasisLocalization.Get("ui.option.off");
    }
}
