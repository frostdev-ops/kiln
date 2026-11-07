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
// Before the overlay: subscribers of the pre-overlay callback (kiln_ui.h) chain their own submit in the same way.
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
#include <string>
#include <memory>
#include <utility>

#include "backends/imgui_impl_vulkan.h"
#include "imgui.h"
#include "safetyhook.hpp"
#include "ui.h"
#include "vk_hooks.h"

namespace kui {
namespace {

std::atomic<bool> g_wine{false};
std::atomic<bool> g_installed{false};

struct Real {
    std::atomic<PFN_vkGetInstanceProcAddr> gipa{nullptr};
    std::atomic<PFN_vkGetDeviceProcAddr> gdpa{nullptr};
    std::atomic<PFN_vkCreateInstance> create_instance{nullptr};
    std::atomic<PFN_vkDestroyInstance> destroy_instance{nullptr};
    std::atomic<PFN_vkCreateDevice> create_device{nullptr};
    std::atomic<PFN_vkDestroyDevice> destroy_device{nullptr};
    std::atomic<PFN_vkGetDeviceQueue> get_queue{nullptr};
    std::atomic<PFN_vkGetDeviceQueue2> get_queue2{nullptr};
    std::atomic<PFN_vkCreateSwapchainKHR> create_swapchain{nullptr};
    std::atomic<PFN_vkDestroySwapchainKHR> destroy_swapchain{nullptr};
    std::atomic<PFN_vkQueuePresentKHR> present{nullptr};
    std::atomic<PFN_vkCreateWin32SurfaceKHR> create_surface{nullptr};
} R;

struct HookState {
    // Only the atomics are shared with the loader notification; everything else is under g_install_mu.
    std::atomic<uintptr_t> target{0};
    std::atomic<uintptr_t> owner{0};
    std::atomic<uint64_t> loads{0}, unloads{0};
    uint64_t seen_loads = 0, seen_unloads = 0;
    SafetyHookInline* hook = nullptr;  // published hooks/trampolines are never moved or destroyed
    vk_hooks::Snapshot bytes;
    bool told_skip = false;
};
static_assert(std::atomic<uintptr_t>::is_always_lock_free && std::atomic<uint64_t>::is_always_lock_free);
HookState h_destroy_instance, h_gipa, h_gdpa, h_create_instance, h_create_device, h_destroy_device, h_get_queue,
    h_get_queue2, h_create_swapchain, h_destroy_swapchain, h_present, h_create_surface;
HookState* const g_hooks[] = {&h_destroy_instance, &h_gipa, &h_gdpa, &h_create_instance, &h_create_device,
    &h_destroy_device, &h_get_queue, &h_get_queue2, &h_create_swapchain, &h_destroy_swapchain, &h_present, &h_create_surface};

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
    VkImageUsageFlags usage = 0;
    std::vector<VkImage> listed;  // the driver's image list, fetched once for the pre-overlay callback
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
std::atomic<HMODULE> g_vulkan{nullptr};

std::atomic<uint64_t> g_presents{0}, g_frames{0}, g_swapchain_count{0}, g_last_draw_ms{0};
std::atomic<bool> g_has_device{false};
std::atomic<uint32_t> g_swap_count{0};  // live swapchains (status only)
std::atomic<uint32_t> g_width{0}, g_height{0}, g_format{0}, g_colorspace{0};
std::atomic<float> g_dpi{1.0f};
std::atomic<double> g_frame_ms{0};
bool g_failed = false;  // overlay disabled after a Vulkan error (presents keep passing through)
bool g_logged_draw = false;

struct PreOverlaySub {
    KilnHandle handle;
    KilnUiPreOverlayFn fn;
    KilnUiSwapchainGoneFn gone;
    void* user;
};
std::vector<PreOverlaySub> g_pre_overlay;  // under g_mu
std::atomic<uint32_t> g_pre_demand{0};
std::atomic<uint32_t> g_extra_usage{0};

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
    if (HMODULE m = g_vulkan.load(); !f && m) f = reinterpret_cast<PFN_vkVoidFunction>(GetProcAddress(m, name));
    if (!f && R.gipa && g_dev && g_dev->instance) f = R.gipa.load()(g_dev->instance, name);
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
// Work before the overlay (kiln_ui.h, subscribe_pre_overlay)

int fault_filter(EXCEPTION_POINTERS* ep);
uint32_t g_fault_code = 0;
void* g_fault_addr = nullptr;

// What was asked for with add_swapchain_usage, as far as this surface supports it: a swapchain the surface would refuse must not fail because of Kiln.
VkImageUsageFlags supported_extra_usage(VkDevice dev, VkSurfaceKHR surface) {
    const uint32_t extra = g_extra_usage.load();
    if (!extra || !R.gipa) return 0;
    VkInstance instance = VK_NULL_HANDLE;
    VkPhysicalDevice physical = VK_NULL_HANDLE;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        auto it = g_devices.find(dev);
        if (it == g_devices.end()) return 0;
        instance = it->second->instance ? it->second->instance : g_instance;
        physical = it->second->physical;
    }
    auto caps_fn = reinterpret_cast<PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR>(R.gipa.load()(instance, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR"));
    VkSurfaceCapabilitiesKHR caps{};
    if (!caps_fn || caps_fn(physical, surface, &caps) != VK_SUCCESS) return 0;
    return extra & caps.supportedUsageFlags;
}

bool guarded_gone(KilnUiSwapchainGoneFn fn, void* device, void* swapchain, void* user) {
    __try {
        fn(device, swapchain, user);
        return true;
    } __except (fault_filter(GetExceptionInformation())) {
        return false;
    }
}

// g_mu held.
void notify_gone(VkDevice dev, VkSwapchainKHR swapchain) {
    for (const PreOverlaySub& s : g_pre_overlay)
        if (s.gone && !guarded_gone(s.gone, dev, swapchain, s.user)) logf(KILN_LOG_ERROR, "ui: a pre-overlay subscriber faulted (0x%08x) as a swapchain went", g_fault_code);
}

bool guarded_pre_overlay_fn(KilnUiPreOverlayFn fn, const KilnUiPreOverlay* p, void** out, void* user, bool* faulted) {
    __try {
        return fn(p, out, user);
    } __except (fault_filter(GetExceptionInformation())) {
        *faulted = true;
        return false;
    }
}

// True: *wait replaces the game's semaphores in this present.
bool run_pre_overlay(VkQueue queue, const VkPresentInfoKHR* info, VkSemaphore* wait) {
    std::lock_guard<std::mutex> lk(g_mu);
    if (g_pre_overlay.empty()) return false;
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
    if (dit == g_devices.end() || !dit->second->fn.vkGetSwapchainImagesKHR) return false;
    Device& d = *dit->second;
    if (s->listed.empty()) {
        uint32_t n = 0;
        if (d.fn.vkGetSwapchainImagesKHR(d.device, s->swapchain, &n, nullptr) != VK_SUCCESS || n == 0) return false;
        std::vector<VkImage> listed(n);
        if (d.fn.vkGetSwapchainImagesKHR(d.device, s->swapchain, &n, listed.data()) != VK_SUCCESS) return false;
        s->listed = std::move(listed);
    }
    if (index >= s->listed.size()) return false;
    uint32_t family = d.graphics_family;
    if (auto q = d.queue_family.find(queue); q != d.queue_family.end()) family = q->second;

    KilnUiPreOverlay p{};
    p.size = sizeof p;
    p.image_index = index;
    p.instance = d.instance ? d.instance : g_instance;
    p.physical_device = d.physical;
    p.device = d.device;
    p.get_instance_proc_addr = reinterpret_cast<void*>(R.gipa.load());
    p.get_device_proc_addr = reinterpret_cast<void*>(d.gdpa);
    p.queue = queue;
    p.swapchain = s->swapchain;
    p.image = s->listed[index];
    p.wait_semaphores = reinterpret_cast<void* const*>(info->pWaitSemaphores);
    p.wait_count = info->waitSemaphoreCount;
    p.queue_family = family;
    p.format = static_cast<uint32_t>(s->format);
    p.color_space = static_cast<uint32_t>(s->color_space);
    p.width = s->extent.width;
    p.height = s->extent.height;
    p.usage = s->usage;
    bool submitted = false;
    for (size_t i = 0; i < g_pre_overlay.size();) {
        const PreOverlaySub sub = g_pre_overlay[i];
        void* out = nullptr;
        bool faulted = false;
        const bool took = guarded_pre_overlay_fn(sub.fn, &p, &out, sub.user, &faulted);
        if (faulted) {
            logf(KILN_LOG_ERROR, "ui: a pre-overlay subscriber faulted (0x%08x at %p): removed", g_fault_code, g_fault_addr);
            g_pre_overlay.erase(g_pre_overlay.begin() + static_cast<ptrdiff_t>(i));
            if (g_pre_overlay.empty()) g_pre_demand = 0;
            continue;
        }
        ++i;
        if (!took || !out) continue;
        // the next one in line waits on this one's work
        *wait = static_cast<VkSemaphore>(out);
        p.wait_semaphores = reinterpret_cast<void* const*>(wait);
        p.wait_count = 1;
        submitted = true;
    }
    return submitted;
}

// ---------------------------------------------------------------------------------------------------------------
// Detours

VkResult VKAPI_CALL hk_create_instance(const VkInstanceCreateInfo* ci, const VkAllocationCallbacks* a, VkInstance* out) {
    const VkResult r = R.create_instance.load()(ci, a, out);
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
    R.destroy_instance.load()(inst, a);
}

VkResult VKAPI_CALL hk_create_device(VkPhysicalDevice phys, const VkDeviceCreateInfo* ci, const VkAllocationCallbacks* a,
                                     VkDevice* out) {
    const VkResult r = R.create_device.load()(phys, ci, a, out);
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
                notify_gone(dev, nullptr);
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
    R.destroy_device.load()(dev, a);
}

void remember_queue(VkDevice dev, uint32_t family, VkQueue q) {
    if (!q) return;
    std::lock_guard<std::mutex> lk(g_mu);
    if (auto it = g_devices.find(dev); it != g_devices.end()) it->second->queue_family[q] = family;
}

void VKAPI_CALL hk_get_queue(VkDevice dev, uint32_t family, uint32_t index, VkQueue* out) {
    R.get_queue.load()(dev, family, index, out);
    if (out) remember_queue(dev, family, *out);
}

void VKAPI_CALL hk_get_queue2(VkDevice dev, const VkDeviceQueueInfo2* info, VkQueue* out) {
    R.get_queue2.load()(dev, info, out);
    if (out && info) remember_queue(dev, info->queueFamilyIndex, *out);
}

VkResult VKAPI_CALL hk_create_surface(VkInstance inst, const VkWin32SurfaceCreateInfoKHR* ci, const VkAllocationCallbacks* a,
                                      VkSurfaceKHR* out) {
    const VkResult r = R.create_surface.load()(inst, ci, a, out);
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
    if (!ci || !out) return R.create_swapchain.load()(dev, ci, a, out);
    VkSwapchainCreateInfoKHR c = *ci;
    c.imageUsage |= VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | supported_extra_usage(dev, ci->surface);
    VkResult r = R.create_swapchain.load()(dev, &c, a, out);
    bool drawable = r == VK_SUCCESS;
    VkImageUsageFlags usage = c.imageUsage;
    if (r != VK_SUCCESS && c.imageUsage != ci->imageUsage) {
        r = R.create_swapchain.load()(dev, ci, a, out);
        drawable = (ci->imageUsage & VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT) != 0;
        usage = ci->imageUsage;
    }
    if (r != VK_SUCCESS) return r;
    g_swapchain_count.fetch_add(1);
    std::lock_guard<std::mutex> lk(g_mu);
    if (ci->oldSwapchain) {
        auto it = g_swaps.find(ci->oldSwapchain);
        auto dit = g_devices.find(dev);
        if (it != g_swaps.end() && dit != g_devices.end()) destroy_swap_resources(*dit->second, it->second);
        notify_gone(dev, ci->oldSwapchain);
    }
    Swap s;
    s.usage = usage;
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
            notify_gone(dev, sc);
            g_swaps.erase(it);
            g_swap_count = static_cast<uint32_t>(g_swaps.size());
        }
    }
    R.destroy_swapchain.load()(dev, sc, a);
}

// draw() under g_mu and a fault guard: an access violation in the overlay turns the overlay off (logged) instead of
// taking the game down. No C++ object with a destructor lives in this frame.
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
    VkPresentInfoKHR after_pre_overlay;
    VkSemaphore pre_overlay_wait = VK_NULL_HANDLE;
    if (info && g_pre_demand.load(std::memory_order_relaxed) > 0 && run_pre_overlay(queue, info, &pre_overlay_wait)) {
        after_pre_overlay = *info;
        after_pre_overlay.waitSemaphoreCount = 1;
        after_pre_overlay.pWaitSemaphores = &pre_overlay_wait;
        info = &after_pre_overlay;
    }
    if (!info || g_failed || !want_draw()) {
        // capture just ended with nothing left to draw: still give the game its cursor clip back (render thread)
        if (imgui_ready()) input_capture_changed();
        return R.present.load()(queue, info);
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
    if (!drew) return R.present.load()(queue, info);
    VkPresentInfoKHR p = *info;
    p.waitSemaphoreCount = 1;
    p.pWaitSemaphores = &wait;
    return R.present.load()(queue, &p);
}

// Windows (Khronos loader): hand out Kiln's functions for the names it needs.
PFN_vkVoidFunction swap_in(const char* name, PFN_vkVoidFunction real) {
    if (!real || !name) return real;
#define KUI_SWAP(n, field, hk)                                                               \
    if (std::strcmp(name, n) == 0) {                                                         \
        auto empty = decltype(R.field.load()){nullptr};                                      \
        if (real != reinterpret_cast<PFN_vkVoidFunction>(&hk))                               \
            R.field.compare_exchange_strong(empty, reinterpret_cast<decltype(empty)>(real)); \
        return R.field ? reinterpret_cast<PFN_vkVoidFunction>(&hk) : nullptr;                 \
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
    return swap_in(name, R.gdpa.load()(dev, name));
}

PFN_vkVoidFunction VKAPI_CALL hk_gipa(VkInstance inst, const char* name) {
    PFN_vkVoidFunction real = R.gipa.load()(inst, name);
    if (name && std::strcmp(name, "vkGetDeviceProcAddr") == 0 && real)
        return R.gdpa ? reinterpret_cast<PFN_vkVoidFunction>(&hk_gdpa) : real;
    if (name && std::strcmp(name, "vkCreateInstance") == 0 && real) {
        PFN_vkCreateInstance empty = nullptr;
        if (real != reinterpret_cast<PFN_vkVoidFunction>(&hk_create_instance))
            R.create_instance.compare_exchange_strong(empty, reinterpret_cast<PFN_vkCreateInstance>(real));
        return R.create_instance ? reinterpret_cast<PFN_vkVoidFunction>(&hk_create_instance) : nullptr;
    }
    return swap_in(name, real);
}

// LoadLibraryExW can return inside another loader operation or a Vulkan call. Never wait for another installer
// while holding those locks, or re-enter installation on the same thread. This mutex does not protect detour calls:
// their originals are atomic, published before enable(), and their trampoline allocations live for the process.
std::mutex g_install_mu;
std::vector<SafetyHookInline*> g_retained_hooks;
int g_installs = 0;
bool g_input_installed = false;

struct ModuleRef {
    HMODULE module = nullptr;
    ModuleRef() = default;
    ModuleRef(const ModuleRef&) = delete;
    ModuleRef& operator=(const ModuleRef&) = delete;
    ModuleRef(ModuleRef&& other) noexcept : module(std::exchange(other.module, nullptr)) {}
    ~ModuleRef() { if (module) FreeLibrary(module); }
};

struct PreparedHook {
    HookState* state;
    ModuleRef owner;
    const char* name;
    vk_hooks::Canary reason;
    bool required;
};

bool read_code(const void* at, uint8_t* out, size_t size) {
    __try {
        std::memcpy(out, at, size);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

const char* canary_name(vk_hooks::Canary c) {
    using enum vk_hooks::Canary;
    switch (c) {
        case First: return "first hook";
        case Moved: return "new address after code-owner replacement";
        case Ours: return "still Kiln's patch";
        case Pristine: return "restored original code";
        case Reloaded: return "new code after code-owner replacement";
        case Foreign: return "unverified code/patch";
        default: return "unreadable";
    }
}

struct LdrUStr {
    USHORT Length, MaximumLength;
    PWSTR Buffer;
};
struct LdrNotice {
    ULONG Flags;
    const LdrUStr* FullDllName;
    const LdrUStr* BaseDllName;
    PVOID DllBase;
    ULONG SizeOfImage;
};
using LdrNoticeFn = VOID(CALLBACK*)(ULONG, const LdrNotice*, PVOID);
using LdrRegisterFn = LONG(NTAPI*)(ULONG, LdrNoticeFn, PVOID, PVOID*);

// Runs under the loader lock: no allocation, logging, locks or module calls. GetProcAddress can forward into
// winevulkan.dll, so watch the actual code owner, not every unrelated DLL named vulkan-1.dll. An unload notification
// runs BEFORE unmapping: for replacement at the same base, require a subsequent load, not just an unload notice.
// Counters are never cleared: a notice arriving during installation remains visible to the next check.
VOID CALLBACK on_dll_notice(ULONG reason, const LdrNotice* d, PVOID) {
    if ((reason != 1 && reason != 2) || !d) return;  // 1: loaded, 2: unloading
    const uintptr_t base = reinterpret_cast<uintptr_t>(d->DllBase);
    for (HookState* h : g_hooks) {
        const uintptr_t owner = h->owner.load();
        if (!owner) continue;
        if (reason == 1 && (base == owner || vk_hooks::contains(base, d->SizeOfImage, h->target.load()))) ++h->loads;
        if (reason == 2 && base == owner) ++h->unloads;
    }
}

void watch_unloads() {
    static bool registered = false, told_failure = false;
    if (registered) return;
    if (auto reg = reinterpret_cast<LdrRegisterFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "LdrRegisterDllNotification"))) {
        PVOID cookie = nullptr;
        registered = reg(0, &on_dll_notice, nullptr, &cookie) >= 0;
    }
    if (!registered && !told_failure) {
        told_failure = true;
        logf(KILN_LOG_WARN, "ui: cannot watch Vulkan code-owner unloads; unknown replacements will not be hooked");
    }
}

template <typename T>
bool hook(HookState& state, void* target, void* detour, std::atomic<T>& original, const char* what, bool& attempted,
          std::vector<PreparedHook>& prepared, bool required = true) {
    // Keep the owner mapped until after inspection/patching. The vulkan-1 ref in vk_install also keeps its forwarder
    // dependencies alive across the whole pass, without pinning the DLL forever (the desktop probe must unload).
    ModuleRef owner;
    vk_hooks::Bytes before{};
    const bool readable = target && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(target), &owner.module) && read_code(target, before.data(), before.size());
    const uint64_t unloads = state.unloads.load();
    const uint64_t loads = state.loads.load();
    const bool replaced = vk_hooks::replaced({state.bytes.address, state.owner.load(), state.seen_loads, state.seen_unloads},
        {reinterpret_cast<uintptr_t>(target), reinterpret_cast<uintptr_t>(owner.module), loads, unloads});
    auto c = vk_hooks::classify(state.bytes, reinterpret_cast<uintptr_t>(target), readable ? &before : nullptr, replaced);

    // Seeing a live patch consumes only the sampled epoch. Otherwise that old notice could later authorise a
    // re-hook after an overlay covers this same live patch. A newer notification is never erased.
    if (c == vk_hooks::Canary::Ours) {
        state.seen_loads = loads;
        state.seen_unloads = unloads;
        return true;
    }
    if (readable && state.hook && state.hook->target() == target &&
        vk_hooks::own_jump(before, reinterpret_cast<uintptr_t>(target), state.hook->trampoline().address(),
                           state.hook->trampoline().size(), state.hook->destination_address())) {
        state.seen_loads = loads;
        state.seen_unloads = unloads;
        return true;
    }

    // Also recognise every retained Kiln trampoline, including aliases/old generations. A changed tail or a new
    // export address is never permission to put a second Kiln detour in front of one already calling us.
    if (readable) {
        for (const SafetyHookInline* h : g_retained_hooks) {
            if (!vk_hooks::own_jump(before, reinterpret_cast<uintptr_t>(target), h->trampoline().address(),
                                    h->trampoline().size(), h->destination_address())) continue;
            c = vk_hooks::Canary::Foreign;
            state.seen_loads = loads;
            state.seen_unloads = unloads;
            break;
        }
    }
    if (!vk_hooks::may_hook(c)) {
        if (!state.told_skip) {
            state.told_skip = true;
            logf(KILN_LOG_WARN, "ui: %s at %p is %s: not re-hooked (an existing hook may be underneath)", what, target, canary_name(c));
        }
        return false;
    }

    attempted = true;
    auto h = std::make_unique<SafetyHookInline>(safetyhook::create_inline(target, detour, SafetyHookInline::StartDisabled));
    if (!*h) {
        logf(KILN_LOG_ERROR, "ui: hooking %s failed", what);
        return false;
    }
    const auto& saved = h->original_bytes();
    vk_hooks::Bytes check{};
    // An overlay may have patched between classification and trampoline creation. Do not publish or enable a
    // trampoline built from a different prologue. A final re-read also narrows the race with an external patcher.
    if (saved.empty() || saved.size() > before.size() || std::memcmp(saved.data(), before.data(), saved.size()) ||
        !read_code(target, check.data(), saved.size()) || std::memcmp(saved.data(), check.data(), saved.size())) {
        logf(KILN_LOG_WARN, "ui: %s changed while preparing its hook: not hooked", what);
        return false;
    }
    // Retain before publication: even if enable fails, no observer may ever see a freed trampoline. Moving a live
    // h_gipa/h_gdpa used to deadlock against .call()'s mutex, or temporarily expose an empty hook to the detour.
    // Allocate the pending entry before publishing anything; its module ref stays held through activation.
    prepared.push_back({&state, std::move(owner), what, c, required});
    g_retained_hooks.push_back(h.get());
    state.hook = h.release();
    state.bytes = {};
    state.bytes.address = reinterpret_cast<uintptr_t>(target);
    state.bytes.size = saved.size();
    std::copy(saved.begin(), saved.end(), state.bytes.original.begin());
    state.owner = reinterpret_cast<uintptr_t>(prepared.back().owner.module);
    state.target = state.bytes.address;
    state.seen_loads = loads;
    state.seen_unloads = unloads;
    original.store(state.hook->original<T>());
    return true;
}

bool enable_hook(const PreparedHook& p) {
    HookState& state = *p.state;
    vk_hooks::Bytes check{};
    if (!read_code(state.hook->target(), check.data(), state.bytes.size) ||
        std::memcmp(check.data(), state.bytes.original.data(), state.bytes.size)) {
        logf(KILN_LOG_WARN, "ui: %s changed before enabling its hook: not hooked", p.name);
        return false;
    }
    if (!state.hook->enable()) {
        logf(KILN_LOG_ERROR, "ui: enabling %s failed", p.name);
        return false;
    }
    // Capture only our own emitted jump, never an overlay that raced us after enable(). Without a fingerprint a
    // later unknown patch is left alone; the retained trampoline check above still recognises our actual jump.
    state.bytes.have_patch = read_code(state.hook->target(), state.bytes.patch.data(), state.bytes.size) &&
        vk_hooks::own_jump(state.bytes.patch, state.bytes.address, state.hook->trampoline().address(),
                           state.hook->trampoline().size(), state.hook->destination_address());
    state.told_skip = false;
    logf(KILN_LOG_INFO, "ui: hooking %s at %p (%s)", p.name, state.hook->target(), canary_name(p.reason));
    return true;
}

}  // namespace

bool vk_install() {
    static thread_local bool installing = false;
    if (installing) return g_installed.load();
    installing = true;
    struct Leave { bool& flag; ~Leave() { flag = false; } } leave{installing};
    std::unique_lock<std::mutex> lk(g_install_mu, std::try_to_lock);
    if (!lk.owns_lock()) return g_installed.load();
    // Temporary refs prevent an unload between GetProcAddress/read/enable. Drop them before releasing the recursion
    // guard; FreeLibrary can itself invoke loader code. No locks are taken by on_dll_notice.
    ModuleRef vulkan;
    if (!GetModuleHandleExW(0, L"vulkan-1.dll", &vulkan.module)) return false;
    g_vulkan = vulkan.module;
    const bool wine = GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "wine_get_version") != nullptr;
    g_wine = wine;
    watch_unloads();
    if (!g_input_installed) {
        g_input_installed = true;
        input_install_iat();
    }
    auto P = [&](const char* n) { return reinterpret_cast<void*>(GetProcAddress(vulkan.module, n)); };
    bool ok = true, attempted = false;
    std::vector<PreparedHook> prepared;
    // Wine's proc-address exports are not detoured. Do not overwrite the Windows trampoline originals with their
    // patched entry points on every LoadLibraryExW (that would immediately recurse).
    if (wine) {
        R.gipa = reinterpret_cast<PFN_vkGetInstanceProcAddr>(P("vkGetInstanceProcAddr"));
        R.gdpa = reinterpret_cast<PFN_vkGetDeviceProcAddr>(P("vkGetDeviceProcAddr"));
    }
    // Each export decides independently: one fresh prologue must never retire/re-hook the other live exports.
    // Prepare every original before enabling ANY new hook: a concurrent Windows proc-address lookup or device
    // creation must not observe null/stale resolver/command originals halfway through an install.
    if (!wine) {
        ok &= hook(h_gdpa, P("vkGetDeviceProcAddr"), reinterpret_cast<void*>(&hk_gdpa), R.gdpa, "vkGetDeviceProcAddr", attempted, prepared);
        ok &= hook(h_gipa, P("vkGetInstanceProcAddr"), reinterpret_cast<void*>(&hk_gipa), R.gipa, "vkGetInstanceProcAddr", attempted, prepared);
    }
    const bool instance_ok = hook(h_create_instance, P("vkCreateInstance"), reinterpret_cast<void*>(&hk_create_instance), R.create_instance, "vkCreateInstance", attempted, prepared, wine);
    if (wine) ok &= instance_ok;
    hook(h_destroy_instance, P("vkDestroyInstance"), reinterpret_cast<void*>(&hk_destroy_instance), R.destroy_instance, "vkDestroyInstance", attempted, prepared, false);
    ok &= hook(h_create_device, P("vkCreateDevice"), reinterpret_cast<void*>(&hk_create_device), R.create_device, "vkCreateDevice", attempted, prepared);
    ok &= hook(h_destroy_device, P("vkDestroyDevice"), reinterpret_cast<void*>(&hk_destroy_device), R.destroy_device, "vkDestroyDevice", attempted, prepared);
    ok &= hook(h_get_queue, P("vkGetDeviceQueue"), reinterpret_cast<void*>(&hk_get_queue), R.get_queue, "vkGetDeviceQueue", attempted, prepared);
    hook(h_get_queue2, P("vkGetDeviceQueue2"), reinterpret_cast<void*>(&hk_get_queue2), R.get_queue2, "vkGetDeviceQueue2", attempted, prepared, false);
    ok &= hook(h_create_swapchain, P("vkCreateSwapchainKHR"), reinterpret_cast<void*>(&hk_create_swapchain), R.create_swapchain, "vkCreateSwapchainKHR", attempted, prepared);
    ok &= hook(h_destroy_swapchain, P("vkDestroySwapchainKHR"), reinterpret_cast<void*>(&hk_destroy_swapchain), R.destroy_swapchain, "vkDestroySwapchainKHR", attempted, prepared);
    ok &= hook(h_present, P("vkQueuePresentKHR"), reinterpret_cast<void*>(&hk_present), R.present, "vkQueuePresentKHR", attempted, prepared);
    const bool surface_ok = hook(h_create_surface, P("vkCreateWin32SurfaceKHR"), reinterpret_cast<void*>(&hk_create_surface), R.create_surface, "vkCreateWin32SurfaceKHR", attempted, prepared, wine);
    if (wine) ok &= surface_ok;
    for (const PreparedHook& p : prepared) {
        const bool enabled = enable_hook(p);
        if (p.required) ok &= enabled;
    }
    g_installed = ok;
    if (attempted) {
        ++g_installs;
        logf(ok ? KILN_LOG_INFO : KILN_LOG_WARN, "ui: Vulkan hooks %s (%s path, vulkan-1 at %p, install #%d)", ok ? "installed" : "INCOMPLETE",
             wine ? "Wine: winevulkan exports" : "Windows: proc-address wrappers", reinterpret_cast<void*>(vulkan.module), g_installs);
    }
    return ok;
}

KilnResult vk_subscribe_pre_overlay(KilnUiPreOverlayFn fn, KilnUiSwapchainGoneFn gone, void* user, KilnHandle* out) {
    if (!fn) return KILN_ERR_INVALID_ARGUMENT;
    const KilnHandle h = next_handle();
    std::lock_guard<std::mutex> lk(g_mu);
    g_pre_overlay.push_back({h, fn, gone, user});
    if (out) *out = h;
    return KILN_OK;
}

bool vk_unsubscribe(KilnHandle handle) {
    std::lock_guard<std::mutex> lk(g_mu);
    return std::erase_if(g_pre_overlay, [&](const PreOverlaySub& s) { return s.handle == handle; }) > 0;
}

void vk_set_pre_overlay_demand(uint32_t count) { g_pre_demand.store(count, std::memory_order_relaxed); }
void vk_add_swapchain_usage(uint32_t usage) { g_extra_usage.fetch_or(usage); }

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
