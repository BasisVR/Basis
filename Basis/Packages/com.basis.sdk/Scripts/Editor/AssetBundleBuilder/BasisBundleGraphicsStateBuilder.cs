using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>
/// Constructs graphics states from one piece of content's meshes/materials during BEE generation
/// and appends the resulting encrypted files to that content's platform section.
/// </summary>
public static class BasisBundleGraphicsStateBuilder
{
    public sealed class BuildInputs
    {
        public readonly List<Mesh> Meshes = new List<Mesh>();
        public readonly List<Material> Materials = new List<Material>();
        private readonly HashSet<(Mesh, Material)> seen = new HashSet<(Mesh, Material)>();

        public void Add(Mesh mesh, Material material)
        {
            if (mesh == null || material == null || material.shader == null) return;
            if (!seen.Add((mesh, material))) return;
            Meshes.Add(mesh);
            Materials.Add(material);
        }
    }

    public static BuildInputs CollectInputs(GameObject root)
    {
        BuildInputs inputs = new BuildInputs();
        if (root == null) return inputs;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer skinned)
                mesh = skinned.sharedMesh;
            else if (renderer.TryGetComponent(out MeshFilter filter))
                mesh = filter.sharedMesh;
            if (mesh == null) continue;

            Material[] materials = renderer.sharedMaterials;
            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                inputs.Add(mesh, materials[materialIndex]);
        }
        return inputs;
    }

    public static BuildInputs CollectInputs(UnityEngine.SceneManagement.Scene scene)
    {
        BuildInputs result = new BuildInputs();
        if (!scene.IsValid()) return result;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            BuildInputs rootInputs = CollectInputs(root);
            for (int i = 0; i < rootInputs.Meshes.Count; i++)
                result.Add(rootInputs.Meshes[i], rootInputs.Materials[i]);
        }
        return result;
    }

    public static async Task<BasisGraphicsStatePayload[]> AppendPayloads(
        string encryptedBundlePath,
        string password,
        BuildTarget target,
        BuildInputs inputs)
    {
        if (string.IsNullOrEmpty(encryptedBundlePath) || inputs == null || inputs.Meshes.Count == 0)
            return Array.Empty<BasisGraphicsStatePayload>();

        GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(target);
        List<BasisGraphicsStatePayload> payloads = new List<BasisGraphicsStatePayload>(2);
        if (!TryRuntimePlatform(target, out RuntimePlatform targetPlatform))
            return payloads.ToArray();
        Mesh[] meshes = inputs.Meshes.ToArray();
        Material[] materials = inputs.Materials.ToArray();
        for (int i = 0; i < apis.Length; i++)
        {
            GraphicsDeviceType api = apis[i];
            if (api != GraphicsDeviceType.Direct3D12 && api != GraphicsDeviceType.Vulkan) continue;
            string[] qualityNames = QualitySettings.names;
            for (int qualityIndex = 0; qualityIndex < qualityNames.Length; qualityIndex++)
            {
                string workDirectory = Path.GetDirectoryName(encryptedBundlePath);
                string suffix = $"{api}.{qualityIndex}";
                string plainPath = Path.Combine(workDirectory, $"{Path.GetFileNameWithoutExtension(encryptedBundlePath)}.{suffix}.graphicsstate");
                string encryptedPath = plainPath + ".encrypted";
                GraphicsStateCollection collection = null;
                try
                {
                    collection = new GraphicsStateCollection
                    {
                        graphicsDeviceType = api,
                        runtimePlatform = targetPlatform,
                        qualityLevelName = qualityNames[qualityIndex]
                    };
                    GenerateStates(collection, meshes, materials);
                    if (collection.variantCount == 0 || collection.totalGraphicsStateCount == 0) continue;
                    collection.SaveToFile(plainPath);
                    if (!File.Exists(plainPath) || new FileInfo(plainPath).Length == 0) continue;

                    var basisPassword = new BasisEncryptionWrapper.BasisPassword { VP = password };
                    await BasisEncryptionWrapper.EncryptFileAsync(
                        BasisGenerateUniqueID.GenerateUniqueID(), basisPassword, plainPath, encryptedPath, new BasisProgressReport());

                    long offset = new FileInfo(encryptedBundlePath).Length;
                    long length = new FileInfo(encryptedPath).Length;
                    using (FileStream destination = new FileStream(encryptedBundlePath, FileMode.Append, FileAccess.Write, FileShare.None))
                    using (FileStream input = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        await input.CopyToAsync(destination);
                    payloads.Add(new BasisGraphicsStatePayload
                    {
                        GraphicsAPI = api.ToString(),
                        RuntimePlatform = targetPlatform.ToString(),
                        QualityLevelName = qualityNames[qualityIndex],
                        Offset = offset,
                        Length = length,
                        VariantCount = collection.variantCount
                    });
                    BasisDebug.Log($"BEE PSO: generated and embedded {collection.variantCount} variant(s) / {collection.totalGraphicsStateCount} state(s) for {targetPlatform}/{api}/{qualityNames[qualityIndex]}, {length / 1024} KB encrypted.");
                }
                catch (Exception ex)
                {
                    // PSO acceleration is optional; generation must never make content unbuildable.
                    BasisDebug.LogWarning($"BEE PSO: could not generate/embed {api} states ({ex.Message}).");
                }
                finally
                {
                    if (collection != null) UnityEngine.Object.DestroyImmediate(collection);
                    if (File.Exists(plainPath)) File.Delete(plainPath);
                    if (File.Exists(encryptedPath)) File.Delete(encryptedPath);
                }
            }
        }
        return payloads.ToArray();
    }

    private static void GenerateStates(GraphicsStateCollection collection, Mesh[] meshes, Material[] materials)
    {
        GraphicsFormat[] colorFormats =
        {
            GraphicsFormat.R8G8B8A8_SRGB,
            GraphicsFormat.B8G8R8A8_SRGB,
            GraphicsFormat.R16G16B16A16_SFloat
        };
        int[] samples = { 1, 2, 4, 8 };
        for (int formatIndex = 0; formatIndex < colorFormats.Length; formatIndex++)
        {
            for (int sampleIndex = 0; sampleIndex < samples.Length; sampleIndex++)
            {
                NativeArray<AttachmentDescriptor> attachments = new NativeArray<AttachmentDescriptor>(2, Allocator.Temp);
                NativeArray<SubPassDescriptor> subpasses = new NativeArray<SubPassDescriptor>(1, Allocator.Temp);
                try
                {
                    attachments[0] = new AttachmentDescriptor(colorFormats[formatIndex]);
                    attachments[1] = new AttachmentDescriptor(GraphicsFormat.D24_UNorm_S8_UInt);
                    subpasses[0] = new SubPassDescriptor
                    {
                        colorOutputs = new AttachmentIndexArray(new[] { 0 })
                    };
                    collection.AddGraphicsStates(meshes, materials, samples[sampleIndex], attachments, subpasses, 0, 1, -1);
                }
                finally
                {
                    subpasses.Dispose();
                    attachments.Dispose();
                }
            }
        }
    }

    private static bool TryRuntimePlatform(BuildTarget target, out RuntimePlatform platform)
    {
        switch (target)
        {
            case BuildTarget.StandaloneWindows:
            case BuildTarget.StandaloneWindows64: platform = RuntimePlatform.WindowsPlayer; return true;
            case BuildTarget.StandaloneOSX: platform = RuntimePlatform.OSXPlayer; return true;
            case BuildTarget.StandaloneLinux64: platform = RuntimePlatform.LinuxPlayer; return true;
            case BuildTarget.Android: platform = RuntimePlatform.Android; return true;
            case BuildTarget.iOS: platform = RuntimePlatform.IPhonePlayer; return true;
            case BuildTarget.WebGL: platform = RuntimePlatform.WebGLPlayer; return true;
            default: platform = default; return false;
        }
    }

}
