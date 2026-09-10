using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;
public static class BasisCamera360
{
    private const int MaxFaceSize = 4096;
    public struct EffectState
    {
        public bool Bloom, DepthOfField, Colour, MotionBlur;
        public TonemappingMode Tonemapping;
    }
    public static int FaceSize(int requested, int perEyeWidth) => Mathf.Min(requested > 0 ? requested : Mathf.NextPowerOfTwo(Mathf.Max(16, perEyeWidth / 2)), MaxFaceSize);
    public static void Bake(BasisHandHeldCameraMetaData meta, out float exposure, out float contrast, out float saturation)
    {
        exposure = contrast = saturation = 1f;
        ColorAdjustments colour = meta.colorAdjustments;
        if (colour == null) return;

        exposure = Mathf.Pow(2f, colour.postExposure.value);
        contrast = 1f + colour.contrast.value / 100f;
        saturation = 1f + colour.saturation.value / 100f;
    }
    public static EffectState SuspendEffects(BasisHandHeldCameraMetaData meta)
    {
        EffectState state = new EffectState
        {
            Bloom = meta.bloom != null && meta.bloom.active,
            DepthOfField = meta.depthOfField != null && meta.depthOfField.active,
            Colour = meta.colorAdjustments != null && meta.colorAdjustments.active,
            MotionBlur = meta.motionBlur != null && meta.motionBlur.active,
            Tonemapping = meta.tonemapping != null ? meta.tonemapping.mode.value : TonemappingMode.None,
        };

        if (meta.bloom != null) meta.bloom.active = false;
        if (meta.depthOfField != null) meta.depthOfField.active = false;
        if (meta.colorAdjustments != null) meta.colorAdjustments.active = false;
        if (meta.motionBlur != null) meta.motionBlur.active = false;
        if (meta.tonemapping != null) meta.tonemapping.mode.value = TonemappingMode.None;
        return state;
    }
    public static void RestoreEffects(BasisHandHeldCameraMetaData meta, in EffectState state)
    {
        if (meta.bloom != null) meta.bloom.active = state.Bloom;
        if (meta.depthOfField != null) meta.depthOfField.active = state.DepthOfField;
        if (meta.colorAdjustments != null) meta.colorAdjustments.active = state.Colour;
        if (meta.motionBlur != null) meta.motionBlur.active = state.MotionBlur;
        if (meta.tonemapping != null) meta.tonemapping.mode.value = state.Tonemapping;
    }
    public static void TagPano(BasisHandHeldCameraPhotoMetadata.PhotoMetadata metadata, bool stereo, int perEyeWidth, int fullHeight, float headingDegrees)
    {
        if (metadata == null) return;

        metadata.HasPano = true;
        metadata.PanoStereoTopBottom = stereo;
        metadata.PanoFullWidth = perEyeWidth;
        metadata.PanoFullHeight = fullHeight;
        metadata.PanoPerEyeHeight = perEyeWidth / 2;
        metadata.PanoHeadingDegrees = headingDegrees;
    }
    public static bool IsBlack(byte[] raw)
    {
        for (int i = 0; i < raw.Length; i += 3988)
        {
            if (raw[i] != 0) return false;
        }
        return true;
    }
    public static byte[] Tonemap(byte[] linearFloatRgba, int width, int height, float exposure, float contrast, float saturation)
    {
        int pixelCount = width * height;
        byte[] output = new byte[pixelCount * 4];

        for (int p = 0; p < pixelCount; p++)
        {
            int fi = p * 16;
            float r = BitConverter.ToSingle(linearFloatRgba, fi) * exposure, g = BitConverter.ToSingle(linearFloatRgba, fi + 4) * exposure;
            float b = BitConverter.ToSingle(linearFloatRgba, fi + 8) * exposure;

            AcesFitted(ref r, ref g, ref b);

            r = (r - 0.5f) * contrast + 0.5f;
            g = (g - 0.5f) * contrast + 0.5f;
            b = (b - 0.5f) * contrast + 0.5f;

            float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            r = luma + (r - luma) * saturation;
            g = luma + (g - luma) * saturation;
            b = luma + (b - luma) * saturation;

            int oi = p * 4;
            output[oi] = ToSrgbByte(r);
            output[oi + 1] = ToSrgbByte(g);
            output[oi + 2] = ToSrgbByte(b);
            output[oi + 3] = 255;
        }

        return output;
    }
    private static void AcesFitted(ref float r, ref float g, ref float b)
    {
        float ir = RRTAndODTFit(0.59719f * r + 0.35458f * g + 0.04823f * b), ig = RRTAndODTFit(0.07600f * r + 0.90834f * g + 0.01566f * b);
        float ib = RRTAndODTFit(0.02840f * r + 0.13383f * g + 0.83777f * b);

        r = Mathf.Clamp01(1.60475f * ir - 0.53108f * ig - 0.07367f * ib);
        g = Mathf.Clamp01(-0.10208f * ir + 1.10813f * ig - 0.00605f * ib);
        b = Mathf.Clamp01(-0.00327f * ir - 0.07276f * ig + 1.07602f * ib);
    }
    private static float RRTAndODTFit(float v) => (v * (v + 0.0245786f) - 0.000090537f) / (v * (0.983729f * v + 0.432951f) + 0.238081f);
    private static byte ToSrgbByte(float linear)
    {
        if (linear <= 0f) return 0;
        if (linear >= 1f) return 255;
        float srgb = linear <= 0.0031308f ? linear * 12.92f : 1.055f * Mathf.Pow(linear, 1f / 2.4f) - 0.055f;
        int value = (int)(srgb * 255f + 0.5f);
        return (byte)(value < 0 ? 0 : (value > 255 ? 255 : value));
    }
}
