using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Tests.GlobalIllumination
{
    public unsafe class BasisGlobalIlluminationSurfaceBatchTests
    {
        private BasisGlobalIlluminationRenderHarness harness;
        private BatchRendererGroup group;
        private GraphicsBuffer instances;
        private BatchID batch;
        private BatchMeshID mesh;
        private BatchMaterialID material;
        private bool previousAsync;
        private AmbientMode ambientMode;
        private Color ambientLight;
        private float reflectionIntensity;
        private long logStart;

        [SetUp]
        public void SetUp()
        {
            previousAsync = UnityEditor.ShaderUtil.allowAsyncCompilation;
            UnityEditor.ShaderUtil.allowAsyncCompilation = false;
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();
            harness = new BasisGlobalIlluminationRenderHarness();
            ambientMode = RenderSettings.ambientMode;
            ambientLight = RenderSettings.ambientLight;
            reflectionIntensity = RenderSettings.reflectionIntensity;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.reflectionIntensity = 0f;
            DynamicGI.UpdateEnvironment();
            harness.Settings.fallbackIntensity = 0f;
            harness.AddSun(Quaternion.Euler(35f, 25f, 0f), 2f);
            harness.Camera.transform.position = new Vector3(0f, 0f, -3f);
            harness.Camera.transform.rotation = Quaternion.identity;
            logStart = new System.IO.FileInfo(Application.consoleLogPath).Length;
        }

        [TearDown]
        public void TearDown()
        {
            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientLight = ambientLight;
            RenderSettings.reflectionIntensity = reflectionIntensity;
            group?.Dispose();
            group = null;
            instances?.Release();
            instances = null;
            harness?.SetDebugView(BasisGlobalIlluminationDebugView.None);
            harness?.Dispose();
            harness = null;
            UnityEditor.ShaderUtil.allowAsyncCompilation = previousAsync;
        }

        private int BatchWarnings()
        {
            GL.Flush();
            using System.IO.FileStream stream = new System.IO.FileStream(Application.consoleLogPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            stream.Seek(logStart, System.IO.SeekOrigin.Begin);
            using System.IO.StreamReader reader = new System.IO.StreamReader(stream);
            string text = reader.ReadToEnd();
            int count = 0;
            foreach (string needle in new[] { "Override shaders (from DrawingSettings", "does not define a DOTS_INSTANCING_ON", "not SRP Batcher compatible" })
            {
                for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal)) { count++; }
            }
            return count;
        }

        private JobHandle Cull(BatchRendererGroup source, BatchCullingContext context, BatchCullingOutput output, IntPtr user)
        {
            BatchCullingOutputDrawCommands* draws = (BatchCullingOutputDrawCommands*)output.drawCommands.GetUnsafePtr();
            draws->drawCommands = (BatchDrawCommand*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawCommand>(), UnsafeUtility.AlignOf<long>(), Allocator.TempJob);
            draws->drawRanges = (BatchDrawRange*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<BatchDrawRange>(), UnsafeUtility.AlignOf<long>(), Allocator.TempJob);
            draws->visibleInstances = (int*)UnsafeUtility.Malloc(sizeof(int), UnsafeUtility.AlignOf<int>(), Allocator.TempJob);
            draws->drawCommandPickingEntityIds = null;
            draws->drawCommandCount = 1;
            draws->drawRangeCount = 1;
            draws->visibleInstanceCount = 1;
            draws->instanceSortingPositions = null;
            draws->instanceSortingPositionFloatCount = 0;
            draws->drawCommands[0] = new BatchDrawCommand
            {
                visibleOffset = 0,
                visibleCount = 1,
                batchID = batch,
                materialID = material,
                meshID = mesh,
                submeshIndex = 0,
                splitVisibilityMask = 0xff,
                flags = 0,
                sortingPosition = 0
            };
            draws->drawRanges[0] = new BatchDrawRange
            {
                drawCommandsBegin = 0,
                drawCommandsCount = 1,
                filterSettings = new BatchFilterSettings { renderingLayerMask = 0xffffffff, layer = 0, batchLayer = BatchLayer.InstanceCullingDirect, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = true }
            };
            draws->visibleInstances[0] = 0;
            return default;
        }

        private void AddBatchedBox(Material lit)
        {
            GameObject primitive = harness.Own(GameObject.CreatePrimitive(PrimitiveType.Cube));
            Mesh cube = primitive.GetComponent<MeshFilter>().sharedMesh;
            primitive.SetActive(false);

            group = new BatchRendererGroup(Cull, IntPtr.Zero);
            group.SetGlobalBounds(new Bounds(Vector3.zero, Vector3.one * 1000f));
            mesh = group.RegisterMesh(cube);
            material = group.RegisterMaterial(lit);

            const int zeroBytes = 64, matrixBytes = 48;
            float[] data = new float[(zeroBytes + matrixBytes * 2) / 4];
            float[] local = { 3f, 0f, 0f, 0f, 3f, 0f, 0f, 0f, 0.2f, 0f, 0f, 0f };
            float[] inverse = { 1f / 3f, 0f, 0f, 0f, 1f / 3f, 0f, 0f, 0f, 5f, 0f, 0f, 0f };
            Array.Copy(local, 0, data, zeroBytes / 4, 12);
            Array.Copy(inverse, 0, data, (zeroBytes + matrixBytes) / 4, 12);
            instances = new GraphicsBuffer(GraphicsBuffer.Target.Raw, data.Length, 4);
            instances.SetData(data);
            NativeArray<MetadataValue> metadata = new NativeArray<MetadataValue>(2, Allocator.Temp);
            metadata[0] = new MetadataValue { NameID = Shader.PropertyToID("unity_ObjectToWorld"), Value = 0x80000000u | zeroBytes };
            metadata[1] = new MetadataValue { NameID = Shader.PropertyToID("unity_WorldToObject"), Value = 0x80000000u | (zeroBytes + matrixBytes) };
            batch = group.AddBatch(metadata, instances.bufferHandle);
            metadata.Dispose();
        }

        private RectInt Centre()
        {
            harness.Render();
            Vector3 screen = harness.Camera.WorldToScreenPoint(Vector3.zero);
            return new RectInt((int)screen.x - 6, (int)screen.y - 6, 12, 12);
        }

        private Color Surface()
        {
            RectInt centre = Centre();
            harness.SetDebugView(BasisGlobalIlluminationDebugView.SurfaceColour);
            for (int frame = 0; frame < 2; frame++) { harness.Render(); }
            Color read = harness.RenderAndSample(centre);
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            return read;
        }

        [TestCase("Universal Render Pipeline/Lit")]
        [TestCase("Universal Render Pipeline/Simple Lit")]
        [TestCase("Universal Render Pipeline/Complex Lit")]
        [TestCase("Universal Render Pipeline/Baked Lit")]
        public void ABatchedDrawReadsTheSameColourAsAnOrdinaryOne(string shaderName)
        {
            Material lit = harness.Own(new Material(Shader.Find(shaderName)));
            lit.SetColor("_BaseColor", new Color(0.8f, 0.1f, 0.1f));
            GameObject ordinary = harness.AddBox(Vector3.zero, new Vector3(3f, 3f, 0.2f), lit);
            Color expected = Surface();
            ordinary.SetActive(false);

            AddBatchedBox(lit);
            Color read = Surface();
            Debug.Log($"[BasisGI] {shaderName} batched surface {read} | ordinary surface {expected}");
            Assert.Greater(read.r, read.b * 2f, $"the group never drew into the surface: {read}");
            Assert.AreEqual(expected.r, read.r, 0.02f, "a batched draw read something other than its base colour");
            Assert.AreEqual(expected.g, read.g, 0.02f, "a batched draw read something other than its base colour");
            Assert.AreEqual(expected.b, read.b, 0.02f, "a batched draw read something other than its base colour");
            Assert.AreEqual(0, BatchWarnings(), "a batched draw warned");
        }

        [Test]
        public void ABatchedDrawIsVoxelizedWithoutWarnings()
        {
            Material lit = harness.CreateLitMaterial(new Color(0.6f, 0.6f, 0.6f), new Color(4f, 2f, 1f));
            AddBatchedBox(lit);
            harness.Settings.worldSpace = true;
            for (int frame = 0; frame < 16; frame++) { harness.Render(); }
            Assert.AreEqual(0, BatchWarnings(), "a batched draw warned while the world voxelized it");
        }

        [Test]
        public void ABatchedShaderGraphHasThePassAndDoesNotWarn()
        {
            const string path = "Packages/com.basis.gizmos/GizmoShader.shadergraph";
            UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceUpdate);
            Shader graph = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(path);
            Assert.IsNotNull(graph, path);
            Material material = harness.Own(new Material(graph));
            Assert.GreaterOrEqual(material.FindPass("BasisSurface"), 0, "the Lit Shader Graph target did not generate the pass");
            AddBatchedBox(material);
            Color read = Surface();
            Debug.Log($"[BasisGI] shader graph batched surface {read}");
            Assert.Greater(read.maxColorComponent, 0.01f, "the batched graph drew nothing into the surface");
            Assert.AreEqual(0, BatchWarnings(), "a batched graph warned");
        }

        [Test]
        public void ABundledGraphWithOnlyTheOld2DPassIsSkippedSilently()
        {
            Shader shader = UnityEditor.ShaderUtil.CreateShaderAsset(OldGraphSource);
            harness.Own(shader);
            Assert.IsTrue(shader.isSupported, "the fixture shader did not compile");
            Material material = harness.Own(new Material(shader));
            Assert.Less(material.FindPass("BasisSurface"), 0);
            AddBatchedBox(material);
            Color read = Surface();
            Debug.Log($"[BasisGI] old graph batched surface {read}");
            Assert.Less(read.maxColorComponent, 0.01f, $"the pixel should be left for the multiply: {read}");
            Assert.AreEqual(0, BatchWarnings(), "a bundled graph warned");
        }

        private const string OldGraphSource = @"Shader ""Hidden/BasisGIT/OldGraph""
{
    SubShader
    {
        Tags { ""RenderPipeline"" = ""UniversalPipeline"" }
        HLSLINCLUDE
        #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""
        struct A { float4 p : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
        float4 V(A a) : SV_POSITION { UNITY_SETUP_INSTANCE_ID(a); return TransformObjectToHClip(a.p.xyz); }
        float4 F() : SV_Target { return float4(0, 1, 0, 1); }
        ENDHLSL
        Pass
        {
            Tags { ""LightMode"" = ""UniversalForward"" }
            HLSLPROGRAM
            #pragma vertex V
            #pragma fragment F
            #pragma target 4.5
            #pragma multi_compile _ DOTS_INSTANCING_ON
            ENDHLSL
        }
        Pass
        {
            Name ""Universal 2D""
            Tags { ""LightMode"" = ""Universal2D"" }
            HLSLPROGRAM
            #pragma vertex V
            #pragma fragment F
            ENDHLSL
        }
    }
}";

        [Test]
        public void ABatchedDrawBehindAnOrdinaryOneDoesNotShowThrough()
        {
            Material red = harness.CreateLitMaterial(new Color(0.8f, 0.1f, 0.1f), Color.black);
            AddBatchedBox(red);
            harness.AddBox(new Vector3(0f, 0f, -1f), new Vector3(3f, 3f, 0.2f), harness.CreateLitMaterial(new Color(0.1f, 0.1f, 0.8f), Color.black));
            Color read = Surface();
            Debug.Log($"[BasisGI] batched behind ordinary {read}");
            Assert.Greater(read.b, read.r * 2f, $"the hidden batched draw won the pixel: {read}");
        }

        [Test]
        public void ABatchedDrawBehindAnUnlitOneIsNotClaimed()
        {
            AddBatchedBox(harness.CreateLitMaterial(new Color(0.8f, 0.1f, 0.1f), Color.black));
            Material unlit = harness.Own(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
            unlit.SetColor("_BaseColor", Color.white);
            harness.AddBox(new Vector3(0f, 0f, -1f), new Vector3(3f, 3f, 0.2f), unlit);
            Color read = Surface();
            Debug.Log($"[BasisGI] batched behind unlit {read}");
            Assert.Less(read.maxColorComponent, 0.05f, $"the unlit pixel took the colour behind it: {read}");
        }
    }
}
