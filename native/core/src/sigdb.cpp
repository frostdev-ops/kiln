// SPDX-License-Identifier: MIT
#include "sigdb.h"

#include <algorithm>
#include <atomic>
#include <cstring>
#include <fstream>
#include <map>
#include <mutex>
#include <optional>
#include <set>
#include <thread>

#include "image.h"
#include "log.h"
#include "nlohmann/json.hpp"
#include "pe_text.h"

namespace kiln::sigdb {
namespace {

using json = nlohmann::ordered_json;

struct FindSpec {
    std::string text;
    Pattern pattern;
    int64_t offset = 0;
    std::string scope_symbol;
    size_t scope_size = 0;
};

struct SymbolDef {
    std::string name;
    bool probe = false;  // "@name": only captures, every strategy evaluated
    std::vector<FindSpec> find;
};

struct ConstDef {
    std::optional<int64_t> fixed;
    int min_votes = 1;
};

struct FeatureDef {
    std::string name;
    std::vector<std::string> symbols, constants;
};

struct Resolved {
    bool ok = false;
    int64_t value = 0;  // address (symbols, absolute) or constant
    std::string source;
    std::string error;
    // Symbols, disk view only: the first live byte that differs from the file's in the bytes this symbol was matched
    // on or in the first 5 bytes at its address (0 = none), and what the live code jumps to there.
    uintptr_t live_diff = 0;
    std::string live_by;
    // kiln.hooks v2 (hooks map): where the attribution started and the 16 live bytes found there at resolve time,
    // before any Kiln hook could change them. live_at is 0 when the bytes could not be read.
    uintptr_t live_at = 0;
    uint8_t live_bytes[16] = {};
};

struct Vote {
    int64_t value;
    std::string source;
};

std::string hex(int64_t v) {
    char b[32];
    std::snprintf(b, sizeof b, "0x%llx", static_cast<unsigned long long>(v));
    return b;
}

// ---- the code sigdb scans ----
// The exe file's .text (image().file, relocated like the live image) when it is the loaded code, else the live .text.
struct Code {
    pe::CodeView view;
    std::string note;             // why this view (kiln.log, report)
    size_t fixups = 0;            // base relocations applied to the file's copy
    size_t live_diff_bytes = 0;   // live .text bytes that differ from the file's, at the time of the check
    std::vector<pe::DiffRun> runs;
    std::vector<uint8_t> private_text;  // relocation/zero-fill buffer; the hashed file view stays pristine
    double ms = 0;
};
Code g_code;
std::once_flag g_code_once;

bool readable(uintptr_t a, size_t n) {
    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(reinterpret_cast<const void*>(a), &mbi, sizeof mbi) || mbi.State != MEM_COMMIT) return false;
    if (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) return false;
    const uintptr_t base = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
    return a >= base && a - base <= mbi.RegionSize && n <= mbi.RegionSize - (a - base);
}

std::string module_at(uintptr_t a) {
    HMODULE mod = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(a), &mod) ||
        !mod)
        return "";
    wchar_t path[MAX_PATH];
    const DWORD n = GetModuleFileNameW(mod, path, MAX_PATH);
    if (!n) return "";
    std::filesystem::path pth(std::wstring(path, n));
    char off[32];
    std::snprintf(off, sizeof off, "+0x%llx", static_cast<unsigned long long>(a - reinterpret_cast<uintptr_t>(mod)));
    return to_utf8(pth.filename().wstring()) + off;
}

// The target of a jmp or call at `a` (E9/E8 rel32, EB rel8, FF 25/FF 15 [rip+disp32], mov rax,imm64 + jmp rax),
// or 0.
uintptr_t jump_target(uintptr_t a) {
    if (!readable(a, 14)) return 0;
    const auto* p = reinterpret_cast<const uint8_t*>(a);
    int32_t d = 0;
    if (p[0] == 0xE9 || p[0] == 0xE8) return std::memcpy(&d, p + 1, 4), a + 5 + d;
    if (p[0] == 0xEB) return a + 2 + static_cast<int8_t>(p[1]);
    if (p[0] == 0xFF && (p[1] == 0x25 || p[1] == 0x15)) {
        std::memcpy(&d, p + 2, 4);
        const uintptr_t slot = a + 6 + d;
        uint64_t t = 0;
        if (!readable(slot, 8)) return 0;
        std::memcpy(&t, reinterpret_cast<const void*>(slot), 8);
        return static_cast<uintptr_t>(t);
    }
    if (p[0] == 0x48 && p[1] == 0xB8 && p[10] == 0xFF && p[11] == 0xE0) {
        uint64_t t = 0;
        std::memcpy(&t, p + 2, 8);
        return static_cast<uintptr_t>(t);
    }
    return 0;
}

// What patched the live code at `a`: follows jmps (relays, trampolines) to the first module outside the exe.
std::string patched_by(uintptr_t a) {
    uintptr_t t = jump_target(a);
    if (!t) return "bytes changed in place";
    const uintptr_t exe_lo = image().base, exe_hi = image().base + image().size;
    for (int hop = 0; hop < 4; ++hop) {
        if (t < exe_lo || t >= exe_hi) {
            const std::string mod = module_at(t);
            if (!mod.empty()) return "jmp to " + mod;
        }
        const uintptr_t next = jump_target(t);
        if (!next) break;
        t = next;
    }
    // A relay stub (saves registers, then calls the hook): name the first module an absolute address in its first
    // 64 bytes (mov r64, imm64) points into.
    if ((t < exe_lo || t >= exe_hi) && readable(t, 64)) {
        const auto* p = reinterpret_cast<const uint8_t*>(t);
        for (size_t i = 0; i + 10 <= 64; ++i) {
            if ((p[i] & 0xF0) != 0x40 || p[i + 1] < 0xB8 || p[i + 1] > 0xBF) continue;
            uint64_t imm = 0;
            std::memcpy(&imm, p + i + 2, 8);
            const std::string mod = module_at(static_cast<uintptr_t>(imm));
            if (!mod.empty()) {
                char b[48];
                std::snprintf(b, sizeof b, "jmp to a relay at 0x%llx for ", static_cast<unsigned long long>(t));
                return b + mod;
            }
        }
    }
    char b[48];
    std::snprintf(b, sizeof b, "jmp to 0x%llx", static_cast<unsigned long long>(t));
    return b;
}

// Where a redirected call may go (pe::redirected_call_at): into a loaded module, or to a relay as hookers write them.
bool call_lands(uintptr_t to) {
    return readable(to, 14) && (!module_at(to).empty() || pe::relay_shape(reinterpret_cast<const uint8_t*>(to)));
}

// Applies the file's fixups to its mapped copy; false if the view can't be written (no C++ objects here: SEH).
bool relocate_guarded(uint8_t* text, size_t size, const pe::FileText* ft, uint64_t base, size_t* fixups) {
    __try {
        *fixups = pe::relocate_text(text, size, *ft, base);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void init_code() {
    const double t0 = now_ms();
    const ImageInfo& img = image();
    const uintptr_t live = reinterpret_cast<uintptr_t>(img.text);
    g_code.view = {img.text, img.text_size, live, false};
    auto fallback = [&](const std::string& why) {
        g_code.note = "live code (" + why + ")";
        g_code.ms = now_ms() - t0;
    };
    if (!img.file) return fallback("the exe file could not be mapped");
    pe::FileText ft;
    std::string err;
    if (!pe::parse_file_text(img.file, img.file_size, ft, err)) return fallback("exe file: " + err);
    if (ft.timestamp != img.timestamp || ft.size_of_image != img.size ||
        ft.text_rva != live - img.base || ft.text_vsize != img.text_size)
        return fallback("the file's .text is not the loaded one");
    uint8_t* text = img.file + ft.text_raw;
    const size_t size = ft.text_vsize;
    if (ft.file_bytes() < size || !ft.relocs64.empty() || !ft.relocs32.empty()) {
        // call_once retries if any later allocation/logging throws. Mutating the file view here would apply
        // the relocation delta twice on retry. Always start fixups from pristine file bytes.
        g_code.private_text.assign(size, 0);
        std::memcpy(g_code.private_text.data(), text, ft.file_bytes());
        text = g_code.private_text.data();
    }
    if (!ft.relocs64.empty() || !ft.relocs32.empty()) {
        // Fixups land in our private buffer; neither the file nor the hashed mapping is modified.
        if (!relocate_guarded(text, size, &ft, img.base, &g_code.fixups))
            return fallback("cannot apply base relocations to the mapped file");
    }
    g_code.runs = pe::diff_runs(text, img.text, size, ft.text_rva, &g_code.live_diff_bytes);
    if (g_code.live_diff_bytes > size / 100) {
        char b[96];
        std::snprintf(b, sizeof b, "%zu of %zu bytes differ from the file's", g_code.live_diff_bytes, size);
        g_code.runs.clear();
        return fallback(b);
    }
    if (!pe::only_hook_differences(text, img.text, size, live, img.base, img.size, call_lands)) {
        g_code.runs.clear();
        return fallback("file/live differences are not recognised inline hooks; the exe may have changed on disk");
    }
    g_code.view = {text, size, live, true};
    g_code.note = "the exe file's code";
    g_code.ms = now_ms() - t0;
}

const pe::CodeView& code() {
    std::call_once(g_code_once, [] {
        init_code();
        const Code& c = g_code;
        if (c.view.disk) {
            KLOG_I("sigdb: scanning %s (.text 0x%zx bytes, %zu base relocations applied); live code differs in %zu "
                   "bytes, %zu runs (hooks or patches made before this check) (%.1f ms)",
                   c.note.c_str(), c.view.size, c.fixups, c.live_diff_bytes, c.runs.size(), c.ms);
        } else {
            KLOG_W("sigdb: scanning the %s; another engine's hooks can hide functions from Kiln", c.note.c_str());
        }
    });
    return g_code.view;
}

// Scans many patterns over the same bytes on worker threads (each pattern is an independent scan of .text).
// results[i] = scan(*patterns[i], begin, end, limit) where done[i] (a scan that threw is left to the caller).
void scan_many(const std::vector<const Pattern*>& patterns, const uint8_t* begin, const uint8_t* end, size_t limit,
               std::vector<std::vector<const uint8_t*>>& results, std::vector<uint8_t>& done) {
    results.assign(patterns.size(), {});
    done.assign(patterns.size(), 0);
    const unsigned hw = std::max(1u, std::thread::hardware_concurrency());
    const size_t workers = patterns.size() < 32 ? 1 : std::min<size_t>({hw, 8, patterns.size()});
    std::atomic<size_t> next{0};
    auto work = [&] {
        for (size_t i; (i = next.fetch_add(1)) < patterns.size();) {
            try {
                results[i] = scan(*patterns[i], begin, end, limit);
                done[i] = 1;
            } catch (...) {
                results[i].clear();  // not done: try_resolve scans it itself
            }
        }
    };
    std::vector<std::thread> pool;
    for (size_t w = 1; w < workers; ++w) {
        try {
            pool.emplace_back(work);
        } catch (...) {
            break;  // fewer threads; the caller's thread still drains the queue
        }
    }
    work();
    for (auto& t : pool) t.join();
}

// One self-contained set of definitions resolved against the running image. The core is one Resolver; each mod
// fragment is another, with the core as its parent so a fragment strategy can be scoped to a core symbol.
class Resolver {
public:
    explicit Resolver(const Resolver* parent = nullptr) : parent_(parent) {}

    std::string load_error;  // set by load(); non-empty = the file is unusable as a whole

    // Reads one definitions file into this resolver. require_prefix (a fragment's "<id>/") rejects any name that is
    // not in that namespace. Appends, so the core can merge server.json + server.d/*.json.
    bool load(const std::filesystem::path& file, const std::string& require_prefix) {
        std::ifstream in(file);
        if (!in) {
            load_error = "cannot open " + to_utf8(file.wstring());
            return false;
        }
        json doc;
        try {
            doc = json::parse(in, nullptr, true, true);
        } catch (const std::exception& e) {
            load_error = std::string("parse error: ") + e.what();
            return false;
        }
        if (doc.value("format", 0) != 1) {
            load_error = "unsupported format";
            return false;
        }
        auto check_prefix = [&](const std::string& name) -> bool {
            if (require_prefix.empty()) return true;
            const std::string bare = (!name.empty() && name[0] == '@') ? name.substr(1) : name;
            if (bare.rfind(require_prefix, 0) == 0) return true;
            load_error = "name '" + name + "' must start with '" + require_prefix + "'";
            return false;
        };
        for (auto& [name, sym] : doc["symbols"].items()) {
            if (!check_prefix(name)) return false;
            SymbolDef def;
            def.name = name;
            def.probe = !name.empty() && name[0] == '@';
            for (auto& f : sym["find"]) {
                FindSpec spec;
                spec.text = f.value("pattern", "");
                spec.offset = f.value("offset", 0);
                if (f.contains("scope")) {
                    spec.scope_symbol = f["scope"].value("symbol", "");
                    spec.scope_size = f["scope"].value("size", 0);
                }
                std::string perr;
                if (!parse_pattern(spec.text, spec.pattern, perr)) {
                    load_error = name + ": " + perr;
                    return false;
                }
                def.find.push_back(std::move(spec));
            }
            if (!defs_.emplace(name, std::move(def)).second) {
                load_error = "symbol " + name + " is defined twice";
                return false;
            }
        }
        if (doc.contains("constants")) {
            for (auto& [name, c] : doc["constants"].items()) {
                if (!check_prefix(name)) return false;
                ConstDef def;
                if (c.contains("value")) def.fixed = c["value"].get<int64_t>();
                def.min_votes = c.value("min_votes", 1);
                if (!const_defs_.emplace(name, def).second) {
                    load_error = "constant " + name + " is defined twice";
                    return false;
                }
            }
        }
        if (doc.contains("features")) {
            for (auto& [name, f] : doc["features"].items()) {
                if (!check_prefix(name)) return false;
                FeatureDef def;
                def.name = name;
                if (f.contains("symbols")) def.symbols = f["symbols"].get<std::vector<std::string>>();
                if (f.contains("constants")) def.constants = f["constants"].get<std::vector<std::string>>();
                features_.push_back(std::move(def));
            }
        }
        return true;
    }

    void resolve() {
        const double t0 = now_ms();
        symbols_.clear();
        constants_.clear();
        feature_ok_.clear();
        captures_.clear();
        matched_.clear();
        state_.clear();
        conflicted_.clear();
        spans_.clear();
        prescan_.clear();

        // Every unscoped strategy scans all of .text: do those scans up front, in parallel. The bytes don't change
        // under us (the file's copy is ours; the live fallback is scanned as before, just sooner).
        const pe::CodeView& cv = code();
        {
            std::vector<const Pattern*> patterns;
            std::vector<std::string> tags;
            for (auto& [name, def] : defs_)
                for (size_t i = 0; i < def.find.size(); ++i)
                    if (def.find[i].scope_symbol.empty()) {
                        patterns.push_back(&def.find[i].pattern);
                        tags.push_back(name + ".find[" + std::to_string(i) + "]");
                    }
            std::vector<std::vector<const uint8_t*>> hits;
            std::vector<uint8_t> done;
            scan_many(patterns, cv.scan, cv.end(), 2, hits, done);
            for (size_t k = 0; k < tags.size(); ++k)
                if (done[k]) prescan_.emplace(std::move(tags[k]), std::move(hits[k]));
        }

        // Fixed point: an unscoped strategy may capture (via a rel32 hole) the very symbol another strategy is
        // scoped to, so we resolve, promote rel32-captured symbols, and repeat until nothing new resolves. This is
        // the "resolve rel32-captured symbols before scoped searches" fix: a scope symbol reached only through a
        // rel32 capture is available by the time a scoped strategy needs it.
        bool progress = true;
        for (size_t guard = 0; progress && guard <= defs_.size() + 4; ++guard) {
            progress = false;
            for (auto& [name, def] : defs_)
                if (try_resolve(name, def)) progress = true;
            if (promote_symbol_votes()) progress = true;
        }

        // Final pass: record errors for anything still unresolved, and finalize constants.
        for (auto& [name, def] : defs_) {
            if (state_[name] == 2) continue;
            Resolved& r = symbols_[name];
            if (r.error.empty()) r.error = "unresolved";
        }
        promote_symbol_votes();  // one last promotion in case the loop hit the guard
        finalize_constants();

        for (const FeatureDef& f : features_) {
            bool ok = true;
            for (auto& s : f.symbols) ok = ok && symbols_.count(s) && symbols_[s].ok;
            for (auto& c : f.constants) ok = ok && constants_.count(c) && constants_[c].ok;
            feature_ok_[f.name] = ok;
        }
        if (cv.disk) check_live(cv);
        prescan_.clear();
        ms_ = now_ms() - t0;
    }

    // Symbols whose live bytes differ from the file's: in the bytes a strategy matched on (that strategy would no
    // longer match the live code) or in the first 5 bytes at the symbol's address (a hook at its entry).
    void check_live(const pe::CodeView& cv) {
        auto first_diff = [&](uintptr_t a, size_t n) -> uintptr_t {
            const uint8_t* f = cv.to_scan(a);
            if (!f) return 0;
            n = std::min<size_t>(n, cv.end() - f);
            const auto* l = reinterpret_cast<const uint8_t*>(a);
            for (size_t i = 0; i < n; ++i)
                if (f[i] != l[i]) return a + i;
            return 0;
        };
        for (auto& [name, r] : symbols_) r.live_diff = 0, r.live_by.clear();
        for (auto& [tag, span] : spans_) {  // tag = "<symbol>.find[i]"
            auto it = symbols_.find(tag.substr(0, tag.rfind(".find[")));
            if (it == symbols_.end() || !it->second.ok || it->second.live_diff) continue;
            it->second.live_diff = first_diff(span.first, span.second);
        }
        for (auto& [name, r] : symbols_) {
            if (!r.ok) continue;
            uintptr_t d = r.live_diff;
            if (!d && name[0] != '@') d = first_diff(static_cast<uintptr_t>(r.value), 5);
            if (!d) continue;
            r.live_diff = d;
            // Attribute from the symbol's entry when it is patched there (the usual hook), else from the first
            // differing byte.
            uintptr_t at = (name[0] != '@' && first_diff(static_cast<uintptr_t>(r.value), 5))
                               ? static_cast<uintptr_t>(r.value)
                               : d;
            r.live_by = patched_by(at);
            // A redirected call (pe::only_hook_differences): the opcode is in place and the first differing byte is
            // in its operand, so the bytes there decode as nothing. Record the call itself and say where it goes.
            // The call is the one the difference check accepted (an E8 among the unchanged operand bytes before `d`
            // is not it: its two targets do not read as a redirected call).
            if (at == d && cv.disk) {
                const auto* live = reinterpret_cast<const uint8_t*>(cv.live);
                for (uintptr_t back = 1; back <= 4 && d - cv.live >= back; ++back) {
                    const size_t start = static_cast<size_t>(d - cv.live - back);
                    if (!pe::redirected_call_at(cv.scan, live, cv.size, start, cv.live, image().base, image().size, call_lands))
                        continue;
                    const std::string by = patched_by(d - back);
                    if (by.rfind("jmp to ", 0) == 0) r.live_by = "call redirected to " + by.substr(7);
                    at = d - back;
                    break;
                }
            }
            if (readable(at, sizeof r.live_bytes)) {
                std::memcpy(r.live_bytes, reinterpret_cast<const void*>(at), sizeof r.live_bytes);
                r.live_at = at;
            }
        }
    }

    bool symbol(std::string_view name, uintptr_t* out) const {
        if (!name.empty() && name[0] == '@') return false;
        auto it = symbols_.find(std::string(name));
        if (it == symbols_.end() || !it->second.ok) return false;
        if (out) *out = static_cast<uintptr_t>(it->second.value);
        return true;
    }
    bool constant(std::string_view name, int64_t* out) const {
        auto it = constants_.find(std::string(name));
        if (it == constants_.end() || !it->second.ok) return false;
        if (out) *out = it->second.value;
        return true;
    }
    bool feature(std::string_view name) const {
        auto it = feature_ok_.find(std::string(name));
        return it != feature_ok_.end() && it->second;
    }
    std::vector<std::string> feature_names() const {
        std::vector<std::string> v;
        for (auto& f : features_) v.push_back(f.name);
        return v;
    }
    std::vector<std::string> failed_features() const {
        std::vector<std::string> v;
        for (auto& [n, ok] : feature_ok_)
            if (!ok) v.push_back(n);
        return v;
    }
    double ms() const { return ms_; }

    // The first piece of `feature` that did not resolve (see sigdb.h). A feature defined twice is judged by its last
    // definition, as resolve() does (feature_ok_ keeps the last one).
    bool feature_problem(std::string_view feature, std::string* name, std::string* text, bool* is_constant) const {
        const FeatureDef* def = nullptr;
        for (const FeatureDef& f : features_)
            if (f.name == feature) def = &f;
        if (!def) return false;
        const auto ok = feature_ok_.find(def->name);
        if (ok != feature_ok_.end() && ok->second) return false;
        auto take = [&](const std::string& n, const std::map<std::string, Resolved>& from, bool constant) {
            const auto it = from.find(n);
            if (it != from.end() && it->second.ok) return false;
            if (name) *name = n;
            if (text) *text = it == from.end() ? "not defined" : it->second.error.empty() ? "unresolved" : it->second.error;
            if (is_constant) *is_constant = constant;
            return true;
        };
        for (const std::string& s : def->symbols)
            if (take(s, symbols_, false)) return true;
        for (const std::string& c : def->constants)
            if (take(c, constants_, true)) return true;
        return false;
    }

    void summarize(FragmentSummary& s) const {
        s.ms = ms_;
        for (auto& [n, r] : symbols_) {
            if (!n.empty() && n[0] == '@') continue;
            ++s.symbols;
            if (r.ok) ++s.symbols_ok;
            else s.problems.push_back(n + ": " + (r.error.empty() ? "unresolved" : r.error));
        }
        for (auto& [n, r] : constants_) {
            ++s.constants;
            if (r.ok) ++s.constants_ok;
            else s.problems.push_back(n + ": " + (r.error.empty() ? "unresolved" : r.error));
        }
        for (auto& [n, ok] : feature_ok_) {
            ++s.features;
            if (ok) ++s.features_ok;
        }
    }

    void write_report(const std::filesystem::path& file) const {
        json doc;
        doc["kiln"] = std::string(kVersion);
        doc["exe_sha256"] = image().sha256;
        doc["codeview"] = image().codeview;
        doc["resolve_ms"] = ms_;
        {
            json c;
            c["source"] = g_code.view.disk ? "disk" : "live";
            c["note"] = g_code.note;
            if (g_code.view.disk) {
                c["base_relocations_applied"] = g_code.fixups;
                c["live_differs_bytes"] = g_code.live_diff_bytes;
                c["live_differs_runs"] = g_code.runs.size();
            }
            json hooked = json::array();
            for (auto& [n, r] : symbols_)
                if (r.live_diff) hooked.push_back(n);
            c["live_differs_symbols"] = hooked;
            doc["code"] = c;
        }
        for (auto& [n, r] : symbols_) {
            json j;
            j["ok"] = r.ok;
            if (r.ok && n[0] != '@') j["rva"] = hex(r.value - static_cast<int64_t>(image().base));
            if (!r.source.empty()) j["source"] = r.source;
            if (!r.error.empty()) j["error"] = r.error;
            if (r.live_diff) j["live"] = {{"differs_at", hex(static_cast<int64_t>(r.live_diff - image().base))},
                                          {"by", r.live_by}};
            doc["symbols"][n] = j;
        }
        for (auto& [n, r] : constants_) {
            json j;
            j["ok"] = r.ok;
            if (r.ok) j["value"] = hex(r.value);
            if (!r.source.empty()) j["source"] = r.source;
            if (!r.error.empty()) j["error"] = r.error;
            doc["constants"][n] = j;
        }
        for (auto& [n, ok] : feature_ok_) doc["features"][n] = ok;
        std::ofstream out(file, std::ios::trunc);
        out << doc.dump(2) << '\n';
    }

    void log_unresolved() const {
        for (auto& [n, r] : symbols_)
            if (r.live_diff)
                KLOG_I("sigdb: %s %s matched on the file's code; its live bytes differ at rva 0x%llx (%s): hooked or "
                       "patched by someone else",
                       n[0] == '@' ? "probe" : "symbol", n.c_str(),
                       static_cast<unsigned long long>(r.live_diff - image().base), r.live_by.c_str());
        for (auto& [n, r] : symbols_)
            if (!r.ok && n[0] != '@') KLOG_W("sigdb: symbol %s unresolved: %s", n.c_str(), r.error.c_str());
        for (auto& [n, r] : constants_)
            if (!r.ok) KLOG_W("sigdb: constant %s unresolved: %s", n.c_str(), r.error.c_str());
    }

    size_t symbol_count_ok() const {
        size_t n = 0;
        for (auto& [k, r] : symbols_) if (r.ok) ++n;
        return n;
    }
    size_t symbol_count() const { return symbols_.size(); }
    // kiln.hooks v2: the first symbol (alphabetical) whose address is `a`; "" when none.
    std::string name_at(uintptr_t a) const {
        for (auto& [n, r] : symbols_)
            if (r.ok && n[0] != '@' && static_cast<uintptr_t>(r.value) == a) return n;
        return {};
    }
    // kiln.hooks v2: every resolved symbol whose live bytes differed from the file's when it was resolved.
    void collect_live_diffs(std::vector<LiveDiff>& out) const {
        for (auto& [n, r] : symbols_) {
            if (!r.ok || !r.live_diff || n[0] == '@') continue;
            LiveDiff d;
            d.symbol = n;
            d.address = static_cast<uintptr_t>(r.value);
            d.at = r.live_at;
            d.bytes_ok = r.live_at != 0;
            std::memcpy(d.bytes, r.live_bytes, sizeof d.bytes);
            d.by = r.live_by;
            out.push_back(std::move(d));
        }
    }
    size_t live_differs_count() const {
        size_t n = 0;
        for (auto& [k, r] : symbols_) if (r.live_diff) ++n;
        return n;
    }
    size_t const_count_ok() const {
        size_t n = 0;
        for (auto& [k, r] : constants_) if (r.ok) ++n;
        return n;
    }
    size_t const_count() const { return constants_.size(); }

private:
    const Resolver* parent_;
    std::map<std::string, SymbolDef> defs_;
    std::map<std::string, ConstDef> const_defs_;
    std::vector<FeatureDef> features_;
    std::map<std::string, Resolved> symbols_;
    std::map<std::string, Resolved> constants_;
    std::map<std::string, bool> feature_ok_;
    // Captures keyed by strategy tag ("name.find[i]"), overwritten on re-evaluation so a strategy never votes twice.
    struct Capture {
        std::string name;
        bool is_symbol;  // rel32 hole: the absolute target is a symbol address
        int64_t value;
    };
    std::map<std::string, std::vector<Capture>> captures_;
    std::set<std::string> matched_;  // strategy tags already matched (not re-scanned)
    std::map<std::string, std::pair<uintptr_t, size_t>> spans_;  // strategy tag -> live address + length matched
    std::map<std::string, std::vector<const uint8_t*>> prescan_;  // unscoped strategy tag -> hits (during resolve)
    std::map<std::string, int> state_;  // 0 unseen/retry, 2 done (resolved or final-failed)
    std::set<std::string> conflicted_;  // symbols whose own pattern and a capture named two addresses: failed for good
    double ms_ = 0;

    bool address_of(const std::string& name, int64_t* out) const {
        auto it = symbols_.find(name);
        if (it != symbols_.end() && it->second.ok) {
            *out = it->second.value;
            return true;
        }
        return parent_ && parent_->address_of(name, out);
    }

    // Returns true when `name` became resolved on this call (new progress).
    bool try_resolve(const std::string& name, const SymbolDef& def) {
        if (state_[name] == 2) return false;
        Resolved& r = symbols_[name];
        const bool before = r.ok;
        r.error.clear();
        bool pending_scope = false;  // a strategy is waiting on an unresolved scope symbol
        size_t unique = 0;

        for (size_t i = 0; i < def.find.size(); ++i) {
            const FindSpec& f = def.find[i];
            const std::string tag = name + ".find[" + std::to_string(i) + "]";
            const pe::CodeView& cv = code();
            const uint8_t* begin = cv.scan;
            const uint8_t* end = cv.end();
            if (!f.scope_symbol.empty()) {
                int64_t base = 0;
                if (!address_of(f.scope_symbol, &base)) {
                    pending_scope = true;
                    r.error += (r.error.empty() ? "" : "; ") + tag + ": scope symbol " + f.scope_symbol +
                               " unresolved";
                    continue;
                }
                begin = cv.to_scan(static_cast<uintptr_t>(base));
                if (!begin) {
                    r.error += (r.error.empty() ? "" : "; ") + tag + ": scope symbol " + f.scope_symbol +
                               " is outside .text";
                    continue;
                }
                end = cv.scope_end(begin, f.scope_size);
            }
            if (matched_.count(tag)) {  // already matched in a previous iteration
                ++unique;
                if (!def.probe) {
                    // non-probe resolved earlier would be state 2; reaching here means probe context
                }
                continue;
            }
            const auto pre = f.scope_symbol.empty() ? prescan_.find(tag) : prescan_.end();
            const auto hits = pre != prescan_.end() ? pre->second : scan(f.pattern, begin, end, 2);
            if (hits.size() != 1) {
                r.error += (r.error.empty() ? "" : "; ") + tag + ": " + std::to_string(hits.size()) +
                           (hits.size() > 1 ? "+ matches" : " matches");
                continue;
            }
            matched_.insert(tag);
            record_captures(f.pattern, hits[0], tag, cv);
            spans_[tag] = {cv.to_live(hits[0]), f.pattern.bytes.size()};
            ++unique;
            if (!def.probe) {
                r.ok = true;
                r.value = static_cast<int64_t>(cv.to_live(hits[0]) + f.offset);
                r.source = tag;
                r.error.clear();
                state_[name] = 2;
                return !before;
            }
        }

        if (def.probe) {
            if (unique == def.find.size()) {
                r.ok = true;
                r.source = std::to_string(unique) + "/" + std::to_string(def.find.size()) + " strategies matched";
                r.error.clear();
                state_[name] = 2;
                return !before;
            }
            // not all matched: keep retrying if any strategy is only waiting on a scope symbol
            if (!pending_scope) {
                r.source = std::to_string(unique) + "/" + std::to_string(def.find.size()) + " strategies matched";
                state_[name] = 2;  // scopes known, still incomplete: a real failure
            }
            return false;
        }

        // non-probe, nothing matched: give up only when no strategy is blocked on a scope symbol
        if (!pending_scope) state_[name] = 2;
        return false;
    }

    // `match` points into the scanned bytes (cv): rel32 targets are computed for the live address.
    void record_captures(const Pattern& p, const uint8_t* match, const std::string& tag, const pe::CodeView& cv) {
        std::vector<Capture>& caps = captures_[tag];
        caps.clear();
        for (const Hole& h : p.holes) {
            int64_t v = 0;
            hole_value(p, match, h.name, &v, cv.delta());
            caps.push_back({h.name, h.kind == Hole::Rel32, v});
        }
    }

    // Build symbol (rel32) votes from all matched strategies and promote agreeing ones. Returns true if any symbol
    // became newly resolved.
    bool promote_symbol_votes() {
        std::map<std::string, std::vector<Vote>> votes;
        for (auto& [tag, caps] : captures_)
            for (const Capture& c : caps)
                if (c.is_symbol) votes[c.name].push_back({c.value, tag});
        bool progress = false;
        for (auto& [name, vs] : votes) {
            Resolved& r = symbols_[name];
            // A disagreement is final. This runs once per pass and once more at the end: a later run used to find the
            // symbol "not resolved" and take the capture after all, which turned a failed cross-check into a pass
            // on the capture's address.
            if (conflicted_.count(name)) continue;
            const bool agree = std::all_of(vs.begin(), vs.end(), [&](const Vote& v) { return v.value == vs[0].value; });
            if (!agree) {
                r.ok = false;
                r.error = "captures disagree";
                continue;
            }
            if (r.ok) {
                if (r.value != vs[0].value) {
                    r.ok = false;
                    r.error = "pattern and captures disagree (" + hex(r.value - image().base) + " vs " +
                              hex(vs[0].value - image().base) + ")";
                    conflicted_.insert(name);
                }
                continue;
            }
            r.ok = true;
            r.value = vs[0].value;
            r.source = "captured by " + vs[0].source;
            r.error.clear();
            if (state_[name] != 2) {
                state_[name] = 2;
                progress = true;
            }
        }
        return progress;
    }

    void finalize_constants() {
        std::map<std::string, std::vector<Vote>> votes;
        for (auto& [tag, caps] : captures_)
            for (const Capture& c : caps)
                if (!c.is_symbol) votes[c.name].push_back({c.value, tag});
        std::map<std::string, ConstDef> all = const_defs_;
        for (auto& [name, vs] : votes) all.try_emplace(name);
        for (auto& [name, def] : all) {
            Resolved r;
            auto& vs = votes[name];
            const bool agree = std::all_of(vs.begin(), vs.end(), [&](const Vote& v) { return v.value == vs[0].value; });
            if (!agree) {
                r.error = "captures disagree:";
                for (const Vote& v : vs) r.error += " " + hex(v.value) + "@" + v.source;
            } else if (def.fixed) {
                if (!vs.empty() && vs[0].value != *def.fixed) {
                    r.error = "fixed value " + hex(*def.fixed) + " but captured " + hex(vs[0].value);
                } else {
                    r.ok = true;
                    r.value = *def.fixed;
                    r.source = vs.empty() ? "fixed" : "fixed, confirmed by " + std::to_string(vs.size()) + " captures";
                }
            } else if (vs.empty() || static_cast<int>(vs.size()) < def.min_votes) {
                r.error = std::to_string(vs.size()) + " captures, need " + std::to_string(def.min_votes);
            } else {
                r.ok = true;
                r.value = vs[0].value;
                r.source = std::to_string(vs.size()) + " captures agree";
            }
            constants_[name] = r;
        }
    }
};

// ---- global state ----
Resolver g_core;
bool g_core_loaded = false;
double g_ms = 0;

std::mutex g_frag_mutex;
std::map<std::string, std::unique_ptr<Resolver>> g_fragments;  // id -> resolver (core as parent)

std::string prefix_of(std::string_view name) {
    const size_t slash = name.find('/');
    return slash == std::string_view::npos ? std::string() : std::string(name.substr(0, slash));
}

}  // namespace

bool load_and_resolve(const std::filesystem::path& definitions) {
    if (!g_core.load(definitions, "")) {
        KLOG_E("sigdb: %s", g_core.load_error.c_str());
        return false;
    }
    // Per-service definition files: <dir>/<stem>.d/*.json, merged in name order. A duplicate name is an error in
    // that file only; the rest still loads.
    std::vector<std::filesystem::path> extra;
    std::error_code ec;
    const auto dir = definitions.parent_path() / (definitions.stem().wstring() + L".d");
    for (auto& e : std::filesystem::directory_iterator(dir, ec))
        if (e.path().extension() == L".json") extra.push_back(e.path());
    std::sort(extra.begin(), extra.end());
    for (const auto& f : extra)
        if (!g_core.load(f, ""))
            KLOG_E("sigdb: %s: %s", to_utf8(f.filename().wstring()).c_str(), g_core.load_error.c_str());

    g_core.resolve();
    g_core_loaded = true;
    g_ms = g_core.ms();
    g_core.log_unresolved();
    KLOG_I("sigdb: %zu/%zu symbols, %zu/%zu constants resolved in %.1f ms", g_core.symbol_count_ok(),
           g_core.symbol_count(), g_core.const_count_ok(), g_core.const_count(), g_ms);
    if (g_code.view.disk && g_core.live_differs_count())
        KLOG_I("sigdb: %zu symbols resolved on the file's code whose live bytes differ (hooked first by someone "
               "else; Kiln's hooks chain behind theirs)", g_core.live_differs_count());
    return true;
}

bool symbol(std::string_view name, uintptr_t* out) { return g_core.symbol(name, out); }
bool constant(std::string_view name, int64_t* out) { return g_core.constant(name, out); }
bool feature(std::string_view name) { return g_core.feature(name); }
std::vector<std::string> features() { return g_core.feature_names(); }
std::vector<std::string> failed_features() { return g_core.failed_features(); }
bool feature_problem(std::string_view feature, std::string* name, std::string* text, bool* is_constant) {
    return g_core.feature_problem(feature, name, text, is_constant);
}
double resolve_ms() { return g_ms; }
void write_report(const std::filesystem::path& file) { g_core.write_report(file); }

bool load_fragment(std::string_view id, const std::filesystem::path& file, const std::filesystem::path& report,
                   FragmentSummary* summary, std::string* error) {
    const std::string prefix = std::string(id) + "/";
    auto res = std::make_unique<Resolver>(&g_core);
    if (!res->load(file, prefix)) {
        if (error) *error = res->load_error;
        return false;
    }
    res->resolve();
    if (!report.empty()) res->write_report(report);
    if (summary) res->summarize(*summary);
    std::lock_guard lock(g_frag_mutex);
    g_fragments[std::string(id)] = std::move(res);
    return true;
}

bool unload_fragment(std::string_view id) {
    std::lock_guard lock(g_frag_mutex);
    return g_fragments.erase(std::string(id)) != 0;
}

bool any_symbol(std::string_view name, uintptr_t* out) {
    if (g_core.symbol(name, out)) return true;
    const std::string pfx = prefix_of(name);
    if (pfx.empty()) return false;
    std::lock_guard lock(g_frag_mutex);
    auto it = g_fragments.find(pfx);
    return it != g_fragments.end() && it->second->symbol(name, out);
}

bool any_constant(std::string_view name, int64_t* out) {
    if (g_core.constant(name, out)) return true;
    const std::string pfx = prefix_of(name);
    if (pfx.empty()) return false;
    std::lock_guard lock(g_frag_mutex);
    auto it = g_fragments.find(pfx);
    return it != g_fragments.end() && it->second->constant(name, out);
}

bool any_feature(std::string_view name) {
    if (g_core.feature(name)) return true;
    const std::string pfx = prefix_of(name);
    if (pfx.empty()) return false;
    std::lock_guard lock(g_frag_mutex);
    auto it = g_fragments.find(pfx);
    return it != g_fragments.end() && it->second->feature(name);
}

std::vector<uintptr_t> scan_code(const Pattern& p, size_t limit) {
    const pe::CodeView& cv = code();
    std::vector<uintptr_t> out;
    for (const uint8_t* h : scan(p, cv.scan, cv.end(), limit)) out.push_back(cv.to_live(h));
    return out;
}

bool hole_value_at(const Pattern& p, uintptr_t live_match, std::string_view name, int64_t* out) {
    return hole_value_in(p, code(), live_match, name, out);
}

bool code_from_disk() { return code().disk; }

// ---- kiln.hooks v2 accessors ----
bool file_bytes(uintptr_t live_addr, uint8_t* out, size_t n) {
    const pe::CodeView& cv = code();
    if (!cv.disk || !out) return false;
    const uint8_t* f = cv.to_scan(live_addr);
    if (!f || n > static_cast<size_t>(cv.end() - f)) return false;
    std::memcpy(out, f, n);
    return true;
}

std::string symbol_name_at(uintptr_t live_addr) {
    std::string n = g_core.name_at(live_addr);
    if (!n.empty()) return n;
    std::lock_guard lock(g_frag_mutex);
    for (auto& [id, res] : g_fragments) {
        n = res->name_at(live_addr);
        if (!n.empty()) return n;
    }
    return {};
}

std::vector<LiveDiff> live_differs() {
    std::vector<LiveDiff> out;
    g_core.collect_live_diffs(out);
    std::lock_guard lock(g_frag_mutex);
    for (auto& [id, res] : g_fragments) res->collect_live_diffs(out);
    return out;
}

}  // namespace kiln::sigdb
