using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The volumetric fog renderer feature.
/// </summary>
[Tooltip("Adds support to render volumetric fog.")]
[DisallowMultipleRendererFeature("Volumetric Fog")]
public sealed class VolumetricFogRendererFeature : ScriptableRendererFeature
{
	#region Private Attributes

	[HideInInspector]
	[SerializeField] private Shader downsampleDepthShader;
	[HideInInspector]
	[SerializeField] private Shader volumetricFogShader;

	[Tooltip("Blue-noise texture used for raymarch jitter, giving less visible banding at the same step count. A small (e.g. 64x64 or 128x128) single-channel blue-noise texture works well. Auto-resolved in the editor if left empty.")]
	[SerializeField] private Texture2D blueNoiseTexture;

	[Header("Baked APV")]
	[Tooltip("Compute shader that bakes APV irradiance into the world-space 3D texture. Auto-resolved in the editor if left empty.")]
	[SerializeField] private ComputeShader bakeAPVComputeShader;
	[Tooltip("World-space center of the baked APV volume. Cover the area the fog is visible in.")]
	[SerializeField] private Vector3 bakeBoundsCenter = Vector3.zero;
	[Tooltip("World-space size of the baked APV volume.")]
	[SerializeField] private Vector3 bakeBoundsSize = new Vector3(128.0f, 32.0f, 128.0f);
	[Tooltip("Approximate world size of one baked voxel, in meters. Fog is low-frequency, so 0.5-2 m is usually plenty. Smaller = sharper and more VRAM.")]
	[SerializeField] private float bakeVoxelSize = 1.0f;
	[Tooltip("Hard cap on baked volume resolution per axis (VRAM / perf safety).")]
	[SerializeField] private int bakeMaxResolution = 256;
	[Tooltip("Optional: a Texture3D produced by the editor baker. When assigned it ships with the renderer and is used directly, skipping the runtime bake. It must have been baked with the bounds above. Leave empty to bake at runtime from the world's APV.")]
	[SerializeField] private Texture3D bakedAPVVolumeAsset;
	[Tooltip("Experimental: schedule the APV bake on the GPU's async compute queue (DX12/Vulkan/Metal only; auto-falls back to sync where unsupported). The fog draw that reads the result is not render-graph-tracked, so no sync fence is inserted; expect possible brief fog flicker while the volume is still settling on world load. Default off.")]
	[SerializeField] private bool bakeUseAsyncCompute = false;

	[Header("Froxel Volume")]
	[Tooltip("Compute shader that lights and integrates the camera-aligned froxel volume used when VolumetricFogQuality.FroxelVolume is on. Auto-resolved in the editor if left empty.")]
	[SerializeField] private ComputeShader froxelComputeShader;
	[Tooltip("Compute shader that prepares the froxel columns: furthest opaque depth, column ray and sun constant. Auto-resolved in the editor if left empty.")]
	[SerializeField] private ComputeShader froxelColumnComputeShader;

	private Material downsampleDepthMaterial;
	private Material volumetricFogMaterial;

	private VolumetricFogRenderPass volumetricFogRenderPass;

	private VolumetricFogAPVBakePass volumetricFogAPVBakePass;
	private RenderTexture bakeRenderTexture;
	private RTHandle bakeRTHandle;
	private Vector3Int bakeResolution;
	private int bakeKernelIndex = -1;
	private Vector3Int failedBakeResolution;

	#endregion

	#region Scriptable Renderer Feature Methods

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	public override void Create()
	{
		ValidateResourcesForVolumetricFogRenderPass(true);

		volumetricFogRenderPass = new VolumetricFogRenderPass(downsampleDepthMaterial, volumetricFogMaterial, VolumetricFogRenderPass.DefaultRenderPassEvent);

		volumetricFogAPVBakePass = new VolumetricFogAPVBakePass();
		ResolveBakeComputeShader();
		ResolveFroxelComputeShader();
		volumetricFogRenderPass.SetFroxelComputeShaders(froxelComputeShader, froxelColumnComputeShader);
	}

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	/// <param name="renderer"></param>
	/// <param name="renderingData"></param>
	public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
	{
		VolumetricFogVolumeComponent fogVolume = ResolveFogVolume(renderingData.cameraData.camera);
		TryEnqueueBakePass(renderer, renderingData.cameraData.cameraType, fogVolume);
		bool isPostProcessEnabled = renderingData.postProcessingEnabled && renderingData.cameraData.postProcessEnabled;
		bool shouldAddVolumetricFogRenderPass = isPostProcessEnabled
			&& ShouldAddVolumetricFogRenderPass(renderingData.cameraData.cameraType, fogVolume);
		
		if (shouldAddVolumetricFogRenderPass)
		{
			volumetricFogRenderPass.fogVolume = fogVolume;
			volumetricFogRenderPass.blueNoiseTexture = blueNoiseTexture;
			volumetricFogRenderPass.renderPassEvent = (RenderPassEvent)fogVolume.renderPassEvent.value;
			volumetricFogRenderPass.ConfigureInput(ScriptableRenderPassInput.Depth);
			renderer.EnqueuePass(volumetricFogRenderPass);
		}
	}

	/// <summary>
	/// <inheritdoc/>
	/// </summary>
	/// <param name="disposing"></param>
	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);

		VolumetricFogAPVBaker.ReleaseApvStreamingForce();
		volumetricFogRenderPass?.Dispose();
		volumetricFogAPVBakePass = null;
		ReleaseBakeRenderTexture();

		CoreUtils.Destroy(downsampleDepthMaterial);
		CoreUtils.Destroy(volumetricFogMaterial);
	}

	#endregion

	#region Methods

	/// <summary>
	/// Validates the resources used by the volumetric fog render pass.
	/// </summary>
	/// <param name="forceRefresh"></param>
	/// <returns></returns>
	private bool ValidateResourcesForVolumetricFogRenderPass(bool forceRefresh)
	{
		if (forceRefresh)
		{
#if UNITY_EDITOR
			downsampleDepthShader = Shader.Find("Hidden/DownsampleDepth");
			volumetricFogShader = Shader.Find("Hidden/VolumetricFog");

			if (blueNoiseTexture == null)
			{
				string[] blueNoiseGuids = UnityEditor.AssetDatabase.FindAssets("VolumetricFogBlueNoise t:Texture2D");
				if (blueNoiseGuids.Length > 0)
					blueNoiseTexture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(UnityEditor.AssetDatabase.GUIDToAssetPath(blueNoiseGuids[0]));
			}
#endif
			CoreUtils.Destroy(downsampleDepthMaterial);
			downsampleDepthMaterial = CoreUtils.CreateEngineMaterial(downsampleDepthShader);

			CoreUtils.Destroy(volumetricFogMaterial);
			volumetricFogMaterial = CoreUtils.CreateEngineMaterial(volumetricFogShader);
		}

		bool okDepth = downsampleDepthShader != null && downsampleDepthMaterial != null;
		bool okVolumetric = volumetricFogShader != null && volumetricFogMaterial != null;
		
		return okDepth && okVolumetric;
	}

	/// <summary>
	/// Gets whether the volumetric fog render pass should be enqueued to the renderer.
	/// </summary>
	/// <param name="cameraType"></param>
	/// <returns></returns>
	private bool ShouldAddVolumetricFogRenderPass(CameraType cameraType, VolumetricFogVolumeComponent fogVolume)
	{
		bool isVolumeOk = fogVolume != null && fogVolume.IsActive();
		bool isCameraOk = cameraType != CameraType.Preview && cameraType != CameraType.Reflection;
		bool areResourcesOk = ValidateResourcesForVolumetricFogRenderPass(false);

		return isActive && isVolumeOk && isCameraOk && areResourcesOk;
	}

	private static VolumetricFogVolumeComponent ResolveFogVolume(Camera camera)
	{
		if (VolumetricFogCameraSource.TryGet(camera, out VolumetricFogCameraSource source))
		{
			return source.ResolveFogVolume();
		}

		return VolumeManager.instance.stack.GetComponent<VolumetricFogVolumeComponent>();
	}

	/// <summary>
	/// Resolves the bake compute shader and caches its kernel index.
	/// </summary>
	private void ResolveBakeComputeShader()
	{
#if UNITY_EDITOR
		if (bakeAPVComputeShader == null)
		{
			string[] guids = UnityEditor.AssetDatabase.FindAssets("BakeAPVFogVolume t:ComputeShader");
			if (guids.Length > 0)
				bakeAPVComputeShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]));
		}
#endif
		bakeKernelIndex = (bakeAPVComputeShader != null && bakeAPVComputeShader.HasKernel("CSBakeAPVFogVolume"))
			? bakeAPVComputeShader.FindKernel("CSBakeAPVFogVolume")
			: -1;
	}

	private void ResolveFroxelComputeShader()
	{
#if UNITY_EDITOR
		if (froxelComputeShader == null)
			froxelComputeShader = FindComputeShader("VolumetricFogFroxel");
		if (froxelColumnComputeShader == null)
			froxelColumnComputeShader = FindComputeShader("VolumetricFogFroxelColumns");
#endif
	}

#if UNITY_EDITOR
	private static ComputeShader FindComputeShader(string assetName)
	{
		string[] guids = UnityEditor.AssetDatabase.FindAssets(assetName + " t:ComputeShader");
		for (int i = 0; i < guids.Length; ++i)
		{
			ComputeShader shader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]));
			if (shader != null && shader.name == assetName)
				return shader;
		}
		return null;
	}
#endif

	/// <summary>
	/// If a rebake has been requested (or the baked volume is missing while a fog volume wants baked APV)
	/// and Unity's APV runtime holds data, ensures the target 3D texture exists, publishes it to the baker,
	/// and enqueues the one-shot bake pass for this frame.
	/// </summary>
	/// <param name="renderer"></param>
	/// <param name="cameraType"></param>
	private void TryEnqueueBakePass(ScriptableRenderer renderer, CameraType cameraType, VolumetricFogVolumeComponent fogVolume)
	{
		if (!isActive)
		{
			VolumetricFogAPVBaker.ReleaseApvStreamingForce();
			return;
		}

		// A shipped editor-baked Texture3D takes precedence: publish it (with the configured bounds) and
		// skip runtime baking entirely.
		if (bakedAPVVolumeAsset != null)
		{
			if (!ReferenceEquals(VolumetricFogAPVBaker.BakedVolume, bakedAPVVolumeAsset))
				VolumetricFogAPVBaker.SetBakedVolume(bakedAPVVolumeAsset, bakeBoundsCenter - bakeBoundsSize * 0.5f, bakeBoundsSize);
			VolumetricFogAPVBaker.ConsumeBakeRequest();
			return;
		}

		// Preview/reflection cameras never bake; don't touch global streaming state here - the real camera
		// owns the bake window.
		if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
			return;

		// Nothing to bake from until Unity's APV runtime is initialized with data. Hand cell streaming back
		// and leave any request pending so the bake happens once a world's APV registers.
		if (volumetricFogAPVBakePass == null || bakeKernelIndex < 0 || !ProbeReferenceVolume.instance.isInitialized)
		{
			VolumetricFogAPVBaker.ReleaseApvStreamingForce();
			return;
		}

		// Self-heal: domain reloads and feature teardowns (frequent in the editor) wipe the baker's static
		// state and release the bake target, and nothing outside world-load code re-requests a bake. When a
		// fog volume actively wants baked APV and no usable volume exists, arm a rebake ourselves.
		if (!VolumetricFogAPVBaker.BakeRequested && !VolumetricFogAPVBaker.IsReady
			&& ComputeBakeResolution() != failedBakeResolution && WantsBakedAPVContribution(fogVolume))
		{
			VolumetricFogAPVBaker.RequestRebake();
		}

		// One dispatch per rendered frame, shared across all cameras and feature instances; the claim also
		// forces APV streaming for the settle window and restores it when the window drains.
		if (!VolumetricFogAPVBaker.TryBeginBakeDispatch(Time.renderedFrameCount))
			return;

		Vector3Int resolution = ComputeBakeResolution();
		if (!EnsureBakeRenderTexture(resolution))
		{
			// Can't produce a target - abandon the window (and don't self-arm again at this resolution).
			failedBakeResolution = resolution;
			VolumetricFogAPVBaker.ConsumeBakeRequest();
			return;
		}
		failedBakeResolution = default;

		Vector3 boundsMin = bakeBoundsCenter - bakeBoundsSize * 0.5f;

		volumetricFogAPVBakePass.computeShader = bakeAPVComputeShader;
		volumetricFogAPVBakePass.kernelIndex = bakeKernelIndex;
		volumetricFogAPVBakePass.targetRT = bakeRTHandle;
		volumetricFogAPVBakePass.boundsMin = boundsMin;
		volumetricFogAPVBakePass.boundsSize = bakeBoundsSize;
		volumetricFogAPVBakePass.resolution = resolution;
		volumetricFogAPVBakePass.useAsyncCompute = bakeUseAsyncCompute;

		// Publish now: the RT reference is valid, and the bake pass (enqueued this frame at
		// BeforeRenderingOpaques) fills it on the GPU before the fog pass samples it. The volume converges to
		// the full world over the settle window as more cells stream in each frame.
		VolumetricFogAPVBaker.SetBakedVolume(bakeRenderTexture, boundsMin, bakeBoundsSize);

		renderer.EnqueuePass(volumetricFogAPVBakePass);
	}

	/// <summary>
	/// Whether the currently blended fog volume wants the baked APV contribution.
	/// </summary>
	/// <returns></returns>
	private static bool WantsBakedAPVContribution(VolumetricFogVolumeComponent fogVolume)
	{
		return fogVolume != null && fogVolume.IsActive() && fogVolume.enableAPVContribution.value
			&& fogVolume.APVContributionWeight.value > 0.0f && VolumetricFogQuality.APVMode == VolumetricFogAPVMode.Baked;
	}

	/// <summary>
	/// Computes the baked volume resolution from the configured bounds, voxel size, and per-axis cap.
	/// </summary>
	/// <returns></returns>
	private Vector3Int ComputeBakeResolution()
	{
		float voxel = Mathf.Max(0.01f, bakeVoxelSize);
		int maxRes = Mathf.Max(1, bakeMaxResolution);

		return new Vector3Int(
			Mathf.Clamp(Mathf.CeilToInt(bakeBoundsSize.x / voxel), 1, maxRes),
			Mathf.Clamp(Mathf.CeilToInt(bakeBoundsSize.y / voxel), 1, maxRes),
			Mathf.Clamp(Mathf.CeilToInt(bakeBoundsSize.z / voxel), 1, maxRes));
	}

	/// <summary>
	/// Ensures the persistent 3D bake render texture exists at the given resolution, recreating it on a
	/// resolution change. Returns false if creation failed.
	/// </summary>
	/// <param name="resolution"></param>
	/// <returns></returns>
	private bool EnsureBakeRenderTexture(Vector3Int resolution)
	{
		if (bakeRenderTexture != null && bakeResolution == resolution && bakeRenderTexture.IsCreated())
			return true;

		ReleaseBakeRenderTexture();

		// RGBA16F is universally UAV-writable on DX11 and Vulkan (unlike packed formats such as
		// R11G11B10F, whose typed UAV store isn't guaranteed on DX11).
		bakeRenderTexture = new RenderTexture(resolution.x, resolution.y, 0, GraphicsFormat.R16G16B16A16_SFloat)
		{
			name = "_BakedAPVFogVolume",
			dimension = TextureDimension.Tex3D,
			volumeDepth = resolution.z,
			enableRandomWrite = true,
			wrapMode = TextureWrapMode.Clamp,
			filterMode = FilterMode.Bilinear,
			useMipMap = false
		};

		if (!bakeRenderTexture.Create())
		{
			ReleaseBakeRenderTexture();
			return false;
		}

		bakeRTHandle = RTHandles.Alloc(bakeRenderTexture);
		bakeResolution = resolution;
		return true;
	}

	/// <summary>
	/// Releases the persistent bake render texture and clears it from the baker if it was published.
	/// </summary>
	private void ReleaseBakeRenderTexture()
	{
		if (bakeRenderTexture != null && ReferenceEquals(VolumetricFogAPVBaker.BakedVolume, bakeRenderTexture))
			VolumetricFogAPVBaker.Clear();

		bakeRTHandle?.Release();
		bakeRTHandle = null;

		if (bakeRenderTexture != null)
		{
			bakeRenderTexture.Release();
			CoreUtils.Destroy(bakeRenderTexture);
			bakeRenderTexture = null;
		}

		bakeResolution = Vector3Int.zero;
	}

	#endregion
}