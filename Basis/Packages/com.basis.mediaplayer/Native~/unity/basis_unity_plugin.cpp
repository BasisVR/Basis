/*
 * basis_unity_plugin.cpp — Unity native plugin glue.
 *
 * Implements:
 *   - UnityPluginLoad/Unload + device-event callback to learn the graphics API
 *     and capture Unity's device handles (D3D11/D3D12/Vulkan) for the decoders.
 *   - basis_gfx_* accessors consumed by the platform backends.
 *   - basis_media_get_render_event_func(): the render-thread entry that dispatches
 *     BASIS_RENDER_UPDATE / BASIS_RENDER_RELEASE to basis_decoder_render_*.
 *
 * Requires Unity's PluginAPI headers (IUnityInterface.h, IUnityGraphics.h,
 * IUnityGraphicsD3D11.h, IUnityGraphicsD3D12.h, IUnityGraphicsVulkan.h). Copy them
 * from <UnityEditor>/Data/PluginAPI into this folder, or add that folder to the
 * compiler include path (see CMakeLists.txt / unity/README.md).
 */

#include "../basis_media_native.h"
#include "../basis_media_internal.h"

#include "IUnityInterface.h"
#include "IUnityGraphics.h"

#if defined(_WIN32)
  #include <d3d11.h>
  #include <d3d12.h>
  #include "IUnityGraphicsD3D11.h"
  #include "IUnityGraphicsD3D12.h"
#endif
#if defined(__ANDROID__)
  #include "IUnityGraphicsVulkan.h"
#endif

static IUnityInterfaces* s_unity = nullptr;
static IUnityGraphics*   s_graphics = nullptr;
static basis_gfx_api_t   s_api = BASIS_GFX_NONE;

static void* s_d3d11Device = nullptr;
static void* s_d3d12Device = nullptr;
static void* s_d3d12Queue  = nullptr;

static uint64_t s_vkInstance = 0, s_vkDevice = 0, s_vkPhys = 0, s_vkQueue = 0;
static uint32_t s_vkQueueFamily = 0;
static void*    s_unityVulkan = nullptr;

/* ---- basis_gfx_* accessors (internal header) ---------------------------- */

extern "C" basis_gfx_api_t basis_gfx_get_api(void) { return s_api; }
extern "C" void* basis_gfx_get_d3d11_device(void) { return s_d3d11Device; }
extern "C" void* basis_gfx_get_d3d12_device(void) { return s_d3d12Device; }
extern "C" void* basis_gfx_get_d3d12_queue(void) { return s_d3d12Queue; }
extern "C" void* basis_gfx_get_unity_vulkan(void) { return s_unityVulkan; }
extern "C" uint64_t basis_gfx_vk_instance(void) { return s_vkInstance; }
extern "C" uint64_t basis_gfx_vk_device(void) { return s_vkDevice; }
extern "C" uint64_t basis_gfx_vk_physical_device(void) { return s_vkPhys; }
extern "C" uint64_t basis_gfx_vk_graphics_queue(void) { return s_vkQueue; }
extern "C" uint32_t basis_gfx_vk_graphics_queue_family(void) { return s_vkQueueFamily; }

extern "C" int basis_gfx_vk_access_texture(void* native_texture,
                                           uint64_t* out_image,
                                           int* out_format,
                                           int* out_w,
                                           int* out_h) {
    if (out_image) *out_image = 0;
    if (out_format) *out_format = 0;
    if (out_w) *out_w = 0;
    if (out_h) *out_h = 0;
#if defined(__ANDROID__)
    if (!s_unityVulkan || !native_texture) return 0;
    IUnityGraphicsVulkan* vk = (IUnityGraphicsVulkan*)s_unityVulkan;
    /* Observe-only: returns the resource attributes without recording anything
     * into Unity's command buffer. The layout/stage/access arguments are moot
     * in this mode; the caller's own submission handles all synchronisation. */
    UnityVulkanImage img = {};
    if (!vk->AccessTexture(native_texture,
                           UnityVulkanWholeImage,
                           VK_IMAGE_LAYOUT_UNDEFINED,
                           VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                           0,
                           kUnityVulkanResourceAccess_ObserveOnly,
                           &img))
        return 0;

    if (out_image) *out_image = (uint64_t)(uintptr_t)img.image;
    if (out_format) *out_format = (int)img.format;
    if (out_w) *out_w = (int)img.extent.width;
    if (out_h) *out_h = (int)img.extent.height;
    return 1;
#else
    (void)native_texture;
    return 0;
#endif
}

/* ---- device discovery --------------------------------------------------- */

static void capture_devices() {
    if (!s_graphics) return;
    UnityGfxRenderer r = s_graphics->GetRenderer();
    switch (r) {
#if defined(_WIN32)
        case kUnityGfxRendererD3D11: {
            s_api = BASIS_GFX_D3D11;
            IUnityGraphicsD3D11* d = s_unity->Get<IUnityGraphicsD3D11>();
            if (d) s_d3d11Device = d->GetDevice();
            break;
        }
        case kUnityGfxRendererD3D12: {
            s_api = BASIS_GFX_D3D12;
            // v2 exposes GetDevice() (enough for OpenSharedHandle). GetCommandQueue
            // is only on v5+ and requires a graphics-queue access opt-in via
            // Configure(); wire that if/when the D3D12 copy+fence path is finished.
            IUnityGraphicsD3D12v2* d = s_unity->Get<IUnityGraphicsD3D12v2>();
            if (d) s_d3d12Device = d->GetDevice();
            break;
        }
#endif
#if defined(__ANDROID__)
        case kUnityGfxRendererVulkan: {
            s_api = BASIS_GFX_VULKAN;
            IUnityGraphicsVulkan* vk = s_unity->Get<IUnityGraphicsVulkan>();
            if (vk) {
                s_unityVulkan = vk;
                UnityVulkanInstance inst = vk->Instance();
                s_vkInstance = (uint64_t)(uintptr_t)inst.instance;
                s_vkPhys     = (uint64_t)(uintptr_t)inst.physicalDevice;
                s_vkDevice   = (uint64_t)(uintptr_t)inst.device;
                s_vkQueue    = (uint64_t)(uintptr_t)inst.graphicsQueue;
                s_vkQueueFamily = inst.queueFamilyIndex;
                /* The update event submits its own command buffer on the
                 * graphics queue; the Allow access has Unity keep its queue
                 * users (including the submission thread) off the queue while
                 * the callback runs. Nothing is recorded into Unity's command
                 * buffers, so no render-pass precondition is needed. The
                 * release event only waits fences and destroys plugin objects,
                 * so it needs no configuration. */
                UnityVulkanPluginEventConfig cfg = {};
                cfg.renderPassPrecondition = kUnityVulkanRenderPass_DontCare;
                cfg.graphicsQueueAccess = kUnityVulkanGraphicsQueueAccess_Allow;
                cfg.flags = kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission;
                vk->ConfigureEvent(BASIS_RENDER_UPDATE, &cfg);
            }
            break;
        }
#endif
        default: s_api = BASIS_GFX_NONE; break;
    }
}

static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType type) {
    if (type == kUnityGfxDeviceEventInitialize) capture_devices();
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
UnityPluginLoad(IUnityInterfaces* interfaces) {
    s_unity = interfaces;
    s_graphics = interfaces->Get<IUnityGraphics>();
    if (s_graphics) {
        s_graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
        OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
UnityPluginUnload() {
    if (s_graphics) s_graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    s_graphics = nullptr;
    s_unity = nullptr;
}

/* ---- render-thread entry ------------------------------------------------ */

static void BASIS_CALL OnRenderEvent(int event_id, void* data) {
    /* Forward through the engine's liveness registry: the pointer comes from Unity
     * and may already have been freed by basis_media_close on the main thread, so
     * the dispatch (and the decoder deref) happens under the registry lock, never
     * on a freed engine. */
    basis_engine_render_event((basis_media_engine_t*)data, event_id);
}

extern "C" BASIS_API basis_render_event_func BASIS_CALL basis_media_get_render_event_func(void) {
    return OnRenderEvent;
}
