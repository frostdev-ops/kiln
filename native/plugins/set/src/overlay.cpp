// SPDX-License-Identifier: MIT
// Implements SET 0.2's Mod Settings/Compatibility/Entities/Hook diagnostics using kiln.ui. All Lua/game work is
// queued to the tick thread. Settings.define's documented next-tick onChange contract lives in lua_api.cpp.
#include "overlay.h"
#include "inspector.h"
#include "lua_api.h"
#include "modsettings.h"
#include "hooks.h"
#include "sandbox.h"
#include <algorithm>
#include <atomic>
#include <charconv>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <limits>
#include <map>
#include <mutex>
#include <cctype>
#include <chrono>
#include <nlohmann/json.hpp>
#ifdef _WIN32
#include <windows.h>
#endif

namespace {
// kiln.ui passes input_text flags to ImGui unchanged: this is ImGuiInputTextFlags_EnterReturnsTrue in the vendored
// ImGui (1 << 5 there is AllowTabInput). Without it the field returns true on every frame its text differs from ours.
constexpr uint32_t kEnterReturnsTrue = 1u << 6;
const KilnUiApi* ui = nullptr;
std::vector<KilnHandle> subscriptions;
std::atomic<bool> active{false}, opened{false}, remapping{false}, cancelRemap{false};
// Serialize demand/capture and inspector requests with window-thread Escape and runtime teardown.
std::mutex lifecycleMx, mx;
std::string requestedMod;
bool requestPending = false, requestedPrompt = false;
std::deque<std::string> lines;
std::filesystem::path logsDir, controlDir;
std::string modFilter, message, binding;
bool debugLog = true, entitiesOpen = false, bindingsOpen = false;
bool lastUnclean = false, promptOnly = false, sessionStarted = false;
std::string crashSummary;
void (*logSink)(const char*) = nullptr;
char console[1024]{}, component[128] = "CurrentTransform", entity[32]{};
uint32_t selected = 0;
struct Buffer { std::string original; std::vector<char> data; };
std::map<std::string, Buffer> buffers;
KilnStr s(const std::string& text) { return {text.data(), text.size()}; }
std::string utf8(const std::filesystem::path& path) { auto v = path.u8string(); return {v.begin(), v.end()}; }
void text(const std::string& v) { ui->text(s(v)); }
bool button(const std::string& label) { return ui->button(s(label), 0, 0); }
void debug(const std::string& v) { if (debugLog && logSink) { const auto line = "[SET UI] " + v; logSink(line.c_str()); } }
KilnResult openToolsFor(KilnStr mod, bool prompt) {
    std::lock_guard lifecycle(lifecycleMx);
    if (!active.load() || mod.len > 256 || (!mod.ptr && mod.len)) return KILN_ERR_UNAVAILABLE;
    { std::lock_guard lock(mx); requestedMod = mod.len ? std::string(mod.ptr, mod.len) : ""; requestPending = true; requestedPrompt = prompt; }
    opened = true;
    ui->set_demand(1); ui->set_capture(1);
    return KILN_OK;
}
KilnResult KILN_CALL openTools(KilnStr mod) { return openToolsFor(mod, false); }
const setui::Api api{{sizeof(setui::Api), 1}, &openTools};
std::string read(const std::filesystem::path& folder, const char* name) {
    FILE* f = kset::sandbox::open(utf8(folder), name, "rb");
    if (!f) return "<not available>\n";
    std::string out; char b[4096]; size_t n;
    while (out.size() < (1u << 20) && (n = fread(b, 1, std::min(sizeof b, (1u << 20) - out.size()), f))) out.append(b, n);
    const bool truncated = fgetc(f) != EOF; fclose(f);
    if (truncated) out += "\n<truncated at 1 MiB>\n";
    return out;
}
std::string lower(std::string v) { for (auto& c : v) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c))); return v; }
std::string redact(std::string out) {
    for (const char* name : {"USERPROFILE", "HOME"}) if (const char* home = std::getenv(name); home && strlen(home) > 3) {
        std::string variants[] = {home, home};
        std::replace(variants[1].begin(), variants[1].end(), '\\', '/');
        for (const auto& v : variants) {
            const auto folded = lower(out), needle = lower(v); std::string result; size_t from = 0, at;
            while ((at = folded.find(needle, from)) != std::string::npos) { result.append(out, from, at - from); result += "%USERPROFILE%"; from = at + v.size(); }
            result.append(out, from); out = std::move(result);
        }
    }
    return out;
}
bool sessionMark(bool running) {
    FILE* f = kset::sandbox::open(utf8(controlDir), "set-session.txt", "wb");
    if (!f) return false;
    bool ok = fputs(running ? "running=1\n" : "running=0\n", f) >= 0; return fclose(f) == 0 && ok;
}
std::string gameCrashSummary() {
    const auto game = logsDir.parent_path().parent_path(); std::error_code ec;
    const auto since = std::filesystem::last_write_time(logsDir / "kiln.prev.log", ec);
    if (ec) return "No matching game crash report was found.";
    auto latest = since - std::chrono::seconds(60); std::string relative;
    auto dir = std::filesystem::directory_iterator(game / "crash_dumps", ec);
    unsigned checked = 0;
    for (auto end = std::filesystem::directory_iterator(); !ec && dir != end && checked++ < 256; dir.increment(ec)) {
        if (!dir->is_directory(ec) || dir->is_symlink(ec)) continue;
        const auto file = dir->path() / "enshrouded.info"; auto time = std::filesystem::last_write_time(file, ec);
        if (ec) { ec.clear(); continue; }
        if (time >= latest) { latest = time; relative = "crash_dumps/" + utf8(dir->path().filename()) + "/enshrouded.info"; }
    }
    if (relative.empty()) return "No matching game crash report was found.";
    const auto info = nlohmann::json::parse(read(game, relative.c_str()), nullptr, false);
    if (!info.is_object()) return "The matching game crash report could not be read.";
    std::string result = "Game crash report:";
    for (const auto* key : {"info", "exception", "source"}) if (info.contains(key) && info[key].is_string()) {
        auto value = info[key].get<std::string>(); if (value.size() > 200) value.resize(200);
        for (auto& c : value) if (static_cast<unsigned char>(c) < 32) c = ' ';
        result += " " + std::string(key) + "=" + value;
    }
    return redact(std::move(result));
}
bool copyClipboard(const std::string& value) {
#ifdef _WIN32
    const int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0);
    if (n <= 0) return false;
    HGLOBAL memory = GlobalAlloc(GMEM_MOVEABLE, (static_cast<size_t>(n) + 1) * sizeof(wchar_t));
    if (!memory) return false;
    auto* data = static_cast<wchar_t*>(GlobalLock(memory));
    if (!data) { GlobalFree(memory); return false; }
    const bool converted = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), data, n) == n;
    data[n] = 0; GlobalUnlock(memory);
    HWND owner = GetForegroundWindow(); DWORD process = 0;
    if (owner) GetWindowThreadProcessId(owner, &process);
    if (!converted || !owner || process != GetCurrentProcessId() || !OpenClipboard(owner)) { GlobalFree(memory); return false; }
    const bool ok = EmptyClipboard() && SetClipboardData(CF_UNICODETEXT, memory); CloseClipboard();
    if (!ok) GlobalFree(memory); return ok;
#else
    (void)value; return false;
#endif
}
void copyLog(bool previous) { message = copyClipboard(setui::logText(previous)) ? "Copied redacted logs. Review before sharing; nothing was uploaded." : "Clipboard unavailable. Save the local report instead."; }
std::string report() {
    std::string out = "Kiln SET 0.2.0 support report\nGame build: " + std::to_string(rt::gameBuild()) + "\n";
    for (const auto& f : rt::features()) out += f.id + ": compatible=" + (f.compatible ? "yes" : "no") + " available=" + (f.available ? "yes" : "no") + " " + f.reason + "\n";
    for (const auto& m : rt::modReports()) out += "mod " + m.name + ": " + m.status + " " + m.reason + "\n";
    for (const auto& t : hk::info()) for (const auto& r : t.rules) out += "rule " + r.key + ": hits=" + std::to_string(r.hits) + " faults=" + std::to_string(r.faults) + " refused=" + std::to_string(r.refused) + " " + r.conflict + "\n";
    for (const auto& d : hk::diagnostics()) out += d + "\n";
    KilnUiStatus status{sizeof status};
    if (ui->status && ui->status(&status) == KILN_OK) out += "Overlay: hooked=" + std::to_string(status.hooked) + " device=" + std::to_string(status.device) + " swapchain=" + std::to_string(status.swapchain) + " error=" + std::string(status.error, strnlen(status.error, sizeof status.error)) + "\n";
    out += "\nCurrent kiln.log:\n" + read(logsDir, "kiln.log");
    out += "\nPrevious kiln.prev.log (if retained):\n" + read(logsDir, "kiln.prev.log");
    return redact(std::move(out));
}
void saveReport() {
    const std::string out = report();
    FILE* f = kset::sandbox::open(utf8(controlDir), "set-report.txt", "wb");
    if (!f) { message = "Could not save report (control folder missing or unsafe path)"; return; }
    bool ok = fwrite(out.data(), 1, out.size(), f) == out.size(); ok = fclose(f) == 0 && ok;
    message = ok ? "Saved " + utf8(controlDir / "set-report.txt") + ". Review it before sharing; nothing was uploaded." : "Report save failed";
}
Buffer& buffer(const std::string& key, const std::string& value, size_t capacity) {
    auto& b = buffers[key];
    if (b.data.empty() || b.original != value || b.data.size() != capacity) {
        b.original = value; b.data.assign(capacity, 0); memcpy(b.data.data(), value.data(), std::min(value.size(), capacity - 1));
    }
    return b;
}
void setting(const std::string& mod, const mset::Field& f, uint64_t revision) {
    ui->push_id(s(f.key)); bool changed = false; double num = f.num; std::string value = f.text;
    if (f.type == mset::Bool) { bool v = num != 0; changed = ui->checkbox(s(f.label), &v); num = v; }
    else if (f.type == mset::Choice) {
        if (ui->header.size >= offsetof(KilnUiApi, combo) + sizeof ui->combo && ui->combo) {
            std::string items; for (const auto& v : f.values) { items += v; items += '\0'; } items += '\0';
            int32_t index = static_cast<int32_t>(num); changed = ui->combo(s(f.label), &index, items.c_str()); num = index;
        } else {
            text(f.label); for (size_t i = 0; i < f.values.size(); ++i) if (ui->selectable(s(f.values[i]), num == static_cast<double>(i))) { num = static_cast<double>(i); changed = true; }
        }
    } else if (f.type == mset::Text) {
        auto& b = buffer(mod + "/" + f.key, value, f.maxLen + 1);
        changed = ui->input_text(s(f.label), b.data.data(), b.data.size(), 0); value = b.data.data();
    } else {
        // The v1 drawing ABI has no double/int64 widgets. Never round a 2^53 integer or double through float32.
        if (f.type == mset::Int && f.lo >= INT32_MIN && f.hi <= INT32_MAX) {
            int32_t n = static_cast<int32_t>(num); changed = ui->slider_int(s(f.label), &n, static_cast<int32_t>(f.lo), static_cast<int32_t>(f.hi)); num = n;
        } else {
            char v[64]; snprintf(v, sizeof v, "%.17g", num);
            auto& b = buffer(mod + "/" + f.key, v, 128);
            if (ui->input_text(s(f.label), b.data.data(), b.data.size(), 0)) {
                char* end = nullptr; num = strtod(b.data.data(), &end);
                changed = end != b.data.data() && !*end && std::isfinite(num) && (f.type != mset::Int || std::trunc(num) == num);
            }
        }
    }
    if (ui->is_item_hovered()) ui->set_tooltip(s(f.key));
    if (changed) mset::set(mod, f.key, num, value, true, revision);
    ui->pop_id();
}
void settings() {
    if (button("All mods")) modFilter.clear();
    const auto pages = mset::pages();
    bool found = false;
    for (const auto& p : pages) {
        if (!modFilter.empty() && modFilter != p.mod) continue;
        found = true;
        ui->push_id(s(p.mod));
        if (ui->collapsing_header(s(p.mod), 1u << 5)) for (const auto& f : p.fields) setting(p.mod, f, p.revision);
        ui->pop_id();
    }
    if (!found) text(modFilter.empty() ? "No mod has settings (Settings.define)." : "This mod has not defined a settings page. Choose All mods to see other pages.");
    text("Edits update values immediately; onChange runs at the next tick. Saves within one second.");
}
void compatibility() {
    for (const auto& f : rt::features()) {
        ui->text_colored(f.available ? 0x80dd99ff : f.compatible ? 0xffd577ff : 0xff8888ff, s(f.label + " [" + f.id + "]"));
        text(std::string("Compatible: ") + (f.compatible ? "yes" : "no") + "   Available: " + (f.available ? "yes" : "no"));
        if (!f.reason.empty()) ui->text_wrapped(s(f.reason));
    }
}
void hooks() {
    for (const auto& t : hk::info()) {
        const std::string id = std::to_string(t.fn); ui->push_id(s(id));
        text("Target " + id + ": " + std::to_string(t.calls) + " calls, " + std::to_string(t.ruleCalls) + " rule calls");
        bool timing = t.timing; if (ui->checkbox(s("Capture timing"), &timing)) hk::setTiming(t.fn, timing);
        for (const auto& r : t.rules) {
            text(r.key + (r.active ? " [active]" : " [inactive]") + (r.faulted ? " [faulted]" : ""));
            text("Hits: " + std::to_string(r.hits) + "  Faults: " + std::to_string(r.faults) + "  Refused: " + std::to_string(r.refused));
            if (!r.conflict.empty()) text("Conflict: " + r.conflict);
        }
        for (const auto& sub : t.subs) text(sub.key + (sub.active ? " [active]" : " [inactive]") + " delivered=" + std::to_string(sub.delivered) + " overflow=" + std::to_string(sub.dropped) + " discarded=" + std::to_string(sub.discarded) + " high-water=" + std::to_string(sub.highWater) + "/" + std::to_string(sub.capacity) + " contention=" + std::to_string(sub.lockWaits));
        ui->pop_id();
    }
    for (const auto& d : hk::diagnostics()) text(d);
}
void bindings() {
    bindingsOpen = true;
    if (cancelRemap.exchange(false)) { binding.clear(); uint32_t mods; ui->take_pressed_key(&mods); }
    int dead = 0; auto all = rt::bindings(dead);
    if (dead && button("Remove dead bindings")) rt::removeDeadBindings();
    for (const auto& b : all) {
        ui->push_id(s(b.key)); text(b.mod + ": " + b.label + (b.input ? " [input]" : ""));
        if (button(binding == b.key ? "Press a key (Escape cancels)" : b.combo.empty() ? "Bind" : b.combo)) { binding = b.key; remapping = true; uint32_t mods; ui->take_pressed_key(&mods); }
        ui->same_line(0, -1); if (button("Unbind")) { rt::setBinding(b.key, 0, 0); binding.clear(); remapping = false; }
        ui->pop_id();
    }
    if (!binding.empty()) {
        uint32_t mods = 0; const auto vk = ui->take_pressed_key(&mods);
        const bool modifier = (vk >= 0x10 && vk <= 0x12) || (vk >= 0xa0 && vk <= 0xa5);
        if (vk && !modifier) { if (vk != 0x1b) rt::setBinding(binding, static_cast<int>(mods & 7), static_cast<int>(vk)); binding.clear(); remapping = false; }
    }
}
void entities() {
    entitiesOpen = true;
    ui->input_text(s("Component filter"), component, sizeof component, 0);
    ui->input_text(s("Entity id (decimal)"), entity, sizeof entity, 0);
    if (button("Select id")) { uint32_t id = 0; auto [end, ec] = std::from_chars(entity, entity + strlen(entity), id); if (ec == std::errc{} && !*end) { selected = id; std::lock_guard lifecycle(lifecycleMx); if (active && opened) setinspect::request(true, component, selected, true); } }
    { std::lock_guard lifecycle(lifecycleMx); if (active && opened) setinspect::request(true, component, selected); }
    auto v = setinspect::view();
    if (v.off) { text("Inspector faulted; disabled until the game restarts. Lua mods remain running."); return; }
    if (!v.inWorld) { text("Not in a world you host"); return; }
    text("Showing up to 200 of " + std::to_string(v.matched) + " matching entities");
    if (ui->begin_child(s("Entity list"), 260, 180, 0)) for (const auto& r : v.rows) {
        std::string label = std::to_string(r.entity);
        if (r.distance >= 0) { char b[64]; snprintf(b, sizeof b, " (%.1f m)", r.distance); label += b; }
        if (ui->selectable(s(label), selected == r.entity)) { selected = r.entity; std::lock_guard lifecycle(lifecycleMx); if (active && opened) setinspect::request(true, component, selected, true); }
    }
    ui->end_child();
    text("Nearest first when a player position is available; otherwise entity id order.");
    if (!v.pins.empty()) text("Watched values (up to 64; missing entities stay gone until unpinned):");
    for (const auto& p : v.pins) {
        ui->push_id(s(std::to_string(p.entity) + "/" + p.field.component + "/" + p.field.path));
        text(std::to_string(p.entity) + " " + p.field.component + "." + p.field.path + " = " + (p.gone ? "<gone>" : p.have ? p.field.value : "<component unavailable>"));
        if (button("Unwatch")) setinspect::unpin(p.entity, p.field);
        ui->pop_id();
    }
    if (v.gone) text("Selected entity is gone; explicitly select an id to inspect again.");
    if (!v.note.empty()) text(v.note);
    if (v.selected != selected) return;
    text("Tick-side copies; refresh 500 ms. Bounded to 512 fields, depth 6, 32 array elements. Dynamic data is read-only.");
    for (const auto& f : v.fields) {
        ui->push_id(s(f.component + "." + f.path));
        if (f.editable) { auto& b = buffer("entity/" + std::to_string(v.serial) + "/" + f.component + "/" + f.path, f.value, 256); ui->input_text(s(f.component + "." + f.path), b.data.data(), b.data.size(), 0); ui->same_line(0, -1); if (button("Apply")) setinspect::edit(v.serial, f, b.data.data()); }
        else text(f.component + "." + f.path + " = " + f.value);
        if (f.editable) { ui->same_line(0, -1); if (button("Watch")) setinspect::pin(v.serial, v.selected, f); }
        ui->pop_id();
    }
}
}
const setui::Api* setui::service() { return &api; }
void setui::configure(std::filesystem::path logs, std::filesystem::path control, void (*sink)(const char*)) {
    logsDir = std::move(logs); controlDir = std::move(control); logSink = sink;
    debugLog = read(controlDir, "set-ui.ini").find("debugLog=0") == std::string::npos;
    lastUnclean = read(controlDir, "set-session.txt").starts_with("running=1");
    crashSummary = lastUnclean ? gameCrashSummary() : "";
    sessionStarted = sessionMark(true);
}
void setui::bind(const KilnUiApi* value, KilnOwner owner) {
    ui = value; active = ui != nullptr;
    if (!ui) return;
    if (ui->header.size >= offsetof(KilnUiApi, menu_add) + sizeof ui->menu_add && ui->menu_add) {
        for (uint32_t menu : {KILN_UI_MENU_MAIN, KILN_UI_MENU_PAUSE}) {
            KilnHandle h = 0;
            if (ui->menu_add(owner, menu, 90, s("SET tools"), [](KilnHandle, void*) { openTools({nullptr, 0}); }, nullptr, &h) == KILN_OK) subscriptions.push_back(h);
        }
    }
    if (ui->subscribe_key) {
        KilnHandle h = 0;
        if (ui->subscribe_key(owner, [](const KilnUiKey* key, void*) -> bool {
            if (!key || !key->down || key->vk != 0x1b) return false;
            std::lock_guard lifecycle(lifecycleMx);
            if (!opened) return false;
            if (remapping.exchange(false)) { cancelRemap = true; return true; }
            opened = false;
            ui->set_capture(0); ui->set_demand(0); setinspect::request(false); return true;
        }, nullptr, &h) == KILN_OK) subscriptions.push_back(h);
    }
    if (lastUnclean) openToolsFor({nullptr, 0}, true);
}
bool setui::isOpen() { return active && opened; }
std::string setui::logText(bool previous) {
    std::string out;
    if (previous && lastUnclean) out = "The last SET session did not close normally (crash or external termination). " + crashSummary + "\n\n";
    return redact(out + read(logsDir, previous ? "kiln.prev.log" : "kiln.log"));
}
void setui::line(const char* v) { std::lock_guard lock(mx); lines.emplace_back(v ? v : "", v ? strnlen(v, 4096) : 0); while (lines.size() > 2000) lines.pop_front(); }
void setui::stop() {
    std::vector<KilnHandle> handles;
    {
        std::lock_guard lifecycle(lifecycleMx);
        active = opened = remapping = cancelRemap = false;
        if (ui) { ui->set_capture(0); ui->set_demand(0); handles.swap(subscriptions); }
        setinspect::request(false);
        if (sessionStarted) { sessionMark(false); sessionStarted = false; }
    }
    // Unsubscribe may wait for a running key callback; never hold its lifecycle lock here.
    for (auto h : handles) ui->unsubscribe(h);
}
void setui::draw() {
    if (!active || !opened) return;
    bool newlyOpened = false;
    { std::lock_guard lock(mx); if (requestPending) { modFilter = requestedMod; promptOnly = requestedPrompt; requestPending = false; buffers.clear(); newlyOpened = true; } }
    if (newlyOpened) { binding.clear(); remapping = cancelRemap = false; debug("Opened tools; build=" + std::to_string(rt::gameBuild())); }
    bool open = true; entitiesOpen = bindingsOpen = false;
    ui->set_next_window_size(760, 600, KILN_UI_COND_FIRST_USE);
    if (ui->begin(s("Shrouded Engine Tweaks###kiln.set.tools"), &open, 0)) {
        if (lastUnclean) {
            ui->text_wrapped(s("Last Session: SET did not close normally (a crash or external termination). " + crashSummary));
            if (button("Copy last session's logs")) copyLog(true);
            if (button("Save local support report")) saveReport();
            if (!message.empty()) ui->text_wrapped(s(message));
            if (button("Dismiss")) { lastUnclean = false; if (promptOnly) open = false; }
        }
        if (!promptOnly && button("Reload mods")) { rt::requestReload(); debug("Reload requested"); }
        if (!promptOnly && ui->begin_tab_bar(s("SET tools"))) {
            auto tab = [&](const char* name, auto fn) { if (ui->begin_tab_item(s(name))) { fn(); ui->end_tab_item(); } };
            tab("Mod Settings", settings); tab("Compatibility", compatibility); tab("Entities", entities); tab("Hooks", hooks); tab("Bindings", bindings);
            tab("Console", [] { if (ui->input_text(s("Lua"), console, sizeof console, kEnterReturnsTrue) || button("Run")) { if (*console) { setui::line((std::string("> ") + console).c_str()); rt::queue(console); console[0] = 0; } } if (button("Clear")) { std::lock_guard lock(mx); lines.clear(); } std::deque<std::string> copy; { std::lock_guard lock(mx); copy = lines; } for (const auto& l : copy) text(l); });
            tab("Bug Report", [] {
                text("Reports contain logs, mod names, compatibility and hook/overlay diagnostics. Review before sharing. No upload.");
                if (ui->checkbox(s("Debug logs"), &debugLog)) { FILE* f = kset::sandbox::open(utf8(controlDir), "set-ui.ini", "wb"); if (f) { auto v = debugLog ? "debugLog=1\n" : "debugLog=0\n"; bool ok = fputs(v, f) >= 0; ok = fclose(f) == 0 && ok; if (!ok) message = "Could not save debug setting"; } else message = "Could not save debug setting"; }
                if (button("Copy to Clipboard")) copyLog(false);
                if (button("Copy last session's logs")) copyLog(true);
                if (button("Save local support report")) saveReport();
                text("Review the logs before sharing them with Kiln support. Nothing is sent automatically.");
                if (!message.empty()) ui->text_wrapped(s(message));
            });
            ui->end_tab_bar();
        }
    }
    ui->end();
    bool closed = false;
    {
        std::lock_guard lifecycle(lifecycleMx);
        closed = !open || !opened || !active;
        if (closed || !entitiesOpen) setinspect::request(false, component, selected);
        if (closed) { opened = false; promptOnly = false; remapping = cancelRemap = false; binding.clear(); buffers.clear(); ui->set_capture(0); ui->set_demand(0); }
        else if (!bindingsOpen) { binding.clear(); remapping = cancelRemap = false; }
    }
    if (closed) debug("Closed tools");
    if (buffers.size() > 2048) buffers.clear();
}
