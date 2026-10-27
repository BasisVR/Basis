using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

public static class VolumetricFogExternalVolume
{
	public static readonly int VolumeId = Shader.PropertyToID("_VFExternalVolume");
	public static readonly int Params0Id = Shader.PropertyToID("_VFExternalVolumeParams0");
	public static readonly int Params1Id = Shader.PropertyToID("_VFExternalVolumeParams1");
	public static readonly int Params2Id = Shader.PropertyToID("_VFExternalVolumeParams2");
	public static readonly int NeutralId = Shader.PropertyToID("_VFExternalVolumeNeutral");

	public static bool Active => Shader.GetGlobalVector(Params0Id).w > 0.5f && Shader.GetGlobalTexture(VolumeId) != null;

	public static void Bind(Material material)
	{
		bool active = Active;
		material.SetTexture(VolumeId, active ? Shader.GetGlobalTexture(VolumeId) : CoreUtils.blackVolumeTexture);
		material.SetVector(Params0Id, active ? Shader.GetGlobalVector(Params0Id) : Vector4.zero);
		material.SetVector(Params1Id, active ? Shader.GetGlobalVector(Params1Id) : Vector4.zero);
		material.SetVector(Params2Id, active ? Shader.GetGlobalVector(Params2Id) : Vector4.zero);
		material.SetVector(NeutralId, active ? Shader.GetGlobalVector(NeutralId) : new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
	}

	public static void Bind(ComputeCommandBuffer cmd, ComputeShader computeShader, int kernelIndex)
	{
		bool active = Active;
		computeShader.SetTexture(kernelIndex, VolumeId, active ? Shader.GetGlobalTexture(VolumeId) : CoreUtils.blackVolumeTexture);
		cmd.SetComputeVectorParam(computeShader, Params0Id, active ? Shader.GetGlobalVector(Params0Id) : Vector4.zero);
		cmd.SetComputeVectorParam(computeShader, Params1Id, active ? Shader.GetGlobalVector(Params1Id) : Vector4.zero);
		cmd.SetComputeVectorParam(computeShader, Params2Id, active ? Shader.GetGlobalVector(Params2Id) : Vector4.zero);
		cmd.SetComputeVectorParam(computeShader, NeutralId, active ? Shader.GetGlobalVector(NeutralId) : new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
	}
}
