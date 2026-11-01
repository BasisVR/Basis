using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Tests.GlobalIllumination
{
    public class BasisGlobalIlluminationSurfaceColourTests
    {
        private BasisGlobalIlluminationRenderHarness harness;
        private bool previousAsync;
        private AmbientMode ambientMode;
        private Color ambientLight;
        private float reflectionIntensity;

        [SetUp]
        public void SetUp()
        {
            previousAsync = UnityEditor.ShaderUtil.allowAsyncCompilation;
            UnityEditor.ShaderUtil.allowAsyncCompilation = false;
            BasisGlobalIlluminationRenderHarness.SkipIfUnavailable();
            BasisGlobalIlluminationEmitter.Registered.Clear();
            harness = new BasisGlobalIlluminationRenderHarness();
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            ambientMode = RenderSettings.ambientMode;
            ambientLight = RenderSettings.ambientLight;
            reflectionIntensity = RenderSettings.reflectionIntensity;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.reflectionIntensity = 0f;
            DynamicGI.UpdateEnvironment();
            harness.Settings.fallbackIntensity = 0f;
        }

        [TearDown]
        public void TearDown()
        {
            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientLight = ambientLight;
            RenderSettings.reflectionIntensity = reflectionIntensity;
            harness?.SetDebugView(BasisGlobalIlluminationDebugView.None);
            harness?.Dispose();
            harness = null;
            UnityEditor.ShaderUtil.allowAsyncCompilation = previousAsync;
            BasisGlobalIlluminationPass.ReleaseWorld();
        }

        private void Room(Material material, Vector3 size)
        {
            const float thickness = 0.3f;
            harness.AddBox(new Vector3(0f, -thickness / 2f, 0f), new Vector3(size.x + 2f * thickness, thickness, size.z + 2f * thickness), material);
            harness.AddBox(new Vector3(0f, size.y + thickness / 2f, 0f), new Vector3(size.x + 2f * thickness, thickness, size.z + 2f * thickness), material);
            harness.AddBox(new Vector3(-size.x / 2f - thickness / 2f, size.y / 2f, 0f), new Vector3(thickness, size.y, size.z), material);
            harness.AddBox(new Vector3(size.x / 2f + thickness / 2f, size.y / 2f, 0f), new Vector3(thickness, size.y, size.z), material);
            harness.AddBox(new Vector3(0f, size.y / 2f, size.z / 2f + thickness / 2f), new Vector3(size.x, size.y, thickness), material);
            harness.AddBox(new Vector3(0f, size.y / 2f, -size.z / 2f - thickness / 2f), new Vector3(size.x, size.y, thickness), material);
        }

        private void Look(Vector3 from, Vector3 at)
        {
            harness.Camera.transform.position = from;
            harness.Camera.transform.rotation = Quaternion.LookRotation(at - from, Vector3.up);
        }

        private RectInt Probe(Vector3 worldPoint, int radius)
        {
            harness.Render();
            Vector3 screen = harness.Camera.WorldToScreenPoint(worldPoint);
            return new RectInt(Mathf.RoundToInt(screen.x) - radius, Mathf.RoundToInt(screen.y) - radius, radius * 2, radius * 2);
        }

        private Color SurfaceAt(Vector3 worldPoint)
        {
            harness.SetDebugView(BasisGlobalIlluminationDebugView.SurfaceColour);
            Color colour = harness.RenderAndSample(Probe(worldPoint, 4));
            harness.SetDebugView(BasisGlobalIlluminationDebugView.None);
            return colour;
        }

        private GameObject FrontBox(Material material)
        {
            Look(new Vector3(0f, 0f, -3f), Vector3.zero);
            return harness.AddBox(Vector3.zero, new Vector3(3f, 3f, 0.2f), material);
        }

        private Material CustomShaderMaterial(Color colour)
        {
            Shader shader = UnityEditor.ShaderUtil.CreateShaderAsset(CustomShaderSource);
            harness.Own(shader);
            Material material = harness.Own(new Material(shader));
            material.SetColor("_Color", colour);
            return material;
        }

        private const string CustomShaderSource = @"Shader ""Hidden/BasisGIT/CustomLit""
{
    Properties { _MainTex(""t"", 2D) = ""white"" {} _Color(""c"", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { ""RenderPipeline"" = ""UniversalPipeline"" }
        Pass
        {
            Tags { ""LightMode"" = ""UniversalForward"" }
            HLSLPROGRAM
            #pragma vertex V
            #pragma fragment F
            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""
            float4 V(float4 p : POSITION) : SV_POSITION { return TransformObjectToHClip(p.xyz); }
            float4 F() : SV_Target { return float4(0, 0, 0, 1); }
            ENDHLSL
        }
        Pass
        {
            Tags { ""LightMode"" = ""DepthOnly"" }
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex V
            #pragma fragment F
            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""
            float4 V(float4 p : POSITION) : SV_POSITION { return TransformObjectToHClip(p.xyz); }
            float4 F() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}";

        [Test]
        public void TheBaseColourOfAUrpMaterialIsRead()
        {
            FrontBox(harness.CreateLitMaterial(new Color(0.8f, 0.1f, 0.1f), Color.black));
            Color read = SurfaceAt(Vector3.zero);
            Debug.Log($"[BasisGI] surface urp colour {read}");
            Assert.Greater(read.r, read.g * 2f, $"red did not come through: {read}");
        }

        [Test]
        public void TheBaseMapOfAUrpMaterialIsRead()
        {
            FrontBox(harness.CreateTexturedMaterial(new Color(0.1f, 0.8f, 0.1f)));
            Color read = SurfaceAt(Vector3.zero);
            Debug.Log($"[BasisGI] surface urp map {read}");
            Assert.Greater(read.g, read.r * 2f, $"green did not come through: {read}");
        }

        [Test]
        public void AShaderThatOnlyDeclaresMainTexAndColorIsRead()
        {
            FrontBox(CustomShaderMaterial(new Color(0.1f, 0.1f, 0.8f)));
            Color read = SurfaceAt(Vector3.zero);
            Debug.Log($"[BasisGI] surface custom {read}");
            Assert.Greater(read.b, read.r * 2f, $"blue did not come through: {read}");
        }

        [Test]
        public void AnUnlitMaterialIsNotClaimed()
        {
            Material unlit = harness.Own(new Material(Shader.Find("Universal Render Pipeline/Unlit")));
            unlit.SetColor("_BaseColor", Color.white);
            FrontBox(unlit);
            Color read = SurfaceAt(Vector3.zero);
            Debug.Log($"[BasisGI] surface unlit {read}");
            Assert.Less(read.maxColorComponent, 0.05f, $"unlit surfaces must not take additive bounce: {read}");
        }

        [Test]
        public void ASurfaceWithNoDirectLightStillTakesBounce()
        {
            Material grey = harness.CreateLitMaterial(new Color(0.7f, 0.7f, 0.7f), Color.black);
            Room(grey, new Vector3(6f, 3f, 6f));
            harness.AddBox(new Vector3(0f, 1.5f, 2.8f), new Vector3(3f, 2f, 0.2f), harness.CreateLitMaterial(Color.black, new Color(20f, 20f, 20f)));
            Look(new Vector3(0f, 1.5f, -2.5f), new Vector3(0f, 0.3f, 1f));
            harness.Settings.worldSpace = true;
            RectInt floor = Probe(new Vector3(0f, 0f, 0.5f), 8);

            harness.Settings.surfaceColour = false;
            Color without = harness.Converged(floor, 40, 6);
            harness.Settings.surfaceColour = true;
            Color with = harness.Converged(floor, 40, 6);
            Debug.Log($"[BasisGI] unlit room floor: multiply {without} | with surface colour {with}");
            
            Assert.Greater(with.maxColorComponent, without.maxColorComponent * 2f, "the floor did not gain the bounce");
        }

        [Test]
        public void TheBounceTakesTheReceiversColour()
        {
            Material black = harness.CreateLitMaterial(new Color(0.02f, 0.02f, 0.02f), Color.black);
            Room(black, new Vector3(6f, 3f, 6f));
            harness.AddBox(new Vector3(0f, 1.5f, 2.8f), new Vector3(3f, 2f, 0.2f), harness.CreateLitMaterial(Color.black, new Color(20f, 20f, 20f)));
            harness.AddBox(new Vector3(-0.8f, 0.01f, 0.5f), new Vector3(1.4f, 0.02f, 1.4f), harness.CreateLitMaterial(new Color(0.8f, 0.05f, 0.05f), Color.black));
            harness.AddBox(new Vector3(0.8f, 0.01f, 0.5f), new Vector3(1.4f, 0.02f, 1.4f), harness.CreateLitMaterial(new Color(0.05f, 0.8f, 0.05f), Color.black));
            Look(new Vector3(0f, 1.5f, -2.5f), new Vector3(0f, 0.3f, 1f));
            harness.Settings.worldSpace = true;
            Color red = harness.Converged(Probe(new Vector3(-0.8f, 0.02f, 0.5f), 6), 40, 6);
            Color green = harness.Converged(Probe(new Vector3(0.8f, 0.02f, 0.5f), 6), 40, 6);
            Debug.Log($"[BasisGI] receivers under white light: red {red} | green {green}");
            Assert.Greater(red.r, red.g * 2f, $"red receiver: {red}");
            Assert.Greater(green.g, green.r * 2f, $"green receiver: {green}");
        }

        [Test]
        public void TheUpsampleStillGuardsSilhouettes()
        {
            Material grey = harness.CreateLitMaterial(new Color(0.7f, 0.7f, 0.7f), Color.black);
            harness.AddBox(new Vector3(0f, 0f, 3f), new Vector3(12f, 12f, 0.2f), grey);
            harness.AddBox(new Vector3(-1f, 0f, 0f), new Vector3(2f, 6f, 0.2f), grey);
            harness.AddBox(new Vector3(1.5f, 0f, 2.5f), new Vector3(1f, 3f, 0.4f), harness.CreateLitMaterial(Color.black, new Color(30f, 4f, 4f)));
            Look(new Vector3(0f, 0f, -3f), Vector3.zero);
            harness.Settings.resolution = BasisGlobalIlluminationResolution.Quarter;
            harness.Render();
            Vector3 edge = harness.Camera.WorldToScreenPoint(Vector3.zero);
            RectInt strip = new RectInt(Mathf.RoundToInt(edge.x) - 2, 30, 4, 60);
            RectInt[] regions = { strip };
            harness.Settings.bilateralUpsample = false;
            BasisGlobalIlluminationRenderHarness.Reading plain = harness.ConvergedReadings(regions, 24, 6)[0];
            harness.Settings.bilateralUpsample = true;
            BasisGlobalIlluminationRenderHarness.Reading guarded = harness.ConvergedReadings(regions, 24, 6)[0];
            float difference = plain.PixelDifference(guarded);
            Debug.Log($"[BasisGI] silhouette strip pixel difference {difference}");
            Assert.Greater(difference, 0.002f, "the bilateral upsample did nothing at a silhouette");
        }

        [Test]
        public void ACutoutHoleShowsWhatIsBehindIt()
        {
            Texture2D map = harness.Own(new Texture2D(2, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp });
            map.SetPixels(new[] { new Color(1f, 0f, 0f, 1f), new Color(1f, 0f, 0f, 0f) });
            map.Apply();
            Material cutout = harness.CreateLitMaterial(Color.white, Color.black);
            cutout.SetTexture("_BaseMap", map);
            cutout.SetFloat("_AlphaClip", 1f);
            cutout.SetFloat("_Cutoff", 0.5f);
            cutout.EnableKeyword("_ALPHATEST_ON");
            cutout.renderQueue = (int)RenderQueue.AlphaTest;
            Look(new Vector3(0f, 0f, -3f), Vector3.zero);
            GameObject quad = harness.Own(GameObject.CreatePrimitive(PrimitiveType.Quad));
            quad.transform.localScale = new Vector3(3f, 3f, 1f);
            quad.GetComponent<MeshRenderer>().sharedMaterial = cutout;
            harness.AddBox(new Vector3(0f, 0f, 1f), new Vector3(6f, 6f, 0.2f), harness.CreateLitMaterial(new Color(0.1f, 0.1f, 0.8f), Color.black));
            Color hole = SurfaceAt(new Vector3(0.75f, 0f, 0f));
            Color solid = SurfaceAt(new Vector3(-0.75f, 0f, 0f));
            Debug.Log($"[BasisGI] cutout: hole {hole} | solid {solid}");
            Assert.Greater(hole.b, hole.r, $"the hole read the clipped surface: {hole}");
            Assert.Greater(solid.r, solid.b, $"the solid part read the wall behind: {solid}");
        }
    }
}
