using NUnit.Framework;
using UnityEngine;

namespace Basis.Rendering.RTAO.Tests
{
    public sealed class BasisRTAOFeatureTests
    {
        private BasisRTAOFeature feature;

        [SetUp]
        public void SetUp()
        {
            feature = ScriptableObject.CreateInstance<BasisRTAOFeature>();
            ResetStatics();
        }

        [TearDown]
        public void TearDown()
        {
            ResetStatics();
            if (feature != null)
                Object.DestroyImmediate(feature);
            feature = null;
        }

        private static void ResetStatics()
        {
            BasisRTAOFeature.CameraFilter = null;
            BasisRTAOFeature.RuntimeEnabled = true;
            BasisRTAOFeature.HasQualityOverride = false;
            BasisRTAOFeature.HasIntensityOverride = false;
            BasisRTAOFeature.HasRadiusOverride = false;
            BasisRTAOFeature.HasTracingModeOverride = false;
            BasisRTAOFeature.HasSkinnedModeOverride = false;
            BasisRTAOFeature.HasDebugViewOverride = false;
            BasisRTAOFeature.HasDirectStrengthOverride = false;
            BasisRTAOFeature.HasDenoisePassesOverride = false;
            BasisRTAOFeature.AllowSecondaryCameras = true;
            BasisRTAOFeature.ViewerPosition = null;
            BasisRTAOFeature.HasApplyModeOverride = false;
        }

        [Test]
        public void AcceptsEveryCameraWithoutAFilter()
        {
            Assert.IsTrue(BasisRTAOFeature.AcceptsCamera(null));
        }

        [Test]
        public void FilterDecidesWhichCameraRuns()
        {
            GameObject wanted = new GameObject("BasisRTAOWanted", typeof(Camera));
            GameObject mirror = new GameObject("BasisRTAOMirror", typeof(Camera));
            try
            {
                Camera main = wanted.GetComponent<Camera>();
                BasisRTAOFeature.CameraFilter = candidate => ReferenceEquals(candidate, main);

                Assert.IsTrue(BasisRTAOFeature.AcceptsCamera(main));
                Assert.IsFalse(BasisRTAOFeature.AcceptsCamera(mirror.GetComponent<Camera>()),
                    "Mirrors and capture cameras are Game cameras too, so identity is the only safe gate.");
            }
            finally
            {
                Object.DestroyImmediate(wanted);
                Object.DestroyImmediate(mirror);
            }
        }

        [Test]
        public void QualityFieldDrivesTheResolvedSettings()
        {
            SerializedFieldSetter.Set(feature, "overrideQualityPreset", false);
            SerializedFieldSetter.Set(feature, "quality", BasisRTAOQuality.Ultra);

            BasisRTAOSettings resolved = feature.ResolveSettings();
            Assert.AreEqual(BasisRTAOSettings.FromQuality(BasisRTAOQuality.Ultra).raysPerPixel, resolved.raysPerPixel);
        }

        [Test]
        public void RuntimeQualityOverrideBeatsTheAuthoredQuality()
        {
            SerializedFieldSetter.Set(feature, "overrideQualityPreset", false);
            SerializedFieldSetter.Set(feature, "quality", BasisRTAOQuality.Ultra);

            BasisRTAOFeature.HasQualityOverride = true;
            BasisRTAOFeature.QualityOverride = BasisRTAOQuality.Low;

            BasisRTAOSettings resolved = feature.ResolveSettings();
            Assert.AreEqual(BasisRTAOSettings.FromQuality(BasisRTAOQuality.Low).raysPerPixel, resolved.raysPerPixel,
                "The settings module drives quality at runtime, so its override has to win over the authored value.");
        }

        [Test]
        public void ManualSettingsIgnoreTheQualityPreset()
        {
            BasisRTAOSettings authored = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Medium);
            authored.raysPerPixel = 9;
            SerializedFieldSetter.Set(feature, "overrideQualityPreset", true);
            SerializedFieldSetter.Set(feature, "settings", authored);
            SerializedFieldSetter.Set(feature, "quality", BasisRTAOQuality.Low);

            Assert.AreEqual(9, feature.ResolveSettings().raysPerPixel);
        }

        [Test]
        public void IntensityAndRadiusOverridesApply()
        {
            BasisRTAOFeature.HasIntensityOverride = true;
            BasisRTAOFeature.IntensityOverride = 2.25f;
            BasisRTAOFeature.HasRadiusOverride = true;
            BasisRTAOFeature.RadiusOverride = 0.75f;

            BasisRTAOSettings resolved = feature.ResolveSettings();
            Assert.AreEqual(2.25f, resolved.intensity, 1e-4f);
            Assert.AreEqual(0.75f, resolved.radius, 1e-4f);
        }

        [Test]
        public void DirectStrengthOverrideApplies()
        {
            BasisRTAOFeature.HasDirectStrengthOverride = true;
            BasisRTAOFeature.DirectStrengthOverride = 0.8f;

            Assert.AreEqual(0.8f, feature.ResolveSettings().directLightingStrength, 1e-4f,
                "URP only lerps direct light toward the occlusion by this much, so it is the knob that decides how much of the buffer reaches a brightly lit surface.");
        }

        [Test]
        public void DirectStrengthOverrideIsClamped()
        {
            BasisRTAOFeature.HasDirectStrengthOverride = true;
            BasisRTAOFeature.DirectStrengthOverride = 5f;
            Assert.AreEqual(1f, feature.ResolveSettings().directLightingStrength, 1e-4f);

            BasisRTAOFeature.DirectStrengthOverride = -3f;
            Assert.AreEqual(0f, feature.ResolveSettings().directLightingStrength, 1e-4f);
        }

        [Test]
        public void DenoisePassesOverrideApplies()
        {
            BasisRTAOFeature.HasDenoisePassesOverride = true;
            BasisRTAOFeature.DenoisePassesOverride = 0;
            Assert.AreEqual(0, feature.ResolveSettings().denoisePasses, "The settings dropdown must be able to switch the filter off.");

            BasisRTAOFeature.DenoisePassesOverride = 3;
            Assert.AreEqual(3, feature.ResolveSettings().denoisePasses);
        }

        [Test]
        public void ResolvedSettingsAreAlwaysValidated()
        {
            BasisRTAOFeature.HasIntensityOverride = true;
            BasisRTAOFeature.IntensityOverride = 999f;

            Assert.LessOrEqual(feature.ResolveSettings().intensity, 4f,
                "A bad runtime override must be clamped before it reaches the shader.");
        }

        [Test]
        public void ViewPlaneReadsLinearDepthFromAViewMatrix()
        {
            GameObject go = new GameObject("BasisRTAOViewPlaneCamera");
            try
            {
                Camera camera = go.AddComponent<Camera>();
                camera.transform.position = new Vector3(2f, 1f, -5f);
                camera.transform.rotation = Quaternion.Euler(15f, 40f, 0f);

                Vector4 plane = BasisRTAOPass.ViewPlaneOf(camera.worldToCameraMatrix);
                Vector3 forward = camera.transform.forward;
                Vector3 point = camera.transform.position + forward * 7f + camera.transform.right * 1.5f;

                float depth = plane.x * point.x + plane.y * point.y + plane.z * point.z + plane.w;
                Assert.AreEqual(7f, depth, 1e-3f,
                    "The denoiser compares this depth against the history, so it must be the distance along the view axis for perspective and orthographic alike.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ViewPlaneNormalPointsAlongTheCameraForward()
        {
            GameObject go = new GameObject("BasisRTAOViewPlaneForward");
            try
            {
                Camera camera = go.AddComponent<Camera>();
                camera.transform.rotation = Quaternion.Euler(-22f, 130f, 7f);

                Vector4 plane = BasisRTAOPass.ViewPlaneOf(camera.worldToCameraMatrix);
                Vector3 normal = new Vector3(plane.x, plane.y, plane.z);

                Assert.AreEqual(1f, normal.magnitude, 1e-4f);
                Assert.Greater(Vector3.Dot(normal, camera.transform.forward), 0.9999f);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ViewPlaneIsZeroAtTheCameraOrigin()
        {
            GameObject go = new GameObject("BasisRTAOViewPlaneOrigin");
            try
            {
                Camera camera = go.AddComponent<Camera>();
                camera.transform.position = new Vector3(-3f, 6f, 2f);
                camera.transform.rotation = Quaternion.Euler(9f, -71f, 0f);

                Vector4 plane = BasisRTAOPass.ViewPlaneOf(camera.worldToCameraMatrix);
                Vector3 origin = camera.transform.position;
                float depth = plane.x * origin.x + plane.y * origin.y + plane.z * origin.z + plane.w;

                Assert.AreEqual(0f, depth, 1e-3f);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ApplyModeDefaultsToLighting()
        {
            Assert.AreEqual(BasisRTAOApplyMode.Lighting, feature.ApplyMode,
                "Feeding URP's lighting is the default: it is the physically honest path, and it leaves a material's own occlusion map able to clamp the result rather than dimming light that already carries its own shadowing.");
        }

        [Test]
        public void ApplyModeOverrideWins()
        {
            BasisRTAOFeature.HasApplyModeOverride = true;
            BasisRTAOFeature.ApplyModeOverride = BasisRTAOApplyMode.AfterOpaque;
            Assert.AreEqual(BasisRTAOApplyMode.AfterOpaque, feature.ApplyMode);
        }

        [Test]
        public void QualityDrivesCostButNotLook()
        {
            BasisRTAOSettings authored = BasisRTAOSettings.FromQuality(BasisRTAOQuality.Medium);
            authored.intensity = 2.75f;
            authored.radius = 0.6f;
            authored.directLightingStrength = 0.9f;
            SerializedFieldSetter.Set(feature, "overrideQualityPreset", false);
            SerializedFieldSetter.Set(feature, "settings", authored);

            BasisRTAOFeature.HasQualityOverride = true;
            BasisRTAOFeature.QualityOverride = BasisRTAOQuality.Ultra;

            BasisRTAOSettings resolved = feature.ResolveSettings();

            Assert.AreEqual(BasisRTAOSettings.FromQuality(BasisRTAOQuality.Ultra).raysPerPixel, resolved.raysPerPixel,
                "Occlusion Quality is a performance tier, so it owns the ray count.");
            Assert.AreEqual(2.75f, resolved.intensity, 1e-4f,
                "It has no business owning the look. Discarding the authored intensity is exactly why dragging it on the feature did nothing.");
            Assert.AreEqual(0.6f, resolved.radius, 1e-4f);
            Assert.AreEqual(0.9f, resolved.directLightingStrength, 1e-4f);
        }

        // SceneSettingsFollowTheOcclusionQuality, AuthoredSceneSettingsAreLeftAlone,
        // TheBudgetOverridePinsItAgainstQuality and ResolvedSceneSettingsAreValidated all measured the
        // per frame re-pose budget, which went with Static and Dynamic. Avatars are proxy capsules now and
        // cost one transform update per limb, so there is no budget for a quality level, an authored value
        // or a developer slider to disagree about.

        [Test]
        public void SecondaryCamerasAreAllowedByDefault()
        {
            Assert.IsTrue(BasisRTAOFeature.AllowSecondaryCameras,
                "A mirror showing the room without its contact shadows reads as a different room, so mirrors and the handheld camera are included unless the player turns them off.");
        }

        [Test]
        public void TheCameraFilterDecidesSecondaryCameras()
        {
            GameObject main = new GameObject("BasisRTAOMainCam", typeof(Camera));
            GameObject mirror = new GameObject("BasisRTAOMirrorCam", typeof(Camera));
            try
            {
                Camera mainCamera = main.GetComponent<Camera>();
                Camera mirrorCamera = mirror.GetComponent<Camera>();

                BasisRTAOFeature.CameraFilter = candidate =>
                    ReferenceEquals(candidate, mainCamera) || BasisRTAOFeature.AllowSecondaryCameras;

                BasisRTAOFeature.AllowSecondaryCameras = true;
                Assert.IsTrue(BasisRTAOFeature.AcceptsCamera(mainCamera));
                Assert.IsTrue(BasisRTAOFeature.AcceptsCamera(mirrorCamera));

                BasisRTAOFeature.AllowSecondaryCameras = false;
                Assert.IsTrue(BasisRTAOFeature.AcceptsCamera(mainCamera), "The player's own view is never dropped.");
                Assert.IsFalse(BasisRTAOFeature.AcceptsCamera(mirrorCamera));
            }
            finally
            {
                BasisRTAOFeature.AllowSecondaryCameras = true;
                Object.DestroyImmediate(main);
                Object.DestroyImmediate(mirror);
            }
        }

        [Test]
        public void ViewerPositionFallsBackWhenNothingSetsIt()
        {
            Assert.IsNull(BasisRTAOFeature.ViewerPosition,
                "With no framework installed the pass falls back to the recording camera, so this must start empty.");
        }

        [Test]
        public void MarkSceneDirtyIsSafeBeforeAnythingHasRendered()
        {
            Assert.DoesNotThrow(() => BasisRTAOFeature.MarkSceneDirty(),
                "The framework calls this from avatar lifecycle events, which fire long before the first frame is recorded.");
        }

        [Test]
        public void SupportFlagMatchesTheDeviceCapability()
        {
            Assert.AreEqual(SystemInfo.supportsRayTracing, BasisRTAOFeature.IsSupported);
        }
    }
}
