// SPDX-License-Identifier: MIT
// The EmfApi table, part 4: engine code. resolve() maps EMF 0.3.3's signature names (src/common/signatures.cpp,
// extracted to sigdb/evidence/emf-0.3.3-signatures.json) onto Kiln's sigdb symbols; names Kiln has no symbol for are
// defined as "emf/<Name>" in sigdb/symbols/server.d/emf.json. hook / hook_pattern / hook_address install each mod's
// detour as a kiln.hooks v2 raw link owned by "emf:<mod id>" and labelled with the signature name: the one physical
// detour per function is shared with Kiln's own components, and *original is the link's continuation (the rest of
// the chain, then the game's function). EMF documents that a later hook on a function runs first and its *original
// calls the previous one; the links keep that: they run before Kiln's own links, newest first. A target outside the
// game exe (another module's code) is not in kiln.hooks' reach and still gets a plain safetyhook detour.
// find_pattern scans the exe as it is on disk.
// write_memory copies into private heap pages only (template data, item definitions), never code or image data.
#include <intrin.h>

#include <fstream>
#include <map>

#include "emf_internal.h"
#include "kiln_hook.hpp"
#include "safetyhook.hpp"

namespace kemf {

namespace {

// ---------------------------------------------------------------------------------------------- aliases -------
// EMF signature name -> Kiln sigdb symbol(s), tried in order; then "emf/<name>". Verified by RVA on server svn
// 1285601 (docs/re/emf-compat.md, "Signature aliases").
struct Alias {
    const char* emf;
    const char* kiln[2];
};
const Alias kAliases[] = {
    {"SimStep", {"SimStep"}},
    {"GetComponent", {"GetComponent"}},
    {"RegisterSingleton", {"RegisterSingleton", "players/RegisterSingleton"}},
    {"RegisterProgram", {"world/RegisterProgram", "items/RegisterProgram"}},
    {"CreateEntity", {"spawn/CreateEntity"}},
    {"DestroyEntity", {"spawn/DestroyEntity"}},
    {"SetTimeOfDay", {"time/SetTimeOfDay"}},
    {"GameLog", {"GameLog"}},
    {"AddLogListener", {"AddLogListener"}},
    {"GetEntityTransform", {"GetEntityTransform"}},
    {"IterBegin", {"world/IterBegin", "items/IterBegin"}},
    {"IterNext", {"world/IterNext"}},
    {"CurrentEntityId", {"world/CurrentEntityId"}},
    {"RegisterAllPrograms", {"world/RegisterAllPrograms", "items/RegisterAllPrograms"}},
    {"SpawnWrapper", {"SpawnWrapper"}},
    {"Save2WriteWorld", {"saves/Save2WriteWorld"}},
    {"LoadSavegameStep", {"saves/LoadSavegameStep"}},
    {"MarkWritten", {"world/MarkWritten"}},
    {"EmitEvent", {"gameplay/EmitEvent"}},
    {"GetTemplateComponent", {"GetTemplateComponent"}},
    {"InventoryTxCtor", {"items/InventoryTxCtor"}},
    {"InventoryTxInit", {"items/InventoryTxInit"}},
    {"InventoryAddItems", {"items/InventoryAddItems"}},
    {"InventoryCommit", {"items/InventoryCommit"}},
    {"InventoryRollback", {"items/InventoryRollback"}},
    {"InventoryFreeArrays", {"items/InventoryFreeArrays"}},
    {"ReadOther", {"world/ReadOther", "items/ReadOther"}},
    {"InventoryRemoveItems", {"items/InventoryRemoveItems"}},
    {"ChatReceive", {"chat/ServerReceive", "net/ServerReceive"}},
    {"ChatLogAdd", {"chat/ChatLogAdd"}},
    {"WaitWorldJobs", {"WaitWorldJobs"}},
    {"AttrFind", {"combat/AttrFind"}},
    {"AllocPendingHit", {"combat/AllocPendingHit"}},
    {"RequestServerSave", {"saves/RequestServerSave"}},
    {"QueuePush", {"world/QueuePush"}},
    {"SendServerInfo", {"settings/SendServerInfo", "time/SendServerInfo"}},
    // defined by kiln-emf itself (sigdb/symbols/server.d/emf.json): NpcWorker, NpcSetState, NpcSetDecision,
    // ServerWorldSetup, WriteOther, AttrAdd; client-only names (ClientWorldSetup, QueueChatMessage, UiFrame,
    // PregameState, PostSelectCharacter, PostSelectWorld, PostSessionArea, PostStart, CharacterSelected) resolve
    // once a client.d/emf.json defines them.
};

uintptr_t resolve_name(std::string_view name) {
    uintptr_t v = 0;
    for (const Alias& a : kAliases)
        if (name == a.emf) {
            for (const char* k : a.kiln)
                if (k && g_api->symbol(kstr(k), &v) == KILN_OK && v) return v;
            break;
        }
    const std::string own = "emf/" + std::string(name);
    if (g_api->symbol(kstr(own), &v) == KILN_OK && v) return v;
    return 0;
}

void* api_resolve(const char* name) {
    if (!name) return nullptr;
    const uintptr_t v = resolve_name(name);
    if (!v) refuse(caller_mod(_ReturnAddress()), "resolve", format("signature %s is not resolved on this game build", name).c_str(), true);
    return reinterpret_cast<void*>(v);
}

// ---------------------------------------------------------------------------------------------- hooks ---------
struct Hook {
    std::string name;
    Mod* mod;
    uintptr_t target;
    KilnHandle link = 0;          // the link in kiln.hooks' chain (the usual case)
    safetyhook::InlineHook hook;  // only for a target outside the game exe
};
std::mutex g_hook_mutex;
std::vector<std::unique_ptr<Hook>> g_hooks;  // live for the whole process (EMF: hooks are never removed)

bool executable_image(uintptr_t a) {
    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(reinterpret_cast<void*>(a), &mbi, sizeof mbi) || mbi.State != MEM_COMMIT || mbi.Type != MEM_IMAGE)
        return false;
    return (mbi.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
}

// A mod's hook name as a link label: printable ASCII, at most 96 bytes. The label goes into kiln.log and
// state/hooks.json; hook_address / hook_pattern take any string from the mod (the JSON writer refuses invalid UTF-8).
std::string link_label(const char* name) {
    if (!name) return "?";
    std::string s(name, strnlen(name, 96));
    for (char& c : s)
        if (static_cast<unsigned char>(c) < 0x20 || static_cast<unsigned char>(c) > 0x7E) c = '?';
    return s.empty() ? "?" : s;
}

// Mods' links run before Kiln's own (priority 0), a later one before an earlier one (EMF: the new detour runs first).
constexpr int32_t kModLinkPriority = 1;

bool install(Mod* m, const char* label, uintptr_t target, void* detour, void** original, const char* fn) {
    if (!target || !detour) return refuse(m, fn, "no target or detour"), false;
    if (!executable_image(target)) return refuse(m, fn, "the target is not executable code of a loaded image"), false;
    std::lock_guard lock(g_hook_mutex);
    auto h = std::make_unique<Hook>();
    h->name = link_label(label);
    h->mod = m;
    h->target = target;
    size_t earlier = 0;  // hooks already on this target (every mod's)
    for (const auto& o : g_hooks) earlier += o->target == target;
    const uintptr_t exe = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    const KilnHooksApi* hooks = kiln::hooks_v2(g_api);
    if (!hooks) return refuse(m, fn, format("%s: kiln.hooks v2 is not available", h->name.c_str()).c_str()), false;
    // The mod's own owner ("emf:<id>"), else kiln-emf's. raw_detour writes *original (the continuation) before the link
    // can be reached, so the detour may call it on its very first run, on any thread.
    const KilnOwner owner = m && m->owner ? m->owner : g_owner;
    KilnHookOrder order{};
    order.size = sizeof order;
    order.priority = kModLinkPriority + static_cast<int32_t>(std::min<size_t>(earlier, 1000));
    order.label = KilnStr{h->name.data(), h->name.size()};
    const KilnResult rc = hooks->raw_detour(owner, target, &order, detour, original, &h->link);
    bool in_chain = rc == KILN_OK;
    if (rc == KILN_ERR_INVALID_ARGUMENT) {
        // Not the game exe (another module's code): kiln.hooks does not reach it. Plain safetyhook detour, as before.
        auto r = safetyhook::InlineHook::create(reinterpret_cast<void*>(target), detour, safetyhook::InlineHook::StartDisabled);
        if (!r) return refuse(m, fn, format("%s: the hook could not be made (safetyhook error %d)", h->name.c_str(),
                                            static_cast<int>(r.error().type)).c_str()),
                      false;
        h->hook = std::move(*r);
        if (original) *original = h->hook.original<void*>();  // set before the hook is live
        if (auto e = h->hook.enable(); !e) {
            if (original) *original = nullptr;
            return refuse(m, fn, format("%s: enabling the hook failed", h->name.c_str()).c_str()), false;
        }
    } else if (rc != KILN_OK) {
        return refuse(m, fn, format("%s: the hook could not be made (kiln.hooks result %d; see kiln.log)", h->name.c_str(),
                                    static_cast<int>(rc)).c_str()),
               false;
    }
    mod_logf(m, KILN_LOG_INFO, "hook %s at exe+0x%llx%s%s", h->name.c_str(),
             static_cast<unsigned long long>(target - exe),
             in_chain ? " (in Kiln's hook chain)" : " (outside the game exe: not in Kiln's hook chain)",
             earlier ? " (chained over an earlier hook)" : "");
    g_hooks.push_back(std::move(h));
    return true;
}

bool api_hook(const char* name, void* detour, void** original) {
    Mod* m = caller_mod(_ReturnAddress());
    const uintptr_t t = name ? resolve_name(name) : 0;
    if (!t) return refuse(m, "hook", format("signature %s is not resolved on this game build", name ? name : "?").c_str()), false;
    return install(m, name, t, detour, original, "hook");
}

bool api_hook_address(const char* name, void* address, void* detour, void** original) {
    return install(caller_mod(_ReturnAddress()), name, reinterpret_cast<uintptr_t>(address), detour, original, "hook_address");
}

// ---------------------------------------------------------------------------------------------- patterns ------
struct Section {
    std::string name;
    uint32_t rva;
    std::vector<uint8_t> bytes;  // raw file bytes
};
std::once_flag g_exe_once;
std::vector<Section> g_sections;
uint32_t g_image_size = 0;

void load_exe() {
    wchar_t path[MAX_PATH * 2] = L"";
    GetModuleFileNameW(nullptr, path, MAX_PATH * 2);
    std::ifstream f(path, std::ios::binary);
    std::vector<uint8_t> file((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    if (file.size() < 0x400) return;
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(file.data());
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || static_cast<size_t>(dos->e_lfanew) + sizeof(IMAGE_NT_HEADERS64) > file.size()) return;
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(file.data() + dos->e_lfanew);
    g_image_size = nt->OptionalHeader.SizeOfImage;
    const auto* sec = IMAGE_FIRST_SECTION(nt);
    for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
        Section s;
        char n[9] = {};
        std::memcpy(n, sec[i].Name, 8);
        s.name = n;
        s.rva = sec[i].VirtualAddress;
        const size_t off = sec[i].PointerToRawData, len = std::min<size_t>(sec[i].SizeOfRawData, sec[i].Misc.VirtualSize);
        if (off + len <= file.size()) s.bytes.assign(file.begin() + static_cast<ptrdiff_t>(off), file.begin() + static_cast<ptrdiff_t>(off + len));
        g_sections.push_back(std::move(s));
    }
}

bool parse_pattern(std::string_view s, std::vector<int16_t>* out, std::string* why) {
    out->clear();
    size_t i = 0;
    while (i < s.size()) {
        while (i < s.size() && s[i] == ' ') ++i;
        if (i >= s.size()) break;
        size_t j = i;
        while (j < s.size() && s[j] != ' ') ++j;
        const std::string_view t = s.substr(i, j - i);
        if (t == "?" || t == "??") {
            out->push_back(-1);
        } else if (t.size() == 2 && std::isxdigit(static_cast<unsigned char>(t[0])) && std::isxdigit(static_cast<unsigned char>(t[1]))) {
            out->push_back(static_cast<int16_t>(std::stoi(std::string(t), nullptr, 16)));
        } else {
            *why = "bad token \"" + std::string(t) + "\"";
            return false;
        }
        i = j;
    }
    if (out->empty()) return *why = "empty pattern", false;
    bool any = false;
    for (int16_t b : *out) any |= b >= 0;
    if (!any) return *why = "only wildcards", false;
    return true;
}

uintptr_t find(Mod* m, const EmfPattern* p, const char* fn) {
    if (!p || p->size < sizeof(EmfPattern) || !p->bytes) return refuse(m, fn, "bad EmfPattern"), 0;
    std::call_once(g_exe_once, load_exe);
    const std::string want = p->section ? p->section : ".text";
    const Section* sec = nullptr;
    for (const auto& s : g_sections)
        if (s.name == want) sec = &s;
    if (!sec || sec->bytes.empty()) return refuse(m, fn, format("no section %s in the game exe", want.c_str()).c_str()), 0;
    std::vector<int16_t> pat;
    std::string why;
    if (!parse_pattern(p->bytes, &pat, &why)) return refuse(m, fn, why.c_str()), 0;
    size_t first = 0;
    while (pat[first] < 0) ++first;
    const uint8_t lead = static_cast<uint8_t>(pat[first]);
    const auto& b = sec->bytes;
    size_t hit = SIZE_MAX, hits = 0;
    for (size_t i = first; i + pat.size() - first <= b.size();) {
        const void* q = std::memchr(b.data() + i, lead, b.size() - i);
        if (!q) break;
        const size_t at = static_cast<size_t>(static_cast<const uint8_t*>(q) - b.data());
        if (at < first || at - first + pat.size() > b.size()) break;
        const size_t start = at - first;
        bool ok = true;
        for (size_t k = 0; k < pat.size() && ok; ++k) ok = pat[k] < 0 || b[start + k] == static_cast<uint8_t>(pat[k]);
        if (ok) {
            if (++hits == 1) hit = start;
            else break;
        }
        i = at + 1;
    }
    if (hits != 1) return refuse(m, fn, format("%s: %s", p->bytes, hits ? "several matches" : "no match").c_str()), 0;
    int64_t rva = static_cast<int64_t>(sec->rva) + static_cast<int64_t>(hit) + p->offset;
    if (p->follow == EMF_FOLLOW_REL32) {
        const int64_t at = static_cast<int64_t>(hit) + p->offset;
        if (at < 0 || static_cast<size_t>(at) + 4 > b.size()) return refuse(m, fn, "rel32 outside the section"), 0;
        int32_t rel = 0;
        std::memcpy(&rel, b.data() + at, 4);
        rva = rva + 4 + p->rel32_tail + rel;
    } else if (p->follow != EMF_FOLLOW_NONE) {
        return refuse(m, fn, "unknown follow mode"), 0;
    }
    if (rva < 0 || rva >= static_cast<int64_t>(g_image_size)) return refuse(m, fn, "the result lies outside the exe"), 0;
    return reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)) + static_cast<uintptr_t>(rva);
}

uintptr_t api_find_pattern(const EmfPattern* p) { return find(caller_mod(_ReturnAddress()), p, "find_pattern"); }

bool api_hook_pattern(const char* name, const EmfPattern* p, void* detour, void** original) {
    Mod* m = caller_mod(_ReturnAddress());
    const uintptr_t t = find(m, p, "hook_pattern");
    if (!t) return false;
    return install(m, name, t, detour, original, "hook_pattern");
}

// ---------------------------------------------------------------------------------------------- memory --------
bool api_write_memory(void* dst, const void* src, uint64_t n) {
    Mod* m = caller_mod(_ReturnAddress());
    if (!dst || !src || n == 0 || n > (64ull << 20)) return refuse(m, "write_memory", "bad arguments"), false;
    const uintptr_t lo = reinterpret_cast<uintptr_t>(dst), hi = lo + n;
    // every page must be committed private memory (heap), not code, image data, a file view or a guard page
    for (uintptr_t a = lo; a < hi;) {
        MEMORY_BASIC_INFORMATION mbi{};
        if (!VirtualQuery(reinterpret_cast<void*>(a), &mbi, sizeof mbi)) return refuse(m, "write_memory", "unmapped memory"), false;
        if (mbi.State != MEM_COMMIT || mbi.Type != MEM_PRIVATE || (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) ||
            (mbi.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)))
            return refuse(m, "write_memory", "only private data pages (template data, item definitions) are written"), false;
        a = reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
    }
    for (uintptr_t a = lo; a < hi;) {
        MEMORY_BASIC_INFORMATION mbi{};
        VirtualQuery(reinterpret_cast<void*>(a), &mbi, sizeof mbi);
        const uintptr_t end = std::min<uintptr_t>(hi, reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize);
        const bool writable = (mbi.Protect & (PAGE_READWRITE | PAGE_WRITECOPY)) != 0;
        DWORD old = 0;
        if (!writable && !VirtualProtect(reinterpret_cast<void*>(a), end - a, PAGE_READWRITE, &old))
            return refuse(m, "write_memory", "the page could not be unlocked"), false;
        const bool ok = seh_copy(reinterpret_cast<void*>(a), static_cast<const uint8_t*>(src) + (a - lo), end - a);
        if (!writable) VirtualProtect(reinterpret_cast<void*>(a), end - a, old, &old);
        if (!ok) return refuse(m, "write_memory", "the copy faulted"), false;
        a = end;
    }
    return true;
}

}  // namespace

void fill_hooks(EmfApi* t) {
    t->resolve = &api_resolve;
    t->hook = &api_hook;
    t->find_pattern = &api_find_pattern;
    t->hook_pattern = &api_hook_pattern;
    t->hook_address = &api_hook_address;
    t->write_memory = &api_write_memory;
}

// for the status file / docs: which EMF names resolve here
std::string aliases_json();
std::string aliases_json() {
    static const char* kAll[] = {"SimStep", "GetComponent", "RegisterSingleton", "RegisterProgram", "CreateEntity", "DestroyEntity",
                                 "NpcWorker", "NpcSetState", "NpcSetDecision", "SetTimeOfDay", "GameLog", "AddLogListener",
                                 "ClientWorldSetup", "ServerWorldSetup", "GetEntityTransform", "IterBegin", "IterNext",
                                 "CurrentEntityId", "RegisterAllPrograms", "SpawnWrapper", "Save2WriteWorld", "LoadSavegameStep",
                                 "MarkWritten", "EmitEvent", "GetTemplateComponent", "QueueChatMessage", "InventoryTxCtor",
                                 "InventoryTxInit", "InventoryAddItems", "InventoryCommit", "InventoryRollback",
                                 "InventoryFreeArrays", "ReadOther", "InventoryRemoveItems", "ChatReceive", "ChatLogAdd",
                                 "WriteOther", "WaitWorldJobs", "AttrFind", "AttrAdd", "AllocPendingHit", "UiFrame",
                                 "PregameState", "PostSelectCharacter", "PostSelectWorld", "PostSessionArea", "PostStart",
                                 "CharacterSelected", "RequestServerSave", "QueuePush", "SendServerInfo"};
    const uintptr_t base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    std::string s = "{";
    bool first = true;
    for (const char* n : kAll) {
        const uintptr_t v = resolve_name(n);
        s += (first ? "\"" : ",\"") + std::string(n) + "\":" + (v ? "\"" + format("0x%llx", static_cast<unsigned long long>(v - base)) + "\"" : "null");
        first = false;
    }
    return s + "}";
}

}  // namespace kemf
