// SPDX-License-Identifier: MIT
#ifndef VK_NO_PROTOTYPES
#define VK_NO_PROTOTYPES
#endif
#include "pass.h"

#include <vulkan/vulkan.h>

#include <atomic>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <exception>
#include <mutex>
#include <thread>
#include <vector>

#if defined(__clang__)
#pragma clang diagnostic ignored "-Wc23-extensions"
#endif

namespace kscreen {
namespace {

// Committed, built by shaders/build.sh: building Kiln needs no shader compiler.
alignas(4) const unsigned char kVertex[] = {
#embed "shaders/build/screen_vs.spv"
};
alignas(4) const unsigned char kFragment[] = {
#embed "shaders/build/screen_fs.spv"
};

constexpr uint64_t kFrameWaitNs = 2'000'000;       // the most a present ever waits for an earlier use of its image
constexpr uint64_t kGoneWaitNs = 1'000'000'000;
constexpr uint32_t kMaxSkipsInARow = 600;          // that many frames with the GPU not done: it is not coming back
constexpr uint32_t kMaxImages = 16;
constexpr uint32_t kMaxWaits = 16;

#define KSCREEN_DEVICE_FNS(X)                                                                                                       \
    X(vkGetSwapchainImagesKHR) X(vkCreateImageView) X(vkDestroyImageView) X(vkCreateImage) X(vkDestroyImage)                        \
    X(vkGetImageMemoryRequirements) X(vkAllocateMemory) X(vkFreeMemory) X(vkBindImageMemory) X(vkCreateBuffer) X(vkDestroyBuffer)   \
    X(vkGetBufferMemoryRequirements) X(vkBindBufferMemory) X(vkMapMemory) X(vkCreateRenderPass) X(vkDestroyRenderPass)              \
    X(vkCreateFramebuffer) X(vkDestroyFramebuffer) X(vkCreateCommandPool) X(vkDestroyCommandPool) X(vkAllocateCommandBuffers)       \
    X(vkFreeCommandBuffers) X(vkBeginCommandBuffer) X(vkEndCommandBuffer) X(vkResetCommandBuffer) X(vkCmdBeginRenderPass)           \
    X(vkCmdEndRenderPass) X(vkCmdBindPipeline) X(vkCmdBindDescriptorSets) X(vkCmdSetViewport) X(vkCmdSetScissor) X(vkCmdDraw)       \
    X(vkCmdPipelineBarrier) X(vkCmdCopyImage) X(vkQueueSubmit) X(vkCreateFence) X(vkDestroyFence) X(vkWaitForFences)                \
    X(vkGetFenceStatus) X(vkResetFences) X(vkCreateSemaphore) X(vkDestroySemaphore) X(vkCreateShaderModule)                         \
    X(vkDestroyShaderModule) X(vkCreateDescriptorSetLayout) X(vkDestroyDescriptorSetLayout) X(vkCreatePipelineLayout)               \
    X(vkDestroyPipelineLayout) X(vkCreateGraphicsPipelines) X(vkDestroyPipeline) X(vkCreateDescriptorPool)                          \
    X(vkDestroyDescriptorPool) X(vkAllocateDescriptorSets) X(vkUpdateDescriptorSets)

struct Fns {
#define KSCREEN_FN(n) PFN_##n n = nullptr;
    KSCREEN_DEVICE_FNS(KSCREEN_FN)
#undef KSCREEN_FN
};

// How the shader reads and writes this swapchain's values (screen.hlsl's `transfer`). -1: Kiln doesn't know what they mean, so it leaves them alone.
int transfer_for(VkFormat f, VkColorSpaceKHR cs) {
    const bool srgb_format = f == VK_FORMAT_R8G8B8A8_SRGB || f == VK_FORMAT_B8G8R8A8_SRGB || f == VK_FORMAT_A8B8G8R8_SRGB_PACK32;
    const bool ten_bit = f == VK_FORMAT_A2B10G10R10_UNORM_PACK32 || f == VK_FORMAT_A2R10G10B10_UNORM_PACK32;
    const bool unorm = ten_bit || f == VK_FORMAT_R8G8B8A8_UNORM || f == VK_FORMAT_B8G8R8A8_UNORM || f == VK_FORMAT_A8B8G8R8_UNORM_PACK32;
    if (cs == VK_COLOR_SPACE_SRGB_NONLINEAR_KHR) return srgb_format ? 0 : unorm ? 1 : -1;
    if (cs == VK_COLOR_SPACE_HDR10_ST2084_EXT) return ten_bit ? 2 : -1;
    if (cs == VK_COLOR_SPACE_EXTENDED_SRGB_LINEAR_EXT) return f == VK_FORMAT_R16G16B16A16_SFLOAT ? 0 : -1;
    return -1;
}

const char* transfer_name(int t) { return t == 0 ? "linear" : t == 1 ? "sRGB" : "PQ"; }

double ms_since(std::chrono::steady_clock::time_point t0) {
    return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
}

}  // namespace

struct Pass::Impl {
    enum State { Idle, Building, Ready };

    LogFn log_fn;
    std::atomic<int> state{Idle};
    std::thread builder;
    std::atomic<bool> is_off{false};
    mutable std::mutex status_mu;
    PassStatus stats;
    bool logged_first = false;
    uint32_t skips_in_a_row = 0;

    // Everything below belongs to whoever holds the state: the builder while Building, otherwise the callers of present and gone, which never
    // overlap (kiln.ui runs both under its lock).
    VkDevice device = VK_NULL_HANDLE;
    Fns fn;
    PFN_vkGetPhysicalDeviceMemoryProperties get_memory = nullptr;
    PFN_vkGetPhysicalDeviceQueueFamilyProperties get_families = nullptr;
    VkPhysicalDevice physical = VK_NULL_HANDLE;
    // the device's
    VkShaderModule vs = VK_NULL_HANDLE, fs = VK_NULL_HANDLE;
    VkDescriptorSetLayout set_layout = VK_NULL_HANDLE;
    VkPipelineLayout layout = VK_NULL_HANDLE;
    VkFormat format = VK_FORMAT_UNDEFINED;
    VkRenderPass render_pass = VK_NULL_HANDLE;
    VkPipeline pipeline = VK_NULL_HANDLE;
    uint32_t family = UINT32_MAX;
    VkCommandPool pool = VK_NULL_HANDLE;
    // the swapchain's
    VkSwapchainKHR swapchain = VK_NULL_HANDLE;
    VkExtent2D extent{};
    int transfer = 0;
    std::vector<VkImage> images;
    std::vector<VkImageView> views;
    std::vector<VkFramebuffer> framebuffers;
    std::vector<VkCommandBuffer> commands;
    std::vector<VkFence> fences;
    std::vector<uint8_t> in_flight;
    std::vector<VkSemaphore> done;
    std::vector<VkDescriptorSet> sets;
    VkDescriptorPool descriptors = VK_NULL_HANDLE;
    VkBuffer uniforms = VK_NULL_HANDLE;
    VkDeviceMemory uniforms_memory = VK_NULL_HANDLE;
    uint8_t* uniforms_map = nullptr;
    VkDeviceSize uniform_stride = 0;
    VkImage copy = VK_NULL_HANDLE;
    VkDeviceMemory copy_memory = VK_NULL_HANDLE;
    VkImageView copy_view = VK_NULL_HANDLE;

    explicit Impl(LogFn l) : log_fn(l) {}

    void logf(int level, const char* fmt, ...) {
        char text[512];
        va_list ap;
        va_start(ap, fmt);
        std::vsnprintf(text, sizeof text, fmt, ap);
        va_end(ap);
        if (log_fn) log_fn(level, text);
    }

    void turn_off(const char* fmt, ...) {
        char text[256];
        va_list ap;
        va_start(ap, fmt);
        std::vsnprintf(text, sizeof text, fmt, ap);
        va_end(ap);
        if (is_off.exchange(true)) return;
        {
            std::lock_guard<std::mutex> lk(status_mu);
            stats.off = true;
            stats.ready = false;
            stats.reason = text;
        }
        logf(KILN_LOG_ERROR, "screen: off, the game carries on: %s", text);
    }

    bool load(const KilnUiPreOverlay& p) {
        device = static_cast<VkDevice>(p.device);
        physical = static_cast<VkPhysicalDevice>(p.physical_device);
        auto gdpa = reinterpret_cast<PFN_vkGetDeviceProcAddr>(p.get_device_proc_addr);
        auto gipa = reinterpret_cast<PFN_vkGetInstanceProcAddr>(p.get_instance_proc_addr);
        auto instance = static_cast<VkInstance>(p.instance);
        if (!gdpa || !gipa) return false;
        bool ok = true;
#define KSCREEN_FN(n)                                                 \
    fn.n = reinterpret_cast<PFN_##n>(gdpa(device, #n));               \
    if (!fn.n) ok = false;
        KSCREEN_DEVICE_FNS(KSCREEN_FN)
#undef KSCREEN_FN
        get_memory = reinterpret_cast<PFN_vkGetPhysicalDeviceMemoryProperties>(gipa(instance, "vkGetPhysicalDeviceMemoryProperties"));
        get_families = reinterpret_cast<PFN_vkGetPhysicalDeviceQueueFamilyProperties>(gipa(instance, "vkGetPhysicalDeviceQueueFamilyProperties"));
        return ok && get_memory && get_families;
    }

    uint32_t memory_type(uint32_t bits, VkMemoryPropertyFlags want) {
        VkPhysicalDeviceMemoryProperties m{};
        get_memory(physical, &m);
        for (uint32_t i = 0; i < m.memoryTypeCount; ++i)
            if ((bits & (1u << i)) && (m.memoryTypes[i].propertyFlags & want) == want) return i;
        return UINT32_MAX;
    }

    // ---- building, on the builder thread ----------------------------------------------------------------------------------------

    bool build_device_objects(const KilnUiPreOverlay& p, const VkFormat new_format) {
        VkResult r;
        if (!vs) {
            VkShaderModuleCreateInfo ci{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO};
            ci.codeSize = sizeof kVertex;
            ci.pCode = reinterpret_cast<const uint32_t*>(kVertex);
            if ((r = fn.vkCreateShaderModule(device, &ci, nullptr, &vs)) != VK_SUCCESS) return no("a shader module", r);
            ci.codeSize = sizeof kFragment;
            ci.pCode = reinterpret_cast<const uint32_t*>(kFragment);
            if ((r = fn.vkCreateShaderModule(device, &ci, nullptr, &fs)) != VK_SUCCESS) return no("a shader module", r);
            VkDescriptorSetLayoutBinding b[2]{};
            b[0].binding = 0;
            b[0].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
            b[0].descriptorCount = 1;
            b[0].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
            b[1].binding = 1;
            b[1].descriptorType = VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE;
            b[1].descriptorCount = 1;
            b[1].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
            VkDescriptorSetLayoutCreateInfo di{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO};
            di.bindingCount = 2;
            di.pBindings = b;
            if ((r = fn.vkCreateDescriptorSetLayout(device, &di, nullptr, &set_layout)) != VK_SUCCESS) return no("a descriptor layout", r);
            VkPipelineLayoutCreateInfo li{VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO};
            li.setLayoutCount = 1;
            li.pSetLayouts = &set_layout;
            if ((r = fn.vkCreatePipelineLayout(device, &li, nullptr, &layout)) != VK_SUCCESS) return no("a pipeline layout", r);
        }
        if (pool && family != p.queue_family) {
            fn.vkDestroyCommandPool(device, pool, nullptr);
            pool = VK_NULL_HANDLE;
        }
        if (!pool) {
            uint32_t n = 0;
            get_families(physical, &n, nullptr);
            std::vector<VkQueueFamilyProperties> props(n);
            get_families(physical, &n, props.data());
            if (p.queue_family >= n || !(props[p.queue_family].queueFlags & VK_QUEUE_GRAPHICS_BIT)) {
                turn_off("the game presents on queue family %u, which cannot draw", p.queue_family);
                return false;
            }
            VkCommandPoolCreateInfo ci{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
            ci.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
            ci.queueFamilyIndex = p.queue_family;
            if ((r = fn.vkCreateCommandPool(device, &ci, nullptr, &pool)) != VK_SUCCESS) return no("a command pool", r);
            family = p.queue_family;
        }
        if (pipeline && format == new_format) return true;
        if (pipeline) fn.vkDestroyPipeline(device, pipeline, nullptr);
        if (render_pass) fn.vkDestroyRenderPass(device, render_pass, nullptr);
        pipeline = VK_NULL_HANDLE;
        render_pass = VK_NULL_HANDLE;
        format = new_format;

        const auto t0 = std::chrono::steady_clock::now();
        VkAttachmentDescription a{};
        a.format = format;
        a.samples = VK_SAMPLE_COUNT_1_BIT;
        a.loadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;  // the triangle covers every pixel
        a.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
        a.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
        a.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
        a.initialLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        a.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
        VkAttachmentReference ref{0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL};
        VkSubpassDescription sub{};
        sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
        sub.colorAttachmentCount = 1;
        sub.pColorAttachments = &ref;
        VkSubpassDependency deps[2]{};
        deps[0].srcSubpass = VK_SUBPASS_EXTERNAL;
        deps[0].dstSubpass = 0;
        deps[0].srcStageMask = VK_PIPELINE_STAGE_TRANSFER_BIT;
        deps[0].dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
        deps[0].dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
        deps[1].srcSubpass = 0;
        deps[1].dstSubpass = VK_SUBPASS_EXTERNAL;
        deps[1].srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
        deps[1].dstStageMask = VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT;
        deps[1].srcAccessMask = VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
        VkRenderPassCreateInfo rp{VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO};
        rp.attachmentCount = 1;
        rp.pAttachments = &a;
        rp.subpassCount = 1;
        rp.pSubpasses = &sub;
        rp.dependencyCount = 2;
        rp.pDependencies = deps;
        if ((r = fn.vkCreateRenderPass(device, &rp, nullptr, &render_pass)) != VK_SUCCESS) return no("a render pass", r);

        VkPipelineShaderStageCreateInfo stages[2]{};
        stages[0].sType = stages[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
        stages[0].module = vs;
        stages[0].pName = "vs_main";
        stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
        stages[1].module = fs;
        stages[1].pName = "ps_main";
        VkPipelineVertexInputStateCreateInfo vertex{VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO};
        VkPipelineInputAssemblyStateCreateInfo assembly{VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO};
        assembly.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
        VkPipelineViewportStateCreateInfo viewport{VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO};
        viewport.viewportCount = 1;
        viewport.scissorCount = 1;
        VkPipelineRasterizationStateCreateInfo raster{VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO};
        raster.polygonMode = VK_POLYGON_MODE_FILL;
        raster.cullMode = VK_CULL_MODE_NONE;
        raster.lineWidth = 1.0f;
        VkPipelineMultisampleStateCreateInfo multisample{VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO};
        multisample.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
        VkPipelineColorBlendAttachmentState blend{};
        blend.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
        VkPipelineColorBlendStateCreateInfo colour{VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO};
        colour.attachmentCount = 1;
        colour.pAttachments = &blend;
        const VkDynamicState dynamics[] = {VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR};
        VkPipelineDynamicStateCreateInfo dynamic{VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO};
        dynamic.dynamicStateCount = 2;
        dynamic.pDynamicStates = dynamics;
        VkGraphicsPipelineCreateInfo gp{VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO};
        gp.stageCount = 2;
        gp.pStages = stages;
        gp.pVertexInputState = &vertex;
        gp.pInputAssemblyState = &assembly;
        gp.pViewportState = &viewport;
        gp.pRasterizationState = &raster;
        gp.pMultisampleState = &multisample;
        gp.pColorBlendState = &colour;
        gp.pDynamicState = &dynamic;
        gp.layout = layout;
        gp.renderPass = render_pass;
        if ((r = fn.vkCreateGraphicsPipelines(device, VK_NULL_HANDLE, 1, &gp, nullptr, &pipeline)) != VK_SUCCESS) {
            pipeline = VK_NULL_HANDLE;
            return no("the pipeline", r);
        }
        const double ms = ms_since(t0);
        {
            std::lock_guard<std::mutex> lk(status_mu);
            stats.pipeline_ms = ms;
        }
        logf(KILN_LOG_INFO, "screen: pipeline for format %d built in %.1f ms", static_cast<int>(format), ms);
        return true;
    }

    bool no(const char* what, VkResult r) {
        turn_off("could not create %s (VkResult %d)", what, static_cast<int>(r));
        return false;
    }

    bool build_swapchain_objects(const KilnUiPreOverlay& p) {
        VkResult r;
        const uint32_t n = static_cast<uint32_t>(images.size());
        views.assign(n, VK_NULL_HANDLE);
        framebuffers.assign(n, VK_NULL_HANDLE);
        fences.assign(n, VK_NULL_HANDLE);
        in_flight.assign(n, 0);
        done.assign(n, VK_NULL_HANDLE);
        sets.assign(n, VK_NULL_HANDLE);
        commands.assign(n, VK_NULL_HANDLE);

        VkCommandBufferAllocateInfo ai{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
        ai.commandPool = pool;
        ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        ai.commandBufferCount = n;
        if ((r = fn.vkAllocateCommandBuffers(device, &ai, commands.data())) != VK_SUCCESS) {
            commands.clear();
            return no("command buffers", r);
        }

        // the copy of the frame
        VkImageCreateInfo ii{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
        ii.imageType = VK_IMAGE_TYPE_2D;
        ii.format = format;
        ii.extent = {extent.width, extent.height, 1};
        ii.mipLevels = 1;
        ii.arrayLayers = 1;
        ii.samples = VK_SAMPLE_COUNT_1_BIT;
        ii.tiling = VK_IMAGE_TILING_OPTIMAL;
        ii.usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT;
        ii.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        if ((r = fn.vkCreateImage(device, &ii, nullptr, &copy)) != VK_SUCCESS) return no("the frame's copy", r);
        VkMemoryRequirements req{};
        fn.vkGetImageMemoryRequirements(device, copy, &req);
        VkMemoryAllocateInfo mi{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
        mi.allocationSize = req.size;
        mi.memoryTypeIndex = memory_type(req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        if (mi.memoryTypeIndex == UINT32_MAX) mi.memoryTypeIndex = memory_type(req.memoryTypeBits, 0);
        if ((r = fn.vkAllocateMemory(device, &mi, nullptr, &copy_memory)) != VK_SUCCESS) return no("memory for the frame's copy", r);
        if ((r = fn.vkBindImageMemory(device, copy, copy_memory, 0)) != VK_SUCCESS) return no("memory for the frame's copy", r);
        VkImageViewCreateInfo vi{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
        vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
        vi.format = format;
        vi.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
        vi.image = copy;
        if ((r = fn.vkCreateImageView(device, &vi, nullptr, &copy_view)) != VK_SUCCESS) return no("a view of the frame's copy", r);

        // one set of parameters per image, as each image's last frame may still be reading its own
        uniform_stride = (sizeof(GpuFrame) + 255) & ~VkDeviceSize{255};  // 256 is the largest minUniformBufferOffsetAlignment Vulkan allows
        VkBufferCreateInfo bi{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
        bi.size = uniform_stride * n;
        bi.usage = VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT;
        if ((r = fn.vkCreateBuffer(device, &bi, nullptr, &uniforms)) != VK_SUCCESS) return no("the parameters' buffer", r);
        fn.vkGetBufferMemoryRequirements(device, uniforms, &req);
        mi.allocationSize = req.size;
        mi.memoryTypeIndex = memory_type(req.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
        if (mi.memoryTypeIndex == UINT32_MAX) {
            turn_off("no host-visible memory for the parameters");
            return false;
        }
        if ((r = fn.vkAllocateMemory(device, &mi, nullptr, &uniforms_memory)) != VK_SUCCESS) return no("memory for the parameters", r);
        if ((r = fn.vkBindBufferMemory(device, uniforms, uniforms_memory, 0)) != VK_SUCCESS) return no("memory for the parameters", r);
        void* mapped = nullptr;
        if ((r = fn.vkMapMemory(device, uniforms_memory, 0, VK_WHOLE_SIZE, 0, &mapped)) != VK_SUCCESS) return no("a mapping of the parameters", r);
        uniforms_map = static_cast<uint8_t*>(mapped);

        VkDescriptorPoolSize sizes[2] = {{VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, n}, {VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, n}};
        VkDescriptorPoolCreateInfo dp{VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO};
        dp.maxSets = n;
        dp.poolSizeCount = 2;
        dp.pPoolSizes = sizes;
        if ((r = fn.vkCreateDescriptorPool(device, &dp, nullptr, &descriptors)) != VK_SUCCESS) return no("a descriptor pool", r);
        std::vector<VkDescriptorSetLayout> set_layouts(n, set_layout);
        VkDescriptorSetAllocateInfo sa{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO};
        sa.descriptorPool = descriptors;
        sa.descriptorSetCount = n;
        sa.pSetLayouts = set_layouts.data();
        if ((r = fn.vkAllocateDescriptorSets(device, &sa, sets.data())) != VK_SUCCESS) return no("descriptor sets", r);

        for (uint32_t i = 0; i < n; ++i) {
            vi.image = images[i];
            if ((r = fn.vkCreateImageView(device, &vi, nullptr, &views[i])) != VK_SUCCESS) return no("a view of a swapchain image", r);
            VkFramebufferCreateInfo fi{VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO};
            fi.renderPass = render_pass;
            fi.attachmentCount = 1;
            fi.pAttachments = &views[i];
            fi.width = extent.width;
            fi.height = extent.height;
            fi.layers = 1;
            if ((r = fn.vkCreateFramebuffer(device, &fi, nullptr, &framebuffers[i])) != VK_SUCCESS) return no("a framebuffer", r);
            VkFenceCreateInfo fc{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
            fc.flags = VK_FENCE_CREATE_SIGNALED_BIT;
            if ((r = fn.vkCreateFence(device, &fc, nullptr, &fences[i])) != VK_SUCCESS) return no("a fence", r);
            VkSemaphoreCreateInfo sc{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
            if ((r = fn.vkCreateSemaphore(device, &sc, nullptr, &done[i])) != VK_SUCCESS) return no("a semaphore", r);

            VkDescriptorBufferInfo ub{uniforms, uniform_stride * i, sizeof(GpuFrame)};
            VkDescriptorImageInfo im{VK_NULL_HANDLE, copy_view, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL};
            VkWriteDescriptorSet w[2]{};
            w[0].sType = w[1].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
            w[0].dstSet = w[1].dstSet = sets[i];
            w[0].dstBinding = 0;
            w[0].descriptorCount = 1;
            w[0].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
            w[0].pBufferInfo = &ub;
            w[1].dstBinding = 1;
            w[1].descriptorCount = 1;
            w[1].descriptorType = VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE;
            w[1].pImageInfo = &im;
            fn.vkUpdateDescriptorSets(device, 2, w, 0, nullptr);
        }
        return true;
    }

    // ---- tearing down ------------------------------------------------------------------------------------------------------------

    void join_builder() {
        if (builder.joinable()) builder.join();
    }

    void destroy_swapchain_objects() {
        if (!device) return;
        for (auto x : framebuffers)
            if (x) fn.vkDestroyFramebuffer(device, x, nullptr);
        for (auto x : views)
            if (x) fn.vkDestroyImageView(device, x, nullptr);
        for (auto x : fences)
            if (x) fn.vkDestroyFence(device, x, nullptr);
        for (auto x : done)
            if (x) fn.vkDestroySemaphore(device, x, nullptr);
        if (!commands.empty() && pool) fn.vkFreeCommandBuffers(device, pool, static_cast<uint32_t>(commands.size()), commands.data());
        if (descriptors) fn.vkDestroyDescriptorPool(device, descriptors, nullptr);
        if (uniforms) fn.vkDestroyBuffer(device, uniforms, nullptr);
        if (uniforms_memory) fn.vkFreeMemory(device, uniforms_memory, nullptr);
        if (copy_view) fn.vkDestroyImageView(device, copy_view, nullptr);
        if (copy) fn.vkDestroyImage(device, copy, nullptr);
        if (copy_memory) fn.vkFreeMemory(device, copy_memory, nullptr);
        framebuffers.clear();
        views.clear();
        fences.clear();
        in_flight.clear();
        done.clear();
        commands.clear();
        sets.clear();
        images.clear();
        descriptors = VK_NULL_HANDLE;
        uniforms = VK_NULL_HANDLE;
        uniforms_memory = VK_NULL_HANDLE;
        uniforms_map = nullptr;
        copy_view = VK_NULL_HANDLE;
        copy = VK_NULL_HANDLE;
        copy_memory = VK_NULL_HANDLE;
        swapchain = VK_NULL_HANDLE;
        state.store(Idle, std::memory_order_release);
    }

    void destroy_device_objects() {
        if (!device) return;
        if (pipeline) fn.vkDestroyPipeline(device, pipeline, nullptr);
        if (render_pass) fn.vkDestroyRenderPass(device, render_pass, nullptr);
        if (layout) fn.vkDestroyPipelineLayout(device, layout, nullptr);
        if (set_layout) fn.vkDestroyDescriptorSetLayout(device, set_layout, nullptr);
        if (vs) fn.vkDestroyShaderModule(device, vs, nullptr);
        if (fs) fn.vkDestroyShaderModule(device, fs, nullptr);
        if (pool) fn.vkDestroyCommandPool(device, pool, nullptr);
        pipeline = VK_NULL_HANDLE;
        render_pass = VK_NULL_HANDLE;
        layout = VK_NULL_HANDLE;
        set_layout = VK_NULL_HANDLE;
        vs = fs = VK_NULL_HANDLE;
        pool = VK_NULL_HANDLE;
        family = UINT32_MAX;
        format = VK_FORMAT_UNDEFINED;
        device = VK_NULL_HANDLE;
    }

    // False: the GPU is still using them after the wait. Nothing may be destroyed then.
    bool wait_for_flight(uint64_t timeout_ns) {
        for (size_t i = 0; i < fences.size(); ++i) {
            if (!in_flight[i] || !fences[i]) continue;
            const VkResult r = fn.vkWaitForFences(device, 1, &fences[i], VK_TRUE, timeout_ns);
            if (r == VK_TIMEOUT) return false;
            in_flight[i] = 0;  // a lost device counts as done: nothing runs any more
        }
        return true;
    }

    void record(uint32_t i, VkImage image) {
        VkCommandBuffer cb = commands[i];
        fn.vkResetCommandBuffer(cb, 0);
        VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
        begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        fn.vkBeginCommandBuffer(cb, &begin);

        VkImageMemoryBarrier b[2]{};
        b[0].sType = b[1].sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
        b[0].srcQueueFamilyIndex = b[0].dstQueueFamilyIndex = b[1].srcQueueFamilyIndex = b[1].dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        b[0].subresourceRange = b[1].subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
        // What the copy holds doesn't matter, but the last frame may still be reading it.
        b[0].image = copy;
        b[0].oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        b[0].newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        b[0].dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        // The game's semaphores cover what it wrote. This covers a present that gives none.
        b[1].image = image;
        b[1].oldLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
        b[1].newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        b[1].srcAccessMask = VK_ACCESS_MEMORY_WRITE_BIT;
        b[1].dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
        fn.vkCmdPipelineBarrier(cb, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 2, b);

        VkImageCopy region{};
        region.srcSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
        region.dstSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
        region.extent = {extent.width, extent.height, 1};
        fn.vkCmdCopyImage(cb, image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, copy, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);

        b[0].oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        b[0].newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
        b[0].srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        b[0].dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
        fn.vkCmdPipelineBarrier(cb, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, 0, 0, nullptr, 0, nullptr, 1, b);

        VkRenderPassBeginInfo rp{VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO};
        rp.renderPass = render_pass;
        rp.framebuffer = framebuffers[i];
        rp.renderArea.extent = extent;
        fn.vkCmdBeginRenderPass(cb, &rp, VK_SUBPASS_CONTENTS_INLINE);
        fn.vkCmdBindPipeline(cb, VK_PIPELINE_BIND_POINT_GRAPHICS, pipeline);
        fn.vkCmdBindDescriptorSets(cb, VK_PIPELINE_BIND_POINT_GRAPHICS, layout, 0, 1, &sets[i], 0, nullptr);
        VkViewport viewport{0, 0, static_cast<float>(extent.width), static_cast<float>(extent.height), 0, 1};
        VkRect2D scissor{{0, 0}, extent};
        fn.vkCmdSetViewport(cb, 0, 1, &viewport);
        fn.vkCmdSetScissor(cb, 0, 1, &scissor);
        fn.vkCmdDraw(cb, 3, 1, 0, 0);
        fn.vkCmdEndRenderPass(cb);
        fn.vkEndCommandBuffer(cb);
    }
};

Pass::Pass(LogFn log) : d_(std::make_unique<Impl>(log)) {}

Pass::~Pass() {
    d_->join_builder();
}

bool Pass::prepare(const KilnUiPreOverlay& p) {
    Impl& d = *d_;
    if (d.is_off.load(std::memory_order_acquire)) {
        if (d.state.load(std::memory_order_acquire) != Impl::Building) d.join_builder();
        return false;
    }
    int st = d.state.load(std::memory_order_acquire);
    if (st == Impl::Building) return false;
    d.join_builder();
    if (d.is_off.load(std::memory_order_acquire)) return false;

    const auto device = static_cast<VkDevice>(p.device);
    const auto format = static_cast<VkFormat>(p.format);
    if (st == Impl::Ready) {
        if (device == d.device && p.swapchain == static_cast<void*>(d.swapchain) && format == d.format && p.width == d.extent.width &&
            p.height == d.extent.height && p.queue_family == d.family)
            return true;
        // a change nobody told us about: nothing is built for this frame, and nothing waits for the old one
        if (!d.wait_for_flight(0)) return false;
        d.destroy_swapchain_objects();
    }
    const int transfer = transfer_for(format, static_cast<VkColorSpaceKHR>(p.color_space));
    if (transfer < 0) {
        d.turn_off("no reading of a swapchain of format %u, colour space %u", p.format, p.color_space);
        return false;
    }
    constexpr uint32_t kNeeds = VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    if ((p.usage & kNeeds) != kNeeds) {
        d.turn_off("the swapchain can't be read (usage 0x%x)", p.usage);
        return false;
    }
    if (d.device && d.device != device) d.destroy_device_objects();
    if (!d.load(p)) {
        d.turn_off("the Vulkan functions it needs are not there");
        return false;
    }
    // Here and not on the builder's thread: the swapchain may not be used by two threads at once, and the game presents on it between frames.
    d.swapchain = static_cast<VkSwapchainKHR>(p.swapchain);
    d.extent = {p.width, p.height};
    uint32_t count = 0;
    VkResult r = d.fn.vkGetSwapchainImagesKHR(d.device, d.swapchain, &count, nullptr);
    if (r == VK_SUCCESS && (count == 0 || count > kMaxImages)) {
        d.turn_off("the swapchain has %u images", count);
        return false;
    }
    if (r == VK_SUCCESS) {
        d.images.resize(count);
        r = d.fn.vkGetSwapchainImagesKHR(d.device, d.swapchain, &count, d.images.data());
    }
    if (r != VK_SUCCESS) {
        d.turn_off("the swapchain's image list gave VkResult %d", static_cast<int>(r));
        return false;
    }
    d.transfer = transfer;
    {
        std::lock_guard<std::mutex> lk(d.status_mu);
        d.stats.transfer = static_cast<uint32_t>(transfer);
        d.stats.format = p.format;
        d.stats.width = p.width;
        d.stats.height = p.height;
    }
    d.logf(KILN_LOG_INFO, "screen: building the pass for swapchain %ux%u format %u (%s)", p.width, p.height, p.format, transfer_name(transfer));
    // The thread takes what it needs by value: `p` ends with this present.
    KilnUiPreOverlay copy = p;
    copy.wait_semaphores = nullptr;
    copy.wait_count = 0;
    d.state.store(Impl::Building, std::memory_order_release);
    try {
        d.builder = std::thread([&d, copy, format] {
            bool ok = false;
            try {
                ok = d.build_device_objects(copy, format) && d.build_swapchain_objects(copy);
            } catch (const std::exception& e) {
                d.turn_off("building the pass threw: %s", e.what());
            }
            if (ok) {
                std::lock_guard<std::mutex> lk(d.status_mu);
                d.stats.ready = true;
            }
            d.state.store(ok ? Impl::Ready : Impl::Idle, std::memory_order_release);
        });
    } catch (const std::exception& e) {
        d.state.store(Impl::Idle, std::memory_order_release);
        d.turn_off("no thread for building the pass: %s", e.what());
    }
    return false;
}

bool Pass::present(const KilnUiPreOverlay& p, GpuFrame& frame, void** out_semaphore) {
    Impl& d = *d_;
    if (d.state.load(std::memory_order_acquire) != Impl::Ready || d.is_off.load(std::memory_order_acquire)) return false;

    const uint32_t i = p.image_index;
    if (p.wait_count > kMaxWaits) {
        d.turn_off("the game's present waits on %u semaphores", p.wait_count);
        return false;
    }
    if (i >= d.images.size() || static_cast<void*>(d.images[i]) != p.image) {
        d.turn_off("present of an image the swapchain did not list");
        return false;
    }
    // The present never waits long for this image's last frame. Skipped, it shows the game's own picture once.
    if (d.in_flight[i]) {
        const VkResult w = d.fn.vkWaitForFences(d.device, 1, &d.fences[i], VK_TRUE, kFrameWaitNs);
        if (w == VK_TIMEOUT) {
            {
                std::lock_guard<std::mutex> lk(d.status_mu);
                ++d.stats.skipped;
            }
            if (++d.skips_in_a_row >= kMaxSkipsInARow) d.turn_off("the GPU has not finished a frame for %u frames", kMaxSkipsInARow);
            return false;
        }
        if (w != VK_SUCCESS) {
            d.turn_off("waiting for the last frame gave VkResult %d", static_cast<int>(w));
            return false;
        }
        d.in_flight[i] = 0;
    }
    d.skips_in_a_row = 0;
    VkResult r = d.fn.vkResetFences(d.device, 1, &d.fences[i]);
    if (r != VK_SUCCESS) {
        d.turn_off("resetting a fence gave VkResult %d", static_cast<int>(r));
        return false;
    }
    frame.resolution[0] = static_cast<float>(p.width);
    frame.resolution[1] = static_cast<float>(p.height);
    frame.transfer = static_cast<uint32_t>(d.transfer);
    std::memcpy(d.uniforms_map + d.uniform_stride * i, &frame, sizeof frame);
    d.record(i, d.images[i]);

    VkPipelineStageFlags stages[kMaxWaits];
    for (uint32_t k = 0; k < p.wait_count; ++k) stages[k] = VK_PIPELINE_STAGE_ALL_COMMANDS_BIT;
    VkSubmitInfo si{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    si.waitSemaphoreCount = p.wait_count;
    si.pWaitSemaphores = reinterpret_cast<const VkSemaphore*>(p.wait_semaphores);
    si.pWaitDstStageMask = stages;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &d.commands[i];
    si.signalSemaphoreCount = 1;
    si.pSignalSemaphores = &d.done[i];
    // The last thing that can go wrong, on purpose: until it returns the game's semaphores are the game's.
    r = d.fn.vkQueueSubmit(static_cast<VkQueue>(p.queue), 1, &si, d.fences[i]);
    if (r != VK_SUCCESS) {
        d.turn_off("submitting the pass gave VkResult %d", static_cast<int>(r));
        return false;
    }
    d.in_flight[i] = 1;
    *out_semaphore = static_cast<void*>(d.done[i]);
    {
        std::lock_guard<std::mutex> lk(d.status_mu);
        ++d.stats.frames;
    }
    if (!d.logged_first) {
        d.logged_first = true;
        d.logf(KILN_LOG_INFO, "screen: first frame drawn over the game's picture (%ux%u, format %u)", p.width, p.height, p.format);
    }
    return true;
}

void Pass::gone(void* device, void* swapchain) {
    Impl& d = *d_;
    d.join_builder();  // it may be fetching this swapchain's images right now
    if (!d.device || device != static_cast<void*>(d.device)) return;
    if (swapchain && swapchain != static_cast<void*>(d.swapchain)) return;
    if (!d.wait_for_flight(kGoneWaitNs)) {
        d.logf(KILN_LOG_WARN, "screen: the GPU is still using the pass after a second: its objects are left alone");
        return;
    }
    const bool had = d.swapchain != VK_NULL_HANDLE;
    d.destroy_swapchain_objects();
    {
        std::lock_guard<std::mutex> lk(d.status_mu);
        d.stats.ready = false;
    }
    if (!swapchain) {
        d.destroy_device_objects();
        d.logf(KILN_LOG_INFO, "screen: the game's device went away");
    } else if (had) {
        d.logf(KILN_LOG_INFO, "screen: the swapchain changed: the pass stands down, and builds again for the new one when something plays");
    }
}

bool Pass::release_when_idle() {
    Impl& d = *d_;
    if (d.state.load(std::memory_order_acquire) == Impl::Building) return false;
    d.join_builder();
    if (!d.device || d.images.empty()) return true;
    for (size_t i = 0; i < d.fences.size(); ++i) {
        if (!d.in_flight[i]) continue;
        const VkResult r = d.fn.vkGetFenceStatus(d.device, d.fences[i]);
        if (r == VK_NOT_READY) return false;
        d.in_flight[i] = 0;
    }
    d.destroy_swapchain_objects();
    {
        std::lock_guard<std::mutex> lk(d.status_mu);
        d.stats.ready = false;
    }
    return true;
}

bool Pass::off() const { return d_->is_off.load(std::memory_order_acquire); }

PassStatus Pass::status() const {
    std::lock_guard<std::mutex> lk(d_->status_mu);
    return d_->stats;
}

}  // namespace kscreen
