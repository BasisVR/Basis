using UnityEngine;

public static class VolumetricFogQuality
{
	public const int MinSteps = 8;
	public const int MaxStepsLimit = 256;
	public const int MaxBlurIterations = 4;
	public const int MinFroxelDivisor = 2;
	public const int MaxFroxelDivisor = 32;
	public const int MinFroxelSlices = 8;
	public const int MaxFroxelSlices = 256;

	private static int maxSteps = 128;
	private static int blurIterations = 1;
	private static int froxelDivisor = 8;
	private static int froxelSlices = 64;
	private static float temporalFeedback = 0.9f;

	public static VolumetricFogResolution Resolution = VolumetricFogResolution.Half;
	public static VolumetricFogAPVMode APVMode = VolumetricFogAPVMode.Live;
	public static bool TemporalReprojection = true;
	public static bool FroxelVolume = true;
	public static bool AnalyticOpticalDepth = true;
	public static bool ScaleStepsWithResolution = true;
	public static bool SunPathTrims = true;
	public static bool FroxelDepthCulling = true;
	public static bool FroxelHalfRateUpdate = true;
	public static bool SharedStereoVolume = true;
	public static bool TemporalLightResponse = true;

	public static int MaxSteps
	{
		get => maxSteps;
		set => maxSteps = Mathf.Clamp(value, MinSteps, MaxStepsLimit);
	}

	public static int BlurIterations
	{
		get => blurIterations;
		set => blurIterations = Mathf.Clamp(value, 0, MaxBlurIterations);
	}

	public static int FroxelDivisor
	{
		get => froxelDivisor;
		set => froxelDivisor = Mathf.Clamp(value, MinFroxelDivisor, MaxFroxelDivisor);
	}

	public static float TemporalFeedback
	{
		get => temporalFeedback;
		set => temporalFeedback = Mathf.Clamp(value, 0.0f, 0.98f);
	}

	public static int FroxelSlices
	{
		get => froxelSlices;
		set => froxelSlices = Mathf.Clamp(value, MinFroxelSlices, MaxFroxelSlices);
	}
}
