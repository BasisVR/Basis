/*
 * basis_android_vk.c — Vulkan zero-copy present for Quest.
 *
 * Imports each decoded AHardwareBuffer (YCbCr) via
 * VK_ANDROID_external_memory_android_hardware_buffer and resolves it to an RGBA
 * VkImage that Unity samples. The resolve is a fullscreen draw that samples the
 * AHB through an immutable Y'CbCr-conversion sampler (the conversion does
 * YCbCr->RGB), recorded into a plugin-owned command buffer and submitted on the
 * graphics queue from the render event. The event is configured with
 * kUnityVulkanGraphicsQueueAccess_Allow, so Unity holds its own queue users off
 * the queue while the callback runs; a fence per ring slot tells us when an
 * imported buffer is free to release. Nothing is ever recorded into Unity's
 * command buffers: a foreign render pass recorded there corrupts the Adreno
 * driver's command-buffer state and crashes later in vkCmdExecuteCommands.
 *
 * No layout coordination with Unity is needed: the render pass takes the target
 * from UNDEFINED (contents discarded — every draw overwrites the full frame) and
 * hands it back in SHADER_READ_ONLY, the layout Unity samples it in. Same-queue
 * submission order plus the render pass's external dependencies order the write
 * against Unity's subsequent sampling.
 *
 * Pipeline objects (Y'CbCr conversion, immutable sampler, descriptor layout,
 * render pass, graphics pipeline) depend only on the AHB external format, so they
 * are built once and reused; only the per-frame source image/view/descriptor are
 * rebuilt, from a small ring so a buffer is never destroyed while the GPU may
 * still be reading it.
 *
 * ON-DEVICE VALIDATION POINTS (can't be exercised off-Quest):
 *   - external-format image view + ycbcr immutable sampler descriptor wiring
 *   - the render pass leaves rgbaImage in SHADER_READ_ONLY_OPTIMAL, which is the
 *     layout Unity expects to sample
 *   - fence-based reclamation depth (BASIS_VK_RING) vs frames in flight
 */

#include "basis_android_vk.h"
#include "../basis_media_internal.h"
#include "basis_vk_shaders.h"

#define VK_USE_PLATFORM_ANDROID_KHR
#include <vulkan/vulkan.h>
#include <vulkan/vulkan_android.h>
#include <android/hardware_buffer.h>
#include <android/log.h>

#include <pthread.h>
#include <stdlib.h>
#include <string.h>

#define BASIS_VK_RING 4   /* imported source buffers kept alive for in-flight frames */

typedef struct {
    AHardwareBuffer* ahb;
    VkImage          image;
    VkDeviceMemory   memory;
    VkImageView      view;
    VkDescriptorSet  set;
    VkCommandBuffer  cmd;    /* allocated once with the pool, re-recorded per submit */
    VkFence          fence;  /* created signaled; reset just before each submit */
    int              inUse;
} basis_vk_slot;

struct basis_vk_present {
    VkInstance instance;
    VkPhysicalDevice phys;
    VkDevice device;
    VkQueue queue;
    uint32_t queueFamily;

    VkCommandPool cmdPool;   /* backs the per-slot command buffers */

    PFN_vkGetAndroidHardwareBufferPropertiesANDROID getAHBProps;

    pthread_mutex_t lock;
    AHardwareBuffer* pending;   /* newest decoded buffer awaiting import */
    int w, h;
    float uv[4];                /* crop UV transform for `pending` (scale.xy, offset.zw) */

    /* format-keyed resolve pipeline (rebuilt only if the external format changes) */
    int      haveFormat;
    uint64_t externalFormat;
    VkSamplerYcbcrConversion ycbcr;
    VkSampler             sampler;     /* immutable, ycbcr conversion attached */
    VkDescriptorSetLayout dsLayout;
    VkPipelineLayout      pipeLayout;
    VkRenderPass          renderPass;
    VkPipeline            pipeline;
    VkShaderModule        vert, frag;
    VkDescriptorPool      descPool;

    /* per-frame ring of imported source images + their descriptor sets */
    basis_vk_slot ring[BASIS_VK_RING];

    /* RGBA target — Unity-owned, populated via IUnityGraphicsVulkan::AccessTexture.
     * The native handle comes from C# (RenderTexture.GetNativeTexturePtr()) via
     * basis_vk_set_output_texture; we only own the framebuffer that pairs it with
     * the YCbCr->RGB render pass, and rebuild that when Unity rotates the
     * underlying VkImage (rare). */
    void*         unityNativeTex;
    int           unityDirty;       /* handle changed on the main thread; the render thread drops the fbo */
    VkImage       cachedUnityImage; /* the VkImage AccessTexture returned for unityNativeTex (render thread) */
    VkImageView   unityImageView;
    VkFramebuffer fbo;
    int           fboW, fboH;       /* extent the fbo was built with — render-thread-owned, distinct from unityW/H */
    int           unityW, unityH;   /* C#-registered RenderTexture size; written by the setter under v->lock */
    int           unityFormat;     /* VkFormat returned by AccessTexture (UNORM or SRGB) */

    uint64_t frameCounter;
};

static void load_handles(basis_vk_present* v) {
    v->instance    = (VkInstance)(uintptr_t)basis_gfx_vk_instance();
    v->phys        = (VkPhysicalDevice)(uintptr_t)basis_gfx_vk_physical_device();
    v->device      = (VkDevice)(uintptr_t)basis_gfx_vk_device();
    v->queue       = (VkQueue)(uintptr_t)basis_gfx_vk_graphics_queue();
    v->queueFamily = basis_gfx_vk_graphics_queue_family();
    if (v->device)
        v->getAHBProps = (PFN_vkGetAndroidHardwareBufferPropertiesANDROID)
            vkGetDeviceProcAddr(v->device, "vkGetAndroidHardwareBufferPropertiesANDROID");
}

basis_vk_present* basis_vk_create(void) {
    basis_vk_present* v = (basis_vk_present*)calloc(1, sizeof(*v));
    if (!v) return NULL;
    pthread_mutex_init(&v->lock, NULL);
    load_handles(v);
    return v;
}

void basis_vk_set_hardware_buffer(basis_vk_present* v, AHardwareBuffer* ahb, int w, int h,
                                  const float uvXform[4]) {
    if (!v || !ahb) return;
    AHardwareBuffer_acquire(ahb);
    pthread_mutex_lock(&v->lock);
    if (v->pending) AHardwareBuffer_release(v->pending);
    v->pending = ahb; v->w = w; v->h = h;
    if (uvXform) { v->uv[0] = uvXform[0]; v->uv[1] = uvXform[1]; v->uv[2] = uvXform[2]; v->uv[3] = uvXform[3]; }
    else { v->uv[0] = v->uv[1] = 1.0f; v->uv[2] = v->uv[3] = 0.0f; }
    pthread_mutex_unlock(&v->lock);
}

/* ---- slot (per-frame source image) ------------------------------------- */

/* Wait out any in-flight submissions before destroying resources their
 * command buffers reference — slot images/descriptors and the Unity
 * framebuffer alike. Never-submitted fences were created signaled. The wait
 * is capped: a resolve draw that hasn't completed after 1s means a wedged or
 * lost device, where hanging teardown would be worse than proceeding — but
 * log it so a teardown-on-hang is diagnosable. */
static void wait_in_flight(basis_vk_present* v) {
    for (int i = 0; i < BASIS_VK_RING; ++i)
        if (v->ring[i].inUse && v->ring[i].fence) {
            VkResult r = vkWaitForFences(v->device, 1, &v->ring[i].fence, VK_TRUE, 1000000000ull);
            if (r != VK_SUCCESS)
                __android_log_print(ANDROID_LOG_WARN, "basis_media",
                    "wait_in_flight: slot %d fence wait returned %d; destroying anyway", i, (int)r);
        }
}

static void destroy_slot(basis_vk_present* v, basis_vk_slot* s) {
    if (s->view)   { vkDestroyImageView(v->device, s->view, NULL); s->view = VK_NULL_HANDLE; }
    if (s->image)  { vkDestroyImage(v->device, s->image, NULL); s->image = VK_NULL_HANDLE; }
    if (s->memory) { vkFreeMemory(v->device, s->memory, NULL); s->memory = VK_NULL_HANDLE; }
    if (s->ahb)    { AHardwareBuffer_release(s->ahb); s->ahb = NULL; }
    s->inUse = 0;
}

/* Import an AHardwareBuffer as an external-format Y'CbCr VkImage + view into slot s. */
static int import_into_slot(basis_vk_present* v, basis_vk_slot* s, AHardwareBuffer* ahb,
                            int w, int h, const VkAndroidHardwareBufferPropertiesANDROID* props,
                            uint64_t externalFormat) {
    VkExternalFormatANDROID extFmt = { VK_STRUCTURE_TYPE_EXTERNAL_FORMAT_ANDROID };
    extFmt.externalFormat = externalFormat;

    VkExternalMemoryImageCreateInfo extImg = { VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO };
    extImg.pNext = &extFmt;
    extImg.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_ANDROID_HARDWARE_BUFFER_BIT_ANDROID;

    VkImageCreateInfo ici = { VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO };
    ici.pNext = &extImg;
    ici.imageType = VK_IMAGE_TYPE_2D;
    ici.format = VK_FORMAT_UNDEFINED;          /* external format */
    ici.extent.width = (uint32_t)w; ici.extent.height = (uint32_t)h; ici.extent.depth = 1;
    ici.mipLevels = 1; ici.arrayLayers = 1;
    ici.samples = VK_SAMPLE_COUNT_1_BIT;
    ici.tiling = VK_IMAGE_TILING_OPTIMAL;
    ici.usage = VK_IMAGE_USAGE_SAMPLED_BIT;
    ici.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ici.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (vkCreateImage(v->device, &ici, NULL, &s->image) != VK_SUCCESS) return -1;

    VkImportAndroidHardwareBufferInfoANDROID importInfo = { VK_STRUCTURE_TYPE_IMPORT_ANDROID_HARDWARE_BUFFER_INFO_ANDROID };
    importInfo.buffer = ahb;
    VkMemoryDedicatedAllocateInfo dedicated = { VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO, &importInfo };
    dedicated.image = s->image;

    uint32_t typeIndex = 0;
    for (uint32_t i = 0; i < 32; ++i) if (props->memoryTypeBits & (1u << i)) { typeIndex = i; break; }

    VkMemoryAllocateInfo mai = { VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, &dedicated };
    mai.allocationSize = props->allocationSize;
    mai.memoryTypeIndex = typeIndex;
    if (vkAllocateMemory(v->device, &mai, NULL, &s->memory) != VK_SUCCESS) return -1;
    if (vkBindImageMemory(v->device, s->image, s->memory, 0) != VK_SUCCESS) return -1;

    /* view carries the same ycbcr conversion so sampling does YCbCr->RGB */
    VkSamplerYcbcrConversionInfo cvInfo = { VK_STRUCTURE_TYPE_SAMPLER_YCBCR_CONVERSION_INFO };
    cvInfo.conversion = v->ycbcr;
    VkImageViewCreateInfo vci = { VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
    vci.pNext = &cvInfo;
    vci.image = s->image;
    vci.viewType = VK_IMAGE_VIEW_TYPE_2D;
    vci.format = VK_FORMAT_UNDEFINED;          /* external format */
    vci.components = (VkComponentMapping){ VK_COMPONENT_SWIZZLE_IDENTITY, VK_COMPONENT_SWIZZLE_IDENTITY,
                                           VK_COMPONENT_SWIZZLE_IDENTITY, VK_COMPONENT_SWIZZLE_IDENTITY };
    vci.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    vci.subresourceRange.levelCount = 1;
    vci.subresourceRange.layerCount = 1;
    if (vkCreateImageView(v->device, &vci, NULL, &s->view) != VK_SUCCESS) return -1;

    /* point this slot's descriptor at the new view (immutable sampler ignored) */
    VkDescriptorImageInfo dii = {0};
    dii.imageView = s->view;
    dii.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    VkWriteDescriptorSet wr = { VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET };
    wr.dstSet = s->set;
    wr.dstBinding = 0;
    wr.descriptorCount = 1;
    wr.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    wr.pImageInfo = &dii;
    vkUpdateDescriptorSets(v->device, 1, &wr, 0, NULL);

    AHardwareBuffer_acquire(ahb);
    s->ahb = ahb;
    return 0;
}

/* ---- format-keyed pipeline objects ------------------------------------- */

static VkShaderModule make_module(basis_vk_present* v, const uint32_t* code, size_t bytes) {
    VkShaderModuleCreateInfo ci = { VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO };
    ci.codeSize = bytes;
    ci.pCode = code;
    VkShaderModule m = VK_NULL_HANDLE;
    vkCreateShaderModule(v->device, &ci, NULL, &m);
    return m;
}

static void destroy_format_objects(basis_vk_present* v) {
    wait_in_flight(v); /* slots may still be in flight on a mid-stream external-format change */
    for (int i = 0; i < BASIS_VK_RING; ++i) destroy_slot(v, &v->ring[i]);
    if (v->pipeline)   { vkDestroyPipeline(v->device, v->pipeline, NULL); v->pipeline = VK_NULL_HANDLE; }
    if (v->renderPass) { vkDestroyRenderPass(v->device, v->renderPass, NULL); v->renderPass = VK_NULL_HANDLE; }
    if (v->pipeLayout) { vkDestroyPipelineLayout(v->device, v->pipeLayout, NULL); v->pipeLayout = VK_NULL_HANDLE; }
    if (v->descPool)   { vkDestroyDescriptorPool(v->device, v->descPool, NULL); v->descPool = VK_NULL_HANDLE; }
    if (v->dsLayout)   { vkDestroyDescriptorSetLayout(v->device, v->dsLayout, NULL); v->dsLayout = VK_NULL_HANDLE; }
    if (v->sampler)    { vkDestroySampler(v->device, v->sampler, NULL); v->sampler = VK_NULL_HANDLE; }
    if (v->ycbcr)      { vkDestroySamplerYcbcrConversion(v->device, v->ycbcr, NULL); v->ycbcr = VK_NULL_HANDLE; }
    if (v->vert)       { vkDestroyShaderModule(v->device, v->vert, NULL); v->vert = VK_NULL_HANDLE; }
    if (v->frag)       { vkDestroyShaderModule(v->device, v->frag, NULL); v->frag = VK_NULL_HANDLE; }
    for (int i = 0; i < BASIS_VK_RING; ++i) v->ring[i].set = VK_NULL_HANDLE;
    v->haveFormat = 0;
}

static int ensure_format_objects(basis_vk_present* v, uint64_t externalFormat,
                                 const VkAndroidHardwareBufferFormatPropertiesANDROID* fmt) {
    if (v->haveFormat && v->externalFormat == externalFormat) return 0;
    destroy_format_objects(v);

    /* Y'CbCr conversion described by the AHB external format. */
    VkExternalFormatANDROID extFmt = { VK_STRUCTURE_TYPE_EXTERNAL_FORMAT_ANDROID };
    extFmt.externalFormat = externalFormat;
    /* Linear filtering is only valid when the format advertises the matching
     * feature bit, and each filter has its own bit: chroma reconstruction needs
     * YCBCR_CONVERSION_LINEAR_FILTER, the ordinary sampler mag/min needs
     * SAMPLED_IMAGE_FILTER_LINEAR, and the two filters may only differ when
     * SEPARATE_RECONSTRUCTION_FILTER is present. Qualcomm's UBWC external formats
     * (VP9/AV1 on Adreno) frequently advertise none of these, and forcing LINEAR
     * there is undefined behaviour. Pick each filter from its own bit; when separate
     * reconstruction is absent, force them equal (downgrading chroma to nearest if
     * the sampler cannot do linear). */
    VkFormatFeatureFlags features = fmt->formatFeatures;
    int separate     = (features & VK_FORMAT_FEATURE_SAMPLED_IMAGE_YCBCR_CONVERSION_SEPARATE_RECONSTRUCTION_FILTER_BIT) != 0;
    int linearSample = (features & VK_FORMAT_FEATURE_SAMPLED_IMAGE_FILTER_LINEAR_BIT) != 0;
    VkFilter yf = (features & VK_FORMAT_FEATURE_SAMPLED_IMAGE_YCBCR_CONVERSION_LINEAR_FILTER_BIT)
                  ? VK_FILTER_LINEAR : VK_FILTER_NEAREST;
    VkFilter samplerFilter = linearSample ? VK_FILTER_LINEAR : VK_FILTER_NEAREST;
    if (!separate) {
        if (yf == VK_FILTER_LINEAR && !linearSample) yf = VK_FILTER_NEAREST;
        samplerFilter = yf;
    }
    VkSamplerYcbcrConversionCreateInfo cy = { VK_STRUCTURE_TYPE_SAMPLER_YCBCR_CONVERSION_CREATE_INFO };
    cy.pNext = &extFmt;
    cy.format = VK_FORMAT_UNDEFINED;
    cy.ycbcrModel = fmt->suggestedYcbcrModel;
    cy.ycbcrRange = fmt->suggestedYcbcrRange;
    cy.components = fmt->samplerYcbcrConversionComponents;
    cy.xChromaOffset = fmt->suggestedXChromaOffset;
    cy.yChromaOffset = fmt->suggestedYChromaOffset;
    cy.chromaFilter = yf;
    if (vkCreateSamplerYcbcrConversion(v->device, &cy, NULL, &v->ycbcr) != VK_SUCCESS) return -1;

    /* immutable sampler with the conversion attached */
    VkSamplerYcbcrConversionInfo cvInfo = { VK_STRUCTURE_TYPE_SAMPLER_YCBCR_CONVERSION_INFO };
    cvInfo.conversion = v->ycbcr;
    VkSamplerCreateInfo sci = { VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO };
    sci.pNext = &cvInfo;
    sci.magFilter = samplerFilter; sci.minFilter = samplerFilter;
    sci.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
    sci.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.unnormalizedCoordinates = VK_FALSE;
    if (vkCreateSampler(v->device, &sci, NULL, &v->sampler) != VK_SUCCESS) return -1;

    /* descriptor set layout: binding 0 = combined image sampler w/ immutable sampler */
    VkDescriptorSetLayoutBinding b = {0};
    b.binding = 0;
    b.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    b.descriptorCount = 1;
    b.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    b.pImmutableSamplers = &v->sampler;
    VkDescriptorSetLayoutCreateInfo dlc = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO };
    dlc.bindingCount = 1; dlc.pBindings = &b;
    if (vkCreateDescriptorSetLayout(v->device, &dlc, NULL, &v->dsLayout) != VK_SUCCESS) return -1;

    /* pool + one descriptor set per ring slot */
    VkDescriptorPoolSize ps = { VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, BASIS_VK_RING };
    VkDescriptorPoolCreateInfo pci = { VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO };
    pci.maxSets = BASIS_VK_RING; pci.poolSizeCount = 1; pci.pPoolSizes = &ps;
    if (vkCreateDescriptorPool(v->device, &pci, NULL, &v->descPool) != VK_SUCCESS) return -1;
    for (int i = 0; i < BASIS_VK_RING; ++i) {
        VkDescriptorSetAllocateInfo dai = { VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO };
        dai.descriptorPool = v->descPool; dai.descriptorSetCount = 1; dai.pSetLayouts = &v->dsLayout;
        if (vkAllocateDescriptorSets(v->device, &dai, &v->ring[i].set) != VK_SUCCESS) return -1;
    }

    /* vec4 crop UV transform, pushed per frame into the vertex stage */
    VkPushConstantRange pcr = { VK_SHADER_STAGE_VERTEX_BIT, 0, 4 * sizeof(float) };
    VkPipelineLayoutCreateInfo plc = { VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO };
    plc.setLayoutCount = 1; plc.pSetLayouts = &v->dsLayout;
    plc.pushConstantRangeCount = 1; plc.pPushConstantRanges = &pcr;
    if (vkCreatePipelineLayout(v->device, &plc, NULL, &v->pipeLayout) != VK_SUCCESS) return -1;

    /* render pass: write rgba then hand it to Unity already SHADER_READ_ONLY */
    VkAttachmentDescription att = {0};
    att.format = VK_FORMAT_R8G8B8A8_UNORM;
    att.samples = VK_SAMPLE_COUNT_1_BIT;
    att.loadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    att.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    att.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    att.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    att.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    att.finalLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    VkAttachmentReference ref = { 0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL };
    VkSubpassDescription sub = {0};
    sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    sub.colorAttachmentCount = 1; sub.pColorAttachments = &ref;
    VkSubpassDependency deps[2] = {0};
    deps[0].srcSubpass = VK_SUBPASS_EXTERNAL; deps[0].dstSubpass = 0;
    deps[0].srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    deps[0].dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    deps[0].srcAccessMask = 0;
    deps[0].dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    deps[1].srcSubpass = 0; deps[1].dstSubpass = VK_SUBPASS_EXTERNAL;
    deps[1].srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    deps[1].dstStageMask = VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT;
    deps[1].srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    deps[1].dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
    VkRenderPassCreateInfo rpc = { VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO };
    rpc.attachmentCount = 1; rpc.pAttachments = &att;
    rpc.subpassCount = 1; rpc.pSubpasses = &sub;
    rpc.dependencyCount = 2; rpc.pDependencies = deps;
    if (vkCreateRenderPass(v->device, &rpc, NULL, &v->renderPass) != VK_SUCCESS) return -1;

    v->vert = make_module(v, basis_vk_resolve_vert_spv, sizeof(basis_vk_resolve_vert_spv));
    v->frag = make_module(v, basis_vk_resolve_frag_spv, sizeof(basis_vk_resolve_frag_spv));
    if (!v->vert || !v->frag) return -1;

    VkPipelineShaderStageCreateInfo stages[2] = {
        { VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO },
        { VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO },
    };
    stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT;   stages[0].module = v->vert; stages[0].pName = "main";
    stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT; stages[1].module = v->frag; stages[1].pName = "main";

    VkPipelineVertexInputStateCreateInfo vin = { VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO };
    VkPipelineInputAssemblyStateCreateInfo ia = { VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO };
    ia.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    VkPipelineViewportStateCreateInfo vp = { VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO };
    vp.viewportCount = 1; vp.scissorCount = 1;
    VkPipelineRasterizationStateCreateInfo rs = { VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO };
    rs.polygonMode = VK_POLYGON_MODE_FILL; rs.cullMode = VK_CULL_MODE_NONE;
    rs.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE; rs.lineWidth = 1.0f;
    VkPipelineMultisampleStateCreateInfo ms = { VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO };
    ms.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
    VkPipelineColorBlendAttachmentState cba = {0};
    cba.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
    VkPipelineColorBlendStateCreateInfo cb = { VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO };
    cb.attachmentCount = 1; cb.pAttachments = &cba;
    VkDynamicState dyn[2] = { VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR };
    VkPipelineDynamicStateCreateInfo ds = { VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO };
    ds.dynamicStateCount = 2; ds.pDynamicStates = dyn;

    VkGraphicsPipelineCreateInfo gpc = { VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO };
    gpc.stageCount = 2; gpc.pStages = stages;
    gpc.pVertexInputState = &vin; gpc.pInputAssemblyState = &ia;
    gpc.pViewportState = &vp; gpc.pRasterizationState = &rs;
    gpc.pMultisampleState = &ms; gpc.pColorBlendState = &cb;
    gpc.pDynamicState = &ds;
    gpc.layout = v->pipeLayout; gpc.renderPass = v->renderPass; gpc.subpass = 0;
    if (vkCreateGraphicsPipelines(v->device, VK_NULL_HANDLE, 1, &gpc, NULL, &v->pipeline) != VK_SUCCESS) return -1;

    v->externalFormat = externalFormat;
    v->haveFormat = 1;
    return 0;
}

static void destroy_unity_fbo(basis_vk_present* v) {
    if (v->fbo) wait_in_flight(v); /* in-flight command buffers reference the framebuffer */
    if (v->fbo)            { vkDestroyFramebuffer(v->device, v->fbo, NULL); v->fbo = VK_NULL_HANDLE; }
    if (v->unityImageView) { vkDestroyImageView(v->device, v->unityImageView, NULL); v->unityImageView = VK_NULL_HANDLE; }
    v->cachedUnityImage = VK_NULL_HANDLE;
}

/* Rebuild the framebuffer that pairs Unity's VkImage with our YCbCr->RGB render
 * pass. Called per frame; the (image,view,fbo) trio is cached and only rebuilt
 * when AccessTexture hands back a different VkImage (Unity allocated a new RT,
 * or rotated under us). The image itself is OWNED BY UNITY — we never destroy
 * it, only the view and framebuffer we created on top. */
static int ensure_unity_fbo(basis_vk_present* v, VkImage image, VkFormat format, int w, int h) {
    if (v->fbo && v->cachedUnityImage == image && v->fboW == w && v->fboH == h) return 0;
    destroy_unity_fbo(v);

    VkImageViewCreateInfo vci = { VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
    vci.image = image;
    vci.viewType = VK_IMAGE_VIEW_TYPE_2D;
    /* Match the format Unity reported for its image — UNORM for linear-space
     * RenderTextures, SRGB for gamma-corrected. Mali only crashes when the
     * view-create receives a format that wasn't declared at image-create time;
     * Unity declared this image with its own format, so using the same format
     * here is always legal. */
    vci.format = format;
    vci.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    vci.subresourceRange.levelCount = 1;
    vci.subresourceRange.layerCount = 1;
    if (vkCreateImageView(v->device, &vci, NULL, &v->unityImageView) != VK_SUCCESS) return -1;

    VkFramebufferCreateInfo fci = { VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO };
    fci.renderPass = v->renderPass;
    fci.attachmentCount = 1;
    fci.pAttachments = &v->unityImageView;
    fci.width = (uint32_t)w;
    fci.height = (uint32_t)h;
    fci.layers = 1;
    if (vkCreateFramebuffer(v->device, &fci, NULL, &v->fbo) != VK_SUCCESS) return -1;

    v->cachedUnityImage = image;
    v->fboW = w;
    v->fboH = h;
    v->unityFormat = (int)format;
    return 0;
}

void basis_vk_set_output_texture(basis_vk_present* v, void* native_texture, int w, int h) {
    if (!v) return;
    /* Runs on the main thread while the render thread may be mid-present using the
     * framebuffer/image-view. Don't destroy them here — just record the change
     * under the lock and let render_update drop+rebuild the fbo on its own thread
     * (unityDirty). We intentionally don't touch cachedUnityImage because
     * AccessTexture may legitimately return the same VkImage for a recreated
     * RenderTexture if Unity reused the slot. */
    pthread_mutex_lock(&v->lock);
    if (v->unityNativeTex != native_texture || v->unityW != w || v->unityH != h) {
        v->unityNativeTex = native_texture;
        v->unityDirty = 1;   /* a same-handle resize still needs the fbo rebuilt */
    }
    v->unityW = w;
    v->unityH = h;
    pthread_mutex_unlock(&v->lock);
}

/* ---- plugin-owned submission objects ------------------------------------ */

static void destroy_cmd_objects(basis_vk_present* v) {
    for (int i = 0; i < BASIS_VK_RING; ++i) {
        if (v->ring[i].fence) { vkDestroyFence(v->device, v->ring[i].fence, NULL); v->ring[i].fence = VK_NULL_HANDLE; }
        v->ring[i].cmd = VK_NULL_HANDLE; /* freed with the pool */
    }
    if (v->cmdPool) { vkDestroyCommandPool(v->device, v->cmdPool, NULL); v->cmdPool = VK_NULL_HANDLE; }
}

/* One pool plus a command buffer and fence per ring slot, created lazily on
 * the render thread. Fences start signaled so a never-submitted slot can't
 * block a wait. */
static int ensure_cmd_objects(basis_vk_present* v) {
    if (v->cmdPool) return 0;
    VkCommandPoolCreateInfo pci = { VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    pci.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pci.queueFamilyIndex = v->queueFamily;
    if (vkCreateCommandPool(v->device, &pci, NULL, &v->cmdPool) != VK_SUCCESS) return -1;

    VkCommandBuffer cbs[BASIS_VK_RING];
    VkCommandBufferAllocateInfo cai = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    cai.commandPool = v->cmdPool;
    cai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    cai.commandBufferCount = BASIS_VK_RING;
    if (vkAllocateCommandBuffers(v->device, &cai, cbs) != VK_SUCCESS) { destroy_cmd_objects(v); return -1; }

    for (int i = 0; i < BASIS_VK_RING; ++i) {
        v->ring[i].cmd = cbs[i];
        VkFenceCreateInfo fci = { VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
        fci.flags = VK_FENCE_CREATE_SIGNALED_BIT;
        if (vkCreateFence(v->device, &fci, NULL, &v->ring[i].fence) != VK_SUCCESS) { destroy_cmd_objects(v); return -1; }
    }
    return 0;
}

/* ---- per-frame resolve ------------------------------------------------- */

int basis_vk_render_update(basis_vk_present* v) {
    if (!v || !v->device || !v->getAHBProps) return 0;

    /* All Unity-registration state is read as one locked snapshot — no unlocked
     * peek at unityNativeTex first. Without a registered output texture there is
     * nowhere to render (C# calls basis_media_set_output_texture once
     * TryGetVideoSize is non-zero; until then the demuxer's AHBs sit in pending). */
    AHardwareBuffer* ahb = NULL; int w, h; float uv[4];
    void* unityTex; int unityDirty; int regW, regH;
    pthread_mutex_lock(&v->lock);
    unityTex = v->unityNativeTex; regW = v->unityW; regH = v->unityH;
    unityDirty = v->unityDirty;
    w = v->w; h = v->h;
    uv[0] = v->uv[0]; uv[1] = v->uv[1]; uv[2] = v->uv[2]; uv[3] = v->uv[3];
    /* Only detach the pending frame when there's a texture to render it into —
     * otherwise it stays queued (the producer replaces + releases it) instead of
     * being dropped here with its AHB reference leaked. */
    if (unityTex) {
        ahb = v->pending; v->pending = NULL;
        if (ahb) v->unityDirty = 0; /* consume the dirty flag only when this pass rebuilds+renders */
    }
    pthread_mutex_unlock(&v->lock);
    if (!unityTex) return 0;
    if (!ahb) return 0;
    /* Handle changed on the main thread: drop the old framebuffer/image-view here,
     * on the render thread, so nothing is destroyed under a present in flight. */
    if (unityDirty) destroy_unity_fbo(v);

    /* AHB format + memory properties (drives the ycbcr conversion + allocation) */
    VkAndroidHardwareBufferFormatPropertiesANDROID fmtProps = { VK_STRUCTURE_TYPE_ANDROID_HARDWARE_BUFFER_FORMAT_PROPERTIES_ANDROID };
    VkAndroidHardwareBufferPropertiesANDROID props = { VK_STRUCTURE_TYPE_ANDROID_HARDWARE_BUFFER_PROPERTIES_ANDROID, &fmtProps };
    if (v->getAHBProps(v->device, ahb, &props) != VK_SUCCESS) { AHardwareBuffer_release(ahb); return 0; }

    if (ensure_format_objects(v, fmtProps.externalFormat, &fmtProps) != 0) { AHardwareBuffer_release(ahb); return 0; }

    /* Query the VkImage behind the Unity RenderTexture (observe-only — nothing
     * is recorded into Unity's command buffer). Going through AccessTexture is
     * still required: the Mali driver crashes when Unity wraps a plugin-owned
     * VkImage via CreateExternalTexture, so C# owns the RT and we resolve into
     * it. The render pass takes the attachment from UNDEFINED and returns it
     * SHADER_READ_ONLY, so no layout coordination with Unity is needed. */
    uint64_t unityImageU64 = 0; int unityFormat = 0, unityW = 0, unityH = 0;
    if (!basis_gfx_vk_access_texture(unityTex,
                                     &unityImageU64, &unityFormat, &unityW, &unityH)) {
        AHardwareBuffer_release(ahb);
        return 0;
    }
    VkImage unityImage = (VkImage)(uintptr_t)unityImageU64;

    /* One target extent for both the framebuffer and the render area, or the
     * render pass area can disagree with the framebuffer it renders into. Prefer
     * the RenderTexture's actual extent (AccessTexture), then the registered size,
     * then the source AHB. */
    int targetW = unityW > 0 ? unityW : (regW > 0 ? regW : w);
    int targetH = unityH > 0 ? unityH : (regH > 0 ? regH : h);
    if (ensure_unity_fbo(v, unityImage, (VkFormat)unityFormat, targetW, targetH) != 0) {
        AHardwareBuffer_release(ahb);
        return 0;
    }

    if (ensure_cmd_objects(v) != 0) { AHardwareBuffer_release(ahb); return 0; }

    /* reclaim slots whose submission has completed, then take a free one */
    for (int i = 0; i < BASIS_VK_RING; ++i)
        if (v->ring[i].inUse && vkGetFenceStatus(v->device, v->ring[i].fence) == VK_SUCCESS)
            destroy_slot(v, &v->ring[i]);
    int slot = -1;
    for (int i = 0; i < BASIS_VK_RING; ++i) if (!v->ring[i].inUse) { slot = i; break; }
    if (slot < 0) { AHardwareBuffer_release(ahb); return 0; } /* all in flight: drop a frame */

    basis_vk_slot* s = &v->ring[slot];
    if (import_into_slot(v, s, ahb, w, h, &props, fmtProps.externalFormat) != 0) {
        destroy_slot(v, s);
        AHardwareBuffer_release(ahb);
        return 0;
    }
    AHardwareBuffer_release(ahb); /* import_into_slot took its own ref */

    VkCommandBuffer cmd = s->cmd;
    VkCommandBufferBeginInfo bi = { VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    if (vkBeginCommandBuffer(cmd, &bi) != VK_SUCCESS) { destroy_slot(v, s); return 0; }

    /* transition the imported source UNDEFINED -> SHADER_READ_ONLY for sampling */
    VkImageMemoryBarrier toRead = { VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
    toRead.srcAccessMask = 0;
    toRead.dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
    toRead.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    toRead.newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    toRead.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    toRead.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    toRead.image = s->image;
    toRead.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    toRead.subresourceRange.levelCount = 1;
    toRead.subresourceRange.layerCount = 1;
    vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                         0, 0, NULL, 0, NULL, 1, &toRead);

    int rw = targetW, rh = targetH;
    VkRenderPassBeginInfo rp = { VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO };
    rp.renderPass = v->renderPass; rp.framebuffer = v->fbo;
    rp.renderArea.extent.width = (uint32_t)rw; rp.renderArea.extent.height = (uint32_t)rh;
    vkCmdBeginRenderPass(cmd, &rp, VK_SUBPASS_CONTENTS_INLINE);

    /* Negative-height viewport flips the resolve vertically so the Unity RT comes
     * out right-way-up (no UV flip on the consumer material). Core in Vulkan 1.1,
     * the Quest baseline; harmless to winding since the pipeline culls nothing. */
    VkViewport vpr = { 0.0f, (float)rh, (float)rw, -(float)rh, 0.0f, 1.0f };
    VkRect2D sc = { {0,0}, { (uint32_t)rw, (uint32_t)rh } };
    vkCmdSetViewport(cmd, 0, 1, &vpr);
    vkCmdSetScissor(cmd, 0, 1, &sc);
    vkCmdBindPipeline(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, v->pipeline);
    vkCmdBindDescriptorSets(cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, v->pipeLayout, 0, 1, &s->set, 0, NULL);
    vkCmdPushConstants(cmd, v->pipeLayout, VK_SHADER_STAGE_VERTEX_BIT, 0, sizeof(uv), uv);
    vkCmdDraw(cmd, 3, 1, 0, 0);
    vkCmdEndRenderPass(cmd);
    if (vkEndCommandBuffer(cmd) != VK_SUCCESS) { destroy_slot(v, s); return 0; }

    /* Submit on the graphics queue. Safe: the update event is configured with
     * kUnityVulkanGraphicsQueueAccess_Allow, so Unity keeps its own queue users
     * off the queue while this callback runs. */
    vkResetFences(v->device, 1, &s->fence);
    VkSubmitInfo si = { VK_STRUCTURE_TYPE_SUBMIT_INFO };
    si.commandBufferCount = 1;
    si.pCommandBuffers = &cmd;
    if (vkQueueSubmit(v->queue, 1, &si, s->fence) != VK_SUCCESS) { destroy_slot(v, s); return 0; }

    s->inUse = 1;
    v->frameCounter++;
    return 1;
}

uint64_t basis_vk_get_image(basis_vk_present* v, int* w, int* h) {
    if (!v) { if (w) *w = 0; if (h) *h = 0; return 0; }
    if (w) *w = v->unityW;
    if (h) *h = v->unityH;
    /* On the AccessTexture path Unity owns the destination; C# already has the
     * handle (it gave it to us). Return the same handle for diagnostics only. */
    return (uint64_t)(uintptr_t)v->unityNativeTex;
}

uint64_t basis_vk_frame_counter(basis_vk_present* v) { return v ? v->frameCounter : 0; }

void basis_vk_release(basis_vk_present* v) {
    if (!v || !v->device) return;
    /* destroy_format_objects waits the in-flight fences before touching slots */
    destroy_format_objects(v);   /* also destroys ring slots */
    destroy_unity_fbo(v);
    destroy_cmd_objects(v);
    v->unityNativeTex = NULL;
    pthread_mutex_lock(&v->lock);
    if (v->pending) { AHardwareBuffer_release(v->pending); v->pending = NULL; }
    pthread_mutex_unlock(&v->lock);
}

void basis_vk_destroy(basis_vk_present* v) {
    if (!v) return;
    basis_vk_release(v);
    pthread_mutex_destroy(&v->lock);
    free(v);
}
