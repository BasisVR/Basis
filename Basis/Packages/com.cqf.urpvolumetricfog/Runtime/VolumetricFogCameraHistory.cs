using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public sealed class VolumetricFogCameraHistory
{
	public readonly RTHandle[] fogHistory = new RTHandle[2];
	public readonly RTHandle[] depthHistory = new RTHandle[2];
	public readonly RenderTexture[] froxelLighting = new RenderTexture[2];
	public readonly RTHandle[] froxelLightingHandle = new RTHandle[2];
	public readonly Matrix4x4[] viewProjection = new Matrix4x4[2];
	public readonly Matrix4x4[] previousViewProjection = new Matrix4x4[2];
	public readonly Matrix4x4[] inverseViewProjection = new Matrix4x4[2];
	public readonly Matrix4x4[] eyeViewProjection = new Matrix4x4[2];
	public readonly Vector4[] cameraPosition = new Vector4[2];
	public int fogWriteIndex;
	public int froxelWriteIndex;
	public int lastFrame = -1;
	public bool fogHistoryValid;
	public bool froxelHistoryValid;
	public Vector3Int froxelSize;

	public bool EnsureFogHistory(RenderTextureDescriptor descriptor)
	{
		RenderTextureDescriptor depthDescriptor = descriptor;
		depthDescriptor.graphicsFormat = GraphicsFormat.R32_SFloat;
		bool reallocated = false;
		for (int i = 0; i < 2; ++i)
		{
			reallocated |= RenderingUtils.ReAllocateHandleIfNeeded(ref fogHistory[i], descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_VolumetricFogHistory" + i);
			reallocated |= RenderingUtils.ReAllocateHandleIfNeeded(ref depthHistory[i], depthDescriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_VolumetricFogDepthHistory" + i);
		}
		if (reallocated)
			fogHistoryValid = false;
		return reallocated;
	}

	public bool EnsureFroxelHistory(Vector3Int size)
	{
		if (froxelSize == size && froxelLighting[0] != null && froxelLighting[0].IsCreated() && froxelLighting[1] != null && froxelLighting[1].IsCreated())
			return true;

		ReleaseFroxelHistory();
		for (int i = 0; i < 2; ++i)
		{
			froxelLighting[i] = new RenderTexture(size.x, size.y, 0, GraphicsFormat.R16G16B16A16_SFloat)
			{
				name = "_VolumetricFogFroxelHistory" + i,
				dimension = TextureDimension.Tex3D,
				volumeDepth = size.z,
				enableRandomWrite = true,
				wrapMode = TextureWrapMode.Clamp,
				filterMode = FilterMode.Bilinear,
				useMipMap = false
			};
			if (!froxelLighting[i].Create())
			{
				ReleaseFroxelHistory();
				return false;
			}
			froxelLightingHandle[i] = RTHandles.Alloc(froxelLighting[i]);
		}
		froxelSize = size;
		froxelHistoryValid = false;
		return true;
	}

	public void ReleaseFroxelHistory()
	{
		for (int i = 0; i < 2; ++i)
		{
			froxelLightingHandle[i]?.Release();
			froxelLightingHandle[i] = null;
			if (froxelLighting[i] != null)
			{
				froxelLighting[i].Release();
				CoreUtils.Destroy(froxelLighting[i]);
				froxelLighting[i] = null;
			}
		}
		froxelSize = Vector3Int.zero;
		froxelHistoryValid = false;
	}

	public void Release()
	{
		for (int i = 0; i < 2; ++i)
		{
			fogHistory[i]?.Release();
			fogHistory[i] = null;
			depthHistory[i]?.Release();
			depthHistory[i] = null;
		}
		fogHistoryValid = false;
		ReleaseFroxelHistory();
	}
}
