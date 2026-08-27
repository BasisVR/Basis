using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.Tests.GlobalIllumination
{
    public class BasisGlobalIlluminationRayTests
    {
        private BasisGlobalIlluminationVolume volume;

        [SetUp]
        public void SetUp()
        {
            volume = ScriptableObject.CreateInstance<BasisGlobalIlluminationVolume>();
        }

        [TearDown]
        public void TearDown()
        {
            if (volume != null) { Object.DestroyImmediate(volume); }
        }

        // The trace kernel declares matching structs and reads them out of a StructuredBuffer, so a field
        // added on either side without the other silently reads the wrong bytes. These pin the layout.
        [Test]
        public void InstanceStrideMatchesTheKernelStruct()
        {
            Assert.AreEqual(BasisGlobalIlluminationRayInstance.Stride, Marshal.SizeOf<BasisGlobalIlluminationRayInstance>());
        }

        [Test]
        public void LightStrideMatchesTheKernelStruct()
        {
            Assert.AreEqual(BasisGlobalIlluminationRayLight.Stride, Marshal.SizeOf<BasisGlobalIlluminationRayLight>());
        }

        [Test]
        public void ScreenSpaceIsTheDefaultMode()
        {
            Assert.AreEqual(BasisGlobalIlluminationMode.ScreenSpace, volume.mode.value);
            Assert.IsFalse(volume.IsRayTraced());
        }

        [Test]
        public void RayTracedModeIsReported()
        {
            volume.mode.value = BasisGlobalIlluminationMode.RayTraced;
            Assert.IsTrue(volume.IsRayTraced());
        }

        [TestCase(BasisGlobalIlluminationQuality.Low, 1)]
        [TestCase(BasisGlobalIlluminationQuality.Medium, 1)]
        [TestCase(BasisGlobalIlluminationQuality.High, 2)]
        [TestCase(BasisGlobalIlluminationQuality.Ultra, 3)]
        public void QualityDrivesTheBounceCount(BasisGlobalIlluminationQuality quality, int expected)
        {
            volume.quality.value = quality;
            volume.overrideQualityCounts.value = false;
            Assert.AreEqual(expected, volume.ResolvedBounces());
        }

        [Test]
        public void OverridingQualityCountsTakesTheAuthoredBounceCount()
        {
            volume.quality.value = BasisGlobalIlluminationQuality.Low;
            volume.overrideQualityCounts.value = true;
            volume.bounces.value = 3;
            Assert.AreEqual(3, volume.ResolvedBounces());
        }

        [Test]
        public void QualityLightLimitIsMonotonic()
        {
            volume.rayTracedLights.value = true;
            int previous = 0;
            BasisGlobalIlluminationQuality[] ladder =
            {
                BasisGlobalIlluminationQuality.Low,
                BasisGlobalIlluminationQuality.Medium,
                BasisGlobalIlluminationQuality.High,
                BasisGlobalIlluminationQuality.Ultra
            };
            for (int index = 0; index < ladder.Length; index++)
            {
                volume.quality.value = ladder[index];
                int limit = volume.ResolvedRayTracedLightLimit();
                Assert.GreaterOrEqual(limit, previous, ladder[index].ToString());
                Assert.LessOrEqual(limit, BasisGlobalIlluminationRayLights.MaxLights);
                previous = limit;
            }
        }

        [Test]
        public void LightsOffGathersNoLights()
        {
            volume.rayTracedLights.value = false;
            Assert.AreEqual(0, volume.ResolvedRayTracedLightLimit());
        }

        [Test]
        public void SceneSettingsCarryTheVolumeValues()
        {
            volume.rayTracedSkinnedMeshes.value = BasisGlobalIlluminationRaySkinnedMode.Static;
            volume.rayTracedSkinnedBudget.value = 5;
            volume.rayTracedSkinnedInterval.value = 7;
            volume.rayTracedSkinnedDistance.value = 21f;
            volume.rayTracedTextureAlbedo.value = false;
            volume.rayTracedEmissiveSurfaces.value = false;
            volume.rayTracedShadowCastersOnly.value = true;

            BasisGlobalIlluminationRaySceneSettings settings = volume.ResolvedSceneSettings();
            Assert.AreEqual(BasisGlobalIlluminationRaySkinnedMode.Static, settings.skinnedMode);
            Assert.AreEqual(5, settings.skinnedBakesPerFrame);
            Assert.AreEqual(7, settings.skinnedBakeInterval);
            Assert.AreEqual(21f, settings.skinnedMaxDistance);
            Assert.IsFalse(settings.textureAlbedo);
            Assert.IsFalse(settings.emissiveSurfaces);
            Assert.IsTrue(settings.shadowCastersOnly);
        }

        [Test]
        public void LightSettingsShareTheEmitterControls()
        {
            volume.emitters.value = true;
            volume.emitterIntensity.value = 2.5f;
            volume.rayTracedShadows.value = false;

            BasisGlobalIlluminationRayLightSettings settings = volume.ResolvedLightSettings();
            Assert.IsTrue(settings.emitters);
            Assert.AreEqual(2.5f, settings.emitterIntensity);
            Assert.IsFalse(settings.shadowRays);
        }

        [Test]
        public void NormalPackingSurvivesTheRoundTrip()
        {
            Vector3[] directions =
            {
                Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back,
                new Vector3(1f, 1f, 1f).normalized,
                new Vector3(-1f, 2f, -3f).normalized,
                new Vector3(0.3f, -0.9f, 0.31f).normalized,
                new Vector3(-0.7f, -0.7f, -0.14f).normalized
            };

            for (int index = 0; index < directions.Length; index++)
            {
                Vector3 decoded = BasisGlobalIlluminationRayScene.UnpackNormal(BasisGlobalIlluminationRayScene.PackNormal(directions[index]));
                Assert.AreEqual(1f, decoded.magnitude, 0.001f, directions[index].ToString());
                Assert.Greater(Vector3.Dot(decoded, directions[index]), 0.999f, directions[index].ToString());
            }
        }

        [Test]
        public void ArenaReusesAReleasedBlock()
        {
            using (BasisGlobalIlluminationRayArena arena = new BasisGlobalIlluminationRayArena("test"))
            {
                BasisGlobalIlluminationRayArena.Block first = arena.Allocate(64);
                BasisGlobalIlluminationRayArena.Block second = arena.Allocate(32);
                Assert.AreEqual(0, first.Offset);
                Assert.AreEqual(64, second.Offset);

                arena.Release(first);
                BasisGlobalIlluminationRayArena.Block third = arena.Allocate(64);
                Assert.AreEqual(first.Offset, third.Offset, "a freed block of the right size should be reused");
                Assert.AreEqual(96, arena.Used);
            }
        }

        [Test]
        public void ArenaRewindsWhenTheLastBlockIsReleased()
        {
            using (BasisGlobalIlluminationRayArena arena = new BasisGlobalIlluminationRayArena("test"))
            {
                BasisGlobalIlluminationRayArena.Block first = arena.Allocate(16);
                BasisGlobalIlluminationRayArena.Block second = arena.Allocate(16);
                arena.Release(second);
                Assert.AreEqual(16, arena.Used);
                arena.Release(first);
                Assert.AreEqual(0, arena.Used);
                Assert.AreEqual(0, arena.FreeBlocks);
            }
        }

        [Test]
        public void ArenaCoalescesAdjacentFreeBlocks()
        {
            using (BasisGlobalIlluminationRayArena arena = new BasisGlobalIlluminationRayArena("test"))
            {
                BasisGlobalIlluminationRayArena.Block first = arena.Allocate(16);
                BasisGlobalIlluminationRayArena.Block second = arena.Allocate(16);
                arena.Allocate(16);

                arena.Release(first);
                arena.Release(second);
                Assert.AreEqual(1, arena.FreeBlocks, "two neighbouring holes should merge into one");

                BasisGlobalIlluminationRayArena.Block merged = arena.Allocate(32);
                Assert.AreEqual(0, merged.Offset);
            }
        }

        [Test]
        public void ArenaGrowsPastItsStartingCapacity()
        {
            using (BasisGlobalIlluminationRayArena arena = new BasisGlobalIlluminationRayArena("test"))
            {
                BasisGlobalIlluminationRayArena.Block block = arena.Allocate(5000);
                Assert.IsTrue(block.IsValid);
                Assert.GreaterOrEqual(arena.Capacity, 5000);
                arena.Data[block.Offset + 4999] = 7u;
                Assert.AreEqual(7u, arena.Data[block.Offset + 4999]);
            }
        }

        [Test]
        public void ZeroLengthAllocationIsRefused()
        {
            using (BasisGlobalIlluminationRayArena arena = new BasisGlobalIlluminationRayArena("test"))
            {
                Assert.IsFalse(arena.Allocate(0).IsValid);
                Assert.AreEqual(0, arena.Used);
            }
        }

        [Test]
        public void DirectionalLightsAreDescribedWithNoRange()
        {
            GameObject host = new GameObject("BasisGIRayTestDirectional");
            try
            {
                Light light = host.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 2f;
                light.color = Color.white;
                light.shadows = LightShadows.Soft;
                host.transform.rotation = Quaternion.Euler(45f, 0f, 0f);

                BasisGlobalIlluminationRayLight described = BasisGlobalIlluminationRayLights.Describe(light, BasisGlobalIlluminationRayLightSettings.Default);
                Assert.AreEqual(BasisGlobalIlluminationRayLight.TypeDirectional, described.direction.w);
                Assert.AreEqual(0f, described.position.w, "a directional light has no range");
                Assert.AreEqual(1f, described.color.w, "shadow casting lights ask for a shadow ray");
                Assert.Greater(described.color.x, 0f);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void SpotLightsCarryTheirConeAsScaleAndOffset()
        {
            GameObject host = new GameObject("BasisGIRayTestSpot");
            try
            {
                Light light = host.AddComponent<Light>();
                light.type = LightType.Spot;
                light.intensity = 1f;
                light.range = 12f;
                light.spotAngle = 60f;
                light.innerSpotAngle = 30f;

                BasisGlobalIlluminationRayLight described = BasisGlobalIlluminationRayLights.Describe(light, BasisGlobalIlluminationRayLightSettings.Default);
                Assert.AreEqual(BasisGlobalIlluminationRayLight.TypeSpot, described.direction.w);
                Assert.AreEqual(12f, described.position.w);
                Assert.AreEqual(1f / 144f, described.spot.z, 0.0001f, "the kernel needs one over range squared");

                // A direction on the cone edge attenuates to zero, one down the axis to one.
                float cosOuter = Mathf.Cos(30f * Mathf.Deg2Rad);
                Assert.AreEqual(0f, Mathf.Clamp01(cosOuter * described.spot.x + described.spot.y), 0.001f);
                Assert.AreEqual(1f, Mathf.Clamp01(1f * described.spot.x + described.spot.y), 0.001f);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void LightsWithNoIndirectContributionAreDropped()
        {
            GameObject host = new GameObject("BasisGIRayTestDropped");
            try
            {
                Light light = host.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 5f;
                light.intensity = 1f;
                Assert.IsTrue(BasisGlobalIlluminationRayLights.Contributes(light, BasisGlobalIlluminationRayLightSettings.Default));

                light.bounceIntensity = 0f;
                Assert.IsFalse(BasisGlobalIlluminationRayLights.Contributes(light, BasisGlobalIlluminationRayLightSettings.Default),
                    "an indirect multiplier of zero means the light does not bounce");

                light.bounceIntensity = 1f;
                light.intensity = 0f;
                Assert.IsFalse(BasisGlobalIlluminationRayLights.Contributes(light, BasisGlobalIlluminationRayLightSettings.Default));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void AreaLightsAreNotTraced()
        {
            GameObject host = new GameObject("BasisGIRayTestArea");
            try
            {
                Light light = host.AddComponent<Light>();
                light.type = LightType.Rectangle;
                Assert.IsFalse(BasisGlobalIlluminationRayLights.IsSupportedType(light));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void SkinnedRenderersAreOnlyTracedWhenTheModeAsksForThem()
        {
            GameObject host = new GameObject("BasisGIRayTestSkinned");
            try
            {
                SkinnedMeshRenderer skinned = host.AddComponent<SkinnedMeshRenderer>();
                Assert.IsFalse(BasisGlobalIlluminationRayScene.IsSupportedRendererType(skinned, BasisGlobalIlluminationRaySkinnedMode.Off));
                Assert.IsTrue(BasisGlobalIlluminationRayScene.IsSupportedRendererType(skinned, BasisGlobalIlluminationRaySkinnedMode.Static));
                Assert.IsTrue(BasisGlobalIlluminationRayScene.IsSupportedRendererType(skinned, BasisGlobalIlluminationRaySkinnedMode.Dynamic));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void ExcludedRenderersStayOutOfTheStructure()
        {
            GameObject host = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                MeshRenderer renderer = host.GetComponent<MeshRenderer>();
                BasisGlobalIlluminationRaySceneSettings settings = BasisGlobalIlluminationRaySceneSettings.Default;
                Assert.IsTrue(BasisGlobalIlluminationRayScene.ShouldInclude(renderer, settings));

                host.AddComponent<BasisGlobalIlluminationRayExclude>();
                Assert.IsFalse(BasisGlobalIlluminationRayScene.ShouldInclude(renderer, settings));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void LayerMaskFiltersRenderers()
        {
            GameObject host = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                MeshRenderer renderer = host.GetComponent<MeshRenderer>();
                BasisGlobalIlluminationRaySceneSettings settings = BasisGlobalIlluminationRaySceneSettings.Default;
                settings.layerMask = 0;
                Assert.IsFalse(BasisGlobalIlluminationRayScene.ShouldInclude(renderer, settings));

                settings.layerMask = 1 << host.layer;
                Assert.IsTrue(BasisGlobalIlluminationRayScene.ShouldInclude(renderer, settings));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void ShadowCastersOnlyDropsRenderersThatCastNothing()
        {
            GameObject host = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                MeshRenderer renderer = host.GetComponent<MeshRenderer>();
                renderer.shadowCastingMode = ShadowCastingMode.Off;

                BasisGlobalIlluminationRaySceneSettings settings = BasisGlobalIlluminationRaySceneSettings.Default;
                Assert.IsTrue(BasisGlobalIlluminationRayScene.ShouldInclude(renderer, settings), "by default a bounce surface need not cast shadows");

                settings.shadowCastersOnly = true;
                Assert.IsFalse(BasisGlobalIlluminationRayScene.ShouldInclude(renderer, settings));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void MeshesWithNoGeometryAreRefused()
        {
            Assert.IsFalse(BasisGlobalIlluminationRayScene.IsUsableMesh(null));
            Mesh empty = new Mesh();
            try
            {
                Assert.IsFalse(BasisGlobalIlluminationRayScene.IsUsableMesh(empty));
            }
            finally
            {
                Object.DestroyImmediate(empty);
            }
        }

        [Test]
        public void EmissiveSurfacesCanBeTurnedOff()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) { Assert.Ignore("No lit shader available in this project to build a material from."); }

            Material material = new Material(shader);
            try
            {
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                material.EnableKeyword("_EMISSION");
                if (material.HasProperty("_EmissionColor")) { material.SetColor("_EmissionColor", new Color(2f, 1f, 0.5f)); }
                if (material.HasProperty("_EmissionEnabled")) { material.SetFloat("_EmissionEnabled", 1f); }

                BasisGlobalIlluminationRaySceneSettings settings = BasisGlobalIlluminationRaySceneSettings.Default;
                settings.textureAlbedo = false;

                BasisGlobalIlluminationRayScene.ReadSurface(material, settings, null, out Color _, out Color emissive);
                Assert.Greater(emissive.r, 0f);

                settings.emissiveSurfaces = false;
                BasisGlobalIlluminationRayScene.ReadSurface(material, settings, null, out Color _, out Color dark);
                Assert.AreEqual(0f, dark.r);
                Assert.AreEqual(0f, dark.g);
                Assert.AreEqual(0f, dark.b);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void BlackEmissiveMaterialsEmitNothing()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) { Assert.Ignore("No lit shader available in this project to build a material from."); }

            Material material = new Material(shader);
            try
            {
                // The colour, not the author-time flag. A surface emits nothing because it is black, and
                // that is the one test of it that stays true when the colour is driven at runtime.
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                material.EnableKeyword("_EMISSION");
                if (material.HasProperty("_EmissionColor")) { material.SetColor("_EmissionColor", Color.black); }

                BasisGlobalIlluminationRayScene.ReadSurface(material, BasisGlobalIlluminationRaySceneSettings.Default, null, out Color _, out Color emission);
                Assert.AreEqual(0f, emission.r);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        /// <summary>
        /// The regression behind "emission raised at runtime never reaches the bounce". globalIlluminationFlags
        /// is stamped by the shader GUI when the material is authored and never refreshed afterwards, so a
        /// material that was not emissive at author time carries EmissiveIsBlack for the rest of its life.
        /// Reading emission through that flag meant a surface could be visibly glowing in the frame and
        /// contribute nothing at all to the traced gather.
        /// </summary>
        [Test]
        public void EmissionRaisedAtRuntimeIsSeenThroughAStaleAuthoredFlag()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) { Assert.Ignore("No lit shader available in this project to build a material from."); }

            Material material = new Material(shader);
            try
            {
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                material.EnableKeyword("_EMISSION");
                if (material.HasProperty("_EmissionColor")) { material.SetColor("_EmissionColor", new Color(3f, 2f, 1f)); }
                if (material.HasProperty("_EmissionEnabled")) { material.SetFloat("_EmissionEnabled", 1f); }

                BasisGlobalIlluminationRaySceneSettings settings = BasisGlobalIlluminationRaySceneSettings.Default;
                settings.textureAlbedo = false;

                BasisGlobalIlluminationRayScene.ReadSurface(material, settings, null, out Color _, out Color emission);
                Assert.Greater(emission.r, 0f, "a runtime emission change is invisible to the traced gather");
                Assert.Greater(emission.r, emission.b, "the emission colour was not carried through");
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        /// <summary>
        /// The other half of that trade: URP's Lit multiplies emission by the _EMISSION keyword, so a
        /// material with the box unchecked emits nothing however bright the leftover colour is. Dropping the
        /// authored flag must not start lighting rooms from surfaces that are black on screen.
        /// </summary>
        [Test]
        public void EmissionIsIgnoredWhileTheShaderKeywordIsOff()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) { Assert.Ignore("No lit shader available in this project to build a material from."); }

            Material material = new Material(shader);
            try
            {
                if (!material.shader.keywordSpace.FindKeyword("_EMISSION").isValid)
                {
                    Assert.Ignore("This shader declares no _EMISSION keyword, so there is no switch to read.");
                }

                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                material.DisableKeyword("_EMISSION");
                if (material.HasProperty("_EmissionColor")) { material.SetColor("_EmissionColor", Color.white); }

                BasisGlobalIlluminationRayScene.ReadSurface(material, BasisGlobalIlluminationRaySceneSettings.Default, null, out Color _, out Color emission);
                Assert.AreEqual(0f, emission.r);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        /// <summary>
        /// A MaterialPropertyBlock is how emission is driven at runtime, and it never touches the material
        /// the renderer shares. Reading the material alone left a surface pinned at its authored colour
        /// while the frame showed it pulsing, so none of it ever reached the bounce.
        /// </summary>
        [Test]
        public void EmissionDrivenByAPropertyBlockReachesTheGather()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) { Assert.Ignore("No lit shader available in this project to build a material from."); }

            GameObject host = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material material = new Material(shader);
            try
            {
                material.EnableKeyword("_EMISSION");
                if (material.HasProperty("_EmissionColor")) { material.SetColor("_EmissionColor", Color.black); }

                MeshRenderer renderer = host.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = material;

                BasisGlobalIlluminationRaySceneSettings settings = BasisGlobalIlluminationRaySceneSettings.Default;
                settings.textureAlbedo = false;

                BasisGlobalIlluminationRayScene.ReadSurface(material, renderer, 0, settings, null, out Color _, out Color dark);
                Assert.AreEqual(0f, dark.r, "the material is black and nothing overrides it yet");

                MaterialPropertyBlock block = new MaterialPropertyBlock();
                block.SetColor("_EmissionColor", new Color(4f, 1f, 0.25f));
                renderer.SetPropertyBlock(block);

                BasisGlobalIlluminationRayScene.ReadSurface(material, renderer, 0, settings, null, out Color _, out Color lit);
                Assert.Greater(lit.r, 0f, "a property block drove emission and the gather never saw it");
                Assert.Greater(lit.r, lit.b, "the block's emission colour was not carried through");
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void APropertyBlockOverridesTheMaterialAlbedo()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) { Assert.Ignore("No lit shader available in this project to build a material from."); }

            GameObject host = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material material = new Material(shader);
            try
            {
                if (material.HasProperty("_BaseColor")) { material.SetColor("_BaseColor", Color.white); }
                if (material.HasProperty("_Color")) { material.SetColor("_Color", Color.white); }

                MeshRenderer renderer = host.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = material;

                BasisGlobalIlluminationRaySceneSettings settings = BasisGlobalIlluminationRaySceneSettings.Default;
                settings.textureAlbedo = false;

                MaterialPropertyBlock block = new MaterialPropertyBlock();
                block.SetColor(material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color", new Color(0.2f, 0.8f, 0.4f));
                renderer.SetPropertyBlock(block);

                BasisGlobalIlluminationRayScene.ReadSurface(material, renderer, 0, settings, null, out Color albedo, out Color _);
                Assert.Greater(albedo.g, albedo.r, "the block's base colour was not carried through");
                Assert.Greater(albedo.g, albedo.b, "the block's base colour was not carried through");
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void AlbedoIsClampedIntoRange()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) { Assert.Ignore("No lit shader available in this project to build a material from."); }

            Material material = new Material(shader);
            try
            {
                if (material.HasProperty("_BaseColor")) { material.SetColor("_BaseColor", new Color(4f, -1f, 0.5f)); }
                else if (material.HasProperty("_Color")) { material.SetColor("_Color", new Color(4f, -1f, 0.5f)); }

                BasisGlobalIlluminationRaySceneSettings settings = BasisGlobalIlluminationRaySceneSettings.Default;
                settings.textureAlbedo = false;
                BasisGlobalIlluminationRayScene.ReadSurface(material, settings, null, out Color albedo, out Color _);

                // A bounce that multiplies throughput by more than one never converges.
                Assert.AreEqual(1f, albedo.r);
                Assert.AreEqual(0f, albedo.g);
                Assert.AreEqual(0.5f, albedo.b, 0.001f);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void NullMaterialsBounceWhiteAndEmitNothing()
        {
            BasisGlobalIlluminationRayScene.ReadSurface(null, BasisGlobalIlluminationRaySceneSettings.Default, null, out Color albedo, out Color emission);
            Assert.AreEqual(Color.white, albedo);
            Assert.AreEqual(0f, emission.r + emission.g + emission.b);
        }

        [Test]
        public void SkyFallbackIsSilentWhenTheVolumeAsksForNone()
        {
            BasisGlobalIlluminationRayTracer.SkyBinding sky = BasisGlobalIlluminationRayTracer.ResolveSky(BasisGlobalIlluminationFallback.None, 1f);
            Assert.IsFalse(sky.IsValid);
            Assert.AreEqual(0f, sky.Intensity);
        }

        [Test]
        public void SkyFallbackIsSilentAtZeroIntensity()
        {
            BasisGlobalIlluminationRayTracer.SkyBinding sky = BasisGlobalIlluminationRayTracer.ResolveSky(BasisGlobalIlluminationFallback.Sky, 0f);
            Assert.IsFalse(sky.IsValid);
        }

        [Test]
        public void SkyFallbackReadsABlurrierMipThanTheProbeFallback()
        {
            BasisGlobalIlluminationRayTracer.SkyBinding sky = BasisGlobalIlluminationRayTracer.ResolveSky(BasisGlobalIlluminationFallback.Sky, 1f);
            BasisGlobalIlluminationRayTracer.SkyBinding probe = BasisGlobalIlluminationRayTracer.ResolveSky(BasisGlobalIlluminationFallback.ReflectionProbe, 1f);
            if (!sky.IsValid || !probe.IsValid) { Assert.Ignore("This project has no default reflection cubemap to bind."); }
            Assert.GreaterOrEqual(sky.Mip, probe.Mip);
        }
    }
}
