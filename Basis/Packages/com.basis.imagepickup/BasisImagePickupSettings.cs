using Basis.Scripts.Common;
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
            CalculateAnimationMemoryLimits(SystemInfo.systemMemorySize, BasisGpuDetection.IsMobileGpu);

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
        public const float RecipientRangeRefreshSeconds = 5f;

        /// <summary>
        /// How often a client re-measures its distance to the images the server has offered it. Cheap
        /// enough to run often - the work is one Burst job over a handful of positions - and short
        /// enough that walking up to a picture loads it rather than making you wait for it.
        /// </summary>
        public const float OfferRangeCheckSeconds = 0.5f;

        /// <summary>How long a transfer may go without a chunk before it is reported as stalled.</summary>
        public const float StalledTransferWarningSeconds = 5f;

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
        /// <summary>
        /// Ceiling on per-frame chunk work, so one frame cannot spend an unbounded amount of time copying
        /// and framing packets. This is a CPU bound, not a bandwidth one — <see cref="BasisImagePickupBandwidth"/>
        /// decides the rate. Sized so it stays out of the way up to roughly 60 MiB/s at 60 fps rather than
        /// silently becoming the real limiter on a fast connection, which is what the old values did.
        /// </summary>
        public const int MaxImageNetworkChunksPerFrame = 64;
        public const int MaxAnimationNetworkChunksPerFrame = 64;
        public const int AnimationPacketBuildChunksPerJob = 32;

        /// <summary>
        /// Share of the measured uplink below that image replication may occupy; the rest stays free for
        /// pose, voice, and object sync. The chunks-per-frame caps above bound per-frame cost only — this
        /// is what bounds the rate, and it is enforced by <see cref="BasisImagePickupBandwidth"/>.
        ///
        /// Applies to the local uplink only. A server-advertised relay budget is already a deliberate
        /// figure from whoever runs the server, so it is spent as given rather than halved again.
        /// </summary>
        public const float ShareBandwidthFraction = 0.5f;

        /// <summary>
        /// Uplink assumed for Basis traffic before <see cref="BasisImagePickupLinkProbe"/> has measured
        /// anything, in bytes per second. Deliberately below what the slowest supported connection can
        /// carry — the probe climbs quickly, so guessing low costs a fraction of a second on a fast link,
        /// while guessing high spends the first seconds of every session congesting a slow one.
        /// </summary>
        public const long StartingUplinkBudgetBytesPerSecond = 64L * 1024L;

        /// <summary>
        /// Uplink the probe will always allow, so a transfer never stalls outright. One chunk every couple
        /// of seconds after the image share is applied, which still refreshes the inbound deadline.
        /// </summary>
        public const long MinUplinkBudgetBytesPerSecond = 16L * 1024L;

        /// <summary>
        /// Ceiling on the probed uplink. Far above what image transfers can use — the largest payload the
        /// feature accepts moves in a fraction of a second here — and present only so a mismeasurement
        /// cannot ask the rest of the client for something absurd.
        /// </summary>
        public const long MaxUplinkBudgetBytesPerSecond = 256L * 1024L * 1024L;

        /// <summary>
        /// Queuing delay the probe aims to sit under, in milliseconds — round-trip time above the quietest
        /// recent round trip. Below this the rate climbs; above it the rate falls. Kept under a voice frame
        /// so transfers yield before anyone can hear or feel them.
        /// </summary>
        public const float TargetQueuingDelayMs = 18f;

        public const float LinkProbeIntervalSeconds = 0.5f;

        /// <summary>
        /// Share of the current rate the probe adds or removes per second. Proportional rather than fixed
        /// because the supported connections span roughly 1 Mb/s to 25 Gb/s: a step sized for the bottom of
        /// that range would take minutes to find the top, and a step sized for the top would obliterate the
        /// bottom. A fixed fraction crosses the whole range in the same handful of seconds either way.
        /// </summary>
        public const float LinkProbeRampFraction = 0.6f;
        /// <summary>
        /// How much round-trip history the quiet baseline is drawn from. Long enough that a transfer's own
        /// queuing never becomes the baseline it is measured against, short enough that a path which
        /// genuinely got slower is not fought forever.
        /// </summary>
        public const float LinkProbeBaselineWindowSeconds = 60f;
        /// <summary>Smallest absolute ramp step, so the proportional term still moves at the floor.</summary>
        public const float LinkProbeRampBytesPerSecond = 32L * 1024L;

        /// <summary>
        /// Fewest queued outgoing packets that can count as a backlog. The live threshold is whichever is
        /// larger, this or one control interval's worth of packets at the current rate — a fixed depth
        /// cannot mean the same thing at 1 Mb/s and at 25 Gb/s, where a perfectly healthy transfer keeps far
        /// more than this in flight at any instant.
        /// </summary>
        public const int LinkProbeQueueBackoffPackets = 96;

        public const float LinkProbeQueueBackoffFactor = 0.5f;

        /// <summary>
        /// Server egress one client may cause, in bytes per second, when the server has not said how much
        /// it can afford. A relayed packet costs this budget once per recipient the server forwards it to;
        /// peers on a direct link cost it nothing.
        ///
        /// Only a guess, and deliberately a timid one, because a client cannot see the far end of the
        /// relay — which is why a server that does advertise a figure overrides it outright rather than
        /// being averaged with it. Divided by the fan-out this is roughly 25 KB/s per sharer in a
        /// twenty-player instance, so an instance running on the fallback shares pictures slowly no matter
        /// how much pipe either end has. Servers should set ImageShareEgressMegabitsPerSecond.
        /// </summary>
        public const long RelayEgressBudgetBytesPerSecond = 512L * 1024L;

        /// <summary>How much unspent budget either bucket may bank, in seconds.</summary>
        public const float ShareBandwidthBurstSeconds = 0.25f;

        /// <summary>How often a transfer's throughput readout is resampled, in seconds.</summary>
        public const float TransferRateSampleSeconds = 0.25f;

        /// <summary>Weight of the newest throughput sample in a transfer's smoothed rate.</summary>
        public const float TransferRateSmoothing = 0.35f;

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
            ShouldUseDepthBufferAnimationVisibility(BasisGpuDetection.IsMobileGpu);

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
