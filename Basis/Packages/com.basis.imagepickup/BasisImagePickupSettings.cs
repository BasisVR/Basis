using UnityEngine;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Tunable limits and persistent runtime options for the image pickup feature.
    /// Caps are enforced on both the sending and receiving side.
    /// </summary>
    public static class BasisImagePickupSettings
    {
        internal readonly struct AnimationMemoryLimits
        {
            public readonly long DecodedBodyBytes;
            public readonly long PendingDecodedBytesPerSender;
            public readonly long DecodedFramePixelsPerSender;
            public readonly long ResidentNativeBytes;
            public readonly long ResidentCompositorBytes;
            public readonly long NativeWorkingSetBytes;

            public AnimationMemoryLimits(
                long decodedBodyBytes,
                long pendingDecodedBytesPerSender,
                long decodedFramePixelsPerSender,
                long residentNativeBytes,
                long residentCompositorBytes,
                long nativeWorkingSetBytes
            )
            {
                DecodedBodyBytes = decodedBodyBytes;
                PendingDecodedBytesPerSender = pendingDecodedBytesPerSender;
                DecodedFramePixelsPerSender = decodedFramePixelsPerSender;
                ResidentNativeBytes = residentNativeBytes;
                ResidentCompositorBytes = residentCompositorBytes;
                NativeWorkingSetBytes = nativeWorkingSetBytes;
            }
        }

        private const long MiB = 1024L * 1024L;
        private static readonly AnimationMemoryLimits RuntimeAnimationMemoryLimits =
            CalculateAnimationMemoryLimits(SystemInfo.systemMemorySize, Application.isMobilePlatform);

        public const string ReceiveEnabledKey = "Basis.ImagePickup.ReceiveEnabled";

        public const int MaxImageBytes = 8 * 1024 * 1024;
        public const int MaxSourceBytes = 32 * 1024 * 1024;
        public const int MaxDimension = 2048;
        public const long MaxTotalPixels = 2048L * 2048L;
        public const int MaxSourceDimension = 4096;
        public const long MaxSourceTotalPixels = 4096L * 4096L;
        public const int ChunkPayloadBytes = 16 * 1024;

        // Large drag batches are allowed by count, then bounded by aggregate decoded pixels and poster bytes.
        public const int MaxConcurrentImagesPerSender = 64;
        public const long MaxRemoteImagePixelsPerSender = 64L * 1024L * 1024L;
        public const long MaxRemoteImageBytesPerSender = 128L * 1024L * 1024L;
        public const int MaxInboundTransfersPerSender = 4;
        public const long MaxInboundTransferBytes = 512L * 1024L * 1024L;
        public const int SpawnRateBurstAllowance = MaxConcurrentImagesPerSender;
        public const float MinSecondsBetweenSpawnsPerSender = 0.5f;
        public const float InboundTransferTimeoutSeconds = 30f;

        public const float SpawnDistance = 1.5f;
        public const float BaseHeightMeters = 0.5f;
        /// <summary>
        /// How many dropped files may be imported in one frame. A static image is decoded, downscaled, and
        /// re-encoded to PNG on the main thread, so importing a whole drag-and-drop batch at once stalls for
        /// as long as every image in it takes together. Kept at one so a big drop degrades into a slower fill
        /// rather than a freeze.
        /// </summary>
        public const int MaxFileImportsPerFrame = 1;
        /// <summary>
        /// How many pickup back panels may be raised or dropped in one frame. Toggling the menu in a busy
        /// instance would otherwise touch every card's world-space canvas at once — up to
        /// <see cref="MaxConcurrentImagesPerSender"/> per player, each a canvas, a graphic raycaster and four
        /// TMP labels — so panels follow the menu over a few frames instead of in one.
        /// </summary>
        public const int MaxBackPanelUpdatesPerFrame = 1;
        public const int BatchSpawnColumns = 4;
        public const int BatchSpawnMaximumColumns = 16;
        public const float BatchSpawnHorizontalSpacingMeters = 1.0f;
        public const float BatchSpawnVerticalSpacingMeters = 0.65f;
        public const float BatchSpawnGroundClearanceMeters = 0.05f;

        public const float TransmitTransformHz = 15f;
        public const float MovedPositionEpsilon = 0.001f;
        public const float MovedRotationEpsilonDegrees = 0.5f;
        public const float MovedScaleEpsilon = 0.01f;

        // Animated images use a separate, larger budget than static images. Frame patches remain
        // bounded to 64M decoded pixels (roughly 256 MiB of Color32 data) to avoid unbounded RAM use.
        public const int MaxAnimationSourceBytes = 64 * 1024 * 1024;
        public const int MaxAnimationNetworkBytes = MaxAnimationSourceBytes;
        public const int MaxAnimationDimension = 2048;
        public const long MaxAnimationCanvasPixels = 2048L * 2048L;
        public const int MaxAnimationFrames = 512;
        public const long MaxAnimationDecodedFramePixels = 64L * 1024L * 1024L;
        public const long MaxAnimationNetworkDecodedBytes =
            MaxAnimationDecodedFramePixels * 4L + MaxAnimationFrames * 64L + 1024L;
        public const long MinAnimationFrameDurationMicroseconds = 33334L;
        public const long MaxAnimationDurationMicroseconds = 5L * 60L * 1000L * 1000L;
        public const int MaxAnimationTransitionsPerFrame = 256;
        public const long MaxAnimationCompositedPixelsPerFrame = 32L * 1024L * 1024L;
		public const float AnimationOffscreenResourceReleaseSeconds = 10f;
        public const float AnimationCompositorBudgetWarningIntervalSeconds = 30f;
        public static long MaxInboundAnimationDecodedBodyBytes =>
            RuntimeAnimationMemoryLimits.DecodedBodyBytes;
        public static long MaxResidentAnimationNativeBytes =>
            RuntimeAnimationMemoryLimits.ResidentNativeBytes;
        public const long MaxResidentAnimationPayloadBytes = 1L * 1024L * 1024L * 1024L;
        public static long MaxResidentAnimationCompositorBytes =>
            RuntimeAnimationMemoryLimits.ResidentCompositorBytes;
        public static long MaxAnimationNativeWorkingSetBytes =>
            RuntimeAnimationMemoryLimits.NativeWorkingSetBytes;

        // Physics-backed front-face visibility is sampled at 10 Hz and globally budgeted per frame.
        public const float AnimationFaceOcclusionCheckIntervalSeconds = 0.1f;
        public const int MaxAnimationFaceOcclusionRaycastsPerFrame = 96;
        public const float AnimationFaceOcclusionSampleHalfExtent = 0.34f;
        public const float AnimationFaceOcclusionSurfaceOffsetMeters = 0.005f;
        public const float AnimationDepthOcclusionBiasMeters = 0.025f;
        public const float AnimationDepthVisibilityResultMaxAgeSeconds = 0.5f;
        public const int AnimationBatchWarningThreshold = 4;
        public const int MaxImageNetworkChunksPerFrame = 8;
        public const int MaxAnimationNetworkChunksPerFrame = 4;
        public const int AnimationPacketBuildChunksPerJob = 32;

        // Keep the number of simultaneous high-memory decoders small. The job system still
        // parallelizes each decoder internally, while admission is controlled by memory reservations.
        public static int MaxConcurrentAnimationDecodeJobs =>
            CalculateAnimationDecodeJobLimit(SystemInfo.processorCount);

        // Completed network payloads wait compressed; these limits apply only to active native decodes.
        public static int MaxPendingInboundAnimationDecodeJobsPerSender =>
            MaxConcurrentAnimationDecodeJobs;
        public static long MaxPendingInboundAnimationDecodedBytesPerSender =>
            RuntimeAnimationMemoryLimits.PendingDecodedBytesPerSender;

        // Includes retained remote compressed payloads plus accepted in-flight transfers and decodes.
        public const long MaxInboundAnimationNetworkBytesPerSender =
            128L * 1024L * 1024L;

        // Payload-backed players share this decoded-data cache per owner. The scheduler keeps the
        // closest animations decoded and restores them from compressed payloads as proximity changes.
        public static long MaxRemoteAnimationDecodedFramePixelsPerSender =>
            RuntimeAnimationMemoryLimits.DecodedFramePixelsPerSender;
        public const long MaxRemoteAnimationCanvasPixelsPerSender = 16L * 1024L * 1024L;

        private static bool _loaded;
        private static bool _receiveEnabled = true;

        public static bool UseDepthBufferAnimationVisibility =>
            ShouldUseDepthBufferAnimationVisibility(Application.isMobilePlatform);

        internal static bool ShouldUseDepthBufferAnimationVisibility(bool mobileOrPortablePlatform)
        {
            return !mobileOrPortablePlatform;
        }

        internal static int CalculateAnimationDecodeJobLimit(int availableProcessorCount)
        {
            return Mathf.Clamp(availableProcessorCount, 1, 2);
        }

        internal static AnimationMemoryLimits CalculateAnimationMemoryLimits(
            int systemMemoryMegabytes,
            bool mobileOrPortablePlatform
        )
        {
            if (mobileOrPortablePlatform || (systemMemoryMegabytes > 0 && systemMemoryMegabytes <= 4096))
            {
                return new AnimationMemoryLimits(
                    64L * MiB,
                    128L * MiB,
                    16L * 1024L * 1024L,
                    256L * MiB,
                    256L * MiB,
                    512L * MiB
                );
            }

            if (systemMemoryMegabytes > 0 && systemMemoryMegabytes <= 8192)
            {
                return new AnimationMemoryLimits(
                    128L * MiB,
                    256L * MiB,
                    64L * 1024L * 1024L,
                    768L * MiB,
                    512L * MiB,
                    1536L * MiB
                );
            }

            return new AnimationMemoryLimits(
                MaxAnimationNetworkDecodedBytes,
                320L * MiB,
                128L * 1024L * 1024L,
                2L * 1024L * MiB,
                1024L * MiB,
                3L * 1024L * MiB
            );
        }

        /// <summary>
        /// When false, inbound images from other players are dropped (the feature still lets you spawn your own).
        /// Persisted across sessions.
        /// </summary>
        public static bool ReceiveEnabled
        {
            get
            {
                if (!_loaded)
                {
                    _receiveEnabled = PlayerPrefs.GetInt(ReceiveEnabledKey, 1) != 0;
                    _loaded = true;
                }
                return _receiveEnabled;
            }
            set
            {
                _receiveEnabled = value;
                _loaded = true;
                PlayerPrefs.SetInt(ReceiveEnabledKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }
    }
}
