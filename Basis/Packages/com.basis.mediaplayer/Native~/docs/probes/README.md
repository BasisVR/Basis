# Device probes

Raw capability dumps from real hardware, used for decode and present
decisions. File names carry the device and OS build, so runs on other devices
can sit beside them.

## Quest Pro, Meta OS v206 (Android 14, Adreno 650), 2026-08-13

- `quest-pro-v206-mediacodec.json`: the full `MediaCodecList` (codec names,
  hardware and software flags, profiles and levels per MIME type, resolution,
  frame-rate and bitrate ranges, performance points), gathered in-app through
  `MediaCodecList.getCodecInfoAt`.
- `quest-pro-v206-vulkan-interception.txt`: the report from a Unity 6000.5,
  OpenXR and Vulkan test app whose plugin registered a Vulkan initialisation
  interceptor through `IUnityGraphicsVulkanV2::AddInterceptInitialization`,
  then imported a synthetic YUV420 `AHardwareBuffer` into Unity's `VkDevice` as
  a YCbCr image (dedicated allocation, driver-suggested conversion). OpenXR kept
  rendering throughout.

What they show:

- Intercepting Vulkan device creation works alongside Unity's OpenXR path on
  Quest; device creation goes through the intercepted `vkGetInstanceProcAddr`
  chain.
- Unity's Quest device already enables
  `VK_ANDROID_external_memory_android_hardware_buffer`,
  `VK_EXT_queue_family_foreign` and `VK_KHR_sampler_ycbcr_conversion`, among
  others, and chains `VkPhysicalDeviceSamplerYcbcrConversionFeatures` itself.
- A YUV420 `AHardwareBuffer` imports through the external-format path
  (`externalFormat` 0x287 on this driver), with a suggested conversion of
  BT.601 narrow range and midpoint chroma, and linear chroma filtering
  supported.
- Hardware decoders: `c2.qti.avc.decoder`, `c2.qti.hevc.decoder`,
  `c2.qti.vp9.decoder`, `c2.qti.vp8.decoder` and `c2.qti.mpeg2.decoder` (plus
  the older `OMX.qcom.*` names). AVC, HEVC and VP9 reach 4096x2304 at 30 fps
  and 3840x2160 at 60.
- There is no AV1 decoder, hardware or software.
- There is no software fallback for AVC, HEVC or VP9 (`c2.android.*` covers
  only VP8 and MPEG-4), so without the hardware decoder those do not play.
