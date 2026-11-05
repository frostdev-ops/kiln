// SPDX-License-Identifier: MIT
// A small headless Vulkan runner for the VFX modules: no window, no swapchain, no game. Python drives it (test/gpu.py).
//
// Safety: every submit waits on a fence with a short timeout. A timeout or a lost device latches the library: every later
// call fails at once, and the caller is expected to end the process. Nothing retries.
#include <vulkan/vulkan.h>

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <vector>

namespace {

struct Buffer { VkBuffer buf = VK_NULL_HANDLE; VkDeviceMemory mem = VK_NULL_HANDLE; void* map = nullptr; VkDeviceSize size = 0; };
struct Image { VkImage img = VK_NULL_HANDLE; VkDeviceMemory mem = VK_NULL_HANDLE; VkImageView view = VK_NULL_HANDLE; VkFormat format = VK_FORMAT_UNDEFINED;
               uint32_t w = 0, h = 0, d = 1; uint32_t bytes = 4; VkImageAspectFlags aspect = VK_IMAGE_ASPECT_COLOR_BIT; };
struct Layout { VkPipelineLayout layout = VK_NULL_HANDLE; std::vector<VkDescriptorSetLayout> setLayouts; std::vector<VkDescriptorSet> sets;
                std::vector<std::vector<VkDescriptorType>> types; };

VkInstance g_instance = VK_NULL_HANDLE;
VkPhysicalDevice g_phys = VK_NULL_HANDLE;
VkDevice g_dev = VK_NULL_HANDLE;
VkQueue g_queue = VK_NULL_HANDLE;
uint32_t g_family = 0;
VkCommandPool g_pool = VK_NULL_HANDLE;
VkCommandBuffer g_cmd = VK_NULL_HANDLE;
VkFence g_fence = VK_NULL_HANDLE;
VkDescriptorPool g_descPool = VK_NULL_HANDLE;
VkSampler g_sampler = VK_NULL_HANDLE;
VkQueryPool g_query = VK_NULL_HANDLE;
double g_tickNs = 1.0;
VkPhysicalDeviceMemoryProperties g_memProps{};
int g_lost = 0;   // non-zero: latched. The VkResult that ended things.
std::vector<Buffer> g_buffers;
std::vector<Image> g_images;
std::vector<Layout> g_layouts;
std::vector<VkPipeline> g_pipelines;
VkPipelineBindPoint g_boundPoint = VK_PIPELINE_BIND_POINT_COMPUTE;
int g_boundLayout = -1;

// Validation messages, one record per message id and severity.
struct Finding { std::string severity, id, firstTag, firstText; int count = 0; };
std::mutex g_findMu;
std::map<std::string, Finding> g_findings, g_heldFindings;
std::string g_tag;
VkDebugUtilsMessengerEXT g_messenger = VK_NULL_HANDLE;
bool g_controlling = false;   // the control below: the layer keeps its invalid calls from the driver

VKAPI_ATTR VkBool32 VKAPI_CALL onValidation(VkDebugUtilsMessageSeverityFlagBitsEXT severity, VkDebugUtilsMessageTypeFlagsEXT,
                                            const VkDebugUtilsMessengerCallbackDataEXT* data, void*)
{
    std::lock_guard<std::mutex> lock(g_findMu);
    std::string sev = severity & VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT ? "error" : "warning";
    std::string id = data->pMessageIdName ? data->pMessageIdName : "unnamed";
    Finding& f = g_findings[sev + "|" + id];
    if (f.count++ == 0) {
        f.severity = sev;
        f.id = id;
        f.firstTag = g_tag;
        f.firstText = data->pMessage ? data->pMessage : "";
    }
    return g_controlling ? VK_TRUE : VK_FALSE;
}

int fail(VkResult r, const char* what)
{
    fprintf(stderr, "vfxgpu: %s: VkResult %d\n", what, (int)r);
    if (r == VK_ERROR_DEVICE_LOST || r == VK_TIMEOUT) g_lost = (int)r;
    return (int)r == 0 ? -1 : (int)r;
}
#define CHECK(call, what) do { VkResult r_ = (call); if (r_ != VK_SUCCESS) return fail(r_, what); } while (0)
#define ALIVE() do { if (g_lost) return g_lost; } while (0)

uint32_t memType(uint32_t bits, VkMemoryPropertyFlags want, VkMemoryPropertyFlags fallback)
{
    for (int pass = 0; pass < 2; pass++) {
        VkMemoryPropertyFlags f = pass == 0 ? want : fallback;
        for (uint32_t i = 0; i < g_memProps.memoryTypeCount; i++)
            if ((bits & (1u << i)) && (g_memProps.memoryTypes[i].propertyFlags & f) == f)
                return i;
    }
    return UINT32_MAX;
}

int oneShot(void (*record)(VkCommandBuffer, void*), void* user, uint32_t timeoutMs)
{
    ALIVE();
    CHECK(vkResetCommandBuffer(g_cmd, 0), "reset");
    VkCommandBufferBeginInfo bi{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    CHECK(vkBeginCommandBuffer(g_cmd, &bi), "begin");
    record(g_cmd, user);
    CHECK(vkEndCommandBuffer(g_cmd), "end");
    VkSubmitInfo si{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    si.commandBufferCount = 1;
    si.pCommandBuffers = &g_cmd;
    CHECK(vkQueueSubmit(g_queue, 1, &si, g_fence), "submit");
    VkResult w = vkWaitForFences(g_dev, 1, &g_fence, VK_TRUE, uint64_t(timeoutMs) * 1000000ull);
    if (w != VK_SUCCESS) return fail(w, "fence wait");
    CHECK(vkResetFences(g_dev, 1, &g_fence), "reset fence");
    return 0;
}

void imageBarrier(VkCommandBuffer c, const Image& im, VkImageLayout from, VkImageLayout to, VkAccessFlags sa, VkAccessFlags da,
                  VkPipelineStageFlags ss, VkPipelineStageFlags ds)
{
    VkImageMemoryBarrier b{VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER};
    b.oldLayout = from; b.newLayout = to; b.srcAccessMask = sa; b.dstAccessMask = da;
    b.srcQueueFamilyIndex = b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = im.img;
    b.subresourceRange = {im.aspect, 0, VK_REMAINING_MIP_LEVELS, 0, 1};
    vkCmdPipelineBarrier(c, ss, ds, 0, 0, nullptr, 0, nullptr, 1, &b);
}

}  // namespace

extern "C" {

int vg_lost(void) { return g_lost; }

// Labels later validation messages with what the caller is doing.
void vg_tag(const char* tag)
{
    std::lock_guard<std::mutex> lock(g_findMu);
    g_tag = tag ? tag : "";
}

// Writes findings as lines: severity TAB count TAB id TAB first tag TAB first message (tabs and newlines in text become spaces).
int vg_validation_report(char* out, int cap)
{
    std::lock_guard<std::mutex> lock(g_findMu);
    std::string all;
    for (auto& kv : g_findings) {
        const Finding& f = kv.second;
        std::string text = f.firstText;
        for (char& c : text)
            if (c == '\t' || c == '\n' || c == '\r') c = ' ';
        all += f.severity + "\t" + std::to_string(f.count) + "\t" + f.id + "\t" + f.firstTag + "\t" + text + "\n";
    }
    if (cap > 0) {
        size_t n = all.size() < size_t(cap - 1) ? all.size() : size_t(cap - 1);
        memcpy(out, all.data(), n);
        out[n] = 0;
    }
    return (int)all.size();
}

// hold 1: sets the findings so far aside, so a control's own messages stay apart from them. hold 0: forgets what came since, puts them back.
void vg_validation_hold(int hold)
{
    std::lock_guard<std::mutex> lock(g_findMu);
    if (hold) {
        g_heldFindings.swap(g_findings);
        g_findings.clear();
    } else {
        g_findings.swap(g_heldFindings);
        g_heldFindings.clear();
    }
}

// Picks the device whose name contains `name`. Enables what the game's log shows it enables: robust buffer and image access,
// null descriptors, scalar layout, descriptor indexing, plus whatever else the device offers among the 1.1-1.3 feature sets.
//
// validate: 0 off. 1 the Khronos layer with synchronization validation. 2 the layer with GPU-assisted validation, which
// instruments shaders to catch out-of-bounds access; plain robustness is then off, or it would hide those accesses.
// Returns -10 when the layer isn't installed.
int vg_init(const char* name, int validate, char* info, int cap)
{
    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
    app.pApplicationName = "kiln-vfxgpu";
    app.apiVersion = VK_API_VERSION_1_3;
    VkInstanceCreateInfo ici{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
    ici.pApplicationInfo = &app;
    const char* layer = "VK_LAYER_KHRONOS_validation";
    const char* instExts[] = {VK_EXT_DEBUG_UTILS_EXTENSION_NAME, VK_EXT_VALIDATION_FEATURES_EXTENSION_NAME};
    VkValidationFeatureEnableEXT enables[2];
    VkValidationFeaturesEXT features{VK_STRUCTURE_TYPE_VALIDATION_FEATURES_EXT};
    VkDebugUtilsMessengerCreateInfoEXT mi{VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT};
    if (validate) {
        uint32_t ln = 0;
        vkEnumerateInstanceLayerProperties(&ln, nullptr);
        std::vector<VkLayerProperties> layers(ln);
        vkEnumerateInstanceLayerProperties(&ln, layers.data());
        bool found = false;
        for (auto& l : layers)
            found = found || strcmp(l.layerName, layer) == 0;
        if (!found) return -10;
        uint32_t en = 0;
        if (validate == 2) {
            enables[en++] = VK_VALIDATION_FEATURE_ENABLE_GPU_ASSISTED_EXT;
            enables[en++] = VK_VALIDATION_FEATURE_ENABLE_GPU_ASSISTED_RESERVE_BINDING_SLOT_EXT;
        } else {
            enables[en++] = VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT;
        }
        features.enabledValidationFeatureCount = en;
        features.pEnabledValidationFeatures = enables;
        mi.messageSeverity = VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_SEVERITY_WARNING_BIT_EXT;
        mi.messageType = VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT | VK_DEBUG_UTILS_MESSAGE_TYPE_PERFORMANCE_BIT_EXT;
        mi.pfnUserCallback = onValidation;
        mi.pNext = &features;
        ici.pNext = &mi;
        ici.enabledLayerCount = 1;
        ici.ppEnabledLayerNames = &layer;
        ici.enabledExtensionCount = 2;
        ici.ppEnabledExtensionNames = instExts;
    }
    CHECK(vkCreateInstance(&ici, nullptr, &g_instance), "instance");
    if (validate) {
        auto create = (PFN_vkCreateDebugUtilsMessengerEXT)vkGetInstanceProcAddr(g_instance, "vkCreateDebugUtilsMessengerEXT");
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
        if (strstr(props.deviceName, name)) { g_phys = d; break; }
    }
    if (!g_phys) return -2;
    vkGetPhysicalDeviceMemoryProperties(g_phys, &g_memProps);
    g_tickNs = props.limits.timestampPeriod;

    uint32_t qn = 0;
    vkGetPhysicalDeviceQueueFamilyProperties(g_phys, &qn, nullptr);
    std::vector<VkQueueFamilyProperties> qf(qn);
    vkGetPhysicalDeviceQueueFamilyProperties(g_phys, &qn, qf.data());
    g_family = UINT32_MAX;
    for (uint32_t i = 0; i < qn; i++)
        if ((qf[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) && (qf[i].queueFlags & VK_QUEUE_COMPUTE_BIT)) { g_family = i; break; }
    if (g_family == UINT32_MAX) return -3;

    VkPhysicalDeviceRobustness2FeaturesEXT rb{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ROBUSTNESS_2_FEATURES_EXT};
    VkPhysicalDeviceVulkan13Features f13{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES};
    f13.pNext = &rb;
    VkPhysicalDeviceVulkan12Features f12{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES};
    f12.pNext = &f13;
    VkPhysicalDeviceVulkan11Features f11{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_1_FEATURES};
    f11.pNext = &f12;
    VkPhysicalDeviceFeatures2 f2{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2};
    f2.pNext = &f11;
    vkGetPhysicalDeviceFeatures2(g_phys, &f2);
    // As the game: plain robustness, not the stricter robustness2 forms.
    rb.robustBufferAccess2 = VK_FALSE;
    rb.robustImageAccess2 = VK_FALSE;
    f2.features.robustBufferAccess = validate == 2 ? VK_FALSE : VK_TRUE;
    f13.robustImageAccess = validate == 2 ? VK_FALSE : VK_TRUE;
    f12.bufferDeviceAddressCaptureReplay = VK_FALSE;
    f12.bufferDeviceAddressMultiDevice = VK_FALSE;
    f2.features.sparseBinding = VK_FALSE;
    f2.features.sparseResidencyBuffer = VK_FALSE;
    f2.features.sparseResidencyImage2D = VK_FALSE;
    f2.features.sparseResidencyImage3D = VK_FALSE;
    f2.features.sparseResidency2Samples = VK_FALSE;
    f2.features.sparseResidency4Samples = VK_FALSE;
    f2.features.sparseResidency8Samples = VK_FALSE;
    f2.features.sparseResidency16Samples = VK_FALSE;
    f2.features.sparseResidencyAliased = VK_FALSE;

    float prio = 1.0f;
    VkDeviceQueueCreateInfo qi{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
    qi.queueFamilyIndex = g_family;
    qi.queueCount = 1;
    qi.pQueuePriorities = &prio;
    const char* exts[] = {VK_EXT_ROBUSTNESS_2_EXTENSION_NAME};
    VkDeviceCreateInfo di{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
    di.pNext = &f2;
    di.queueCreateInfoCount = 1;
    di.pQueueCreateInfos = &qi;
    di.enabledExtensionCount = 1;
    di.ppEnabledExtensionNames = exts;
    CHECK(vkCreateDevice(g_phys, &di, nullptr, &g_dev), "device");
    vkGetDeviceQueue(g_dev, g_family, 0, &g_queue);

    VkCommandPoolCreateInfo pi{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
    pi.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
    pi.queueFamilyIndex = g_family;
    CHECK(vkCreateCommandPool(g_dev, &pi, nullptr, &g_pool), "pool");
    VkCommandBufferAllocateInfo ai{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
    ai.commandPool = g_pool;
    ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
    ai.commandBufferCount = 1;
    CHECK(vkAllocateCommandBuffers(g_dev, &ai, &g_cmd), "cmd");
    VkFenceCreateInfo fi{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    CHECK(vkCreateFence(g_dev, &fi, nullptr, &g_fence), "fence");
    VkDescriptorPoolSize sizes[] = {{VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, 512}, {VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 2048}, {VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, 70000},
                                    {VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, 512}, {VK_DESCRIPTOR_TYPE_SAMPLER, 2048}};
    VkDescriptorPoolCreateInfo dp{VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO};
    dp.maxSets = 128;
    dp.poolSizeCount = 5;
    dp.pPoolSizes = sizes;
    CHECK(vkCreateDescriptorPool(g_dev, &dp, nullptr, &g_descPool), "descriptor pool");
    VkSamplerCreateInfo sa{VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO};
    sa.magFilter = sa.minFilter = VK_FILTER_LINEAR;
    sa.mipmapMode = VK_SAMPLER_MIPMAP_MODE_LINEAR;
    sa.addressModeU = sa.addressModeV = sa.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sa.maxLod = VK_LOD_CLAMP_NONE;
    CHECK(vkCreateSampler(g_dev, &sa, nullptr, &g_sampler), "sampler");
    VkQueryPoolCreateInfo qp{VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO};
    qp.queryType = VK_QUERY_TYPE_TIMESTAMP;
    qp.queryCount = 2;
    CHECK(vkCreateQueryPool(g_dev, &qp, nullptr, &g_query), "query pool");

    if (info && cap > 0) {
        VkPhysicalDeviceSubgroupProperties sub{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SUBGROUP_PROPERTIES};
        VkPhysicalDeviceProperties2 p2{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2};
        p2.pNext = &sub;
        vkGetPhysicalDeviceProperties2(g_phys, &p2);
        snprintf(info, cap, "%s; api %u.%u.%u; driver 0x%x; queue family %u; subgroup size %u; robustBufferAccess %d, nullDescriptor %u, scalarBlockLayout %u; validation %d",
                 props.deviceName, VK_API_VERSION_MAJOR(props.apiVersion), VK_API_VERSION_MINOR(props.apiVersion), VK_API_VERSION_PATCH(props.apiVersion),
                 props.driverVersion, g_family, sub.subgroupSize, validate == 2 ? 0 : 1, rb.nullDescriptor, f12.scalarBlockLayout, validate);
    }
    return 0;
}

// ---- buffers ----------------------------------------------------------------------------------------------------------
int vg_buffer(uint64_t size)
{
    ALIVE();
    Buffer b;
    b.size = size ? size : 4;
    VkBufferCreateInfo ci{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
    ci.size = b.size;
    ci.usage = VK_BUFFER_USAGE_STORAGE_BUFFER_BIT | VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT | VK_BUFFER_USAGE_INDIRECT_BUFFER_BIT |
               VK_BUFFER_USAGE_INDEX_BUFFER_BIT | VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT;
    CHECK(vkCreateBuffer(g_dev, &ci, nullptr, &b.buf), "buffer");
    VkMemoryRequirements mr{};
    vkGetBufferMemoryRequirements(g_dev, b.buf, &mr);
    VkMemoryAllocateInfo ai{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    ai.allocationSize = mr.size;
    ai.memoryTypeIndex = memType(mr.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT | VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT,
                                 VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    if (ai.memoryTypeIndex == UINT32_MAX) return -4;
    CHECK(vkAllocateMemory(g_dev, &ai, nullptr, &b.mem), "buffer memory");
    CHECK(vkBindBufferMemory(g_dev, b.buf, b.mem, 0), "bind buffer");
    CHECK(vkMapMemory(g_dev, b.mem, 0, VK_WHOLE_SIZE, 0, &b.map), "map");
    memset(b.map, 0, (size_t)b.size);
    g_buffers.push_back(b);
    return (int)g_buffers.size() - 1;
}
void* vg_buffer_map(int id) { return g_buffers[id].map; }

// Frees a buffer once nothing in flight uses it (all submits here wait on their fence).
void vg_buffer_free(int id)
{
    Buffer& b = g_buffers[id];
    if (b.buf) { vkDestroyBuffer(g_dev, b.buf, nullptr); vkFreeMemory(g_dev, b.mem, nullptr); b = Buffer{}; }
}

// Breaks two rules on purpose, so that "no findings" means something. The callback tells the layer to drop each invalid call, and the
// command buffer is thrown away unsubmitted. Returns bit 0 if the layer reported the core rule, bit 1 the synchronization rule.
int vg_validation_control(void)
{
    ALIVE();
    int id = vg_buffer(64);
    if (id < 0) return id;
    vg_validation_hold(1);
    VkBuffer b = g_buffers[id].buf;
    CHECK(vkResetCommandBuffer(g_cmd, 0), "reset");
    VkCommandBufferBeginInfo bi{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    CHECK(vkBeginCommandBuffer(g_cmd, &bi), "begin");
    vkCmdFillBuffer(g_cmd, b, 0, 16, 0);
    g_controlling = true;
    vkCmdFillBuffer(g_cmd, b, 0, 16, 0);   // write after write, no barrier
    vkCmdFillBuffer(g_cmd, b, 1, 16, 0);   // offset not a multiple of 4
    g_controlling = false;
    CHECK(vkEndCommandBuffer(g_cmd), "end");
    CHECK(vkResetCommandBuffer(g_cmd, 0), "reset");
    vg_buffer_free(id);
    int seen = 0;
    {
        std::lock_guard<std::mutex> lock(g_findMu);
        for (auto& kv : g_findings) {
            if (kv.second.id.rfind("VUID-", 0) == 0) seen |= 1;
            if (kv.second.id.rfind("SYNC-", 0) == 0) seen |= 2;
        }
    }
    vg_validation_hold(0);
    return seen;
}

// ---- pipeline layouts -------------------------------------------------------------------------------------------------
// `types` etc are flat arrays, `setCounts[i]` entries per set. flags: 1 = partially bound, 2 = an immutable sampler.
int vg_layout(int nSets, const int* setCounts, const int* types, const int* counts, const int* flags, int pushSize)
{
    ALIVE();
    Layout L;
    int cursor = 0;
    for (int s = 0; s < nSets; s++) {
        std::vector<VkDescriptorSetLayoutBinding> b(setCounts[s]);
        std::vector<VkDescriptorBindingFlags> bf(setCounts[s]);
        std::vector<VkDescriptorType> ty(setCounts[s]);
        for (int i = 0; i < setCounts[s]; i++, cursor++) {
            b[i].binding = i;
            b[i].descriptorType = (VkDescriptorType)types[cursor];
            b[i].descriptorCount = counts[cursor];
            b[i].stageFlags = VK_SHADER_STAGE_ALL;
            b[i].pImmutableSamplers = (flags[cursor] & 2) ? &g_sampler : nullptr;
            bf[i] = (flags[cursor] & 1) ? VK_DESCRIPTOR_BINDING_PARTIALLY_BOUND_BIT : 0;
            ty[i] = (VkDescriptorType)types[cursor];
        }
        VkDescriptorSetLayoutBindingFlagsCreateInfo fi{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_BINDING_FLAGS_CREATE_INFO};
        fi.bindingCount = (uint32_t)bf.size();
        fi.pBindingFlags = bf.data();
        VkDescriptorSetLayoutCreateInfo ci{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO};
        ci.pNext = &fi;
        ci.bindingCount = (uint32_t)b.size();
        ci.pBindings = b.data();
        VkDescriptorSetLayout sl;
        CHECK(vkCreateDescriptorSetLayout(g_dev, &ci, nullptr, &sl), "set layout");
        L.setLayouts.push_back(sl);
        L.types.push_back(ty);
        VkDescriptorSetAllocateInfo ai{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO};
        ai.descriptorPool = g_descPool;
        ai.descriptorSetCount = 1;
        ai.pSetLayouts = &sl;
        VkDescriptorSet ds;
        CHECK(vkAllocateDescriptorSets(g_dev, &ai, &ds), "descriptor set");
        L.sets.push_back(ds);
    }
    VkPushConstantRange pr{VK_SHADER_STAGE_ALL, 0, (uint32_t)pushSize};
    VkPipelineLayoutCreateInfo pl{VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO};
    pl.setLayoutCount = (uint32_t)L.setLayouts.size();
    pl.pSetLayouts = L.setLayouts.data();
    pl.pushConstantRangeCount = pushSize ? 1 : 0;
    pl.pPushConstantRanges = &pr;
    CHECK(vkCreatePipelineLayout(g_dev, &pl, nullptr, &L.layout), "pipeline layout");
    g_layouts.push_back(L);
    return (int)g_layouts.size() - 1;
}

// buf < 0: a null descriptor.
int vg_bind_buffer(int layout, int set, int binding, int buf)
{
    ALIVE();
    Layout& L = g_layouts[layout];
    VkDescriptorBufferInfo bi{};
    bi.buffer = buf < 0 ? VK_NULL_HANDLE : g_buffers[buf].buf;
    bi.offset = 0;
    bi.range = VK_WHOLE_SIZE;
    VkWriteDescriptorSet w{VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET};
    w.dstSet = L.sets[set];
    w.dstBinding = binding;
    w.descriptorCount = 1;
    w.descriptorType = L.types[set][binding];
    w.pBufferInfo = &bi;
    vkUpdateDescriptorSets(g_dev, 1, &w, 0, nullptr);
    return 0;
}

// image < 0: a null descriptor.
int vg_bind_image(int layout, int set, int binding, int element, int image)
{
    ALIVE();
    Layout& L = g_layouts[layout];
    VkDescriptorImageInfo ii{};
    ii.imageView = image < 0 ? VK_NULL_HANDLE : g_images[image].view;
    VkDescriptorType t = L.types[set][binding];
    ii.imageLayout = t == VK_DESCRIPTOR_TYPE_STORAGE_IMAGE ? VK_IMAGE_LAYOUT_GENERAL : VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    VkWriteDescriptorSet w{VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET};
    w.dstSet = L.sets[set];
    w.dstBinding = binding;
    w.dstArrayElement = element;
    w.descriptorCount = 1;
    w.descriptorType = t;
    w.pImageInfo = &ii;
    vkUpdateDescriptorSets(g_dev, 1, &w, 0, nullptr);
    return 0;
}

int vg_bind_sampler(int layout, int set, int binding, int element)
{
    ALIVE();
    Layout& L = g_layouts[layout];
    VkDescriptorImageInfo ii{};
    ii.sampler = g_sampler;
    VkWriteDescriptorSet w{VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET};
    w.dstSet = L.sets[set];
    w.dstBinding = binding;
    w.dstArrayElement = element;
    w.descriptorCount = 1;
    w.descriptorType = VK_DESCRIPTOR_TYPE_SAMPLER;
    w.pImageInfo = &ii;
    vkUpdateDescriptorSets(g_dev, 1, &w, 0, nullptr);
    return 0;
}

// ---- pipelines --------------------------------------------------------------------------------------------------------
static int shaderModule(const void* code, size_t size, VkShaderModule* out)
{
    VkShaderModuleCreateInfo ci{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO};
    ci.codeSize = size;
    ci.pCode = (const uint32_t*)code;
    CHECK(vkCreateShaderModule(g_dev, &ci, nullptr, out), "shader module");
    return 0;
}

int vg_compute_pipeline(int layout, const void* code, size_t size, const char* entry, double* ms)
{
    ALIVE();
    auto t0 = std::chrono::steady_clock::now();
    VkShaderModule m;
    int r = shaderModule(code, size, &m);
    if (r) return r;
    VkComputePipelineCreateInfo ci{VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO};
    ci.stage = {VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO};
    ci.stage.stage = VK_SHADER_STAGE_COMPUTE_BIT;
    ci.stage.module = m;
    ci.stage.pName = entry;
    ci.layout = g_layouts[layout].layout;
    VkPipeline p;
    VkResult cr = vkCreateComputePipelines(g_dev, VK_NULL_HANDLE, 1, &ci, nullptr, &p);
    if (ms) *ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    vkDestroyShaderModule(g_dev, m, nullptr);
    if (cr != VK_SUCCESS) return fail(cr, "compute pipeline") < 0 ? -(int)g_pipelines.size() - 1000 : -1;
    g_pipelines.push_back(p);
    return (int)g_pipelines.size() - 1;
}

// A graphics pipeline for dynamic rendering. Blend per target: {enable, srcColor, dstColor, srcAlpha, dstAlpha}, VkBlendFactor values, add ops.
int vg_graphics_pipeline(int layout, const void* vs, size_t vsSize, const char* vsEntry, const void* fs, size_t fsSize, const char* fsEntry,
                         int nTargets, const int* formats, const int* blend, int depthFormat, int depthCompare, int depthWrite,
                         int cullMode, int frontFace, double* ms)
{
    ALIVE();
    auto t0 = std::chrono::steady_clock::now();
    VkShaderModule mv, mf;
    int r = shaderModule(vs, vsSize, &mv);
    if (r) return r;
    r = shaderModule(fs, fsSize, &mf);
    if (r) return r;
    VkPipelineShaderStageCreateInfo stages[2] = {{VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO}, {VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO}};
    stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT;
    stages[0].module = mv;
    stages[0].pName = vsEntry;
    stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT;
    stages[1].module = mf;
    stages[1].pName = fsEntry;
    VkPipelineVertexInputStateCreateInfo vi{VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO};
    VkPipelineInputAssemblyStateCreateInfo ia{VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO};
    ia.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    VkPipelineViewportStateCreateInfo vp{VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO};
    vp.viewportCount = vp.scissorCount = 1;
    VkPipelineRasterizationStateCreateInfo rs{VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO};
    rs.polygonMode = VK_POLYGON_MODE_FILL;
    rs.cullMode = (VkCullModeFlags)cullMode;
    rs.frontFace = (VkFrontFace)frontFace;
    rs.lineWidth = 1.0f;
    VkPipelineMultisampleStateCreateInfo ms_{VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO};
    ms_.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
    VkPipelineDepthStencilStateCreateInfo ds{VK_STRUCTURE_TYPE_PIPELINE_DEPTH_STENCIL_STATE_CREATE_INFO};
    ds.depthTestEnable = VK_TRUE;
    ds.depthWriteEnable = depthWrite ? VK_TRUE : VK_FALSE;
    ds.depthCompareOp = (VkCompareOp)depthCompare;
    std::vector<VkPipelineColorBlendAttachmentState> att(nTargets);
    std::vector<VkFormat> fmts(nTargets);
    for (int i = 0; i < nTargets; i++) {
        const int* b = blend + i * 5;
        att[i].blendEnable = b[0] ? VK_TRUE : VK_FALSE;
        att[i].srcColorBlendFactor = (VkBlendFactor)b[1];
        att[i].dstColorBlendFactor = (VkBlendFactor)b[2];
        att[i].srcAlphaBlendFactor = (VkBlendFactor)b[3];
        att[i].dstAlphaBlendFactor = (VkBlendFactor)b[4];
        att[i].colorBlendOp = att[i].alphaBlendOp = VK_BLEND_OP_ADD;
        att[i].colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT | VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
        fmts[i] = (VkFormat)formats[i];
    }
    VkPipelineColorBlendStateCreateInfo cb{VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO};
    cb.attachmentCount = (uint32_t)att.size();
    cb.pAttachments = att.data();
    VkDynamicState dyn[] = {VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR};
    VkPipelineDynamicStateCreateInfo dy{VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO};
    dy.dynamicStateCount = 2;
    dy.pDynamicStates = dyn;
    VkPipelineRenderingCreateInfo ri{VK_STRUCTURE_TYPE_PIPELINE_RENDERING_CREATE_INFO};
    ri.colorAttachmentCount = (uint32_t)fmts.size();
    ri.pColorAttachmentFormats = fmts.data();
    ri.depthAttachmentFormat = (VkFormat)depthFormat;
    VkGraphicsPipelineCreateInfo ci{VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO};
    ci.pNext = &ri;
    ci.stageCount = 2;
    ci.pStages = stages;
    ci.pVertexInputState = &vi;
    ci.pInputAssemblyState = &ia;
    ci.pViewportState = &vp;
    ci.pRasterizationState = &rs;
    ci.pMultisampleState = &ms_;
    ci.pDepthStencilState = &ds;
    ci.pColorBlendState = &cb;
    ci.pDynamicState = &dy;
    ci.layout = g_layouts[layout].layout;
    VkPipeline p;
    VkResult cr = vkCreateGraphicsPipelines(g_dev, VK_NULL_HANDLE, 1, &ci, nullptr, &p);
    if (ms) *ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
    vkDestroyShaderModule(g_dev, mv, nullptr);
    vkDestroyShaderModule(g_dev, mf, nullptr);
    if (cr != VK_SUCCESS) { fail(cr, "graphics pipeline"); return -1000; }
    g_pipelines.push_back(p);
    return (int)g_pipelines.size() - 1;
}

// ---- images -----------------------------------------------------------------------------------------------------------
// kind 0: sampled, uploaded from pixels. kind 1: colour target. kind 2: depth target.
int vg_image(uint32_t w, uint32_t h, uint32_t d, int format, uint32_t bytesPerPixel, int kind, const void* pixels)
{
    ALIVE();
    Image im;
    im.format = (VkFormat)format;
    im.w = w; im.h = h; im.d = d; im.bytes = bytesPerPixel;
    im.aspect = kind == 2 ? VK_IMAGE_ASPECT_DEPTH_BIT : VK_IMAGE_ASPECT_COLOR_BIT;
    VkImageCreateInfo ci{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    ci.imageType = d > 1 ? VK_IMAGE_TYPE_3D : VK_IMAGE_TYPE_2D;
    ci.format = im.format;
    ci.extent = {w, h, d};
    ci.mipLevels = ci.arrayLayers = 1;
    ci.samples = VK_SAMPLE_COUNT_1_BIT;
    ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = kind == 0 ? (VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT)
             : kind == 1 ? (VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT)
                         : VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT;
    CHECK(vkCreateImage(g_dev, &ci, nullptr, &im.img), "image");
    VkMemoryRequirements mr{};
    vkGetImageMemoryRequirements(g_dev, im.img, &mr);
    VkMemoryAllocateInfo ai{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    ai.allocationSize = mr.size;
    ai.memoryTypeIndex = memType(mr.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, 0);
    CHECK(vkAllocateMemory(g_dev, &ai, nullptr, &im.mem), "image memory");
    CHECK(vkBindImageMemory(g_dev, im.img, im.mem, 0), "bind image");
    VkImageViewCreateInfo vi{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
    vi.image = im.img;
    vi.viewType = d > 1 ? VK_IMAGE_VIEW_TYPE_3D : VK_IMAGE_VIEW_TYPE_2D;
    vi.format = im.format;
    vi.subresourceRange = {im.aspect, 0, 1, 0, 1};
    CHECK(vkCreateImageView(g_dev, &vi, nullptr, &im.view), "image view");
    g_images.push_back(im);
    int id = (int)g_images.size() - 1;
    if (kind == 0) {
        int stagingId = vg_buffer(uint64_t(w) * h * d * bytesPerPixel);
        if (stagingId < 0) return stagingId;
        memcpy(g_buffers[stagingId].map, pixels, size_t(w) * h * d * bytesPerPixel);
        struct Ctx { Image im; VkBuffer b; } ctx{im, g_buffers[stagingId].buf};
        int r = oneShot([](VkCommandBuffer c, void* u) {
            Ctx& x = *(Ctx*)u;
            imageBarrier(c, x.im, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT,
                         VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
            VkBufferImageCopy rg{};
            rg.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
            rg.imageExtent = {x.im.w, x.im.h, x.im.d};
            vkCmdCopyBufferToImage(c, x.b, x.im.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &rg);
            imageBarrier(c, x.im, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_ACCESS_TRANSFER_WRITE_BIT,
                         VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT);
        }, &ctx, 2000);
        vg_buffer_free(stagingId);
        if (r) return r;
    }
    return id;
}

// A BC7 texture with its whole mip chain, as cooked: 16 bytes per 4x4 block, mips largest first. -11 when `size` isn't that chain's size.
int vg_image_bc7(uint32_t w, uint32_t h, int format, uint32_t levels, const void* data, uint64_t size)
{
    ALIVE();
    std::vector<VkBufferImageCopy> regions;
    uint64_t offset = 0;
    for (uint32_t l = 0, mw = w, mh = h; l < levels; l++, mw = mw > 1 ? mw / 2 : 1, mh = mh > 1 ? mh / 2 : 1) {
        VkBufferImageCopy rg{};
        rg.bufferOffset = offset;
        rg.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, l, 0, 1};
        rg.imageExtent = {mw, mh, 1};
        regions.push_back(rg);
        offset += uint64_t((mw + 3) / 4) * ((mh + 3) / 4) * 16;
    }
    if (offset != size) return -11;
    Image im;
    im.format = (VkFormat)format;
    im.w = w; im.h = h; im.bytes = 1;
    VkImageCreateInfo ci{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    ci.imageType = VK_IMAGE_TYPE_2D;
    ci.format = im.format;
    ci.extent = {w, h, 1};
    ci.mipLevels = levels;
    ci.arrayLayers = 1;
    ci.samples = VK_SAMPLE_COUNT_1_BIT;
    ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    CHECK(vkCreateImage(g_dev, &ci, nullptr, &im.img), "image");
    VkMemoryRequirements mr{};
    vkGetImageMemoryRequirements(g_dev, im.img, &mr);
    VkMemoryAllocateInfo ai{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    ai.allocationSize = mr.size;
    ai.memoryTypeIndex = memType(mr.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, 0);
    CHECK(vkAllocateMemory(g_dev, &ai, nullptr, &im.mem), "image memory");
    CHECK(vkBindImageMemory(g_dev, im.img, im.mem, 0), "bind image");
    VkImageViewCreateInfo vi{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
    vi.image = im.img;
    vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
    vi.format = im.format;
    vi.subresourceRange = {im.aspect, 0, levels, 0, 1};
    CHECK(vkCreateImageView(g_dev, &vi, nullptr, &im.view), "image view");
    g_images.push_back(im);
    int id = (int)g_images.size() - 1;
    int stagingId = vg_buffer(size);
    if (stagingId < 0) return stagingId;
    memcpy(g_buffers[stagingId].map, data, (size_t)size);
    struct Ctx { Image im; VkBuffer b; const std::vector<VkBufferImageCopy>* regions; } ctx{im, g_buffers[stagingId].buf, &regions};
    int r = oneShot([](VkCommandBuffer c, void* u) {
        Ctx& x = *(Ctx*)u;
        imageBarrier(c, x.im, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT,
                     VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
        vkCmdCopyBufferToImage(c, x.b, x.im.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, (uint32_t)x.regions->size(), x.regions->data());
        imageBarrier(c, x.im, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_ACCESS_TRANSFER_WRITE_BIT,
                     VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT);
    }, &ctx, 2000);
    vg_buffer_free(stagingId);
    return r ? r : id;
}

// Copies a colour target (left in TRANSFER_SRC by vg_cmd_render_end) out to memory.
int vg_read_image(int id, void* out)
{
    ALIVE();
    Image& im = g_images[id];
    int stagingId = vg_buffer(uint64_t(im.w) * im.h * im.bytes);
    if (stagingId < 0) return stagingId;
    struct Ctx { Image im; VkBuffer b; } ctx{im, g_buffers[stagingId].buf};
    int r = oneShot([](VkCommandBuffer c, void* u) {
        Ctx& x = *(Ctx*)u;
        VkBufferImageCopy rg{};
        rg.imageSubresource = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1};
        rg.imageExtent = {x.im.w, x.im.h, 1};
        vkCmdCopyImageToBuffer(c, x.im.img, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, x.b, 1, &rg);
        VkMemoryBarrier mb{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
        mb.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        mb.dstAccessMask = VK_ACCESS_HOST_READ_BIT;
        vkCmdPipelineBarrier(c, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT, 0, 1, &mb, 0, nullptr, 0, nullptr);
    }, &ctx, 2000);
    if (r == 0) memcpy(out, g_buffers[stagingId].map, size_t(im.w) * im.h * im.bytes);
    vg_buffer_free(stagingId);
    return r;
}

// ---- recording --------------------------------------------------------------------------------------------------------
int vg_begin(void)
{
    ALIVE();
    CHECK(vkResetCommandBuffer(g_cmd, 0), "reset");
    VkCommandBufferBeginInfo bi{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    CHECK(vkBeginCommandBuffer(g_cmd, &bi), "begin");
    vkCmdResetQueryPool(g_cmd, g_query, 0, 2);
    vkCmdWriteTimestamp(g_cmd, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, g_query, 0);
    return 0;
}

static void bindSets(int layout, VkPipelineBindPoint point)
{
    Layout& L = g_layouts[layout];
    vkCmdBindDescriptorSets(g_cmd, point, L.layout, 0, (uint32_t)L.sets.size(), L.sets.data(), 0, nullptr);
    g_boundPoint = point;
    g_boundLayout = layout;
}

void vg_cmd_bind_compute(int pipeline, int layout)
{
    vkCmdBindPipeline(g_cmd, VK_PIPELINE_BIND_POINT_COMPUTE, g_pipelines[pipeline]);
    bindSets(layout, VK_PIPELINE_BIND_POINT_COMPUTE);
}

void vg_cmd_push(int layout, const void* data, uint32_t size)
{
    vkCmdPushConstants(g_cmd, g_layouts[layout].layout, VK_SHADER_STAGE_ALL, 0, size, data);
}

void vg_cmd_dispatch(uint32_t x, uint32_t y, uint32_t z) { vkCmdDispatch(g_cmd, x, y, z); }

// The barrier the game records between depths: compute writes visible to later compute.
void vg_cmd_barrier(void)
{
    VkMemoryBarrier mb{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
    mb.srcAccessMask = VK_ACCESS_SHADER_WRITE_BIT;
    mb.dstAccessMask = VK_ACCESS_SHADER_READ_BIT | VK_ACCESS_SHADER_WRITE_BIT | VK_ACCESS_INDIRECT_COMMAND_READ_BIT;
    vkCmdPipelineBarrier(g_cmd, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                         VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT | VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT | VK_PIPELINE_STAGE_VERTEX_SHADER_BIT | VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT,
                         0, 1, &mb, 0, nullptr, 0, nullptr);
}

// One render: clears, binds, draws `count` DrawIndexedIndirect records from `indirectBuf` (stride bytes apart), ends.
int vg_cmd_render(int nColors, const int* colors, int depth, int pipeline, int layout, int indexBuf, int indirectBuf, uint32_t count, uint32_t stride, const float* clear)
{
    ALIVE();
    std::vector<VkRenderingAttachmentInfo> atts(nColors);
    uint32_t w = g_images[colors[0]].w, h = g_images[colors[0]].h;
    for (int i = 0; i < nColors; i++) {
        imageBarrier(g_cmd, g_images[colors[i]], VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL, 0, VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                     VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT);
        atts[i] = {VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO};
        atts[i].imageView = g_images[colors[i]].view;
        atts[i].imageLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
        atts[i].loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
        atts[i].storeOp = VK_ATTACHMENT_STORE_OP_STORE;
        memcpy(atts[i].clearValue.color.float32, clear, 16);
    }
    imageBarrier(g_cmd, g_images[depth], VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_DEPTH_ATTACHMENT_OPTIMAL, 0, VK_ACCESS_DEPTH_STENCIL_ATTACHMENT_WRITE_BIT,
                 VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_EARLY_FRAGMENT_TESTS_BIT);
    VkRenderingAttachmentInfo da{VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO};
    da.imageView = g_images[depth].view;
    da.imageLayout = VK_IMAGE_LAYOUT_DEPTH_ATTACHMENT_OPTIMAL;
    da.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    da.storeOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    da.clearValue.depthStencil = {0.0f, 0};   // reverse-Z: far
    VkRenderingInfo ri{VK_STRUCTURE_TYPE_RENDERING_INFO};
    ri.renderArea = {{0, 0}, {w, h}};
    ri.layerCount = 1;
    ri.colorAttachmentCount = (uint32_t)atts.size();
    ri.pColorAttachments = atts.data();
    ri.pDepthAttachment = &da;
    vkCmdBeginRendering(g_cmd, &ri);
    VkViewport vp{0.0f, 0.0f, (float)w, (float)h, 0.0f, 1.0f};
    VkRect2D sc{{0, 0}, {w, h}};
    vkCmdSetViewport(g_cmd, 0, 1, &vp);
    vkCmdSetScissor(g_cmd, 0, 1, &sc);
    vkCmdBindPipeline(g_cmd, VK_PIPELINE_BIND_POINT_GRAPHICS, g_pipelines[pipeline]);
    bindSets(layout, VK_PIPELINE_BIND_POINT_GRAPHICS);
    vkCmdBindIndexBuffer(g_cmd, g_buffers[indexBuf].buf, 0, VK_INDEX_TYPE_UINT16);
    if (count) vkCmdDrawIndexedIndirect(g_cmd, g_buffers[indirectBuf].buf, 0, count, stride);
    vkCmdEndRendering(g_cmd);
    for (int i = 0; i < nColors; i++)
        imageBarrier(g_cmd, g_images[colors[i]], VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                     VK_ACCESS_TRANSFER_READ_BIT, VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT);
    return 0;
}

// Ends recording, submits, waits with a timeout. Results reach the host mapping. gpuMs: top to bottom of the command buffer.
int vg_submit_wait(uint32_t timeoutMs, double* gpuMs)
{
    ALIVE();
    VkMemoryBarrier mb{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
    mb.srcAccessMask = VK_ACCESS_SHADER_WRITE_BIT;
    mb.dstAccessMask = VK_ACCESS_HOST_READ_BIT;
    vkCmdPipelineBarrier(g_cmd, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT, VK_PIPELINE_STAGE_HOST_BIT, 0, 1, &mb, 0, nullptr, 0, nullptr);
    vkCmdWriteTimestamp(g_cmd, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, g_query, 1);
    CHECK(vkEndCommandBuffer(g_cmd), "end");
    VkSubmitInfo si{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    si.commandBufferCount = 1;
    si.pCommandBuffers = &g_cmd;
    VkResult r = vkQueueSubmit(g_queue, 1, &si, g_fence);
    if (r != VK_SUCCESS) return fail(r, "submit");
    r = vkWaitForFences(g_dev, 1, &g_fence, VK_TRUE, uint64_t(timeoutMs) * 1000000ull);
    if (r != VK_SUCCESS) return fail(r, "fence wait");
    CHECK(vkResetFences(g_dev, 1, &g_fence), "reset fence");
    if (gpuMs) {
        uint64_t ts[2] = {0, 0};
        vkGetQueryPoolResults(g_dev, g_query, 0, 2, sizeof ts, ts, sizeof(uint64_t), VK_QUERY_RESULT_64_BIT);
        *gpuMs = double(ts[1] - ts[0]) * g_tickNs / 1e6;
    }
    return 0;
}

void vg_shutdown(void)
{
    if (g_lost) return;   // leave it to process exit
    if (g_dev) {
        vkDeviceWaitIdle(g_dev);
        for (VkPipeline p : g_pipelines) vkDestroyPipeline(g_dev, p, nullptr);
        for (Layout& L : g_layouts) {
            vkDestroyPipelineLayout(g_dev, L.layout, nullptr);
            for (VkDescriptorSetLayout sl : L.setLayouts) vkDestroyDescriptorSetLayout(g_dev, sl, nullptr);
        }
        for (Image& im : g_images) {
            vkDestroyImageView(g_dev, im.view, nullptr);
            vkDestroyImage(g_dev, im.img, nullptr);
            vkFreeMemory(g_dev, im.mem, nullptr);
        }
        for (size_t i = 0; i < g_buffers.size(); i++) vg_buffer_free((int)i);
        vkDestroyQueryPool(g_dev, g_query, nullptr);
        vkDestroySampler(g_dev, g_sampler, nullptr);
        vkDestroyDescriptorPool(g_dev, g_descPool, nullptr);
        vkDestroyFence(g_dev, g_fence, nullptr);
        vkDestroyCommandPool(g_dev, g_pool, nullptr);
        vkDestroyDevice(g_dev, nullptr);
    }
    g_pipelines.clear();
    g_layouts.clear();
    g_images.clear();
    g_buffers.clear();
    if (g_messenger) {
        auto destroy = (PFN_vkDestroyDebugUtilsMessengerEXT)vkGetInstanceProcAddr(g_instance, "vkDestroyDebugUtilsMessengerEXT");
        if (destroy) destroy(g_instance, g_messenger, nullptr);
        g_messenger = VK_NULL_HANDLE;
    }
    if (g_instance) vkDestroyInstance(g_instance, nullptr);
    g_dev = VK_NULL_HANDLE;
    g_instance = VK_NULL_HANDLE;
}

}  // extern "C"
