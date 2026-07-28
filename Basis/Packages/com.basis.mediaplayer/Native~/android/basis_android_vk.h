/* Vulkan zero-copy present for Android/Quest: imports a decoded AHardwareBuffer
 * (YCbCr, from MediaCodec via AImageReader) and produces an RGBA VkImage that
 * Unity samples through Texture2D.CreateExternalTexture.
 *
 * Uses VK_ANDROID_external_memory_android_hardware_buffer to import the AHB and a
 * VkSamplerYcbcrConversion + a small graphics pass to resolve YCbCr -> RGBA. The
 * Vulkan device/queue come from Unity via IUnityGraphicsVulkan (basis_gfx_vk_*).
 */
#ifndef BASIS_ANDROID_VK_H
#define BASIS_ANDROID_VK_H

#include <stdint.h>

struct AHardwareBuffer;

#ifdef __cplusplus
extern "C" {
#endif

typedef struct basis_vk_present basis_vk_present;

basis_vk_present* basis_vk_create(void);
void             basis_vk_destroy(basis_vk_present* v);

/* Decode thread: hand off the newest decoded AHardwareBuffer (the present takes
 * its own ref). uvXform is (scaleX, scaleY, offsetX, offsetY) applied to the
 * 0..1 sample quad so only the codec's crop rectangle is resolved (the coded
 * buffer pads the height up to a macroblock multiple; sampling the whole buffer
 * draws the pad as an edge strip). Pass (1,1,0,0) for the full buffer. Cheap;
 * the GPU work happens in basis_vk_render_update. */
void basis_vk_set_hardware_buffer(basis_vk_present* v, struct AHardwareBuffer* ahb, int w, int h,
                                  const float uvXform[4]);

/* Render thread: import the pending AHB (if any) and resolve it to the RGBA
 * VkImage. Returns 1 if a new frame was published. */
int basis_vk_render_update(basis_vk_present* v);

/* Returns the registered Unity texture native handle (for diagnostics) and the
 * size. Returns 0 until basis_vk_set_output_texture has been called. */
uint64_t basis_vk_get_image(basis_vk_present* v, int* w, int* h);
uint64_t basis_vk_frame_counter(basis_vk_present* v);

/* Main thread: register the Unity-owned VkImage (RenderTexture.GetNativeTexturePtr())
 * the engine should render into. Replaces the old "plugin allocates VkImage,
 * Unity wraps via CreateExternalTexture" handoff — Mali's vkCreateImageView
 * crashes when wrapping plugin-allocated images on Pixel 7/8/9 + Android 16.
 * Pass NULL to clear. Idempotent if called with the same handle/size. */
void basis_vk_set_output_texture(basis_vk_present* v, void* native_texture, int w, int h);

/* Render thread: free all Vulkan + AHB resources. */
void basis_vk_release(basis_vk_present* v);

#ifdef __cplusplus
}
#endif
#endif
