// SPDX-License-Identifier: MIT
// kiln.screen's pass on a real GPU: no window, no game. Python drives it (test_gpu.py). Each frame here does what the game and kiln.ui do around
// a present: the "game" fills the image and signals a semaphore, the pass chains onto it through the same KilnUiPreOverlay kiln.ui fills in, and
// what comes out is read back before it is presented. The target is a list of plain images, or a real swapchain on VK_EXT_headless_surface.
//
// Device functions reach the pass through a wrapped vkGetDeviceProcAddr, which can make any of them fail on request, and timestamps the pass from
// its first barrier to the end of its render pass.
//
// Safety: every wait here has a timeout. A timeout or a lost device outside a requested failure latches the library, and the caller ends the process.
#include <vulkan/vulkan.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <vector>

#include "../core.h"
#include "../pass.h"

namespace {

constexpr uint64_t kWaitNs = 5'000'000'000ull;

VkInstance g_instance = VK_NULL_HANDLE;
VkPhysicalDevice g_phys = VK_NULL_HANDLE;
VkDevice g_dev = VK_NULL_HANDLE;
VkQueue g_queue = VK_NULL_HANDLE;
uint32_t g_family = 0;
VkCommandPool g_pool = VK_NULL_HANDLE;
VkPhysicalDeviceMemoryProperties g_mem{};
double g_tick_ns = 1.0;
int g_lost = 0;
int g_headless = 0;

// Validation messages, one record per id and severity.
struct Finding { std::string severity, id, first_tag, first_text; int count = 0; };
std::mutex g_find_mu;
std::map<std::string, Finding> g_findings;
std::string g_tag;
VkDebugUtilsMessengerEXT g_messenger = VK_NULL_HANDLE;
bool g_controlling = false;

VKAPI_ATTR VkBool32 VKAPI_CALL on_validation(VkDebugUtilsMessageSeverityFlagBitsEXT severity, VkDebugUtilsMessageTypeFlagsEXT,
                                             const VkDebugUtilsMessengerCallbackDataEXT* data, void*) {
    std::lock_guard<std::mutex> lock(g_find_mu);
    const std::string sev = severity & VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT ? "error" : "warning";
    const std::string id = data->pMessageIdName ? data->pMessageIdName : "unnamed";
    Finding& f = g_findings[sev + "|" + id];
    if (f.count++ == 0) {
        f.severity = sev;
        f.id = id;
        f.first_tag = g_tag;
        f.first_text = data->pMessage ? data->pMessage : "";
    }
    return g_controlling ? VK_TRUE : VK_FALSE;
}

int fail(VkResult r, const char* what) {
    fprintf(stderr, "screengpu: %s: VkResult %d\n", what, static_cast<int>(r));
    if (r == VK_ERROR_DEVICE_LOST || r == VK_TIMEOUT) g_lost = static_cast<int>(r);
    return static_cast<int>(r) == 0 ? -1 : static_cast<int>(r);
}
#define CHECK(call, what)                       \
    do {                                        \
        VkResult r_ = (call);                   \
        if (r_ != VK_SUCCESS) return fail(r_, what); \
    } while (0)
#define ALIVE()                 \
    do {                        \
        if (g_lost) return g_lost; \
    } while (0)

uint32_t mem_type(uint32_t bits, VkMemoryPropertyFlags want) {
    for (uint32_t i = 0; i < g_mem.memoryTypeCount; i++)
        if ((bits & (1u << i)) && (g_mem.memoryTypes[i].propertyFlags & want) == want) return i;
    return UINT32_MAX;
}

// ---- what the pass gets as vkGetDeviceProcAddr --------------------------------------------------------------------------------------------

struct Injected { VkResult result = VK_SUCCESS; int after = 0; bool on = false; };
std::mutex g_inject_mu;
std::map<std::string, Injected> g_inject;

bool injected(const char* name, VkResult* out) {
    std::lock_guard<std::mutex> lock(g_inject_mu);
    auto it = g_inject.find(name);
    if (it == g_inject.end() || !it->second.on) return false;
    if (it->second.after > 0) {
        --it->second.after;
        return false;
    }
    *out = it->second.result;
    return true;
}

PFN_vkCreateGraphicsPipelines real_create_pipelines;
PFN_vkQueueSubmit real_submit;
PFN_vkWaitForFences real_wait;
PFN_vkCreateImage real_create_image;
PFN_vkAllocateMemory real_alloc;
PFN_vkCreateShaderModule real_create_module;
PFN_vkCmdPipelineBarrier real_barrier;
PFN_vkCmdEndRenderPass real_end_pass;

// timestamps: written into the pass's own command buffer from outside it. The query pool is reset by the frame before the pass records.
VkQueryPool g_queries = VK_NULL_HANDLE;
bool g_stamped = false;
std::atomic<int> g_pipeline_calls{0};

VKAPI_ATTR VkResult VKAPI_CALL w_create_pipelines(VkDevice d, VkPipelineCache c, uint32_t n, const VkGraphicsPipelineCreateInfo* i, const VkAllocationCallbacks* a, VkPipeline* p) {
    g_pipeline_calls.fetch_add(1);
    VkResult r;
    if (injected("vkCreateGraphicsPipelines", &r)) return r;
    return real_create_pipelines(d, c, n, i, a, p);
}
VKAPI_ATTR VkResult VKAPI_CALL w_submit(VkQueue q, uint32_t n, const VkSubmitInfo* i, VkFence f) {
    VkResult r;
    if (injected("vkQueueSubmit", &r)) return r;
    return real_submit(q, n, i, f);
}
VKAPI_ATTR VkResult VKAPI_CALL w_wait(VkDevice d, uint32_t n, const VkFence* f, VkBool32 all, uint64_t t) {
    VkResult r;
    if (injected("vkWaitForFences", &r)) return r;
    return real_wait(d, n, f, all, t);
}
VKAPI_ATTR VkResult VKAPI_CALL w_create_image(VkDevice d, const VkImageCreateInfo* i, const VkAllocationCallbacks* a, VkImage* o) {
    VkResult r;
    if (injected("vkCreateImage", &r)) return r;
    return real_create_image(d, i, a, o);
}
VKAPI_ATTR VkResult VKAPI_CALL w_alloc(VkDevice d, const VkMemoryAllocateInfo* i, const VkAllocationCallbacks* a, VkDeviceMemory* o) {
    VkResult r;
    if (injected("vkAllocateMemory", &r)) return r;
    return real_alloc(d, i, a, o);
}
VKAPI_ATTR VkResult VKAPI_CALL w_create_module(VkDevice d, const VkShaderModuleCreateInfo* i, const VkAllocationCallbacks* a, VkShaderModule* o) {
    VkResult r;
    if (injected("vkCreateShaderModule", &r)) return r;
    return real_create_module(d, i, a, o);
}
VKAPI_ATTR void VKAPI_CALL w_barrier(VkCommandBuffer c, VkPipelineStageFlags s, VkPipelineStageFlags d, VkDependencyFlags f, uint32_t mn, const VkMemoryBarrier* m,
                                     uint32_t bn, const VkBufferMemoryBarrier* b, uint32_t in, const VkImageMemoryBarrier* i) {
    if (!g_stamped && g_queries) {
        vkCmdWriteTimestamp(c, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, g_queries, 0);
        g_stamped = true;
    }
    real_barrier(c, s, d, f, mn, m, bn, b, in, i);
}
VKAPI_ATTR void VKAPI_CALL w_end_pass(VkCommandBuffer c) {
    real_end_pass(c);
    if (g_queries) vkCmdWriteTimestamp(c, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, g_queries, 1);
}

VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL wrapped_gdpa(VkDevice d, const char* name) {
    auto real = vkGetDeviceProcAddr(d, name);
#define WRAP(fname, wrapper, slot)                                  \
    if (strcmp(name, fname) == 0) {                                 \
        slot = reinterpret_cast<decltype(slot)>(real);              \
        return reinterpret_cast<PFN_vkVoidFunction>(&wrapper);      \
    }
    WRAP("vkCreateGraphicsPipelines", w_create_pipelines, real_create_pipelines)
    WRAP("vkQueueSubmit", w_submit, real_submit)
    WRAP("vkWaitForFences", w_wait, real_wait)
    WRAP("vkCreateImage", w_create_image, real_create_image)
    WRAP("vkAllocateMemory", w_alloc, real_alloc)
    WRAP("vkCreateShaderModule", w_create_module, real_create_module)
    WRAP("vkCmdPipelineBarrier", w_barrier, real_barrier)
    WRAP("vkCmdEndRenderPass", w_end_pass, real_end_pass)
#undef WRAP
    return real;
}

// ---- the target and the frame ----------------------------------------------------------------------------------------------------------------

struct Target {
    VkFormat format = VK_FORMAT_UNDEFINED;
    VkColorSpaceKHR color_space = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    uint32_t w = 0, h = 0, usage = 0;
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    VkSwapchainKHR swapchain = VK_NULL_HANDLE;
    std::vector<VkImage> images;
    std::vector<VkDeviceMemory> memory;  // plain images only
    std::vector<VkSemaphore> rendered, shown;
    VkSemaphore acquired = VK_NULL_HANDLE;
    uint32_t next = 0;
    bool fresh = true;
} g_target;

VkBuffer g_upload = VK_NULL_HANDLE, g_download = VK_NULL_HANDLE;
VkDeviceMemory g_upload_mem = VK_NULL_HANDLE, g_download_mem = VK_NULL_HANDLE;
void* g_upload_map = nullptr;
void* g_download_map = nullptr;
VkDeviceSize g_stage_size = 0;
VkCommandBuffer g_cmd_game = VK_NULL_HANDLE, g_cmd_read = VK_NULL_HANDLE;
VkFence g_fence = VK_NULL_HANDLE;

std::mutex g_log_mu;
std::string g_log;
void on_log(int, const char* text) {
    std::lock_guard<std::mutex> lock(g_log_mu);
    g_log += text;
    g_log += '\n';
}

std::unique_ptr<kscreen::Pass> g_pass;
kscreen::Mixer g_mixer;
double g_last_cpu_us = 0, g_last_gpu_us = 0;
bool g_last_stamped = false;

int make_stage(VkDeviceSize size, VkBufferUsageFlags usage, VkBuffer* b, VkDeviceMemory* m, void** map) {
    if (*b) {
        vkDestroyBuffer(g_dev, *b, nullptr);
        vkFreeMemory(g_dev, *m, nullptr);
    }
    VkBufferCreateInfo bi{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
    bi.size = size;
    bi.usage = usage;
    CHECK(vkCreateBuffer(g_dev, &bi, nullptr, b), "stage buffer");
    VkMemoryRequirements req{};
    vkGetBufferMemoryRequirements(g_dev, *b, &req);
    VkMemoryAllocateInfo mi{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    mi.allocationSize = req.size;
    mi.memoryTypeIndex = mem_type(req.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    CHECK(vkAllocateMemory(g_dev, &mi, nullptr, m), "stage memory");
    CHECK(vkBindBufferMemory(g_dev, *b, *m, 0), "stage bind");
    CHECK(vkMapMemory(g_dev, *m, 0, VK_WHOLE_SIZE, 0, map), "stage map");
    return 0;
}

void barrier(VkCommandBuffer c, VkImage image, VkImageLayout from, VkImageLayout to, VkAccessFlags sa, VkAccessFlags da, VkPipelineStageFlags ss, VkPipelineStageFlags ds) {
    VkImageMemoryBarrier b{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
    b.oldLayout = from;
    b.newLayout = to;
    b.srcAccessMask = sa;
    b.dstAccessMask = da;
    b.srcQueueFamilyIndex = b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = image;
    b.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
    vkCmdPipelineBarrier(c, ss, ds, 0, 0, nullptr, 0, nullptr, 1, &b);
}

// What vkDestroySwapchainKHR and a device's end do in kiln.ui: tell the pass, then destroy.
void drop_target(bool keep_swapchain_for_old) {
    Target& t = g_target;
    if (t.swapchain) {
        g_pass->gone(g_dev, t.swapchain);
        if (!keep_swapchain_for_old) vkDestroySwapchainKHR(g_dev, t.swapchain, nullptr);
    } else if (!t.images.empty()) {
        // a plain image list stands for a swapchain: its handle is the first image's
        g_pass->gone(g_dev, reinterpret_cast<void*>(t.images[0]));
        for (auto i : t.images) vkDestroyImage(g_dev, i, nullptr);
        for (auto m : t.memory) vkFreeMemory(g_dev, m, nullptr);
    }
    for (auto s : t.rendered) vkDestroySemaphore(g_dev, s, nullptr);
    for (auto s : t.shown) vkDestroySemaphore(g_dev, s, nullptr);
    if (t.acquired) vkDestroySemaphore(g_dev, t.acquired, nullptr);
    t.images.clear();
    t.memory.clear();
    t.rendered.clear();
    t.shown.clear();
    t.acquired = VK_NULL_HANDLE;
}

// The pass asks a swapchain for its images. A plain target has no swapchain, so this answers for it.
VKAPI_ATTR VkResult VKAPI_CALL w_get_images(VkDevice d, VkSwapchainKHR sc, uint32_t* n, VkImage* out) {
    if (g_target.swapchain) return vkGetSwapchainImagesKHR(d, sc, n, out);
    if (!out) {
        *n = static_cast<uint32_t>(g_target.images.size());
        return VK_SUCCESS;
    }
    for (uint32_t i = 0; i < *n && i < g_target.images.size(); i++) out[i] = g_target.images[i];
    return VK_SUCCESS;
}

VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL harness_gdpa(VkDevice d, const char* name) {
    if (strcmp(name, "vkGetSwapchainImagesKHR") == 0) return reinterpret_cast<PFN_vkVoidFunction>(&w_get_images);
    return wrapped_gdpa(d, name);
}

}  // namespace

extern "C" {

int sg_lost(void) { return g_lost; }

void sg_tag(const char* tag) {
    std::lock_guard<std::mutex> lock(g_find_mu);
    g_tag = tag ? tag : "";
}

int sg_validation_report(char* out, int cap) {
    std::lock_guard<std::mutex> lock(g_find_mu);
    std::string all;
    for (auto& kv : g_findings) {
        const Finding& f = kv.second;
        std::string text = f.first_text;
        for (char& c : text)
            if (c == '\t' || c == '\n' || c == '\r') c = ' ';
        all += f.severity + "\t" + std::to_string(f.count) + "\t" + f.id + "\t" + f.first_tag + "\t" + text + "\n";
    }
    if (cap > 0) {
        const size_t n = all.size() < size_t(cap - 1) ? all.size() : size_t(cap - 1);
        memcpy(out, all.data(), n);
        out[n] = 0;
    }
    return static_cast<int>(all.size());
}

// A clean run means nothing if the layer can't report: a core rule and a synchronization hazard, on a command buffer that is thrown away. Bit 0 the
// core rule was seen, bit 1 the hazard. What it found is forgotten.
int sg_validation_control(void) {
    ALIVE();
    VkBufferCreateInfo bi{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
    bi.size = 64;
    bi.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT;
    VkBuffer b = VK_NULL_HANDLE;
    CHECK(vkCreateBuffer(g_dev, &bi, nullptr, &b), "control buffer");
    VkMemoryRequirements req{};
    vkGetBufferMemoryRequirements(g_dev, b, &req);
    VkMemoryAllocateInfo mi{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    mi.allocationSize = req.size;
    mi.memoryTypeIndex = mem_type(req.memoryTypeBits, 0);
    VkDeviceMemory m = VK_NULL_HANDLE;
    CHECK(vkAllocateMemory(g_dev, &mi, nullptr, &m), "control memory");
    CHECK(vkBindBufferMemory(g_dev, b, m, 0), "control bind");
    std::map<std::string, Finding> held;
    {
        std::lock_guard<std::mutex> lock(g_find_mu);
        held.swap(g_findings);
    }
    CHECK(vkResetCommandBuffer(g_cmd_game, 0), "reset");
    VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    CHECK(vkBeginCommandBuffer(g_cmd_game, &begin), "begin");
    vkCmdFillBuffer(g_cmd_game, b, 0, 16, 0);
    g_controlling = true;
    vkCmdFillBuffer(g_cmd_game, b, 0, 16, 0);  // write after write, no barrier
    vkCmdFillBuffer(g_cmd_game, b, 1, 16, 0);  // offset not a multiple of 4
    g_controlling = false;
    CHECK(vkEndCommandBuffer(g_cmd_game), "end");
    CHECK(vkResetCommandBuffer(g_cmd_game, 0), "reset");
    vkDestroyBuffer(g_dev, b, nullptr);
    vkFreeMemory(g_dev, m, nullptr);
    int seen = 0;
    {
        std::lock_guard<std::mutex> lock(g_find_mu);
        for (auto& kv : g_findings) {
            if (kv.second.id.rfind("VUID-", 0) == 0) seen |= 1;
            if (kv.second.id.rfind("SYNC-", 0) == 0) seen |= 2;
        }
        g_findings.swap(held);
    }
    return seen;
}

// validate: 0 off, 1 the Khronos layer with synchronization validation. Returns -10 without the layer, -2 no such device. Info: the device's name and so on.
int sg_init(const char* name, int validate, char* info, int cap) {
    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
    app.pApplicationName = "kiln-screengpu";
    app.apiVersion = VK_API_VERSION_1_3;
    VkInstanceCreateInfo ici{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
    ici.pApplicationInfo = &app;
    const char* layer = "VK_LAYER_KHRONOS_validation";
    std::vector<const char*> inst_exts;
    uint32_t en = 0;
    vkEnumerateInstanceExtensionProperties(nullptr, &en, nullptr);
    std::vector<VkExtensionProperties> have(en);
    vkEnumerateInstanceExtensionProperties(nullptr, &en, have.data());
    for (auto& e : have)
        if (strcmp(e.extensionName, VK_EXT_HEADLESS_SURFACE_EXTENSION_NAME) == 0) g_headless = 1;
    if (g_headless) {
        inst_exts.push_back(VK_KHR_SURFACE_EXTENSION_NAME);
        inst_exts.push_back(VK_EXT_HEADLESS_SURFACE_EXTENSION_NAME);
    }
    VkValidationFeatureEnableEXT enables[1] = {VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT};
    VkValidationFeaturesEXT features{VK_STRUCTURE_TYPE_VALIDATION_FEATURES_EXT};
    VkDebugUtilsMessengerCreateInfoEXT mi{VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT};
    if (validate) {
        uint32_t ln = 0;
        vkEnumerateInstanceLayerProperties(&ln, nullptr);
        std::vector<VkLayerProperties> layers(ln);
        vkEnumerateInstanceLayerProperties(&ln, layers.data());
        bool found = false;
        for (auto& l : layers) found = found || strcmp(l.layerName, layer) == 0;
        if (!found) return -10;
        inst_exts.push_back(VK_EXT_DEBUG_UTILS_EXTENSION_NAME);
        inst_exts.push_back(VK_EXT_VALIDATION_FEATURES_EXTENSION_NAME);
        features.enabledValidationFeatureCount = 1;
        features.pEnabledValidationFeatures = enables;
        mi.messageSeverity = VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT;
        mi.messageType = VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_TYPE_PERFORMANCE_BIT_EXT;
        mi.pfnUserCallback = on_validation;
        mi.pNext = &features;
        ici.pNext = &mi;
        ici.enabledLayerCount = 1;
        ici.ppEnabledLayerNames = &layer;
    }
    ici.enabledExtensionCount = static_cast<uint32_t>(inst_exts.size());
    ici.ppEnabledExtensionNames = inst_exts.data();
    CHECK(vkCreateInstance(&ici, nullptr, &g_instance), "instance");
    if (validate) {
        auto create = reinterpret_cast<PFN_vkCreateDebugUtilsMessengerEXT>(vkGetInstanceProcAddr(g_instance, "vkCreateDebugUtilsMessengerEXT"));
        VkDebugUtilsMessengerCreateInfoEXT m2 = mi;
        m2.pNext = nullptr;
        if (create) CHECK(create(g_instance, &m2, nullptr, &g_messenger), "messenger");
    }
    uint32_t n = 0;
    vkEnumeratePhysicalDevices(g_instance, &n, nullptr);
    std::vector<VkPhysicalDevice> devs(n);
    vkEnumeratePhysicalDevices(g_instance, &n, devs.data());
    VkPhysicalDeviceProperties props{};
    for (auto d : devs) {
        vkGetPhysicalDeviceProperties(d, &props);
        if (strstr(props.deviceName, name)) {
            g_phys = d;
            break;
        }
    }
    if (!g_phys) return -2;
    vkGetPhysicalDeviceMemoryProperties(g_phys, &g_mem);
    g_tick_ns = props.limits.timestampPeriod;
    uint32_t qn = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(g_phys, &qn, nullptr);
    std::vector<VkQueueFamilyProperties> qf(qn);
    vkGetPhysicalDeviceQueueFamilyProperties(g_phys, &qn, qf.data());
    g_family = UINT32_MAX;
    for (uint32_t i = 0; i < qn; i++)
        if (qf[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) {
            g_family = i;
            break;
        }
    if (g_family == UINT32_MAX) return -3;
    float prio = 1.0f;
    VkDeviceQueueCreateInfo qi{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
    qi.queueFamilyIndex = g_family;
    qi.queueCount = 1;
    qi.pQueuePriorities = &prio;
    const char* dev_exts[] = {VK_KHR_SWAPCHAIN_EXTENSION_NAME};
    VkDeviceCreateInfo di{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
    di.queueCreateInfoCount = 1;
    di.pQueueCreateInfos = &qi;
    di.enabledExtensionCount = g_headless ? 1 : 0;
    di.ppEnabledExtensionNames = dev_exts;
    CHECK(vkCreateDevice(g_phys, &di, nullptr, &g_dev), "device");
    vkGetDeviceQueue(g_dev, g_family, 0, &g_queue);
    VkCommandPoolCreateInfo pi{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
    pi.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pi.queueFamilyIndex = g_family;
    CHECK(vkCreateCommandPool(g_dev, &pi, nullptr, &g_pool), "pool");
    VkCommandBuffer cbs[2];
    VkCommandBufferAllocateInfo ai{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
    ai.commandPool = g_pool;
    ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    ai.commandBufferCount = 2;
    CHECK(vkAllocateCommandBuffers(g_dev, &ai, cbs), "commands");
    g_cmd_game = cbs[0];
    g_cmd_read = cbs[1];
    VkFenceCreateInfo fi{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    CHECK(vkCreateFence(g_dev, &fi, nullptr, &g_fence), "fence");
    VkQueryPoolCreateInfo qp{VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO};
    qp.queryType = VK_QUERY_TYPE_TIMESTAMP;
    qp.queryCount = 2;
    CHECK(vkCreateQueryPool(g_dev, &qp, nullptr, &g_queries), "queries");
    g_pass = std::make_unique<kscreen::Pass>(&on_log);
    if (info && cap > 0)
        snprintf(info, cap, "%s; api %u.%u.%u; driver 0x%x; queue family %u; headless surface %d; validation %d", props.deviceName, VK_API_VERSION_MAJOR(props.apiVersion),
                 VK_API_VERSION_MINOR(props.apiVersion), VK_API_VERSION_PATCH(props.apiVersion), props.driverVersion, g_family, g_headless, validate);
    return 0;
}

// Formats a swapchain on the headless surface offers, as "format colourspace" lines; and what usage it allows. Returns the usage flags, or < 0.
int sg_surface_formats(char* out, int cap) {
    ALIVE();
    if (!g_headless) return -1;
    if (!g_target.surface) {
        VkHeadlessSurfaceCreateInfoEXT si{VK_STRUCTURE_TYPE_HEADLESS_SURFACE_CREATE_INFO_EXT};
        auto create = reinterpret_cast<PFN_vkCreateHeadlessSurfaceEXT>(vkGetInstanceProcAddr(g_instance, "vkCreateHeadlessSurfaceEXT"));
        CHECK(create(g_instance, &si, nullptr, &g_target.surface), "headless surface");
    }
    VkBool32 ok = VK_FALSE;
    CHECK(vkGetPhysicalDeviceSurfaceSupportKHR(g_phys, g_family, g_target.surface, &ok), "surface support");
    if (!ok) return -2;
    uint32_t n = 0;
    CHECK(vkGetPhysicalDeviceSurfaceFormatsKHR(g_phys, g_target.surface, &n, nullptr), "surface formats");
    std::vector<VkSurfaceFormatKHR> f(n);
    CHECK(vkGetPhysicalDeviceSurfaceFormatsKHR(g_phys, g_target.surface, &n, f.data()), "surface formats");
    std::string all;
    for (auto& x : f) all += std::to_string(static_cast<int>(x.format)) + " " + std::to_string(static_cast<int>(x.colorSpace)) + "\n";
    if (cap > 0) snprintf(out, cap, "%s", all.c_str());
    VkSurfaceCapabilitiesKHR caps{};
    CHECK(vkGetPhysicalDeviceSurfaceCapabilitiesKHR(g_phys, g_target.surface, &caps), "surface caps");
    return static_cast<int>(caps.supportedUsageFlags);
}

// A new target, as the game makes a swapchain. The old one goes first, through the pass's `gone`, except that a real swapchain is passed on as
// oldSwapchain. kind: 0 a list of plain images in place of a swapchain, 1 a real swapchain. usage 0: everything the pass needs.
int sg_target(int format, int color_space, int w, int h, int kind, int count, uint32_t usage) {
    ALIVE();
    vkQueueWaitIdle(g_queue);
    const bool real = kind == 1;
    VkSwapchainKHR old = g_target.swapchain;
    drop_target(real);
    Target& t = g_target;
    t.format = static_cast<VkFormat>(format);
    t.color_space = static_cast<VkColorSpaceKHR>(color_space);
    t.w = w;
    t.h = h;
    t.usage = usage ? usage : (VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT);
    t.next = 0;
    t.swapchain = VK_NULL_HANDLE;
    if (real) {
        char ignore[1];
        const int su = sg_surface_formats(ignore, 0);
        if (su < 0) return su;
        VkSurfaceCapabilitiesKHR caps{};
        CHECK(vkGetPhysicalDeviceSurfaceCapabilitiesKHR(g_phys, t.surface, &caps), "surface caps");
        VkSwapchainCreateInfoKHR ci{VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR};
        ci.surface = t.surface;
        ci.minImageCount = std::max<uint32_t>(caps.minImageCount, static_cast<uint32_t>(count));
        ci.imageFormat = t.format;
        ci.imageColorSpace = t.color_space;
        ci.imageExtent = {t.w, t.h};
        ci.imageArrayLayers = 1;
        ci.imageUsage = t.usage;
        ci.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
        ci.preTransform = caps.currentTransform;
        ci.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
        ci.presentMode = VK_PRESENT_MODE_FIFO_KHR;
        ci.clipped = VK_TRUE;
        ci.oldSwapchain = old;
        CHECK(vkCreateSwapchainKHR(g_dev, &ci, nullptr, &t.swapchain), "swapchain");
        if (old) vkDestroySwapchainKHR(g_dev, old, nullptr);
        uint32_t n = 0;
        CHECK(vkGetSwapchainImagesKHR(g_dev, t.swapchain, &n, nullptr), "images");
        t.images.resize(n);
        CHECK(vkGetSwapchainImagesKHR(g_dev, t.swapchain, &n, t.images.data()), "images");
    } else {
        for (int i = 0; i < count; i++) {
            VkImageCreateInfo ii{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
            ii.imageType = VK_IMAGE_TYPE_2D;
            ii.format = t.format;
            ii.extent = {t.w, t.h, 1};
            ii.mipLevels = 1;
            ii.arrayLayers = 1;
            ii.samples = VK_SAMPLE_COUNT_1_BIT;
            ii.tiling = VK_IMAGE_TILING_OPTIMAL;
            ii.usage = t.usage | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
            VkImage img = VK_NULL_HANDLE;
            CHECK(vkCreateImage(g_dev, &ii, nullptr, &img), "image");
            VkMemoryRequirements req{};
            vkGetImageMemoryRequirements(g_dev, img, &req);
            VkMemoryAllocateInfo mi{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
            mi.allocationSize = req.size;
            mi.memoryTypeIndex = mem_type(req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
            VkDeviceMemory m = VK_NULL_HANDLE;
            CHECK(vkAllocateMemory(g_dev, &mi, nullptr, &m), "image memory");
            CHECK(vkBindImageMemory(g_dev, img, m, 0), "image bind");
            t.images.push_back(img);
            t.memory.push_back(m);
        }
    }
    t.rendered.resize(t.images.size());
    t.shown.resize(t.images.size());
    VkSemaphoreCreateInfo sc{VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO};
    for (size_t i = 0; i < t.images.size(); i++) {
        CHECK(vkCreateSemaphore(g_dev, &sc, nullptr, &t.rendered[i]), "semaphore");
        CHECK(vkCreateSemaphore(g_dev, &sc, nullptr, &t.shown[i]), "semaphore");
    }
    CHECK(vkCreateSemaphore(g_dev, &sc, nullptr, &t.acquired), "semaphore");
    const VkDeviceSize need = VkDeviceSize(w) * h * 8;
    if (need > g_stage_size) {
        int r = make_stage(need, VK_BUFFER_USAGE_TRANSFER_SRC_BIT, &g_upload, &g_upload_mem, &g_upload_map);
        if (r) return r;
        r = make_stage(need, VK_BUFFER_USAGE_TRANSFER_DST_BIT, &g_download, &g_download_mem, &g_download_map);
        if (r) return r;
        g_stage_size = need;
    }
    t.fresh = true;
    return static_cast<int>(t.images.size());
}

uint32_t sg_play(const KilnScreenEffect* e) { return g_mixer.play(1, *e); }
int sg_stop(uint32_t h, int immediate) { return g_mixer.stop(h, immediate != 0) ? 0 : 1; }
int sg_set_strength(uint32_t h, float s) { return g_mixer.set_strength(h, s) ? 0 : 1; }
int sg_playing(uint32_t h) { return g_mixer.playing(h) ? 1 : 0; }
void sg_clear(void) { g_mixer.clear(); }
int sg_effect_size(void) { return static_cast<int>(sizeof(KilnScreenEffect)); }
int sg_frame_size(void) { return static_cast<int>(sizeof(kscreen::GpuFrame)); }

// One frame, as in a game: `pixels` are what the game rendered (bytes per pixel `bpp`), `out` what comes out, read back before the present. `now` is
// the mixer's clock. drew: the pass drew. Returns 0.
int sg_frame(const void* pixels, void* out, int bpp, double now, int* drew) {
    ALIVE();
    Target& t = g_target;
    const size_t size = size_t(t.w) * t.h * bpp;
    uint32_t index;
    VkSemaphore acquired = VK_NULL_HANDLE;
    if (t.swapchain) {
        VkResult ar = vkAcquireNextImageKHR(g_dev, t.swapchain, kWaitNs, t.acquired, VK_NULL_HANDLE, &index);
        if (ar != VK_SUCCESS && ar != VK_SUBOPTIMAL_KHR) return fail(ar, "acquire");
        acquired = t.acquired;
    } else {
        index = t.next;
        t.next = (t.next + 1) % t.images.size();
    }
    const VkImage image = t.images[index];
    memcpy(g_upload_map, pixels, size);

    // the game
    VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    CHECK(vkBeginCommandBuffer(g_cmd_game, &begin), "begin game");
    vkCmdResetQueryPool(g_cmd_game, g_queries, 0, 2);
    barrier(g_cmd_game, image, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkBufferImageCopy region{};
    region.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
    region.imageExtent = {t.w, t.h, 1};
    vkCmdCopyBufferToImage(g_cmd_game, g_upload, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
    barrier(g_cmd_game, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR, VK_ACCESS_TRANSFER_WRITE_BIT, 0, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT);
    CHECK(vkEndCommandBuffer(g_cmd_game), "end game");
    VkPipelineStageFlags stage = VK_PIPELINE_STAGE_ALL_COMMANDS_BIT;
    VkSubmitInfo si{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    si.waitSemaphoreCount = acquired ? 1 : 0;
    si.pWaitSemaphores = &acquired;
    si.pWaitDstStageMask = &stage;
    si.commandBufferCount = 1;
    si.pCommandBuffers = &g_cmd_game;
    si.signalSemaphoreCount = 1;
    si.pSignalSemaphores = &t.rendered[index];
    CHECK(vkQueueSubmit(g_queue, 1, &si, VK_NULL_HANDLE), "submit game");

    // what kiln.ui's present hook does
    void* wait_list[1] = {t.rendered[index]};
    KilnUiPreOverlay p{};
    p.size = sizeof p;
    p.image_index = index;
    p.instance = g_instance;
    p.physical_device = g_phys;
    p.device = g_dev;
    p.get_instance_proc_addr = reinterpret_cast<void*>(&vkGetInstanceProcAddr);
    p.get_device_proc_addr = reinterpret_cast<void*>(&harness_gdpa);
    p.queue = g_queue;
    p.swapchain = t.swapchain ? static_cast<void*>(t.swapchain) : static_cast<void*>(t.images[0]);
    p.image = image;
    p.wait_semaphores = wait_list;
    p.wait_count = 1;
    p.queue_family = g_family;
    p.format = static_cast<uint32_t>(t.format);
    p.color_space = static_cast<uint32_t>(t.color_space);
    p.width = t.w;
    p.height = t.h;
    p.usage = t.usage;

    const auto t0 = std::chrono::steady_clock::now();
    bool took = false;
    void* out_sem = nullptr;
    g_stamped = false;
    if (g_mixer.count() > 0 && g_pass->prepare(p)) {
        kscreen::GpuFrame frame;
        if (g_mixer.compose(now, frame) > 0) took = g_pass->present(p, frame, &out_sem);
    }
    g_last_cpu_us = std::chrono::duration<double, std::micro>(std::chrono::steady_clock::now() - t0).count();
    g_last_stamped = took;
    *drew = took ? 1 : 0;
    VkSemaphore wait = took ? static_cast<VkSemaphore>(out_sem) : t.rendered[index];

    // what comes out, read back; then the present
    CHECK(vkBeginCommandBuffer(g_cmd_read, &begin), "begin read");
    barrier(g_cmd_read, image, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, 0, VK_ACCESS_TRANSFER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    vkCmdCopyImageToBuffer(g_cmd_read, image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, g_download, 1, &region);
    barrier(g_cmd_read, image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_IMAGE_LAYOUT_PRESENT_SRC_KHR, VK_ACCESS_TRANSFER_READ_BIT, 0, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT);
    CHECK(vkEndCommandBuffer(g_cmd_read), "end read");
    VkPipelineStageFlags read_stage = VK_PIPELINE_STAGE_TRANSFER_BIT;
    VkSubmitInfo ri{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    ri.waitSemaphoreCount = 1;
    ri.pWaitSemaphores = &wait;
    ri.pWaitDstStageMask = &read_stage;
    ri.commandBufferCount = 1;
    ri.pCommandBuffers = &g_cmd_read;
    ri.signalSemaphoreCount = t.swapchain ? 1 : 0;
    ri.pSignalSemaphores = &t.shown[index];
    CHECK(vkQueueSubmit(g_queue, 1, &ri, g_fence), "submit read");
    const VkResult w = vkWaitForFences(g_dev, 1, &g_fence, VK_TRUE, kWaitNs);
    if (w != VK_SUCCESS) return fail(w, "frame fence");
    CHECK(vkResetFences(g_dev, 1, &g_fence), "reset fence");
    memcpy(out, g_download_map, size);
    if (took && g_queries) {
        uint64_t stamps[2] = {0, 0};
        if (vkGetQueryPoolResults(g_dev, g_queries, 0, 2, sizeof stamps, stamps, sizeof(uint64_t), VK_QUERY_RESULT_64_BIT) == VK_SUCCESS)
            g_last_gpu_us = static_cast<double>(stamps[1] - stamps[0]) * g_tick_ns / 1000.0;
    }
    if (t.swapchain) {
        VkPresentInfoKHR pi{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR};
        pi.waitSemaphoreCount = 1;
        pi.pWaitSemaphores = &t.shown[index];
        pi.swapchainCount = 1;
        pi.pSwapchains = &t.swapchain;
        pi.pImageIndices = &index;
        const VkResult pr = vkQueuePresentKHR(g_queue, &pi);
        if (pr != VK_SUCCESS && pr != VK_SUBOPTIMAL_KHR) return fail(pr, "present");
    }
    return 0;
}

double sg_cpu_us(void) { return g_last_cpu_us; }
double sg_gpu_us(void) { return g_last_gpu_us; }
int sg_pipeline_calls(void) { return g_pipeline_calls.load(); }

// Makes `function` return `result` from its next call but `after` on. result 0 takes it back.
void sg_inject(const char* function, int result, int after) {
    std::lock_guard<std::mutex> lock(g_inject_mu);
    Injected& i = g_inject[function];
    i.result = static_cast<VkResult>(result);
    i.after = after;
    i.on = result != 0;
}

// A new pass: what a fresh process would have. Everything of the old one goes first.
void sg_new_pass(void) {
    vkQueueWaitIdle(g_queue);
    g_pass->gone(g_dev, nullptr);
    g_pass = std::make_unique<kscreen::Pass>(&on_log);
    g_mixer.clear();
}

int sg_release(void) { return g_pass->release_when_idle() ? 1 : 0; }

// off, ready, transfer, frames, skipped, pipeline_ms; reason into `reason`.
void sg_status(int* off, int* ready, int* transfer, uint64_t* frames, uint64_t* skipped, double* pipeline_ms, char* reason, int cap) {
    const kscreen::PassStatus s = g_pass->status();
    *off = s.off;
    *ready = s.ready;
    *transfer = static_cast<int>(s.transfer);
    *frames = s.frames;
    *skipped = s.skipped;
    *pipeline_ms = s.pipeline_ms;
    if (cap > 0) snprintf(reason, cap, "%s", s.reason.c_str());
}

int sg_log(char* out, int cap) {
    std::lock_guard<std::mutex> lock(g_log_mu);
    if (cap > 0) snprintf(out, cap, "%s", g_log.c_str());
    return static_cast<int>(g_log.size());
}

void sg_shutdown(void) {
    if (!g_dev) return;
    vkQueueWaitIdle(g_queue);
    drop_target(false);
    g_pass->gone(g_dev, nullptr);
    g_pass.reset();
    if (g_upload) {
        vkDestroyBuffer(g_dev, g_upload, nullptr);
        vkFreeMemory(g_dev, g_upload_mem, nullptr);
    }
    if (g_download) {
        vkDestroyBuffer(g_dev, g_download, nullptr);
        vkFreeMemory(g_dev, g_download_mem, nullptr);
    }
    vkDestroyQueryPool(g_dev, g_queries, nullptr);
    vkDestroyFence(g_dev, g_fence, nullptr);
    vkDestroyCommandPool(g_dev, g_pool, nullptr);
    vkDestroyDevice(g_dev, nullptr);
    g_dev = VK_NULL_HANDLE;
    if (g_target.surface) vkDestroySurfaceKHR(g_instance, g_target.surface, nullptr);
    if (g_messenger) {
        auto destroy = reinterpret_cast<PFN_vkDestroyDebugUtilsMessengerEXT>(vkGetInstanceProcAddr(g_instance, "vkDestroyDebugUtilsMessengerEXT"));
        if (destroy) destroy(g_instance, g_messenger, nullptr);
    }
    vkDestroyInstance(g_instance, nullptr);
}

}  // extern "C"
