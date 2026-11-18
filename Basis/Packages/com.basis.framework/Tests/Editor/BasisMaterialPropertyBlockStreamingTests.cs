using System.IO;
using Basis.Scripts.Drivers;
using Basis.Scripts.Rendering;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

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

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void ObservedMirrorUpgradesSharedTextureBeforeSweepFinishes(int mathPath)
    {
        if (!SystemInfo.supportsMipStreaming) Assert.Ignore("Requires Unity mip streaming support.");
        var batch = mathPath == 0 ? null : CreateBatch(mathPath == 2);

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
            texture = ImportStreamedTexture(path);

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

            var shader = Shader.Find("Unlit/Texture");
            Assert.That(shader, Is.Not.Null);
            material = new Material(shader);
            var block = new MaterialPropertyBlock();
            block.SetTexture("_MainTex", texture);
            var far = CreateQuad(root.transform, material, block, -30f);
            var near = CreateQuad(root.transform, material, block, -0.5f);
            var pending = CreateQuad(root.transform, material, null, 5f);
            var service = root.AddComponent<BasisMaterialPropertyBlockStreaming>();
            service.MipBatch = batch;
            service.TrackRenderers(new[] { far, near, pending });
            service.ObserveRenderedCamera(mirror, 1f);
            service.ProcessFrame(1f, true);
            Assert.That(texture.requestedMipmapLevel, Is.GreaterThan(0), "The disabled, rendered camera sees the distant override.");
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
            service.ObserveRenderedCamera(mirror, 3f);
            service.ProcessFrame(3f, true);
            service.ProcessFrame(3f, true);
            Assert.That(texture.requestedMipmapLevel, Is.EqualTo(initialRequest), "Removing the last override restores the request that preceded the sweep.");

            // A request that the service never needed to write belongs to its original caller.
            texture.requestedMipmapLevel = 0;
            far.SetPropertyBlock(block);
            service.ObserveRenderedCamera(mirror, 4f);
            service.ProcessFrame(4f, true);
            service.ProcessFrame(4f, true);
            service.ProcessFrame(5f, false);
            Assert.That(texture.requestedMipmapLevel, Is.Zero);
        }
        finally
        {
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

    [TestCase(false)]
    [TestCase(true)]
    public void DisabledCameraRequestsMipsOnlyWhileItsRenderIsRecent(bool registerCamera)
    {
        if (!SystemInfo.supportsMipStreaming) Assert.Ignore("Requires Unity mip streaming support.");

        string path = AssetDatabase.GenerateUniqueAssetPath("Assets/BasisStreamingTest.png");
        bool previousStreaming = QualitySettings.streamingMipmapsActive;
        bool previousAllCameras = QualitySettings.streamingMipmapsAddAllCameras;
        Camera previousMain = BasisLocalCameraDriver.CameraInstance;
        var root = new GameObject("Basis manual render streaming test");
        var target = new RenderTexture(512, 512, 24);
        Material material = null;
        Camera reflection = null;
        Texture2D texture = null;
        BasisMaterialPropertyBlockStreaming service = null;
        try
        {
            QualitySettings.streamingMipmapsActive = true;
            QualitySettings.streamingMipmapsAddAllCameras = true;
            texture = ImportStreamedTexture(path);
            texture.ClearRequestedMipmapLevel();
            int initialRequest = texture.requestedMipmapLevel;
            var main = new GameObject("Main view").AddComponent<Camera>();
            main.transform.SetParent(root.transform);
            main.targetTexture = target;
            BasisLocalCameraDriver.CameraInstance = main;

            reflection = new GameObject("Manual reflection").AddComponent<Camera>();
            reflection.transform.SetParent(root.transform);
            reflection.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            reflection.enabled = false;
            if (registerCamera) BasisCullingCameraRegistry.Register(reflection);

            material = new Material(Shader.Find("Unlit/Texture"));
            var block = new MaterialPropertyBlock();
            block.SetTexture("_MainTex", texture);
            var renderer = CreateQuad(root.transform, material, block, -5f);
            renderer.transform.rotation = reflection.transform.rotation;
            Assert.That(GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(main), renderer.bounds), Is.False);
            Assert.That(GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(reflection), renderer.bounds), Is.True);

            service = root.AddComponent<BasisMaterialPropertyBlockStreaming>();
            service.TrackRenderers(new[] { renderer });
            service.ProcessFrame(Time.unscaledTime, true);
            Assert.That(texture.requestedMipmapLevel, Is.EqualTo(initialRequest), "An idle registered camera contributes no demand.");

            // Edit Mode does not run Awake for the service. Subscribe the production callback to a real URP request.
            RenderPipelineManager.beginCameraRendering += service.OnBeginCameraRendering;
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
            Assert.That(RenderPipeline.SupportsRenderRequest(reflection, request), Is.True);
            RenderPipeline.SubmitRenderRequest(reflection, request);
            float renderedAt = Time.unscaledTime;
            Assert.That(reflection.targetTexture, Is.Null, "The pipeline restores the camera's target after the request.");
            reflection.transform.rotation = Quaternion.identity;

            service.ProcessFrame(renderedAt + 0.2f, true);
            int observedRequest = texture.requestedMipmapLevel;
            Assert.That(observedRequest, Is.GreaterThanOrEqualTo(0), "The render callback provides the disabled camera's frustum.");
            Assert.That(observedRequest, Is.Not.EqualTo(initialRequest));

            service.ProcessFrame(renderedAt + 1.1f, true);
            Assert.That(texture.requestedMipmapLevel, Is.EqualTo(observedRequest), "A one-second rate-limited gap preserves demand.");

            service.ProcessFrame(renderedAt + 1.7f, true);
            Assert.That(reflection.gameObject.activeInHierarchy, Is.True);
            Assert.That(texture.requestedMipmapLevel, Is.EqualTo(initialRequest), "The completed sweep releases an expired view's request.");
        }
        finally
        {
            if (service != null) RenderPipelineManager.beginCameraRendering -= service.OnBeginCameraRendering;
            BasisCullingCameraRegistry.Unregister(reflection);
            BasisLocalCameraDriver.CameraInstance = previousMain;
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(target);
            Object.DestroyImmediate(material);
            if (texture != null) texture.ClearRequestedMipmapLevel();
            AssetDatabase.DeleteAsset(path);
            QualitySettings.streamingMipmapsActive = previousStreaming;
            QualitySettings.streamingMipmapsAddAllCameras = previousAllCameras;
        }
    }

    private static Texture2D ImportStreamedTexture(string path)
    {
        var pixels = new Texture2D(512, 512);
        try { File.WriteAllBytes(path, pixels.EncodeToPNG()); }
        finally { Object.DestroyImmediate(pixels); }
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.mipmapEnabled = true;
        importer.streamingMipmaps = true;
        importer.SaveAndReimport();
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        Assert.That(texture.streamingMipmaps, Is.True);
        return texture;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BatchedMipMathMatchesScalarAtRoundingBoundaries(bool useBurst)
    {
        var batch = CreateBatch(useBurst);
        var expected = new int[BasisMaterialPropertyBlockMipBatch.Capacity];
        var biases = new[] { -1.5f, -0.25f, 0f, 0.25f, 0.5f, 0.731f, 2f };
        foreach (float bias in biases)
        {
            for (int mip = 0; mip < 14; mip++)
            {
                int bits = System.BitConverter.SingleToInt32Bits((float)System.Math.Pow(2, 2 * (mip - (double)bias)));
                for (int offset = -8; offset <= 8; offset++)
                {
                    AddSample(new BasisMaterialPropertyBlockMipBatch.Sample
                    {
                        Width = 1, Height = 1, MipCount = 15,
                        DistanceSquared = System.BitConverter.Int32BitsToSingle(bits + offset),
                        UvMetric = 1f, ProjectionArea = 1f, Bias = bias,
                    });
                }
            }
        }
        var random = new System.Random(71);
        for (int i = 0; i < 4099; i++)
        {
            AddSample(new BasisMaterialPropertyBlockMipBatch.Sample
            {
                Width = 1 << random.Next(5, 14), Height = 1 << random.Next(5, 14), MipCount = random.Next(1, 15),
                DistanceSquared = (float)System.Math.Pow(2, random.NextDouble() * 30 - 18),
                UvMetric = (float)System.Math.Pow(2, random.NextDouble() * 16 - 8),
                ProjectionArea = (float)System.Math.Pow(2, random.NextDouble() * 24),
                Bias = (float)(random.NextDouble() * 8 - 4),
            });
        }
        foreach (float area in new[] { 0f, float.Epsilon, float.MaxValue, float.PositiveInfinity, float.NaN })
        {
            AddSample(new BasisMaterialPropertyBlockMipBatch.Sample
            {
                Width = 512, Height = 1024, MipCount = 11,
                DistanceSquared = 1f, UvMetric = 1f, ProjectionArea = area, Bias = 2f,
            });
        }
        CheckBatch();

        void AddSample(BasisMaterialPropertyBlockMipBatch.Sample sample)
        {
            expected[batch.Count] = sample.CalculateScalar();
            batch.Add(null, sample);
            if (batch.IsFull) CheckBatch();
        }

        void CheckBatch()
        {
            batch.Calculate();
            for (int i = 0; i < batch.Count; i++)
                Assert.That(batch.Mips[i], Is.EqualTo(expected[i]));
            batch.Clear();
        }
    }

    private static BasisMaterialPropertyBlockMipBatch CreateBatch(bool useBurst)
    {
        var burst = useBurst ? BasisMipBurstKernel.TryCreate() : null;
        if (useBurst && burst == null) Assert.Ignore("Requires a native Burst kernel.");
        return new BasisMaterialPropertyBlockMipBatch(burst);
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
