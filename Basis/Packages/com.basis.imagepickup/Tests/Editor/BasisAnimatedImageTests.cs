using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Basis.Scripts.Drivers;
using Basis.Scripts.Networking;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Basis.ImagePickup.Tests
{
    public class BasisAnimatedImageTests
    {
        // The manager is a static service, so registered players and scheduler resources would
        // otherwise leak from one case into the next.
        [SetUp]
        public void ResetImagePickupManager()
        {
            BasisImagePickupManager.Shutdown();
        }

        [Test]
        public void NativeDataAndPlaybackWork()
        {
            using BasisAnimatedImageData data = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    1,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Red }
                ),
                new BasisAnimatedImageFrameSource(
                    new RectInt(1, 0, 1, 1),
                    100000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Green }
                )
            );

            Assert.That(data.FrameCount, Is.EqualTo(2));
            Assert.That(
                data.GetFrame(0).DurationMicroseconds,
                Is.EqualTo(
                    BasisImagePickupSettings.MinAnimationFrameDurationMicroseconds
                )
            );
            Assert.That(data.DecodedFramePixels, Is.EqualTo(2));
            data.GetPlaybackState(data.TotalDurationMicroseconds + 1, out long play, out int frame, out bool completed);
            Assert.That(play, Is.EqualTo(1));
            Assert.That(frame, Is.EqualTo(0));
            Assert.That(completed, Is.False);
        }

        [Test]
        public void BurstCpuCanvasRestoresPrevious()
        {
            using BasisAnimatedImageData data = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 2, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Red, Green }
                ),
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.Previous,
                    new[] { Blue }
                ),
                new BasisAnimatedImageFrameSource(
                    new RectInt(1, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { White }
                )
            );
            using var canvas = new BasisAnimatedImageCpuCanvas(data);

            canvas.UpdateToState(0, 2);
            Color32[] pixels = ((Texture2D)canvas.OutputTexture).GetPixels32();
            Assert.That(pixels[0], Is.EqualTo(Red));
            Assert.That(pixels[1], Is.EqualTo(White));
        }

        [Test]
        public void CpuCompositionAdvancesWithinTransitionBudget()
        {
            using BasisAnimatedImageData data = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Red }
                ),
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Green }
                ),
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Blue }
                )
            );
            using var canvas = new BasisAnimatedImageCpuCanvas(data);

            Assert.That(canvas.UpdateToState(0, 2, 1, long.MaxValue, out _), Is.EqualTo(1));
            Assert.That(((Texture2D)canvas.OutputTexture).GetPixels32()[0], Is.EqualTo(Red));
            Assert.That(canvas.UpdateToState(0, 2, 1, long.MaxValue, out _), Is.EqualTo(1));
            Assert.That(((Texture2D)canvas.OutputTexture).GetPixels32()[0], Is.EqualTo(Green));
            Assert.That(canvas.UpdateToState(0, 2, 1, long.MaxValue, out _), Is.EqualTo(1));
            Assert.That(((Texture2D)canvas.OutputTexture).GetPixels32()[0], Is.EqualTo(Blue));
        }

        [Test]
        public void FrameAtlasUploadsIncrementally()
        {
            using BasisAnimatedImageData data = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Red }
                )
            );
            using var atlas = new BasisAnimationFrameAtlas(data);

            Assert.That(atlas.IsReady, Is.False);
            Assert.That(atlas.TryBuildNextPage(0, out long blockedPixels), Is.False);
            Assert.That(blockedPixels, Is.EqualTo(0));
            Assert.That(atlas.TryBuildNextPage(long.MaxValue, out long uploadedPixels), Is.True);
            Assert.That(uploadedPixels, Is.GreaterThan(0));
            Assert.That(atlas.IsReady, Is.True);
            Assert.That(atlas.GetPage(0), Is.Not.Null);
        }

        [Test]
        public void AnimatedPlayerCreatesResourcesLazilyAndSuspendsOffscreen()
        {
            var host = new GameObject("BasisAnimatedImagePlayerTest");
            var pickup = host.AddComponent<BasisImagePickupObject>();
            var player = host.AddComponent<BasisAnimatedImagePlayer>();
            BasisAnimatedImageData data = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 2, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Red, Green }
                ),
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 2, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Blue, White }
                )
            );
            var commands = new CommandBuffer();
            bool initialized = false;
            try
            {
                initialized = player.Initialize(data, pickup, 1, true);
                Assert.That(initialized, Is.True);
                Assert.That(player.HasAllocatedCompositor, Is.False);

                int transitionsRemaining = 16;
                long pixelsRemaining = 1024;
                bool gpuCommandsAdded = false;
                player.Schedule(commands, 1, ref transitionsRemaining, ref pixelsRemaining, ref gpuCommandsAdded);
                player.FlushPendingJobs();

                Assert.That(player.HasAllocatedCompositor, Is.True);
                float resumeTime =
                    Time.unscaledTime
                    + BasisImagePickupSettings.AnimationOffscreenResourceReleaseSeconds
                    + 0.1f;
                player.UpdateVisibilityState(false, resumeTime);
                Assert.That(player.HasAllocatedCompositor, Is.False);

                player.UpdateVisibilityState(true, resumeTime);
                transitionsRemaining = 16;
                pixelsRemaining = 1024;
                player.Schedule(
                    commands,
                    500001,
                    ref transitionsRemaining,
                    ref pixelsRemaining,
                    ref gpuCommandsAdded
                );
                player.FlushPendingJobs();
                Assert.That(player.HasAllocatedCompositor, Is.True);
                Assert.That(player.OutputTexture, Is.Not.Null);
                Color32[] resumedPixels = ((Texture2D)player.OutputTexture).GetPixels32();
                Assert.That(resumedPixels[0], Is.EqualTo(Blue));
                Assert.That(resumedPixels[1], Is.EqualTo(White));
            }
            finally
            {
                commands.Release();
                Object.DestroyImmediate(host);
                if (!initialized)
                    data.Dispose();
                BasisImagePickupManager.Shutdown();
            }
        }

        [Test]
        public void CompositorBudgetWarningIsGloballyThrottled()
        {
            long nextLogTimestamp = 0;
            int suppressedCount = 0;

            Assert.That(
                BasisAnimatedImagePlayer.ShouldLogCompositorBudgetWarning(
                    100,
                    30,
                    ref nextLogTimestamp,
                    ref suppressedCount,
                    out int firstSuppressed
                ),
                Is.True
            );
            Assert.That(firstSuppressed, Is.EqualTo(0));
            Assert.That(nextLogTimestamp, Is.EqualTo(130));

            Assert.That(
                BasisAnimatedImagePlayer.ShouldLogCompositorBudgetWarning(
                    101,
                    30,
                    ref nextLogTimestamp,
                    ref suppressedCount,
                    out _
                ),
                Is.False
            );
            Assert.That(
                BasisAnimatedImagePlayer.ShouldLogCompositorBudgetWarning(
                    129,
                    30,
                    ref nextLogTimestamp,
                    ref suppressedCount,
                    out _
                ),
                Is.False
            );
            Assert.That(suppressedCount, Is.EqualTo(2));

            Assert.That(
                BasisAnimatedImagePlayer.ShouldLogCompositorBudgetWarning(
                    130,
                    30,
                    ref nextLogTimestamp,
                    ref suppressedCount,
                    out int repeatedSuppressed
                ),
                Is.True
            );
            Assert.That(repeatedSuppressed, Is.EqualTo(2));
            Assert.That(suppressedCount, Is.EqualTo(0));
            Assert.That(nextLogTimestamp, Is.EqualTo(160));
        }

        [Test]
        public void CompositorPressureStartsClosestAnimationAfterReleasingFarthest()
        {
            Camera previousCamera = BasisLocalCameraDriver.CameraInstance;
            var cameraHost = new GameObject("BasisAnimatedImageCompositorCamera");
            var nearHost = new GameObject("BasisAnimatedImageCompositorNear");
            var middleHost = new GameObject("BasisAnimatedImageCompositorMiddle");
            var farHost = new GameObject("BasisAnimatedImageCompositorFar");
            BasisAnimatedImageData nearData = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Red }
                )
            );
            BasisAnimatedImageData middleData = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Green }
                )
            );
            BasisAnimatedImageData farData = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Blue }
                )
            );
            var commands = new CommandBuffer();
            bool nearInitialized = false;
            bool middleInitialized = false;
            bool farInitialized = false;
            bool compositorReservationHeld = false;
            long compositorReservation = 0;
            try
            {
                BasisLocalCameraDriver.CameraInstance = cameraHost.AddComponent<Camera>();
                nearHost.transform.position = new Vector3(0f, 0f, 1f);
                middleHost.transform.position = new Vector3(0f, 0f, 5f);
                farHost.transform.position = new Vector3(0f, 0f, 10f);

                var middlePlayer = middleHost.AddComponent<BasisAnimatedImagePlayer>();
                var farPlayer = farHost.AddComponent<BasisAnimatedImagePlayer>();
                var nearPlayer = nearHost.AddComponent<BasisAnimatedImagePlayer>();
                middleInitialized = middlePlayer.Initialize(
                    middleData,
                    middleHost.AddComponent<BasisImagePickupObject>(),
                    1,
                    true
                );
                farInitialized = farPlayer.Initialize(
                    farData,
                    farHost.AddComponent<BasisImagePickupObject>(),
                    1,
                    true
                );
                nearInitialized = nearPlayer.Initialize(
                    nearData,
                    nearHost.AddComponent<BasisImagePickupObject>(),
                    1,
                    true
                );
                Assert.That(nearInitialized && middleInitialized && farInitialized, Is.True);

                SchedulePlayerOnce(middlePlayer, commands);
                SchedulePlayerOnce(farPlayer, commands);
                Assert.That(middlePlayer.HasAllocatedCompositor, Is.True);
                Assert.That(farPlayer.HasAllocatedCompositor, Is.True);
                Assert.That(nearPlayer.HasAllocatedCompositor, Is.False);

                compositorReservation =
                    BasisImagePickupSettings.MaxResidentAnimationCompositorBytes
                    - BasisAnimatedImageData.TotalResidentCompositorBytes;
                Assert.That(compositorReservation, Is.GreaterThan(0));
                compositorReservationHeld = BasisAnimatedImageData.TryReserveCompositorBytes(
                    compositorReservation,
                    out string reservationError
                );
                Assert.That(compositorReservationHeld, Is.True, reservationError);

                SchedulePlayerOnce(nearPlayer, commands);
                Assert.That(nearPlayer.HasAllocatedCompositor, Is.False);
                Assert.That(farPlayer.HasAllocatedCompositor, Is.True);

                BasisImagePickupManager.ApplyPendingCompositorReleases();
                Assert.That(middlePlayer.HasAllocatedCompositor, Is.True);
                Assert.That(farPlayer.HasAllocatedCompositor, Is.False);

                FieldInfo startIndexField = typeof(BasisImagePickupManager).GetField(
                    "_visiblePassStartIndex",
                    BindingFlags.Static | BindingFlags.NonPublic
                );
                Assert.That(startIndexField, Is.Not.Null);
                startIndexField.SetValue(null, 0);
                BasisImagePickupManager.PrioritizeDeferredCompositorCandidate();
                Assert.That(startIndexField.GetValue(null), Is.EqualTo(2));

                SchedulePlayerOnce(nearPlayer, commands);
                Assert.That(nearPlayer.HasAllocatedCompositor, Is.True);
                Assert.That(middlePlayer.HasAllocatedCompositor, Is.True);

                BasisImagePickupManager.RequestCompositorMemory(farPlayer);
                BasisImagePickupManager.ApplyPendingCompositorReleases();
                Assert.That(nearPlayer.HasAllocatedCompositor, Is.True);
                Assert.That(middlePlayer.HasAllocatedCompositor, Is.True);
            }
            finally
            {
                if (compositorReservationHeld)
                    BasisAnimatedImageData.ReleaseCompositorBytes(compositorReservation);
                BasisLocalCameraDriver.CameraInstance = previousCamera;
                commands.Release();
                Object.DestroyImmediate(nearHost);
                Object.DestroyImmediate(middleHost);
                Object.DestroyImmediate(farHost);
                Object.DestroyImmediate(cameraHost);
                if (!nearInitialized)
                    nearData.Dispose();
                if (!middleInitialized)
                    middleData.Dispose();
                if (!farInitialized)
                    farData.Dispose();
                BasisImagePickupManager.Shutdown();
            }
        }

        [Test]
		public void ReloadableAnimatedPlayerReleasesAndRestoresDecodedFrames()
        {
            var host = new GameObject("BasisAnimatedImageReloadTest");
            var pickup = host.AddComponent<BasisImagePickupObject>();
            var player = host.AddComponent<BasisAnimatedImagePlayer>();
            BasisAnimatedImageData data = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 2, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Red, Green }
                ),
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 2, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Blue, White }
                )
            );
            long decodedFramePixels = data.DecodedFramePixels;
            long canvasPixels = (long)data.CanvasWidth * data.CanvasHeight;
            BasisNativeAnimationPayload payload = null;
            var commands = new CommandBuffer();
            bool initialized = false;
            try
            {
                using BasisBurstAnimationEncodeRequest encode =
                    new BasisBurstAnimationEncodeRequest(data);
                BasisBurstAnimationEncodeResult encoded = encode.Complete();
                Assert.That(encoded.Ok, Is.True, encoded.Error);
                payload = encoded.TakePayload();
                Assert.That(payload.AllocatedBytes, Is.EqualTo(payload.Length));

                initialized = player.Initialize(data, pickup, 1, true, payload);
                Assert.That(initialized, Is.True);
                player.ReleaseDecodedDataForMemoryPressure();
				Assert.That(player.Data, Is.Null);
                Assert.That(player.DecodedFramePixels, Is.EqualTo(decodedFramePixels));
                Assert.That(player.CanvasPixels, Is.EqualTo(canvasPixels));
                Assert.That(payload.IsCreated, Is.True);

				for (int attempt = 0; attempt < 10000 && player.Data == null; attempt++)
                {
                    int transitionsRemaining = 16;
                    long pixelsRemaining = 1024;
                    bool gpuCommandsAdded = false;
                    player.Schedule(
                        commands,
                        500001,
                        ref transitionsRemaining,
                        ref pixelsRemaining,
                        ref gpuCommandsAdded
                    );
                    JobHandle.ScheduleBatchedJobs();
                    Thread.Yield();
                }

                Assert.That(player.Data, Is.Not.Null);
                Assert.That(player.HasAllocatedCompositor, Is.True);
                Assert.That(payload.IsCreated, Is.True);
            }
            finally
            {
                commands.Release();
                player.ClearReloadPayload();
                Object.DestroyImmediate(host);
                payload?.Dispose();
                if (!initialized)
                    data.Dispose();
                BasisImagePickupManager.Shutdown();
            }
        }

        [Test]
        public void SceneViewAndPreviewCamerasDoNotDriveAnimationVisibility()
        {
            Assert.That(BasisImagePickupManager.IsSupportedVisibilityCameraType(CameraType.Game), Is.True);
            Assert.That(BasisImagePickupManager.IsSupportedVisibilityCameraType(CameraType.SceneView), Is.False);
            Assert.That(BasisImagePickupManager.IsSupportedVisibilityCameraType(CameraType.Preview), Is.False);
        }

        [Test]
        public void DepthVisibilityComputeShaderIsPackaged()
        {
            ComputeShader compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.basis.imagepickup/Resources/BasisImageDepthVisibility.compute"
            );
            Assert.That(compute, Is.Not.Null);
        }

        [Test]
        public void AnimationCompositorShaderHasRequiredPasses()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(
                "Packages/com.basis.imagepickup/Resources/BasisImageAnimationComposite.shader"
            );
            Assert.That(shader, Is.Not.Null);
            Assert.That(
                shader.passCount,
                Is.GreaterThanOrEqualTo(
                    BasisImagePickupRuntimeUtility.RequiredAnimationCompositorPassCount
                )
            );
            Assert.That(
                BasisImagePickupRuntimeUtility.CanUseAnimationCompositorShader(shader),
                Is.EqualTo(shader.isSupported)
            );
            Assert.That(
                BasisImagePickupRuntimeUtility.CanUseAnimationCompositorShader(null),
                Is.False
            );
        }

        [Test]
        public void DesktopRendererTemplatesIncludeDepthVisibilityFeature()
        {
            const string rendererRoot =
                "Packages/com.basis.setup/Templates~/Assets/Basis/Settings/"
                + "Unity Rendering Defaults/";
            string[] rendererPaths =
            {
                rendererRoot + "DesktopRenderer.asset",
                rendererRoot + "DesktopRendererCamera.asset",
            };
            int rendererCount = rendererPaths.Length;
            for (int i = 0; i < rendererCount; i++)
            {
                string rendererYaml = File.ReadAllText(rendererPaths[i]);
                Assert.That(rendererYaml, Does.Contain("BasisImageDepthVisibilityFeature"));
                Assert.That(rendererYaml, Does.Contain("820949bd2ba14c7699a6b5d0dbee4869"));
                Assert.That(rendererYaml, Does.Contain("- {fileID: -7061326814219087741}"));
            }
        }

        [Test]
        public void PortablePlatformsUseFrontFaceAndDesktopUsesDepthBuffer()
        {
            Assert.That(BasisImagePickupSettings.ShouldUseDepthBufferAnimationVisibility(true), Is.False);
            Assert.That(BasisImagePickupSettings.ShouldUseDepthBufferAnimationVisibility(false), Is.True);
        }

        [Test]
        public void DepthVisibilityExpiresAndCanBeReset()
        {
            var host = new GameObject("DepthVisibilityResultTest");
            try
            {
                var player = host.AddComponent<BasisAnimatedImagePlayer>();
                player.SetDepthVisibility(false, 10f, false);
                Assert.That(player.DepthVisibilityFromGpu, Is.False);

                player.SetDepthVisibility(true, 10f);
                Assert.That(player.DepthVisibilityFromGpu, Is.True);
                Assert.That(player.TryGetDepthVisibility(10.1f, out bool visible), Is.True);
                Assert.That(visible, Is.True);
                Assert.That(
                    player.TryGetDepthVisibility(
                        10f
                            + BasisImagePickupSettings.AnimationDepthVisibilityResultMaxAgeSeconds
                            + 0.01f,
                        out _
                    ),
                    Is.False
                );

                player.ResetDepthVisibility();
                Assert.That(player.TryGetDepthVisibility(10f, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void ResettingFaceVisibilityForcesImmediateReevaluation()
        {
            var host = new GameObject("VisibilityModeResetTest");
            try
            {
                var player = host.AddComponent<BasisAnimatedImagePlayer>();
                player.SetFaceVisibility(true, 10f);
                Assert.That(player.IsFaceVisible, Is.True);
                Assert.That(player.NeedsFaceOcclusionCheck(10f), Is.False);

                player.ResetFaceVisibility();
                Assert.That(player.IsFaceVisible, Is.False);
                Assert.That(player.NeedsFaceOcclusionCheck(0f), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void CardFrontFacePointsTowardNegativeLocalZ()
        {
            Assert.That(
                BasisImagePickupManager.IsFrontFacingCamera(
                    Vector3.back,
                    Vector3.zero,
                    new Vector3(0f, 0f, -2f),
                    Vector3.forward,
                    false
                ),
                Is.True
            );
            Assert.That(
                BasisImagePickupManager.IsFrontFacingCamera(
                    Vector3.back,
                    Vector3.zero,
                    new Vector3(0f, 0f, 2f),
                    Vector3.back,
                    false
                ),
                Is.False
            );
        }

        [Test]
        public void CpuFrontFacingCandidateRejectsBackFacesLayersAndFrustumMisses()
        {
            Plane[] frustum =
            {
                new Plane(Vector3.right, 10f),
                new Plane(Vector3.left, 10f),
                new Plane(Vector3.up, 10f),
                new Plane(Vector3.down, 10f),
                new Plane(Vector3.forward, 10f),
                new Plane(Vector3.back, 10f),
            };
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            Assert.That(
                BasisImagePickupManager.IsCpuFrontFacingCandidate(
                    0,
                    bounds,
                    frustum,
                    Vector3.back,
                    Vector3.zero,
                    new Vector3(0f, 0f, -2f),
                    Vector3.forward,
                    false,
                    1
                ),
                Is.True
            );
            Assert.That(
                BasisImagePickupManager.IsCpuFrontFacingCandidate(
                    0,
                    bounds,
                    frustum,
                    Vector3.back,
                    Vector3.zero,
                    new Vector3(0f, 0f, 2f),
                    Vector3.back,
                    false,
                    1
                ),
                Is.False
            );
            Assert.That(
                BasisImagePickupManager.IsCpuFrontFacingCandidate(
                    0,
                    bounds,
                    frustum,
                    Vector3.back,
                    Vector3.zero,
                    new Vector3(0f, 0f, -2f),
                    Vector3.forward,
                    false,
                    0
                ),
                Is.False
            );
            bounds.center = new Vector3(20f, 0f, 0f);
            Assert.That(
                BasisImagePickupManager.IsCpuFrontFacingCandidate(
                    0,
                    bounds,
                    frustum,
                    Vector3.back,
                    bounds.center,
                    new Vector3(20f, 0f, -2f),
                    Vector3.forward,
                    false,
                    1
                ),
                Is.False
            );
        }

        [Test]
        public void CpuFrontFacingMaskIsFrameScopedAndInvalidatesVisibility()
        {
            var host = new GameObject("CpuFrontFacingMaskTest");
            try
            {
                var player = host.AddComponent<BasisAnimatedImagePlayer>();
                player.SetCpuFrontFacingCameraMask(1, 100);
                player.SetFaceVisibility(true, 10f);
                player.SetDepthVisibility(true, 10f);

                player.SetCpuFrontFacingCameraMask(2, 101);
                Assert.That(player.IsFaceVisible, Is.False);
                Assert.That(player.TryGetDepthVisibility(10f, out _), Is.False);
                Assert.That(player.TryGetCpuFrontFacingCameraMask(101, out ulong mask), Is.True);
                Assert.That(mask, Is.EqualTo(2UL));
                Assert.That(player.TryGetCpuFrontFacingCameraMask(100, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void FaceOcclusionIgnoresOwnAndUnrelatedTriggerColliders()
        {
            var targetObject = new GameObject("TargetImage");
            var target = targetObject.AddComponent<BasisImagePickupObject>();
            var ownCollider = targetObject.AddComponent<BoxCollider>();
            ownCollider.isTrigger = true;

            var triggerObject = new GameObject("UnrelatedTrigger");
            var unrelatedTrigger = triggerObject.AddComponent<BoxCollider>();
            unrelatedTrigger.isTrigger = true;

            var wallObject = new GameObject("Wall");
            var wallCollider = wallObject.AddComponent<BoxCollider>();

            var otherImageObject = new GameObject("OtherImage");
            var otherImage = otherImageObject.AddComponent<BasisImagePickupObject>();
            var otherImageCollider = otherImageObject.AddComponent<BoxCollider>();
            otherImageCollider.isTrigger = true;
            BasisImagePickupObject.RegisterCollider(otherImageCollider, otherImage);

            try
            {
                Assert.That(BasisImagePickupManager.IsBlockingOcclusionCollider(ownCollider, target), Is.False);
                Assert.That(
                    BasisImagePickupManager.IsBlockingOcclusionCollider(
                        unrelatedTrigger,
                        target
                    ),
                    Is.False
                );
                Assert.That(BasisImagePickupManager.IsBlockingOcclusionCollider(wallCollider, target), Is.True);
                Assert.That(
                    BasisImagePickupManager.IsBlockingOcclusionCollider(
                        otherImageCollider,
                        target
                    ),
                    Is.True
                );
            }
            finally
            {
                BasisImagePickupObject.UnregisterCollider(otherImageCollider);
                Object.DestroyImmediate(targetObject);
                Object.DestroyImmediate(triggerObject);
                Object.DestroyImmediate(wallObject);
                Object.DestroyImmediate(otherImageObject);
            }
        }

        [TestCase(60, 2, 1, 1)]
        [TestCase(64, 0, 0, 0)]
        [TestCase(63, 1, 1, 0)]
        public void LocalImageSlotsIncludeQueuedAndActiveGifImports(
            int ownedCount,
            int pendingCount,
            int queuedCount,
            int expected
        )
        {
            Assert.That(
                BasisImagePickupManager.CalculateAvailableLocalImageSlots(
                    ownedCount,
                    pendingCount,
                    queuedCount
                ),
                Is.EqualTo(expected)
            );
        }

        [Test]
        public void ImageManagerIsStaticAndNeedsNoGameObject()
        {
            System.Type managerType = typeof(BasisImagePickupManager);
            Assert.That(managerType.IsAbstract && managerType.IsSealed, Is.True);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(managerType), Is.False);
        }

        [Test]
        public void OwnerNameReaderRejectsOversizedUtf8Length()
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(new string('x', BasisImagePickupManager.MaxOwnerNameUtf8Bytes + 1));
            }
            stream.Position = 0;
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            Assert.Throws<InvalidDataException>(() => BasisImagePickupManager.ReadBoundedOwnerName(reader));
        }

        [Test]
        public void OwnerNameNormalizationPreservesValidUtf8Boundary()
        {
            var source = new StringBuilder();
            for (int i = 0; i < 100; i++)
                source.Append("😀");

            string normalized = BasisImagePickupManager.NormalizeOwnerNameForNetwork(source.ToString());
            Assert.That(
                Encoding.UTF8.GetByteCount(normalized),
                Is.LessThanOrEqualTo(BasisImagePickupManager.MaxOwnerNameUtf8Bytes)
            );
            Assert.That(normalized, Is.Not.Empty);
            Assert.That(char.IsHighSurrogate(normalized[normalized.Length - 1]), Is.False);

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                writer.Write(normalized);
            stream.Position = 0;
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            Assert.That(BasisImagePickupManager.ReadBoundedOwnerName(reader), Is.EqualTo(normalized));
        }

        [Test]
        public void GifJobResultTransfersAnimationOwnershipExplicitly()
        {
            BasisAnimatedImageData animation = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Red }
                )
            );
            var result = new BasisGifDecodeJobResult { Animation = animation };
            BasisAnimatedImageData transferred = result.TakeAnimation();
            try
            {
                Assert.That(transferred, Is.SameAs(animation));
                Assert.That(result.Animation, Is.Null);
                result.Dispose();
                Assert.That(transferred.IsCreated, Is.True);
            }
            finally
            {
                result.Dispose();
                transferred?.Dispose();
            }
        }

        [TestCase(true, true, true, false, true)]
        [TestCase(false, true, true, false, false)]
        [TestCase(true, false, true, false, false)]
        [TestCase(true, true, false, false, false)]
        [TestCase(true, true, true, true, false)]
        public void AcceptedInboundAnimationContinuesUntilItsPickupIsInvalid(
            bool receiveEnabled,
            bool imageExists,
            bool ownerMatches,
            bool animationAlreadyAttached,
            bool expected
        )
        {
            Assert.That(
                BasisImagePickupManager.ShouldContinueAcceptedInboundAnimation(
                    receiveEnabled,
                    imageExists,
                    ownerMatches,
                    animationAlreadyAttached
                ),
                Is.EqualTo(expected)
            );
        }

        [Test]
        public void InboundAnimationDecodeBudgetDefersTemporaryOverflow()
        {
            long decodedByteLimit =
                BasisImagePickupSettings.MaxPendingInboundAnimationDecodedBytesPerSender;
            int jobLimit =
                BasisImagePickupSettings.MaxPendingInboundAnimationDecodeJobsPerSender;

            Assert.That(BasisImagePickupManager.FitsInboundAnimationDecodeBudget(0, 0, 1), Is.True);
            Assert.That(BasisImagePickupManager.FitsInboundAnimationDecodeBudget(jobLimit, 0, 1), Is.False);
            Assert.That(
                BasisImagePickupManager.FitsInboundAnimationDecodeBudget(
                    1,
                    decodedByteLimit - 1024,
                    1024
                ),
                Is.True
            );
            Assert.That(
                BasisImagePickupManager.FitsInboundAnimationDecodeBudget(
                    1,
                    decodedByteLimit - 1023,
                    1024
                ),
                Is.False
            );
            Assert.That(
                BasisImagePickupManager.FitsInboundAnimationDecodeBudget(
                    0,
                    0,
                    (int)decodedByteLimit + 1
                ),
                Is.False
            );
        }

        [TestCase(1, 1, 1, 1, true)]
        [TestCase(1, 1, 2048, 2048, false)]
        [TestCase(2048, 2048, 1, 1, false)]
        [TestCase(1024, 512, 1024, 513, false)]
        public void InboundImageDimensionsMustMatchClaim(
            int claimedWidth,
            int claimedHeight,
            int decodedWidth,
            int decodedHeight,
            bool expected
        )
        {
            Assert.That(
                BasisImagePickupManager.MatchesClaimedDimensions(
                    claimedWidth,
                    claimedHeight,
                    decodedWidth,
                    decodedHeight
                ),
                Is.EqualTo(expected)
            );
        }

        [Test]
        public void InboundTransferBudgetCapsAllSendersTogether()
        {
            long limit = BasisImagePickupSettings.MaxInboundTransferBytes;
            Assert.That(BasisImagePickupManager.FitsInboundTransferBudget(0, 1), Is.True);
            Assert.That(BasisImagePickupManager.FitsInboundTransferBudget(limit - 1, 1), Is.True);
            Assert.That(BasisImagePickupManager.FitsInboundTransferBudget(limit, 1), Is.False);
            Assert.That(BasisImagePickupManager.FitsInboundTransferBudget(limit - 1, 2), Is.False);
        }

        [Test]
        public void RemoteAnimationAggregateBudgetMatchesSenderAdmission()
        {
            Assert.That(
                BasisImagePickupManager.IsWithinRemoteAnimationBudget(
                    BasisImagePickupSettings.MaxRemoteAnimationDecodedFramePixelsPerSender,
                    BasisImagePickupSettings.MaxRemoteAnimationCanvasPixelsPerSender,
                    out string acceptedReason
                ),
                Is.True,
                acceptedReason
            );
            Assert.That(
                BasisImagePickupManager.IsWithinRemoteAnimationBudget(
                    BasisImagePickupSettings.MaxRemoteAnimationDecodedFramePixelsPerSender
                        + 1,
                    1,
                    out _
                ),
                Is.False
            );
            Assert.That(
                BasisImagePickupManager.IsWithinRemoteAnimationBudget(
                    1,
                    BasisImagePickupSettings.MaxRemoteAnimationCanvasPixelsPerSender
                        + 1,
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void GifDecodeWorkingEstimateIncludesWorstCaseNativePools()
        {
            long estimate = BasisAnimatedImageData.EstimateGifDecodeWorkingBytes(
                BasisImagePickupSettings.MaxAnimationSourceBytes
            );
            BasisImagePickupSettings.AnimationMemoryLimits desktopLimits =
                BasisImagePickupSettings.CalculateAnimationMemoryLimits(16384, false);
            Assert.That(estimate, Is.GreaterThan(BasisImagePickupSettings.MaxAnimationDecodedFramePixels * 4L));
            Assert.That(estimate, Is.LessThan(desktopLimits.NativeWorkingSetBytes));
        }

        [TestCase(16384, true, 64, 16, 256, 256, 512)]
        [TestCase(4096, false, 64, 16, 256, 256, 512)]
        [TestCase(8192, false, 128, 64, 768, 512, 1536)]
        [TestCase(16384, false, -1, 128, 2048, 1024, 3072)]
        public void AnimationMemoryLimitsScaleByDeviceTier(
            int systemMemoryMegabytes,
            bool mobile,
            int expectedDecodedBodyMiB,
            int expectedDecodedCacheMiPixels,
            int expectedResidentNativeMiB,
            int expectedCompositorMiB,
            int expectedWorkingSetMiB
        )
        {
            BasisImagePickupSettings.AnimationMemoryLimits limits =
                BasisImagePickupSettings.CalculateAnimationMemoryLimits(
                    systemMemoryMegabytes,
                    mobile
                );
            const long mib = 1024L * 1024L;
            long expectedDecodedBodyBytes =
                expectedDecodedBodyMiB < 0
                    ? BasisImagePickupSettings.MaxAnimationNetworkDecodedBytes
                    : expectedDecodedBodyMiB * mib;
            Assert.That(limits.DecodedBodyBytes, Is.EqualTo(expectedDecodedBodyBytes));
            Assert.That(
                limits.DecodedFramePixelsPerSender,
                Is.EqualTo(expectedDecodedCacheMiPixels * 1024L * 1024L)
            );
            Assert.That(limits.ResidentNativeBytes, Is.EqualTo(expectedResidentNativeMiB * mib));
            Assert.That(limits.ResidentCompositorBytes, Is.EqualTo(expectedCompositorMiB * mib));
            Assert.That(limits.NativeWorkingSetBytes, Is.EqualTo(expectedWorkingSetMiB * mib));
        }

        [Test]
        public void LocalOwnerReloadUsesTrustedDecodeLimit()
        {
            Assert.That(
                BasisAnimatedImagePlayer.ResolveReloadDecodeTrust(true),
                Is.EqualTo(BasisAnimationDecodeTrust.TrustedLocal)
            );
            Assert.That(
                BasisAnimatedImagePlayer.ResolveReloadDecodeTrust(false),
                Is.EqualTo(BasisAnimationDecodeTrust.UntrustedRemote)
            );
        }

        [TestCase(-1, 1)]
        [TestCase(0, 1)]
        [TestCase(1, 1)]
        [TestCase(2, 2)]
        [TestCase(8, 2)]
        public void AnimationDecodeConcurrencyIsCappedForMemorySafety(int availableProcessorCount, int expectedJobLimit)
        {
            Assert.That(
                BasisImagePickupSettings.CalculateAnimationDecodeJobLimit(
                    availableProcessorCount
                ),
                Is.EqualTo(expectedJobLimit)
            );
        }

        [Test]
        public void ImageManagerHandlesDirectAndServerFallbackNetworkRoutes()
        {
            System.Type managerType = typeof(BasisImagePickupManager);
            Assert.That(
                managerType.GetMethod(nameof(BasisImagePickupManager.OnDirectNetworkMessage)),
                Is.Not.Null
            );
            Assert.That(managerType.GetMethod("OnNetworkMessage"), Is.Null);
        }

        [Test]
        public void LargeBatchSpreadsHorizontallyAndStaysAboveMinimumHeight()
        {
            const int count = 64;
            const float batchCenterY = 1.6f;
            const float minimumCenterY = 0.3f;
            int columns = BasisImagePickupManager.CalculateBatchSpawnColumns(count, batchCenterY, minimumCenterY);

            Assert.That(columns, Is.GreaterThan(BasisImagePickupSettings.BatchSpawnColumns));
            Assert.That(columns, Is.LessThanOrEqualTo(BasisImagePickupSettings.BatchSpawnMaximumColumns));
            for (int index = 0; index < count; index++)
            {
                Vector3 offset = BasisImagePickupManager.CalculateBatchSpawnLocalOffset(
                    index,
                    count,
                    columns,
                    minimumCenterY - batchCenterY
                );
                Assert.That(batchCenterY + offset.y, Is.GreaterThanOrEqualTo(minimumCenterY - 0.0001f));
            }
        }

        [Test]
        public void LowBatchCenterUsesMaximumWidthAndShiftsAboveGround()
        {
            const int count = 64;
            const float batchCenterY = 0.7f;
            const float minimumCenterY = 0.3f;
            int columns = BasisImagePickupManager.CalculateBatchSpawnColumns(count, batchCenterY, minimumCenterY);

            Assert.That(columns, Is.EqualTo(BasisImagePickupSettings.BatchSpawnMaximumColumns));
            for (int index = 0; index < count; index++)
            {
                Vector3 offset = BasisImagePickupManager.CalculateBatchSpawnLocalOffset(
                    index,
                    count,
                    columns,
                    minimumCenterY - batchCenterY
                );
                Assert.That(batchCenterY + offset.y, Is.GreaterThanOrEqualTo(minimumCenterY - 0.0001f));
            }
        }

        [Test]
        public void BatchOffsetsPlaceTwoImagesSideBySide()
        {
            Vector3 left = BasisImagePickupManager.CalculateBatchSpawnLocalOffset(0, 2);
            Vector3 right = BasisImagePickupManager.CalculateBatchSpawnLocalOffset(1, 2);

            Assert.That(left.x, Is.EqualTo(-right.x).Within(0.0001f));
            Assert.That(left.y, Is.EqualTo(right.y).Within(0.0001f));
            Assert.That(left.x, Is.LessThan(0f));
            Assert.That(right.x, Is.GreaterThan(0f));
        }

        [Test]
        public void BatchOffsetsUseStableRowsForFiveImages()
        {
            Vector3 first = BasisImagePickupManager.CalculateBatchSpawnLocalOffset(0, 5);
            Vector3 fourth = BasisImagePickupManager.CalculateBatchSpawnLocalOffset(3, 5);
            Vector3 fifth = BasisImagePickupManager.CalculateBatchSpawnLocalOffset(4, 5);

            Assert.That(first.y, Is.GreaterThan(fifth.y));
            Assert.That(first.x, Is.LessThan(fourth.x));
            Assert.That(fifth.x, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void NativeAdoptionRejectsBackgroundAlphaFlagMismatch()
        {
            using var frames = new NativeArray<BasisAnimatedImageFrame>(
                1,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory
            );
            using var pixels = new NativeArray<Color32>(1, Allocator.Temp, NativeArrayOptions.ClearMemory);
            using var frameEnds = new NativeArray<long>(1, Allocator.Temp, NativeArrayOptions.ClearMemory);
            WriteFrame(
                frames,
                0,
                new BasisAnimatedImageFrame
                {
                    Width = 1,
                    Height = 1,
                    PixelCount = 1,
                    DurationMicroseconds = 50000,
                    EndTimeMicroseconds = 50000,
                    Blend = BasisAnimationBlend.Source,
                    Disposal = BasisAnimationDisposal.None,
                }
            );
            WriteFrameEnd(frameEnds, 0, 50000);

            Assert.That(
                BasisAnimatedImageData.TryAdoptNative(
                    1,
                    1,
                    0,
                    new Color32(0, 0, 0, 128),
                    frames,
                    pixels,
                    frameEnds,
                    50000,
                    false,
                    false,
                    false,
                    out BasisAnimatedImageData data,
                    out string error
                ),
                Is.False
            );
            Assert.That(data, Is.Null);
            StringAssert.Contains("alpha flags", error);
        }

        [Test]
        public void NativeAdoptionRejectsInconsistentPreviousCanvasFlag()
        {
            var frames = new NativeArray<BasisAnimatedImageFrame>(1, Allocator.Temp, NativeArrayOptions.ClearMemory);
            var pixels = new NativeArray<Color32>(1, Allocator.Temp, NativeArrayOptions.ClearMemory);
            var frameEnds = new NativeArray<long>(1, Allocator.Temp, NativeArrayOptions.ClearMemory);
            BasisAnimatedImageData data = null;
            try
            {
                frames[0] = new BasisAnimatedImageFrame
                {
                    Width = 1,
                    Height = 1,
                    PixelCount = 1,
                    DurationMicroseconds = 50000,
                    EndTimeMicroseconds = 50000,
                    Blend = BasisAnimationBlend.Source,
                    Disposal = BasisAnimationDisposal.Previous,
                };
                frameEnds[0] = 50000;

                Assert.That(
                    BasisAnimatedImageData.TryAdoptNative(
                        1,
                        1,
                        0,
                        new Color32(0, 0, 0, 0),
                        frames,
                        pixels,
                        frameEnds,
                        50000,
                        true,
                        false,
                        false,
                        out data,
                        out string error
                    ),
                    Is.False
                );
                StringAssert.Contains("previous-canvas flag", error);
                Assert.That(data, Is.Null);
            }
            finally
            {
                data?.Dispose();
                if (frames.IsCreated)
                    frames.Dispose();
                if (pixels.IsCreated)
                    pixels.Dispose();
                if (frameEnds.IsCreated)
                    frameEnds.Dispose();
            }
        }

        [Test]
        public void NativeAdoptionRejectsFrameOutsideCanvas()
        {
            using var frames = new NativeArray<BasisAnimatedImageFrame>(
                1,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory
            );
            using var pixels = new NativeArray<Color32>(1, Allocator.Temp, NativeArrayOptions.ClearMemory);
            using var frameEnds = new NativeArray<long>(1, Allocator.Temp, NativeArrayOptions.ClearMemory);
            WriteFrame(
                frames,
                0,
                new BasisAnimatedImageFrame
                {
                    X = 1,
                    Width = 1,
                    Height = 1,
                    PixelCount = 1,
                    DurationMicroseconds = 50000,
                    EndTimeMicroseconds = 50000,
                    Blend = BasisAnimationBlend.Source,
                    Disposal = BasisAnimationDisposal.None,
                }
            );
            WriteFrameEnd(frameEnds, 0, 50000);

            Assert.That(
                BasisAnimatedImageData.TryAdoptNative(
                    1,
                    1,
                    0,
                    new Color32(0, 0, 0, 0),
                    frames,
                    pixels,
                    frameEnds,
                    50000,
                    true,
                    false,
                    false,
                    out BasisAnimatedImageData data,
                    out string error
                ),
                Is.False
            );
            Assert.That(data, Is.Null);
            StringAssert.Contains("bounds", error);
        }

        [Test]
        public void RemoteTransformSmoothingIsFrameRateIndependentAndBounded()
        {
            float oneSixtieth =
                BasisImagePickupObject.CalculateRemoteTransformLerpFactor(1f / 60f);
            float oneOneTwentieth =
                BasisImagePickupObject.CalculateRemoteTransformLerpFactor(1f / 120f);
            float twoOneTwentiethSteps =
                1f - (1f - oneOneTwentieth) * (1f - oneOneTwentieth);

            Assert.That(twoOneTwentiethSteps, Is.EqualTo(oneSixtieth).Within(0.000001f));
            Assert.That(BasisImagePickupObject.CalculateRemoteTransformLerpFactor(10f), Is.InRange(0f, 1f));
            Assert.That(BasisImagePickupObject.CalculateRemoteTransformLerpFactor(-1f), Is.EqualTo(0f));
        }

        [TestCase(0, 1, 1000, 64)]
        [TestCase(64, 65, 1000, 128)]
        [TestCase(128, 129, 200, 200)]
        [TestCase(64, 65, 64, 64)]
        public void DepthVisibilityCapacityGrowsGeometrically(
            int currentCapacity,
            int minimumRequired,
            int maximumCapacity,
            int expected
        )
        {
            Assert.That(
                BasisAnimatedImageDepthVisibility.CalculateGrowthCapacity(
                    currentCapacity,
                    minimumRequired,
                    maximumCapacity
                ),
                Is.EqualTo(expected)
            );
        }

        [TestCase(79, 0)]
        [TestCase(80, 1)]
        [TestCase(1024, 12)]
        public void DepthVisibilityCapacityHonorsMaximumGraphicsBufferSize(long maximumBufferBytes, int expected)
        {
            Assert.That(
                BasisAnimatedImageDepthVisibility.CalculateMaximumCardCapacity(
                    maximumBufferBytes
                ),
                Is.EqualTo(expected)
            );
        }

        [Test]
        public void SchedulerStartIndexTracksPlayerRemoval()
        {
            Assert.That(BasisImagePickupManager.AdjustStartIndexAfterRemoval(3, 1, 4), Is.EqualTo(2));
            Assert.That(BasisImagePickupManager.AdjustStartIndexAfterRemoval(3, 3, 4), Is.EqualTo(3));
            Assert.That(BasisImagePickupManager.AdjustStartIndexAfterRemoval(4, 4, 4), Is.EqualTo(0));
        }

        [Test]
        public void AtlasFramesWithoutPaddingReceiveDedicatedPages()
        {
            using var frames = new NativeArray<BasisAnimatedImageFrame>(
                2,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory
            );
            using var locations = new NativeArray<BasisAnimationFrameAtlasLocation>(
                2,
                Allocator.Temp,
                NativeArrayOptions.ClearMemory
            );
            using var cursorX = new NativeArray<int>(2, Allocator.Temp);
            using var cursorY = new NativeArray<int>(2, Allocator.Temp);
            using var rowHeight = new NativeArray<int>(2, Allocator.Temp);
            using var usedWidth = new NativeArray<int>(2, Allocator.Temp);
            using var usedHeight = new NativeArray<int>(2, Allocator.Temp);
            using var result = new NativeArray<int>(3, Allocator.Temp);
            WriteFrame(frames, 0, new BasisAnimatedImageFrame { Width = 4, Height = 1 });
            WriteFrame(frames, 1, new BasisAnimatedImageFrame { Width = 1, Height = 1 });

            new BasisAnimationAtlasLayoutJob
            {
                PageLimit = 4,
                Frames = frames,
                Locations = locations,
                CursorX = cursorX,
                CursorY = cursorY,
                RowHeight = rowHeight,
                UsedWidth = usedWidth,
                UsedHeight = usedHeight,
                Result = result,
            }.Execute();

            Assert.That(result[2], Is.EqualTo(0));
            Assert.That(locations[0].Padding, Is.EqualTo(0));
            Assert.That(locations[0].PageIndex, Is.Not.EqualTo(locations[1].PageIndex));
        }

        [Test]
        public void NetworkDecodeRejectsAlphaFlagsThatDoNotMatchPixels()
        {
            using var pixels = new NativeArray<Color32>(new[] { new Color32(255, 255, 255, 128) }, Allocator.TempJob);
            using var errors = new NativeArray<int>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory);

            new BasisAnimationValidateAlphaJob
            {
                Header = new BasisAnimationBodyHeader
                {
                    BackgroundColor = new Color32(0, 0, 0, 255),
                    HasAnyAlpha = 0,
                    HasPartialAlpha = 0,
                },
                Pixels = pixels,
                Errors = errors,
                ErrorIndex = 0,
            }
                .Schedule()
                .Complete();

            Assert.That(errors[0], Is.EqualTo((int)BasisAnimationCodecError.InvalidHeader));
        }

        [Test]
        public void NetworkDecodeSchedulesSharedErrorWritersInDependencyOrder()
        {
            using BasisAnimatedImageData data = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { new Color32(255, 255, 255, 128) }
                )
            );
            using var encode = new BasisBurstAnimationEncodeRequest(data);
            BasisBurstAnimationEncodeResult encoded = encode.Complete();
            Assert.That(encoded.Ok, Is.True, encoded.Error);
            using BasisNativeAnimationPayload payload = encoded.TakePayload();
            Assert.That(payload, Is.Not.Null);

            using var decode = new BasisBurstAnimationDecodeRequest(payload.Bytes, payload.Length, false);
            BasisBurstAnimationDecodeResult decoded = decode.Complete();
            Assert.That(decoded.Ok, Is.True, decoded.Error);
            using BasisAnimatedImageData decodedData = decoded.TakeAnimation();
            Assert.That(decodedData, Is.Not.Null);
            Assert.That(decodedData.HasAnyAlpha, Is.True);
            Assert.That(decodedData.HasPartialAlpha, Is.True);
        }

        [Test]
        public void DecodedPixelBudgetKeepsClosestPayloadBackedAnimation()
        {
            Camera previousCamera = BasisLocalCameraDriver.CameraInstance;
            var cameraHost = new GameObject("BasisAnimatedImageBudgetCamera");
            var farHost = new GameObject("BasisAnimatedImageBudgetFar");
            var nearHost = new GameObject("BasisAnimatedImageBudgetNear");
            BasisAnimatedImageData farData = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Red }
                )
            );
            BasisAnimatedImageData nearData = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Blue }
                )
            );
            BasisNativeAnimationPayload farPayload = null;
            BasisNativeAnimationPayload nearPayload = null;
            BasisAnimatedImagePlayer farPlayer = null;
            BasisAnimatedImagePlayer nearPlayer = null;
            bool reloadSlotAcquired = false;
            long farNativeBytes = farData.NativeByteCount;
            try
            {
                BasisLocalCameraDriver.CameraInstance = cameraHost.AddComponent<Camera>();
                cameraHost.transform.position = Vector3.zero;
                farHost.transform.position = new Vector3(0f, 0f, 10f);
                nearHost.transform.position = new Vector3(0f, 0f, 1f);

                using (var farEncode = new BasisBurstAnimationEncodeRequest(farData))
                {
                    BasisBurstAnimationEncodeResult encoded = farEncode.Complete();
                    Assert.That(encoded.Ok, Is.True, encoded.Error);
                    farPayload = encoded.TakePayload();
                }
                using (var nearEncode = new BasisBurstAnimationEncodeRequest(nearData))
                {
                    BasisBurstAnimationEncodeResult encoded = nearEncode.Complete();
                    Assert.That(encoded.Ok, Is.True, encoded.Error);
                    nearPayload = encoded.TakePayload();
                }

                var farPickup = farHost.AddComponent<BasisImagePickupObject>();
                var nearPickup = nearHost.AddComponent<BasisImagePickupObject>();
                farPickup.OwnerId = 77;
                nearPickup.OwnerId = 77;
                farPlayer = farHost.AddComponent<BasisAnimatedImagePlayer>();
                nearPlayer = nearHost.AddComponent<BasisAnimatedImagePlayer>();
                Assert.That(farPlayer.Initialize(farData, farPickup, 1, false, farPayload), Is.True);
                farData = null;
                Assert.That(nearPlayer.Initialize(nearData, nearPickup, 1, false, nearPayload), Is.True);
                nearData = null;

                BasisImagePickupManager.EnforceDecodedPixelBudget(nearPlayer, nearPlayer.DecodedFramePixels);
                Assert.That(farPlayer.Data, Is.Null);
                Assert.That(nearPlayer.Data, Is.Not.Null);

                farHost.transform.position = new Vector3(0f, 0f, 0.5f);
                nearHost.transform.position = new Vector3(0f, 0f, 10f);
                reloadSlotAcquired = BasisImagePickupManager.TryAcquireReloadDecodeSlot(
                    farPlayer,
                    farNativeBytes,
                    farPlayer.DecodedFramePixels
                );
                Assert.That(reloadSlotAcquired, Is.False);
                Assert.That(nearPlayer.Data, Is.Not.Null);

                BasisImagePickupManager.ApplyPendingDecodedReleases();
                Assert.That(nearPlayer.Data, Is.Null);
                reloadSlotAcquired = BasisImagePickupManager.TryAcquireReloadDecodeSlot(
                    farPlayer,
                    farNativeBytes,
                    farPlayer.DecodedFramePixels
                );
                Assert.That(reloadSlotAcquired, Is.True);
                BasisImagePickupManager.ReleaseReloadDecodeSlot(farPlayer, farNativeBytes);
                reloadSlotAcquired = false;
            }
            finally
            {
                if (reloadSlotAcquired)
                    BasisImagePickupManager.ReleaseReloadDecodeSlot(farPlayer, farNativeBytes);
                BasisLocalCameraDriver.CameraInstance = previousCamera;
                farPlayer?.ClearReloadPayload();
                nearPlayer?.ClearReloadPayload();
                Object.DestroyImmediate(farHost);
                Object.DestroyImmediate(nearHost);
                Object.DestroyImmediate(cameraHost);
                farPayload?.Dispose();
                nearPayload?.Dispose();
                farData?.Dispose();
                nearData?.Dispose();
                BasisImagePickupManager.Shutdown();
            }
        }

        [Test]
        public void AnimationNativeMemoryBudgetHonorsExactBoundary()
        {
            Assert.That(BasisAnimatedImageData.FitsMemoryBudget(100, 50, 25, 175), Is.True);
            Assert.That(BasisAnimatedImageData.FitsMemoryBudget(100, 50, 26, 175), Is.False);
            Assert.That(BasisAnimatedImageData.FitsMemoryBudget(-1, 0, 0, 175), Is.False);
            Assert.That(BasisAnimatedImageData.FitsMemoryBudget(0, 0, 176, 175), Is.False);
        }

        [Test]
        public void NativeDataTracksAndReleasesResidentBytes()
        {
            long before = BasisAnimatedImageData.TotalResidentNativeBytes;
            BasisAnimatedImageData data = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Red }
                )
            );
            long nativeBytes = data.NativeByteCount;
            try
            {
                Assert.That(nativeBytes, Is.GreaterThan(0));
                Assert.That(BasisAnimatedImageData.TotalResidentNativeBytes, Is.EqualTo(before + nativeBytes));
            }
            finally
            {
                data.Dispose();
            }
            Assert.That(BasisAnimatedImageData.TotalResidentNativeBytes, Is.EqualTo(before));
        }

        [Test]
        public void AnimatedPlayerReleasesNativeDataSynchronously()
        {
            long before = BasisAnimatedImageData.TotalResidentNativeBytes;
            var host = new GameObject("BasisAnimatedImageDisposeTest");
            var pickup = host.AddComponent<BasisImagePickupObject>();
            var player = host.AddComponent<BasisAnimatedImagePlayer>();
            BasisAnimatedImageData data = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Red }
                )
            );
            bool initialized = false;
            try
            {
                initialized = player.Initialize(data, pickup, 1, true);
                Assert.That(initialized, Is.True);
                Assert.That(BasisAnimatedImageData.TotalResidentNativeBytes, Is.GreaterThan(before));

                player.DisposeOwnedResources();
                Assert.That(BasisAnimatedImageData.TotalResidentNativeBytes, Is.EqualTo(before));
                Assert.That(player.IsInitialized, Is.False);
                Assert.DoesNotThrow(player.DisposeOwnedResources);
            }
            finally
            {
                Object.DestroyImmediate(host);
                if (!initialized)
                    data.Dispose();
                BasisImagePickupManager.Shutdown();
            }
        }

        [Test]
        public void AnimatedPlayerDisposalCompletesPendingReloadDecode()
        {
            long before = BasisAnimatedImageData.TotalResidentNativeBytes;
            var host = new GameObject("BasisAnimatedImagePendingReloadDisposeTest");
            var pickup = host.AddComponent<BasisImagePickupObject>();
            var player = host.AddComponent<BasisAnimatedImagePlayer>();
            BasisAnimatedImageData data = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    new[] { Red }
                )
            );
            BasisNativeAnimationPayload payload = null;
            var commands = new CommandBuffer();
            bool initialized = false;
            try
            {
                using var encode = new BasisBurstAnimationEncodeRequest(data);
                BasisBurstAnimationEncodeResult encoded = encode.Complete();
                Assert.That(encoded.Ok, Is.True, encoded.Error);
                payload = encoded.TakePayload();
                Assert.That(payload, Is.Not.Null);

                initialized = player.Initialize(data, pickup, 1, true, payload);
                Assert.That(initialized, Is.True);
                player.ReleaseDecodedDataForMemoryPressure();
                Assert.That(player.Data, Is.Null);
                Assert.That(BasisAnimatedImageData.TotalResidentNativeBytes, Is.EqualTo(before));

                int transitionsRemaining = 1;
                long pixelsRemaining = 1;
                bool gpuCommandsAdded = false;
                player.Schedule(
                    commands,
                    1,
                    ref transitionsRemaining,
                    ref pixelsRemaining,
                    ref gpuCommandsAdded
                );

                FieldInfo reloadRequestField = typeof(BasisAnimatedImagePlayer).GetField(
                    "_reloadRequest",
                    BindingFlags.Instance | BindingFlags.NonPublic
                );
                Assert.That(reloadRequestField, Is.Not.Null);
                Assert.That(reloadRequestField.GetValue(player), Is.Not.Null);

                player.DisposeOwnedResources();
                Assert.That(reloadRequestField.GetValue(player), Is.Null);
                Assert.That(BasisAnimatedImageData.TotalResidentNativeBytes, Is.EqualTo(before));
                Assert.That(payload.IsCreated, Is.True);
            }
            finally
            {
                commands.Release();
                payload?.Dispose();
                Object.DestroyImmediate(host);
                if (!initialized)
                    data.Dispose();
                BasisImagePickupManager.Shutdown();
            }
        }

        [Test]
        public void DestroyedPickupReleasesManagerOwnedAnimationPayload()
        {
            long payloadBytesBefore = BasisNativeAnimationPayload.TotalAllocatedBytes;
            var pickupHost = new GameObject("BasisImagePickupDisposeTest");
            BasisNativeAnimationPayload payload = null;
            try
            {
                using BasisAnimatedImageData data = Create(
                    new BasisAnimatedImageFrameSource(
                        new RectInt(0, 0, 1, 1),
                        50000,
                        BasisAnimationBlend.Source,
                        BasisAnimationDisposal.None,
                        new[] { Red }
                    )
                );
                using var encode = new BasisBurstAnimationEncodeRequest(data);
                BasisBurstAnimationEncodeResult encoded = encode.Complete();
                Assert.That(encoded.Ok, Is.True, encoded.Error);
                payload = encoded.TakePayload();
                Assert.That(payload, Is.Not.Null);
                Assert.That(BasisNativeAnimationPayload.TotalAllocatedBytes, Is.GreaterThan(payloadBytesBefore));

                var pickup = pickupHost.AddComponent<BasisImagePickupObject>();
                System.Guid id = System.Guid.NewGuid();
                pickup.ImageId = id;
                SetPickupManaged(pickup);

                IDictionary images = GetManagerDictionary("_images");
                images.Add(id, pickup);

                System.Type ownedImageType = typeof(BasisImagePickupManager).GetNestedType(
                    "OwnedImage",
                    BindingFlags.NonPublic
                );
                Assert.That(ownedImageType, Is.Not.Null);
                object ownedImage = System.Activator.CreateInstance(ownedImageType);
                FieldInfo ownedObjectField = ownedImageType.GetField("Object");
                FieldInfo ownedPayloadField = ownedImageType.GetField("AnimationPayload");
                Assert.That(ownedObjectField, Is.Not.Null);
                Assert.That(ownedPayloadField, Is.Not.Null);
                ownedObjectField.SetValue(ownedImage, pickup);
                ownedPayloadField.SetValue(ownedImage, payload);

                IDictionary ownedImages = GetManagerDictionary("_owned");
                ownedImages.Add(id, ownedImage);

                Object.DestroyImmediate(pickupHost);
                Assert.That(images.Contains(id), Is.False);
                Assert.That(ownedImages.Contains(id), Is.False);
                Assert.That(BasisNativeAnimationPayload.TotalAllocatedBytes, Is.EqualTo(payloadBytesBefore));
            }
            finally
            {
                payload?.Dispose();
                if (pickupHost != null)
                    Object.DestroyImmediate(pickupHost);
                BasisImagePickupManager.Shutdown();
            }
        }

        private static IDictionary GetManagerDictionary(string fieldName)
        {
            FieldInfo field = typeof(BasisImagePickupManager).GetField(
                fieldName,
                BindingFlags.Static | BindingFlags.NonPublic
            );
            Assert.That(field, Is.Not.Null, fieldName);
            var dictionary = (IDictionary)field.GetValue(null);
            Assert.That(dictionary, Is.Not.Null, fieldName);
            dictionary.Clear();
            return dictionary;
        }

        private static void SetPickupManaged(BasisImagePickupObject pickup)
        {
            FieldInfo managedField = typeof(BasisImagePickupObject).GetField(
                "_managed",
                BindingFlags.Instance | BindingFlags.NonPublic
            );
            Assert.That(managedField, Is.Not.Null);
            managedField.SetValue(pickup, true);
        }

        [Test]
        public void DestroyedRemotePickupReleasesRetainedAnimationPayload()
        {
            long payloadBytesBefore = BasisNativeAnimationPayload.TotalAllocatedBytes;
            var pickupHost = new GameObject("BasisRemoteImagePickupDisposeTest");
            BasisNativeAnimationPayload payload = null;
            try
            {
                using BasisAnimatedImageData data = Create(
                    new BasisAnimatedImageFrameSource(
                        new RectInt(0, 0, 1, 1),
                        50000,
                        BasisAnimationBlend.Source,
                        BasisAnimationDisposal.None,
                        new[] { Red }
                    )
                );
                using var encode = new BasisBurstAnimationEncodeRequest(data);
                BasisBurstAnimationEncodeResult encoded = encode.Complete();
                Assert.That(encoded.Ok, Is.True, encoded.Error);
                payload = encoded.TakePayload();
                Assert.That(payload, Is.Not.Null);

                var pickup = pickupHost.AddComponent<BasisImagePickupObject>();
                System.Guid id = System.Guid.NewGuid();
                pickup.ImageId = id;
                SetPickupManaged(pickup);

                IDictionary images = GetManagerDictionary("_images");
                images.Add(id, pickup);

                IDictionary payloads = GetManagerDictionary("_remoteAnimationPayloads");
                payloads.Add(id, payload);

                Object.DestroyImmediate(pickupHost);
                Assert.That(images.Contains(id), Is.False);
                Assert.That(payloads.Contains(id), Is.False);
                Assert.That(BasisNativeAnimationPayload.TotalAllocatedBytes, Is.EqualTo(payloadBytesBefore));
            }
            finally
            {
                payload?.Dispose();
                if (pickupHost != null)
                    Object.DestroyImmediate(pickupHost);
                BasisImagePickupManager.Shutdown();
            }
        }

        [Test]
        public void NativeDataCopiesAuthoringPixels()
        {
            var pixels = new[] { Red };
            using BasisAnimatedImageData data = Create(
                new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, 1, 1),
                    50000,
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    pixels
                )
            );
            pixels[0] = Blue;
            Assert.That(data.CopyFramePixelsToManaged(0)[0], Is.EqualTo(Red));
        }

        private static void SchedulePlayerOnce(BasisAnimatedImagePlayer player, CommandBuffer commands)
        {
            int transitionsRemaining = 16;
            long pixelsRemaining = 1024;
            bool gpuCommandsAdded = false;
            player.Schedule(
                commands,
                1,
                ref transitionsRemaining,
                ref pixelsRemaining,
                ref gpuCommandsAdded
            );
        }

        private static void WriteFrame(
            NativeArray<BasisAnimatedImageFrame> destination,
            int index,
            BasisAnimatedImageFrame value
        )
        {
            destination[index] = value;
        }

        private static void WriteFrameEnd(NativeArray<long> destination, int index, long value)
        {
            destination[index] = value;
        }

        private static BasisAnimatedImageData Create(params BasisAnimatedImageFrameSource[] frames)
        {
            Assert.That(
                BasisAnimatedImageData.TryCreate(
					2,
					1,
                    0,
                    new Color32(0, 0, 0, 0),
                    frames,
                    out BasisAnimatedImageData data,
                    out string error
                ),
                Is.True,
                error
            );
            return data;
        }

        private static readonly Color32 Red = new Color32(255, 0, 0, 255);
        private static readonly Color32 Green = new Color32(0, 255, 0, 255);
        private static readonly Color32 Blue = new Color32(0, 0, 255, 255);
        private static readonly Color32 White = new Color32(255, 255, 255, 255);
    }
}
