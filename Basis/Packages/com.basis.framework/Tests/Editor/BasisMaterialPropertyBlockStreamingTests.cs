using System.IO;
using Basis.Scripts.Drivers;
using Basis.Scripts.Rendering;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class BasisMaterialPropertyBlockStreamingTests
{
    [Test]
    public void DisabledStreamingPrunesDestroyedRenderersWithinTheScanBudget()
    {
        int previousBudget = QualitySettings.streamingMipmapsRenderersPerFrame;
        var root = new GameObject("Basis disabled streaming test");
        try
        {
            QualitySettings.streamingMipmapsRenderersPerFrame = 1;
            var service = root.AddComponent<BasisMaterialPropertyBlockStreaming>();
            var first = CreateQuad(root.transform, null, null, 0f);
            var second = CreateQuad(root.transform, null, null, 0f);
            service.TrackRenderers(new[] { first, second });
            Object.DestroyImmediate(first.gameObject);
            Object.DestroyImmediate(second.gameObject);

            service.ProcessFrame(1f, false);
            Assert.That(service.TrackedRendererCount, Is.EqualTo(1));

            var appended = CreateQuad(root.transform, null, null, 0f);
            service.TrackRenderers(new[] { appended });
            Object.DestroyImmediate(appended.gameObject);
            service.ProcessFrame(1f, false);
            Assert.That(service.TrackedRendererCount, Is.EqualTo(1), "New registrations stay beyond the active sweep.");
            service.ProcessFrame(2f, false);
            Assert.That(service.TrackedRendererCount, Is.Zero);
        }
        finally
        {
            Object.DestroyImmediate(root);
            QualitySettings.streamingMipmapsRenderersPerFrame = previousBudget;
        }
    }

    [Test]
    public void RegisteredMirrorUpgradesSharedTextureBeforeSweepFinishes()
    {
        if (!SystemInfo.supportsMipStreaming) Assert.Ignore("Requires Unity mip streaming support.");

        string path = AssetDatabase.GenerateUniqueAssetPath("Assets/BasisStreamingTest.png");
        int previousBudget = QualitySettings.streamingMipmapsRenderersPerFrame;
        bool previousAllCameras = QualitySettings.streamingMipmapsAddAllCameras;
        bool previousStreaming = QualitySettings.streamingMipmapsActive;
        int previousReduction = QualitySettings.streamingMipmapsMaxLevelReduction;
        Camera previousMain = BasisLocalCameraDriver.CameraInstance;
        var root = new GameObject("Basis streaming test");
        var target = new RenderTexture(512, 512, 0);
        Material material = null;
        Camera mirror = null;
        Texture2D texture = null;
        try
        {
            var pixels = new Texture2D(512, 512);
            try { File.WriteAllBytes(path, pixels.EncodeToPNG()); }
            finally { Object.DestroyImmediate(pixels); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.mipmapEnabled = true;
            importer.streamingMipmaps = true;
            importer.SaveAndReimport();
            texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.That(texture.streamingMipmaps, Is.True);

            QualitySettings.streamingMipmapsActive = true;
            QualitySettings.streamingMipmapsAddAllCameras = true;
            QualitySettings.streamingMipmapsRenderersPerFrame = 1;
            QualitySettings.streamingMipmapsMaxLevelReduction = 4;
            texture.ClearRequestedMipmapLevel();
            int initialRequest = texture.requestedMipmapLevel;
            Camera main = new GameObject("Main").AddComponent<Camera>();
            main.transform.SetParent(root.transform);
            main.targetTexture = target;
            BasisLocalCameraDriver.CameraInstance = main;
            mirror = new GameObject("Manual reflection").AddComponent<Camera>();
            mirror.transform.SetParent(root.transform);
            mirror.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            mirror.targetTexture = target;
            mirror.enabled = false;
            BasisCullingCameraRegistry.Register(mirror);

            var shader = Shader.Find("Unlit/Texture");
            Assert.That(shader, Is.Not.Null);
            material = new Material(shader);
            var block = new MaterialPropertyBlock();
            block.SetTexture("_MainTex", texture);
            var far = CreateQuad(root.transform, material, block, -30f);
            var near = CreateQuad(root.transform, material, block, -0.5f);
            var pending = CreateQuad(root.transform, material, null, 5f);
            var service = root.AddComponent<BasisMaterialPropertyBlockStreaming>();
            service.TrackRenderers(new[] { far, near, pending });
            service.ProcessFrame(1f, true);
            Assert.That(texture.requestedMipmapLevel, Is.GreaterThan(0), "The disabled, registered camera sees the distant override.");
            service.ProcessFrame(1f, true);
            Assert.That(texture.requestedMipmapLevel, Is.Zero, "Near demand takes effect before the final renderer is visited.");
            service.ProcessFrame(1f, true);

            Object.DestroyImmediate(near.gameObject);
            service.ProcessFrame(2f, true);
            Assert.That(texture.requestedMipmapLevel, Is.Zero, "A partial sweep retains the earlier detail.");
            service.ProcessFrame(2f, true);
            service.ProcessFrame(2f, true);
            Assert.That(texture.requestedMipmapLevel, Is.GreaterThan(0), "The surviving renderer still requests detail.");

            far.SetPropertyBlock(null);
            service.ProcessFrame(3f, true);
            service.ProcessFrame(3f, true);
            Assert.That(texture.requestedMipmapLevel, Is.EqualTo(initialRequest), "Removing the last override restores the request that preceded the sweep.");

            // A request that the service never needed to write belongs to its original caller.
            texture.requestedMipmapLevel = 0;
            far.SetPropertyBlock(block);
            service.ProcessFrame(4f, true);
            service.ProcessFrame(4f, true);
            service.ProcessFrame(5f, false);
            Assert.That(texture.requestedMipmapLevel, Is.Zero);
        }
        finally
        {
            BasisCullingCameraRegistry.Unregister(mirror);
            BasisLocalCameraDriver.CameraInstance = previousMain;
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(target);
            Object.DestroyImmediate(material);
            if (texture != null) texture.ClearRequestedMipmapLevel();
            AssetDatabase.DeleteAsset(path);
            QualitySettings.streamingMipmapsRenderersPerFrame = previousBudget;
            QualitySettings.streamingMipmapsAddAllCameras = previousAllCameras;
            QualitySettings.streamingMipmapsActive = previousStreaming;
            QualitySettings.streamingMipmapsMaxLevelReduction = previousReduction;
        }
    }

    private static Renderer CreateQuad(Transform parent, Material material, MaterialPropertyBlock block, float z)
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.transform.SetParent(parent);
        quad.transform.position = new Vector3(0f, 0f, z);
        quad.TryGetComponent<Renderer>(out var renderer);
        renderer.sharedMaterial = material;
        renderer.SetPropertyBlock(block);
        return renderer;
    }

    [Test]
    public void RuntimeServiceCanBeConstructedWithoutNativeFieldInitializers()
    {
        var host = new GameObject("PropertyBlockStreamingConstructionTest");
        try { Assert.That(host.AddComponent<BasisMaterialPropertyBlockStreaming>(), Is.Not.Null); }
        finally { Object.DestroyImmediate(host); }
    }

    [Test]
    public void DemandChangesWithDistanceResolutionAndTiling()
    {
        // A 1024-square texture spanning a unit of UV/world area projects to 256 pixels.
        int baseline = BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 1f, 1f, 256f * 256f, 0f, 11);
        Assert.That(baseline, Is.EqualTo(2));
        Assert.That(BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 16f, 1f, 256f * 256f, 0f, 11), Is.EqualTo(baseline + 2), "Four times farther away needs two fewer detail levels.");
        Assert.That(BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 1f, 1f, 512f * 512f, 0f, 11), Is.EqualTo(baseline - 1), "Doubling camera resolution needs one more detail level.");
        Assert.That(BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 1f, 0.25f, 256f * 256f, 0f, 11), Is.EqualTo(baseline + 1), "Doubling UV tiling uses a smaller source-texture footprint per pixel.");
        Assert.That(BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 1f, 1f, 256f * 256f, 1f, 11), Is.EqualTo(baseline + 1));
    }

    [Test]
    public void DemandStaysWithinAvailableMips()
    {
        Assert.That(BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 0f, 1f, 256f * 256f, 0f, 11), Is.Zero);
        Assert.That(BasisMaterialPropertyBlockStreaming.CalculateMip(1024, 1024, 1e20f, 1f, 1f, 0f, 11), Is.EqualTo(10));
    }

    [Test]
    public void PerMaterialBlockTakesPrecedenceEvenWhenItOnlyOverridesAFloat()
    {
        var rendererBlock = new MaterialPropertyBlock();
        var indexedBlock = new MaterialPropertyBlock();
        var rendererTexture = new Texture2D(2, 2);
        var indexedTexture = new Texture2D(2, 2);
        int textureId = Shader.PropertyToID("_MainTex");
        try
        {
            rendererBlock.SetTexture(textureId, rendererTexture);
            Assert.That(BasisMaterialPropertyBlockStreaming.SelectBlock(rendererBlock, indexedBlock).GetTexture(textureId), Is.SameAs(rendererTexture));
            indexedBlock.SetFloat("_Cutoff", 0.5f);
            Assert.That(BasisMaterialPropertyBlockStreaming.SelectBlock(rendererBlock, indexedBlock).GetTexture(textureId), Is.Null, "Unity does not merge the renderer block into a nonempty indexed block.");
            indexedBlock.SetTexture(textureId, indexedTexture);
            Assert.That(BasisMaterialPropertyBlockStreaming.SelectBlock(rendererBlock, indexedBlock).GetTexture(textureId), Is.SameAs(indexedTexture));
            indexedBlock.Clear();
            Assert.That(BasisMaterialPropertyBlockStreaming.SelectBlock(rendererBlock, indexedBlock).GetTexture(textureId), Is.SameAs(rendererTexture));
        }
        finally
        {
            Object.DestroyImmediate(rendererTexture);
            Object.DestroyImmediate(indexedTexture);
        }
    }
}
