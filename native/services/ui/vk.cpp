// SPDX-License-Identifier: MIT
// kiln.ui: the game's Vulkan presents (docs/re/client-render.md).
//
// Interception
//  - Wine/Proton: vulkan-1.dll's exports forward to winevulkan.dll, and winevulkan's vkGetDeviceProcAddr returns those
//    same exported functions, so inline hooks on them (resolved through vulkan-1 with GetProcAddress) see every call
//    the game makes, however it got its pointers.
//  - Windows: the loader's exports are trampolines the game does not use for device calls; Kiln hooks
//    vkGetInstanceProcAddr / vkGetDeviceProcAddr and hands out its own functions for the few it needs.
// Drawing (only while something is to be drawn)
//  - per swapchain of the game window: image views, a render pass that LOADs the game's picture and keeps it in
//    PRESENT_SRC, framebuffers, one command buffer + fence + semaphore per image;
//  - in vkQueuePresentKHR: build the ImGui frame, record it into the presented image, submit on the present queue
//    waiting on the game's semaphores, present waiting on Kiln's semaphore instead.
#ifndef VK_NO_PROTOTYPES
#define VK_NO_PROTOTYPES
#endif
#define VK_USE_PLATFORM_WIN32_KHR
#include <vulkan/vulkan.h>

#include <algorithm>
#include <chrono>
#include <cstring>
#include <map>
#include <mutex>
#include <vector>

#include "backends/imgui_impl_vulkan.h"
#include "imgui.h"
#include "safetyhook.hpp"
#include "ui.h"

namespace kui {
namespace {

bool g_wine = false;
std::atomic<bool> g_installed{false};

struct Real {
    PFN_vkGetInstanceProcAddr gipa = nullptr;
    PFN_vkGetDeviceProcAddr gdpa = nullptr;
    PFN_vkCreateInstance create_instance = nullptr;
    PFN_vkDestroyInstance destroy_instance = nullptr;
    PFN_vkCreateDevice create_device = nullptr;
    PFN_vkDestroyDevice destroy_device = nullptr;
    PFN_vkGetDeviceQueue get_queue = nullptr;
    PFN_vkGetDeviceQueue2 get_queue2 = nullptr;
    PFN_vkCreateSwapchainKHR create_swapchain = nullptr;
    PFN_vkDestroySwapchainKHR destroy_swapchain = nullptr;
    PFN_vkQueuePresentKHR present = nullptr;
    PFN_vkCreateWin32SurfaceKHR create_surface = nullptr;
} R;

SafetyHookInline h_destroy_instance;
SafetyHookInline h_gipa, h_gdpa, h_create_instance, h_create_device, h_destroy_device, h_get_queue, h_get_queue2,
    h_create_swapchain, h_destroy_swapchain, h_present, h_create_surface;

// Device-level functions Kiln calls itself (from the game device's vkGetDeviceProcAddr).
struct DeviceFns {
#define KUI_FN(n) PFN_##n n = nullptr;
    KUI_FN(vkGetSwapchainImagesKHR) KUI_FN(vkCreateImageView) KUI_FN(vkDestroyImageView) KUI_FN(vkCreateRenderPass)
    KUI_FN(vkDestroyRenderPass) KUI_FN(vkCreateFramebuffer) KUI_FN(vkDestroyFramebuffer) KUI_FN(vkCreateCommandPool)
    KUI_FN(vkDestroyCommandPool) KUI_FN(vkAllocateCommandBuffers) KUI_FN(vkFreeCommandBuffers)
    KUI_FN(vkBeginCommandBuffer) KUI_FN(vkEndCommandBuffer) KUI_FN(vkResetCommandBuffer) KUI_FN(vkCmdBeginRenderPass)
    KUI_FN(vkCmdEndRenderPass) KUI_FN(vkQueueSubmit) KUI_FN(vkCreateFence) KUI_FN(vkDestroyFence)
    KUI_FN(vkWaitForFences) KUI_FN(vkResetFences) KUI_FN(vkCreateSemaphore) KUI_FN(vkDestroySemaphore)
    KUI_FN(vkDeviceWaitIdle)
#undef KUI_FN
};

struct Device {
    VkInstance instance = VK_NULL_HANDLE;
    VkPhysicalDevice physical = VK_NULL_HANDLE;
    VkDevice device = VK_NULL_HANDLE;
    PFN_vkGetDeviceProcAddr gdpa = nullptr;  // real
    DeviceFns fn;
    std::map<VkQueue, uint32_t> queue_family;
    uint32_t graphics_family = 0;
    VkCommandPool pool = VK_NULL_HANDLE;
    uint32_t pool_family = UINT32_MAX;
    bool imgui_vk = false;      // ImGui_ImplVulkan_Init done for this device
    VkRenderPass imgui_pass = VK_NULL_HANDLE;  // render pass the ImGui pipeline was made for
    VkFormat imgui_format = VK_FORMAT_UNDEFINED;
};

struct Swap {
    VkDevice device = VK_NULL_HANDLE;
    VkSwapchainKHR swapchain = VK_NULL_HANDLE;
    HWND hwnd = nullptr;
    VkFormat format = VK_FORMAT_UNDEFINED;
    VkColorSpaceKHR color_space = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    VkExtent2D extent{};
    uint32_t min_images = 2;
    bool drawable = false;  // created with COLOR_ATTACHMENT usage
    bool ready = false;     // resources below exist
    VkRenderPass pass = VK_NULL_HANDLE;
    std::vector<VkImage> images;
    std::vector<VkImageView> views;
    std::vector<VkFramebuffer> framebuffers;
    std::vector<VkCommandBuffer> commands;
    std::vector<VkFence> fences;
    std::vector<VkSemaphore> done;
};

std::mutex g_mu;  // device/swapchain maps (creation threads vs. the render thread)
VkInstance g_instance = VK_NULL_HANDLE;
uint32_t g_instance_api = VK_API_VERSION_1_0;
std::map<VkDevice, Device*> g_devices;  // every live device the game created
Device* g_dev = nullptr;  // the device ImGui's backend is set up for (the drawable swapchain's)
std::map<VkSwapchainKHR, Swap> g_swaps;
std::map<VkSurfaceKHR, HWND> g_surfaces;
HMODULE g_vulkan = nullptr;

std::atomic<uint64_t> g_presents{0}, g_frames{0}, g_swapchain_count{0}, g_last_draw_ms{0};
std::atomic<bool> g_has_device{false};
std::atomic<uint32_t> g_swap_count{0};  // live swapchains (status only)
std::atomic<uint32_t> g_width{0}, g_height{0}, g_format{0}, g_colorspace{0};
std::atomic<float> g_dpi{1.0f};
std::atomic<double> g_frame_ms{0};
bool g_failed = false;  // overlay disabled after a Vulkan error (presents keep passing through)
bool g_logged_draw = false;

template <typename T>
T dfn(Device& d, const char* name) {
    return reinterpret_cast<T>(d.gdpa ? d.gdpa(d.device, name) : nullptr);
}

void load_device_fns(Device& d) {
#define KUI_FN(n) d.fn.n = dfn<PFN_##n>(d, #n);
    KUI_FN(vkGetSwapchainImagesKHR) KUI_FN(vkCreateImageView) KUI_FN(vkDestroyImageView) KUI_FN(vkCreateRenderPass)
    KUI_FN(vkDestroyRenderPass) KUI_FN(vkCreateFramebuffer) KUI_FN(vkDestroyFramebuffer) KUI_FN(vkCreateCommandPool)
    KUI_FN(vkDestroyCommandPool) KUI_FN(vkAllocateCommandBuffers) KUI_FN(vkFreeCommandBuffers)
    KUI_FN(vkBeginCommandBuffer) KUI_FN(vkEndCommandBuffer) KUI_FN(vkResetCommandBuffer) KUI_FN(vkCmdBeginRenderPass)
    KUI_FN(vkCmdEndRenderPass) KUI_FN(vkQueueSubmit) KUI_FN(vkCreateFence) KUI_FN(vkDestroyFence)
    KUI_FN(vkWaitForFences) KUI_FN(vkResetFences) KUI_FN(vkCreateSemaphore) KUI_FN(vkDestroySemaphore)
    KUI_FN(vkDeviceWaitIdle)
#undef KUI_FN
}

bool device_fns_ok(const Device& d) {
    const void* const* p = reinterpret_cast<const void* const*>(&d.fn);
    for (size_t i = 0; i < sizeof(DeviceFns) / sizeof(void*); ++i)
        if (!p[i]) return false;
    return true;
}

// ImGui's function loader: instance functions through the real vkGetInstanceProcAddr, the rest through the device,
// last the module's exports (on Wine every function is exported by winevulkan).
PFN_vkVoidFunction imgui_loader(const char* name, void*) {
    PFN_vkVoidFunction f = nullptr;
    if (g_dev && g_dev->gdpa) f = g_dev->gdpa(g_dev->device, name);
    if (!f && g_vulkan) f = reinterpret_cast<PFN_vkVoidFunction>(GetProcAddress(g_vulkan, name));
    if (!f && R.gipa && g_dev && g_dev->instance) f = R.gipa(g_dev->instance, name);
    return f;
}

void destroy_swap_resources(Device& d, Swap& s) {
    if (!s.ready) return;
    if (!s.fences.empty()) d.fn.vkWaitForFences(d.device, static_cast<uint32_t>(s.fences.size()), s.fences.data(), VK_TRUE, 1000000000ull);
    for (auto f : s.framebuffers) d.fn.vkDestroyFramebuffer(d.device, f, nullptr);
    for (auto v : s.views) d.fn.vkDestroyImageView(d.device, v, nullptr);
    for (auto f : s.fences) d.fn.vkDestroyFence(d.device, f, nullptr);
    for (auto x : s.done) d.fn.vkDestroySemaphore(d.device, x, nullptr);
    if (!s.commands.empty() && d.pool) d.fn.vkFreeCommandBuffers(d.device, d.pool, static_cast<uint32_t>(s.commands.size()), s.commands.data());
    // the render pass may be ImGui's pipeline pass: keep it while the ImGui backend uses it
    if (s.pass && s.pass != d.imgui_pass) d.fn.vkDestroyRenderPass(d.device, s.pass, nullptr);
    s.framebuffers.clear();
    s.views.clear();
    s.fences.clear();
    s.done.clear();
    s.commands.clear();
    s.images.clear();
    s.pass = VK_NULL_HANDLE;
    s.ready = false;
}

VkRenderPass make_pass(Device& d, VkFormat format) {
    VkAttachmentDescription a{};
    a.format = format;
    a.samples = VK_SAMPLE_COUNT_1_BIT;
    a.loadOp = VK_ATTACHMENT_LOAD_OP_LOAD;  // keep the game's frame
    a.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    a.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    a.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    a.initialLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    a.finalLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
    VkAttachmentReference ref{0, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL};
    VkSubpassDescription sub{};
    sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    sub.colorAttachmentCount = 1;
    sub.pColorAttachments = &ref;
    VkSubpassDependency dep{};
    dep.srcSubpass = VK_SUBPASS_EXTERNAL;
    dep.dstSubpass = 0;
    dep.srcStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dep.dstStageMask = VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT;
    dep.srcAccessMask = 0;
    dep.dstAccessMask = VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT;
    VkRenderPassCreateInfo ci{VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO};
    ci.attachmentCount = 1;
    ci.pAttachments = &a;
    ci.subpassCount = 1;
    ci.pSubpasses = &sub;
    ci.dependencyCount = 1;
    ci.pDependencies = &dep;
    VkRenderPass p = VK_NULL_HANDLE;
    return d.fn.vkCreateRenderPass(d.device, &ci, nullptr, &p) == VK_SUCCESS ? p : VK_NULL_HANDLE;
}

bool ensure_pool(Device& d, uint32_t family) {
    if (d.pool && d.pool_family == family) return true;
    if (d.pool) {
        // a different queue family than before: rebuild everything that uses the pool
        d.fn.vkDeviceWaitIdle(d.device);
        for (auto& [k, s] : g_swaps)
            if (s.device == d.device) destroy_swap_resources(d, s);
        d.fn.vkDestroyCommandPool(d.device, d.pool, nullptr);
        d.pool = VK_NULL_HANDLE;
    }
    VkCommandPoolCreateInfo ci{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
    ci.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    ci.queueFamilyIndex = family;
    if (d.fn.vkCreateCommandPool(d.device, &ci, nullptr, &d.pool) != VK_SUCCESS) return false;
    d.pool_family = family;
    return true;
}

bool ensure_swap_resources(Device& d, Swap& s) {
    if (s.ready) return true;
    uint32_t n = 0;
    if (d.fn.vkGetSwapchainImagesKHR(d.device, s.swapchain, &n, nullptr) != VK_SUCCESS || n == 0 || n > 16) return false;
    s.images.resize(n);
    if (d.fn.vkGetSwapchainImagesKHR(d.device, s.swapchain, &n, s.images.data()) != VK_SUCCESS) return false;
    s.pass = (d.imgui_pass && d.imgui_format == s.format) ? d.imgui_pass : make_pass(d, s.format);
    if (!s.pass) return false;
    s.views.assign(n, VK_NULL_HANDLE);
    s.framebuffers.assign(n, VK_NULL_HANDLE);
    s.fences.assign(n, VK_NULL_HANDLE);
    s.done.assign(n, VK_NULL_HANDLE);
    s.commands.assign(n, VK_NULL_HANDLE);
    VkCommandBufferAllocateInfo ai{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
    ai.commandPool = d.pool;
    ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    ai.commandBufferCount = n;
    if (d.fn.vkAllocateCommandBuffers(d.device, &ai, s.commands.data()) != VK_SUCCESS) {
        s.commands.clear();
        return false;
    }
    for (uint32_t i = 0; i < n; ++i) {
        VkImageViewCreateInfo vi{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
        vi.image = s.images[i];
        vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
        vi.format = s.format;
        vi.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
        if (d.fn.vkCreateImageView(d.device, &vi, nullptr, &s.views[i]) != VK_SUCCESS) return false;
        VkFramebufferCreateInfo fi{VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO};
        fi.renderPass = s.pass;
        fi.attachmentCount = 1;
        fi.pAttachments = &s.views[i];
        fi.width = s.extent.width;
        fi.height = s.extent.height;
        fi.layers = 1;
        if (d.fn.vkCreateFramebuffer(d.device, &fi, nullptr, &s.framebuffers[i]) != VK_SUCCESS) return false;
        VkFenceCreateInfo fc{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
        fc.flags = VK_FENCE_CREATE_SIGNALED_BIT;
        if (d.fn.vkCreateFence(d.device, &fc, nullptr, &s.fences[i]) != VK_SUCCESS) return false;
        VkSemaphoreCreateInfo sc{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
        if (d.fn.vkCreateSemaphore(d.device, &sc, nullptr, &s.done[i]) != VK_SUCCESS) return false;
    }
    s.ready = true;
    logf(KILN_LOG_INFO, "ui: overlay resources for swapchain %ux%u format %d (%u images)", s.extent.width, s.extent.height,
         static_cast<int>(s.format), n);
    return true;
}

bool ensure_imgui_vulkan(Device& d, Swap& s, VkQueue queue, uint32_t family) {
    if (d.imgui_vk && d.imgui_format == s.format) return true;
    if (d.imgui_vk) {
        // the swapchain format changed (e.g. SDR -> HDR): rebuild ImGui's pipeline for a compatible render pass
        ImGui_ImplVulkan_PipelineInfo pi{};
        pi.RenderPass = s.pass;
        pi.Subpass = 0;
        pi.MSAASamples = VK_SAMPLE_COUNT_1_BIT;
        ImGui_ImplVulkan_CreateMainPipeline(&pi);
        VkRenderPass old = d.imgui_pass;
        d.imgui_pass = s.pass;
        d.imgui_format = s.format;
        bool still_used = false;
        for (auto& [k, o] : g_swaps)
            if (o.pass == old) still_used = true;
        if (old && !still_used) d.fn.vkDestroyRenderPass(d.device, old, nullptr);
        logf(KILN_LOG_INFO, "ui: ImGui pipeline rebuilt for format %d", static_cast<int>(s.format));
        return true;
    }
    static bool loaded = false;
    if (!loaded) {
        loaded = ImGui_ImplVulkan_LoadFunctions(VK_API_VERSION_1_0, &imgui_loader, nullptr);
        if (!loaded) {
            set_error("ImGui could not load Vulkan functions");
            return false;
        }
    }
    ImGui_ImplVulkan_InitInfo ii{};
    ii.ApiVersion = VK_API_VERSION_1_0;
    ii.Instance = d.instance;
    ii.PhysicalDevice = d.physical;
    ii.Device = d.device;
    ii.QueueFamily = family;
    ii.Queue = queue;
    ii.DescriptorPoolSize = 64;
    ii.MinImageCount = std::max<uint32_t>(2, s.min_images);
    ii.ImageCount = std::max<uint32_t>(ii.MinImageCount, static_cast<uint32_t>(s.images.size()));
    ii.PipelineInfoMain.RenderPass = s.pass;
    ii.PipelineInfoMain.Subpass = 0;
    ii.PipelineInfoMain.MSAASamples = VK_SAMPLE_COUNT_1_BIT;
    if (!ImGui_ImplVulkan_Init(&ii)) {
        set_error("ImGui_ImplVulkan_Init failed");
        return false;
    }
    d.imgui_vk = true;
    d.imgui_pass = s.pass;
    d.imgui_format = s.format;
    logf(KILN_LOG_INFO, "ui: ImGui Vulkan backend ready (queue family %u, %zu images)", family, s.images.size());
    return true;
}

// The overlay's work inside one present. False = present as the game asked.
bool draw(VkQueue queue, const VkPresentInfoKHR* info, VkSemaphore* out_wait, uint64_t t0) {
    Swap* s = nullptr;
    uint32_t index = 0;
    for (uint32_t i = 0; i < info->swapchainCount; ++i) {
        auto it = g_swaps.find(info->pSwapchains[i]);
        if (it != g_swaps.end() && it->second.drawable && it->second.hwnd) {
            s = &it->second;
            index = info->pImageIndices[i];
            break;
        }
    }
    if (!s) return false;
    auto dit = g_devices.find(s->device);
    if (dit == g_devices.end() || !device_fns_ok(*dit->second)) return false;
    Device& d = *dit->second;
    if (g_dev && g_dev != &d && g_dev->imgui_vk) {
        // the game draws with another device now: move ImGui's backend over
        g_dev->fn.vkDeviceWaitIdle(g_dev->device);
        ImGui_ImplVulkan_Shutdown();
        g_dev->imgui_vk = false;
    }
    g_dev = &d;
    static bool logged_steps = false;
    const bool steps = !logged_steps;
    logged_steps = true;
    if (steps) logf(KILN_LOG_INFO, "ui: first draw: device %p, swapchain %p image %u", reinterpret_cast<void*>(d.device), reinterpret_cast<void*>(s->swapchain), index);
    uint32_t family = d.graphics_family;
    if (auto q = d.queue_family.find(queue); q != d.queue_family.end()) family = q->second;
    if (steps) logf(KILN_LOG_INFO, "ui: first draw: queue family %u", family);
    if (!ensure_pool(d, family) || !ensure_swap_resources(d, *s) || index >= s->images.size()) {
        set_error("overlay resources could not be created");
        g_failed = true;
        return false;
    }
    const float dpi = g_dpi.load();
    if (steps) logf(KILN_LOG_INFO, "ui: first draw: resources ready, creating ImGui");
    if (!imgui_ready() && !imgui_create(s->hwnd, dpi)) {
        g_failed = true;
        return false;
    }
    if (!ensure_imgui_vulkan(d, *s, queue, family)) {
        g_failed = true;
        return false;
    }
    input_attach(s->hwnd);
    if (steps) logf(KILN_LOG_INFO, "ui: first draw: ImGui backends ready, building the frame");
    // ImGui frame (callbacks run here)
    ImGui_ImplVulkan_NewFrame();
    if (!build_frame(static_cast<float>(s->extent.width), static_cast<float>(s->extent.height), dpi)) return false;
    ImDrawData* dd = ImGui::GetDrawData();
    if (!dd || dd->CmdListsCount == 0) return false;  // nothing visible this frame
    if (steps) logf(KILN_LOG_INFO, "ui: first draw: frame built (%d lists), recording", dd->CmdListsCount);

    VkFence fence = s->fences[index];
    if (d.fn.vkWaitForFences(d.device, 1, &fence, VK_TRUE, 1000000000ull) != VK_SUCCESS) {
        set_error("overlay fence timeout");
        return false;
    }
    d.fn.vkResetFences(d.device, 1, &fence);
    VkCommandBuffer cb = s->commands[index];
    d.fn.vkResetCommandBuffer(cb, 0);
    VkCommandBufferBeginInfo bi{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    d.fn.vkBeginCommandBuffer(cb, &bi);
    VkRenderPassBeginInfo rp{VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO};
    rp.renderPass = s->pass;
    rp.framebuffer = s->framebuffers[index];
    rp.renderArea.extent = s->extent;
    d.fn.vkCmdBeginRenderPass(cb, &rp, VK_SUBPASS_CONTENTS_INLINE);
    ImGui_ImplVulkan_RenderDrawData(dd, cb);
    d.fn.vkCmdEndRenderPass(cb);
    d.fn.vkEndCommandBuffer(cb);

    std::vector<VkPipelineStageFlags> stages(info->waitSemaphoreCount, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT);
    VkSubmitInfo si{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    si.waitSemaphoreCount = info->waitSemaphoreCount;
    si.pWaitSemaphores = info->pWaitSemaphores;
    si.pWaitDstStageMask = stages.empty() ? nullptr : stages.data();
    si.commandBufferCount = 1;
    si.pCommandBuffers = &cb;
    si.signalSemaphoreCount = 1;
    si.pSignalSemaphores = &s->done[index];
    const VkResult r = d.fn.vkQueueSubmit(queue, 1, &si, fence);
    if (r != VK_SUCCESS) {
        set_error("overlay submit failed (%d)", static_cast<int>(r));
        g_failed = true;
        // the fence stays unsignaled; recreate it signaled so a later attempt does not wait forever
        d.fn.vkDestroyFence(d.device, fence, nullptr);
        VkFenceCreateInfo fc{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
        fc.flags = VK_FENCE_CREATE_SIGNALED_BIT;
        d.fn.vkCreateFence(d.device, &fc, nullptr, &s->fences[index]);
        return false;
    }
    *out_wait = s->done[index];
    g_frames.fetch_add(1, std::memory_order_relaxed);
    g_last_draw_ms.store(now_ms(), std::memory_order_relaxed);
    g_frame_ms.store(static_cast<double>(GetTickCount64() - t0));
    if (!g_logged_draw) {
        g_logged_draw = true;
        logf(KILN_LOG_INFO, "ui: first overlay frame drawn (%ux%u)", s->extent.width, s->extent.height);
    }
    return true;
}

// ---------------------------------------------------------------------------------------------------------------
// Detours

VkResult VKAPI_CALL hk_create_instance(const VkInstanceCreateInfo* ci, const VkAllocationCallbacks* a, VkInstance* out) {
    const VkResult r = R.create_instance(ci, a, out);
    if (r == VK_SUCCESS && out) {
        std::lock_guard<std::mutex> lk(g_mu);
        g_instance = *out;
        if (ci && ci->pApplicationInfo && ci->pApplicationInfo->apiVersion) g_instance_api = ci->pApplicationInfo->apiVersion;
        logf(KILN_LOG_INFO, "ui: game Vulkan instance %p (api %u.%u)", reinterpret_cast<void*>(*out),
             VK_API_VERSION_MAJOR(g_instance_api), VK_API_VERSION_MINOR(g_instance_api));
    }
    return r;
}

void VKAPI_CALL hk_destroy_instance(VkInstance inst, const VkAllocationCallbacks* a) {
    {
        std::lock_guard<std::mutex> lk(g_mu);
        if (g_instance == inst) g_instance = VK_NULL_HANDLE;
        for (auto& [k, d] : g_devices)
            if (d->instance == inst) d->instance = VK_NULL_HANDLE;
    }
    R.destroy_instance(inst, a);
}

VkResult VKAPI_CALL hk_create_device(VkPhysicalDevice phys, const VkDeviceCreateInfo* ci, const VkAllocationCallbacks* a,
                                     VkDevice* out) {
    const VkResult r = R.create_device(phys, ci, a, out);
    if (r != VK_SUCCESS || !out) return r;
    std::lock_guard<std::mutex> lk(g_mu);
    auto* d = new Device();
    d->instance = g_instance;
    d->physical = phys;
    d->device = *out;
    d->gdpa = R.gdpa;
    if (ci && ci->queueCreateInfoCount > 0) d->graphics_family = ci->pQueueCreateInfos[0].queueFamilyIndex;
    load_device_fns(*d);
    g_devices[*out] = d;
    g_has_device = true;
    logf(KILN_LOG_INFO, "ui: game Vulkan device %p (%u queue families requested, functions %s; %zu live)", reinterpret_cast<void*>(*out),
         ci ? ci->queueCreateInfoCount : 0, device_fns_ok(*d) ? "ok" : "MISSING", g_devices.size());
    return r;
}

void VKAPI_CALL hk_destroy_device(VkDevice dev, const VkAllocationCallbacks* a) {
    {
        std::lock_guard<std::mutex> lk(g_mu);
        auto dit = g_devices.find(dev);
        if (dit != g_devices.end()) {
            Device& d = *dit->second;
            if (device_fns_ok(d)) {
                d.fn.vkDeviceWaitIdle(dev);
                for (auto it = g_swaps.begin(); it != g_swaps.end();) {
                    if (it->second.device == dev) {
                        destroy_swap_resources(d, it->second);
                        it = g_swaps.erase(it);
                    } else {
                        ++it;
                    }
                }
                if (d.imgui_vk) {
                    ImGui_ImplVulkan_Shutdown();
                    d.imgui_vk = false;
                }
                if (d.imgui_pass) d.fn.vkDestroyRenderPass(dev, d.imgui_pass, nullptr);
                if (d.pool) d.fn.vkDestroyCommandPool(dev, d.pool, nullptr);
            }
            if (g_dev == &d) g_dev = nullptr;
            delete dit->second;
            g_devices.erase(dit);
            g_has_device = !g_devices.empty();
            g_swap_count = static_cast<uint32_t>(g_swaps.size());
            logf(KILN_LOG_INFO, "ui: game Vulkan device %p destroyed (%zu live)", reinterpret_cast<void*>(dev), g_devices.size());
        }
    }
    R.destroy_device(dev, a);
}

void remember_queue(VkDevice dev, uint32_t family, VkQueue q) {
    if (!q) return;
    std::lock_guard<std::mutex> lk(g_mu);
    if (auto it = g_devices.find(dev); it != g_devices.end()) it->second->queue_family[q] = family;
}

void VKAPI_CALL hk_get_queue(VkDevice dev, uint32_t family, uint32_t index, VkQueue* out) {
    R.get_queue(dev, family, index, out);
    if (out) remember_queue(dev, family, *out);
}

void VKAPI_CALL hk_get_queue2(VkDevice dev, const VkDeviceQueueInfo2* info, VkQueue* out) {
    R.get_queue2(dev, info, out);
    if (out && info) remember_queue(dev, info->queueFamilyIndex, *out);
}

VkResult VKAPI_CALL hk_create_surface(VkInstance inst, const VkWin32SurfaceCreateInfoKHR* ci, const VkAllocationCallbacks* a,
                                      VkSurfaceKHR* out) {
    const VkResult r = R.create_surface(inst, ci, a, out);
    if (r == VK_SUCCESS && out && ci) {
        std::lock_guard<std::mutex> lk(g_mu);
        g_surfaces[*out] = ci->hwnd;
        g_hwnd.store(ci->hwnd);
        if (!g_instance) g_instance = inst;
        logf(KILN_LOG_INFO, "ui: game window %p has a Vulkan surface", reinterpret_cast<void*>(ci->hwnd));
    }
    return r;
}

VkResult VKAPI_CALL hk_create_swapchain(VkDevice dev, const VkSwapchainCreateInfoKHR* ci, const VkAllocationCallbacks* a,
                                        VkSwapchainKHR* out) {
    if (!ci || !out) return R.create_swapchain(dev, ci, a, out);
    VkSwapchainCreateInfoKHR c = *ci;
    c.imageUsage |= VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    VkResult r = R.create_swapchain(dev, &c, a, out);
    bool drawable = r == VK_SUCCESS;
    if (r != VK_SUCCESS && c.imageUsage != ci->imageUsage) {
        r = R.create_swapchain(dev, ci, a, out);
        drawable = (ci->imageUsage & VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT) != 0;
    }
    if (r != VK_SUCCESS) return r;
    g_swapchain_count.fetch_add(1);
    std::lock_guard<std::mutex> lk(g_mu);
    if (ci->oldSwapchain) {
        auto it = g_swaps.find(ci->oldSwapchain);
        auto dit = g_devices.find(dev);
        if (it != g_swaps.end() && dit != g_devices.end()) destroy_swap_resources(*dit->second, it->second);
    }
    Swap s;
    s.device = dev;
    s.swapchain = *out;
    s.format = ci->imageFormat;
    s.color_space = ci->imageColorSpace;
    s.extent = ci->imageExtent;
    s.min_images = ci->minImageCount;
    s.drawable = drawable;
    auto sf = g_surfaces.find(ci->surface);
    s.hwnd = sf != g_surfaces.end() ? sf->second : g_hwnd.load();
    g_swaps[*out] = s;
    g_swap_count = static_cast<uint32_t>(g_swaps.size());
    g_width.store(s.extent.width);
    g_height.store(s.extent.height);
    g_format.store(static_cast<uint32_t>(s.format));
    g_colorspace.store(static_cast<uint32_t>(s.color_space));
    if (s.hwnd) {
        const UINT dpi = GetDpiForWindow(s.hwnd);
        float scale = dpi ? dpi / 96.0f : 1.0f;
        // Wine often reports 96 dpi on large screens: scale with the height too
        scale = std::max(scale, static_cast<float>(s.extent.height) / 1440.0f);
        g_dpi.store(std::clamp(scale, 1.0f, 3.0f));
    }
    logf(KILN_LOG_INFO, "ui: swapchain %ux%u format %d colour space %d, %u+ images, window %p%s", s.extent.width,
         s.extent.height, static_cast<int>(s.format), static_cast<int>(s.color_space), s.min_images,
         reinterpret_cast<void*>(s.hwnd), drawable ? "" : " (no colour-attachment usage: overlay off)");
    return r;
}

void VKAPI_CALL hk_destroy_swapchain(VkDevice dev, VkSwapchainKHR sc, const VkAllocationCallbacks* a) {
    {
        std::lock_guard<std::mutex> lk(g_mu);
        auto it = g_swaps.find(sc);
        if (it != g_swaps.end()) {
            if (auto dit = g_devices.find(dev); dit != g_devices.end()) destroy_swap_resources(*dit->second, it->second);
            g_swaps.erase(it);
            g_swap_count = static_cast<uint32_t>(g_swaps.size());
        }
    }
    R.destroy_swapchain(dev, sc, a);
}

// draw() under g_mu and a fault guard: an access violation in the overlay turns the overlay off (logged) instead of
// taking the game down. No C++ object with a destructor lives in this frame.
uint32_t g_fault_code = 0;
void* g_fault_addr = nullptr;
int fault_filter(EXCEPTION_POINTERS* ep) {
    g_fault_code = ep->ExceptionRecord->ExceptionCode;
    g_fault_addr = ep->ExceptionRecord->ExceptionAddress;
    return EXCEPTION_EXECUTE_HANDLER;
}

bool guarded_draw(VkQueue queue, const VkPresentInfoKHR* info, VkSemaphore* wait, uint64_t t0, bool* faulted) {
    bool r = false;
    g_mu.lock();
    __try {
        r = draw(queue, info, wait, t0);
    } __except (fault_filter(GetExceptionInformation())) {
        r = false;
        *faulted = true;
    }
    g_mu.unlock();
    return r;
}

VkResult VKAPI_CALL hk_present(VkQueue queue, const VkPresentInfoKHR* info) {
    g_presents.fetch_add(1, std::memory_order_relaxed);
    // The window must be subclassed even while nothing is drawn (the menu key opens the overlay); checked once a
    // second, which also re-subclasses if the game set its own WndProc again.
    static std::atomic<uint64_t> last_attach{0};
    const uint64_t t = now_ms();
    if (t - last_attach.load(std::memory_order_relaxed) >= 1000) {
        last_attach.store(t, std::memory_order_relaxed);
        if (HWND h = g_hwnd.load()) input_attach(h);
    }
    if (!info || g_failed || !want_draw()) {
        // capture just ended with nothing left to draw: still give the game its cursor clip back (render thread)
        if (imgui_ready()) input_capture_changed();
        return R.present(queue, info);
    }
    const uint64_t t0 = GetTickCount64();
    VkSemaphore wait = VK_NULL_HANDLE;
    bool faulted = false;
    const bool drew = guarded_draw(queue, info, &wait, t0, &faulted);
    if (faulted) {
        g_failed = true;
        HMODULE m = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           static_cast<LPCWSTR>(g_fault_addr), &m);
        wchar_t path[MAX_PATH] = L"?";
        if (m) GetModuleFileNameW(m, path, MAX_PATH);
        set_error("overlay faulted (0x%08x at %p = %ls+0x%llx): overlay off", g_fault_code, g_fault_addr,
                  wcsrchr(path, L'\\') ? wcsrchr(path, L'\\') + 1 : path,
                  m ? static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(g_fault_addr) - reinterpret_cast<uintptr_t>(m)) : 0ull);
    }
    if (!drew) return R.present(queue, info);
    VkPresentInfoKHR p = *info;
    p.waitSemaphoreCount = 1;
    p.pWaitSemaphores = &wait;
    return R.present(queue, &p);
}

// Windows (Khronos loader): hand out Kiln's functions for the names it needs.
PFN_vkVoidFunction swap_in(const char* name, PFN_vkVoidFunction real) {
    if (!real || !name) return real;
#define KUI_SWAP(n, field, hk)                                                  \
    if (std::strcmp(name, n) == 0) {                                            \
        if (!R.field) R.field = reinterpret_cast<decltype(R.field)>(real);      \
        return reinterpret_cast<PFN_vkVoidFunction>(&hk);                        \
    }
    KUI_SWAP("vkCreateDevice", create_device, hk_create_device)
    KUI_SWAP("vkDestroyDevice", destroy_device, hk_destroy_device)
    KUI_SWAP("vkGetDeviceQueue", get_queue, hk_get_queue)
    KUI_SWAP("vkGetDeviceQueue2", get_queue2, hk_get_queue2)
    KUI_SWAP("vkCreateSwapchainKHR", create_swapchain, hk_create_swapchain)
    KUI_SWAP("vkDestroySwapchainKHR", destroy_swapchain, hk_destroy_swapchain)
    KUI_SWAP("vkQueuePresentKHR", present, hk_present)
    KUI_SWAP("vkCreateWin32SurfaceKHR", create_surface, hk_create_surface)
#undef KUI_SWAP
    if (std::strcmp(name, "vkGetDeviceProcAddr") == 0) return real;  // the wrapped one is returned below
    return real;
}

PFN_vkVoidFunction VKAPI_CALL hk_gdpa(VkDevice dev, const char* name) {
    return swap_in(name, h_gdpa.call<PFN_vkVoidFunction>(dev, name));
}

PFN_vkVoidFunction VKAPI_CALL hk_gipa(VkInstance inst, const char* name) {
    PFN_vkVoidFunction real = h_gipa.call<PFN_vkVoidFunction>(inst, name);
    if (name && std::strcmp(name, "vkGetDeviceProcAddr") == 0 && real) return reinterpret_cast<PFN_vkVoidFunction>(&hk_gdpa);
    if (name && std::strcmp(name, "vkCreateInstance") == 0 && real) {
        if (!R.create_instance) R.create_instance = reinterpret_cast<PFN_vkCreateInstance>(real);
        return reinterpret_cast<PFN_vkVoidFunction>(&hk_create_instance);
    }
    return swap_in(name, real);
}

template <typename T>
bool hook(SafetyHookInline& h, void* target, void* detour, T* original, const char* what) {
    if (!target) {
        logf(KILN_LOG_WARN, "ui: %s not found", what);
        return false;
    }
    h = safetyhook::create_inline(target, detour);
    if (!h) {
        logf(KILN_LOG_ERROR, "ui: hooking %s failed", what);
        return false;
    }
    if (original) *original = h.original<T>();
    return true;
}

// The game unloads and reloads vulkan-1.dll during its start (device enumeration, then the real device): winevulkan
// comes back at the same address with fresh code, so the hooks are checked after every LoadLibraryExW and put back
// when the code is pristine. Old hook objects are leaked on purpose: their destructors would write into code that may
// belong to another mapping by now.
std::mutex g_install_mu;
void* g_present_target = nullptr;
std::vector<SafetyHookInline*> g_leaked;
int g_installs = 0;

bool hooks_intact(void* present_now) {
    if (!g_present_target || present_now != g_present_target) return false;
    uint8_t b = 0;
    __try {
        b = *static_cast<const uint8_t*>(present_now);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    return b == 0xE9 || b == 0xFF;  // safetyhook's jmp rel32 / jmp [rip]
}

void retire_hooks() {
    for (SafetyHookInline* h : {&h_destroy_instance, &h_gipa, &h_gdpa, &h_create_instance, &h_create_device, &h_destroy_device, &h_get_queue,
                                &h_get_queue2, &h_create_swapchain, &h_destroy_swapchain, &h_present, &h_create_surface})
        if (*h) g_leaked.push_back(new SafetyHookInline(std::move(*h)));
}

}  // namespace

bool vk_install() {
    std::lock_guard<std::mutex> lk(g_install_mu);
    g_vulkan = GetModuleHandleW(L"vulkan-1.dll");
    if (!g_vulkan) return false;
    void* present_now = reinterpret_cast<void*>(GetProcAddress(g_vulkan, "vkQueuePresentKHR"));
    if (g_installed.load() && hooks_intact(present_now)) return true;
    if (g_installed.load()) {
        logf(KILN_LOG_INFO, "ui: vulkan-1.dll was reloaded (present at %p, hooked %p): hooking again", present_now, g_present_target);
        retire_hooks();
    }
    g_installed = true;
    ++g_installs;
    g_wine = GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "wine_get_version") != nullptr;
    if (g_installs == 1) input_install_iat();
    auto P = [](const char* n) { return reinterpret_cast<void*>(GetProcAddress(g_vulkan, n)); };
    R.gipa = reinterpret_cast<PFN_vkGetInstanceProcAddr>(P("vkGetInstanceProcAddr"));
    R.gdpa = reinterpret_cast<PFN_vkGetDeviceProcAddr>(P("vkGetDeviceProcAddr"));
    bool ok = true;
    if (g_wine) {
        // winevulkan's exports are the functions vkGet*ProcAddr return: hook them directly.
        ok &= hook(h_create_instance, P("vkCreateInstance"), reinterpret_cast<void*>(&hk_create_instance), &R.create_instance, "vkCreateInstance");
        hook(h_destroy_instance, P("vkDestroyInstance"), reinterpret_cast<void*>(&hk_destroy_instance), &R.destroy_instance, "vkDestroyInstance");
        ok &= hook(h_create_device, P("vkCreateDevice"), reinterpret_cast<void*>(&hk_create_device), &R.create_device, "vkCreateDevice");
        ok &= hook(h_destroy_device, P("vkDestroyDevice"), reinterpret_cast<void*>(&hk_destroy_device), &R.destroy_device, "vkDestroyDevice");
        ok &= hook(h_get_queue, P("vkGetDeviceQueue"), reinterpret_cast<void*>(&hk_get_queue), &R.get_queue, "vkGetDeviceQueue");
        hook(h_get_queue2, P("vkGetDeviceQueue2"), reinterpret_cast<void*>(&hk_get_queue2), &R.get_queue2, "vkGetDeviceQueue2");
        ok &= hook(h_create_swapchain, P("vkCreateSwapchainKHR"), reinterpret_cast<void*>(&hk_create_swapchain), &R.create_swapchain, "vkCreateSwapchainKHR");
        ok &= hook(h_destroy_swapchain, P("vkDestroySwapchainKHR"), reinterpret_cast<void*>(&hk_destroy_swapchain), &R.destroy_swapchain, "vkDestroySwapchainKHR");
        ok &= hook(h_present, P("vkQueuePresentKHR"), reinterpret_cast<void*>(&hk_present), &R.present, "vkQueuePresentKHR");
        ok &= hook(h_create_surface, P("vkCreateWin32SurfaceKHR"), reinterpret_cast<void*>(&hk_create_surface), &R.create_surface, "vkCreateWin32SurfaceKHR");
    } else {
        ok &= hook(h_gipa, reinterpret_cast<void*>(R.gipa), reinterpret_cast<void*>(&hk_gipa), static_cast<PFN_vkGetInstanceProcAddr*>(nullptr), "vkGetInstanceProcAddr");
        ok &= hook(h_gdpa, reinterpret_cast<void*>(R.gdpa), reinterpret_cast<void*>(&hk_gdpa), static_cast<PFN_vkGetDeviceProcAddr*>(nullptr), "vkGetDeviceProcAddr");
        // instance-level calls through the loader's exports too
        hook(h_create_instance, P("vkCreateInstance"), reinterpret_cast<void*>(&hk_create_instance), &R.create_instance, "vkCreateInstance");
        hook(h_create_surface, P("vkCreateWin32SurfaceKHR"), reinterpret_cast<void*>(&hk_create_surface), &R.create_surface, "vkCreateWin32SurfaceKHR");
        R.gipa = h_gipa ? h_gipa.original<PFN_vkGetInstanceProcAddr>() : R.gipa;
        R.gdpa = h_gdpa ? h_gdpa.original<PFN_vkGetDeviceProcAddr>() : R.gdpa;
    }
    g_present_target = g_wine ? present_now : reinterpret_cast<void*>(R.gipa);
    if (!g_wine) g_present_target = reinterpret_cast<void*>(GetProcAddress(g_vulkan, "vkGetInstanceProcAddr"));
    logf(ok ? KILN_LOG_INFO : KILN_LOG_WARN, "ui: Vulkan hooks %s (%s path, vulkan-1 at %p, install #%d)", ok ? "installed" : "INCOMPLETE",
         g_wine ? "Wine: winevulkan exports" : "Windows: proc-address wrappers", reinterpret_cast<void*>(g_vulkan), g_installs);
    return ok;
}

void vk_status(KilnUiStatus* out) {
    out->hooked = g_installed.load() ? 1 : 0;
    out->wine = g_wine ? 1 : 0;
    // no lock here: frame callbacks (which may ask for the status) run while draw() holds g_mu
    out->device = g_has_device.load() ? 1 : 0;
    out->swapchain = g_swap_count.load() > 0 ? 1 : 0;
    out->width = g_width.load();
    out->height = g_height.load();
    out->format = g_format.load();
    out->color_space = g_colorspace.load();
    out->dpi_scale = g_dpi.load();
    out->presents = g_presents.load();
    out->frames_drawn = g_frames.load();
    out->swapchains = g_swapchain_count.load();
    out->frame_ms = g_frame_ms.load();
    out->drawing = now_ms() - g_last_draw_ms.load() < 1000 ? 1 : 0;
}

}  // namespace kui
