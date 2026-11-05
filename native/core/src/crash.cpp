// SPDX-License-Identifier: MIT
// Compiled as part of guard.cpp (#include at its end) while native/CMakeLists.txt lists core sources explicitly.
#include "crash.h"

#include <atomic>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

#include "image.h"
#include "log.h"

namespace kiln::crash {
namespace {

std::atomic<int> g_reports{0};
constexpr int kMaxReports = 10;

// No C++ objects: runs inside a filter, under its own guard (a bad context must not fault the dispatcher).
void walk(const CONTEXT* source, Capture* cap) {
    __try {
        CONTEXT ctx = *source;
        for (cap->frames = 0; cap->frames < kMaxFrames && ctx.Rip;) {
            cap->stack[cap->frames++] = ctx.Rip;
            DWORD64 image = 0;
            PRUNTIME_FUNCTION fn = RtlLookupFunctionEntry(ctx.Rip, &image, nullptr);
            if (!fn) {  // leaf function (or code without unwind data): the return address is on top of the stack
                ctx.Rip = *reinterpret_cast<const DWORD64*>(ctx.Rsp);
                ctx.Rsp += 8;
                continue;
            }
            PVOID handler_data = nullptr;
            DWORD64 establisher = 0;
            RtlVirtualUnwind(UNW_FLAG_NHANDLER, image, ctx.Rip, fn, &ctx, &handler_data, &establisher, nullptr);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
    }
}

std::string where_is(uintptr_t address) {
    char buf[320];
    HMODULE module = nullptr;
    if (!address || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                        reinterpret_cast<LPCWSTR>(address), &module) || !module) {
        std::snprintf(buf, sizeof buf, "0x%llx (no module: JIT code or freed)", static_cast<unsigned long long>(address));
        return buf;
    }
    wchar_t path[MAX_PATH] = {};
    GetModuleFileNameW(module, path, MAX_PATH);
    const std::string name = to_utf8(std::filesystem::path(path).filename().wstring());
    std::snprintf(buf, sizeof buf, "%s+0x%llx", name.c_str(),
                  static_cast<unsigned long long>(address - reinterpret_cast<uintptr_t>(module)));
    return buf;
}

const char* code_name(DWORD code) {
    switch (code) {
        case EXCEPTION_ACCESS_VIOLATION: return "access violation";
        case EXCEPTION_STACK_OVERFLOW: return "stack overflow";
        case EXCEPTION_ILLEGAL_INSTRUCTION: return "illegal instruction";
        case EXCEPTION_PRIV_INSTRUCTION: return "privileged instruction";
        case EXCEPTION_INT_DIVIDE_BY_ZERO: return "integer divide by zero";
        case EXCEPTION_IN_PAGE_ERROR: return "in-page error";
        case EXCEPTION_BREAKPOINT: return "breakpoint";
        case EXCEPTION_DATATYPE_MISALIGNMENT: return "misaligned access";
        case 0xE06D7363: return "C++ exception";
        case 0xE0434352: return "CLR exception";
        default: return "exception";
    }
}

std::string read_file(const std::filesystem::path& file) {
    std::ifstream in(file, std::ios::binary);
    if (!in) return {};
    std::ostringstream s;
    s << in.rdbuf();
    return s.str();
}

std::string tail(const std::filesystem::path& file, int lines) {
    HANDLE h = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return "(kiln.log unreadable)\n";
    LARGE_INTEGER size{};
    GetFileSizeEx(h, &size);
    const LONGLONG take = std::min<LONGLONG>(size.QuadPart, 64 * 1024);
    LARGE_INTEGER at{};
    at.QuadPart = size.QuadPart - take;
    SetFilePointerEx(h, at, nullptr, FILE_BEGIN);
    std::string data(static_cast<size_t>(take), '\0');
    DWORD read = 0;
    ReadFile(h, data.data(), static_cast<DWORD>(take), &read, nullptr);
    CloseHandle(h);
    data.resize(read);
    size_t pos = data.size();
    for (int n = 0; n <= lines && pos > 0;) {
        pos = data.rfind('\n', pos - 1);
        if (pos == std::string::npos) {
            pos = 0;
            break;
        }
        ++n;
        if (n > lines) ++pos;
    }
    return data.substr(pos == 0 ? 0 : pos);
}

}  // namespace

int filter(EXCEPTION_POINTERS* ep, Capture* cap, unsigned long* code) {
    const EXCEPTION_RECORD* rec = ep->ExceptionRecord;
    *code = rec->ExceptionCode;
    cap->code = rec->ExceptionCode;
    cap->address = reinterpret_cast<uintptr_t>(rec->ExceptionAddress);
    cap->nparams = rec->NumberParameters < 2 ? rec->NumberParameters : 2;
    for (uint32_t i = 0; i < cap->nparams; ++i) cap->info[i] = rec->ExceptionInformation[i];
    cap->rsp = ep->ContextRecord->Rsp;
    walk(ep->ContextRecord, cap);
    return EXCEPTION_EXECUTE_HANDLER;
}

void report(const Capture& cap, const char* where, const void* callback) {
    if (g_reports.fetch_add(1) >= kMaxReports) return;
    try {
        SYSTEMTIME t;
        GetLocalTime(&t);
        char stamp[64];
        std::snprintf(stamp, sizeof stamp, "%04u-%02u-%02u_%02u.%02u.%02u.%03u", t.wYear, t.wMonth, t.wDay, t.wHour,
                      t.wMinute, t.wSecond, t.wMilliseconds);
        const auto dir = paths().kiln / L"crash-reports";
        std::error_code ec;
        std::filesystem::create_directories(dir, ec);
        const auto file = dir / to_wide(std::string("crash-") + stamp + "-native-fault.txt");

        const std::string culprit = where_is(reinterpret_cast<uintptr_t>(callback));
        std::string s = "---- Kiln crash report ----\n";
        char line[512];
        std::snprintf(line, sizeof line, "Time: %04u-%02u-%02u %02u:%02u:%02u.%03u local\nKind: native-fault\n", t.wYear,
                      t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
        s += line;
        std::snprintf(line, sizeof line, "Description: %s (0x%08lx) in a %s (%s); it was removed, the server runs on\n\n",
                      code_name(cap.code), cap.code, where, culprit.c_str());
        s += line;
        s += "-- System --\n";
        s += "Kiln: " + std::string(kVersion) + " (core)\n";
        s += "Game: " + to_utf8(paths().exe.filename().wstring()) + " sha256 " + image().sha256 + "\n";
        std::snprintf(line, sizeof line, "Process: pid %lu, thread %lu\n", GetCurrentProcessId(), GetCurrentThreadId());
        s += line;
        s += "\n-- Mods (load order) --\n";
        std::string mods = read_file(paths().logs / L"mods.txt");
        if (const auto nl = mods.find('\n'); !mods.empty() && mods[0] == '#' && nl != std::string::npos) mods.erase(0, nl + 1);
        s += mods.empty() ? std::string("   (no mods.txt: Kiln.Host has not loaded mods)\n") : mods;
        s += "\n-- Details --\n";
        std::snprintf(line, sizeof line, "Exception: 0x%08lx (%s) at %s\n", cap.code, code_name(cap.code),
                      where_is(cap.address).c_str());
        s += line;
        if (cap.code == EXCEPTION_ACCESS_VIOLATION && cap.nparams == 2) {
            std::snprintf(line, sizeof line, "Access: %s of 0x%llx\n",
                          cap.info[0] == 1 ? "write" : cap.info[0] == 8 ? "execute" : "read",
                          static_cast<unsigned long long>(cap.info[1]));
            s += line;
        }
        std::snprintf(line, sizeof line, "Callback: %s, called as a %s\nStack (unwound from the fault, rsp 0x%llx):\n",
                      culprit.c_str(), where, static_cast<unsigned long long>(cap.rsp));
        s += line;
        for (uint32_t i = 0; i < cap.frames; ++i) {
            std::snprintf(line, sizeof line, "  #%-2u %s\n", i, where_is(cap.stack[i]).c_str());
            s += line;
        }
        s += "\n-- Last kiln.log lines --\n" + tail(paths().logs / L"kiln.log", 60);

        const auto tmp = std::filesystem::path(file.wstring() + L".tmp");
        {
            std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
            out << s;
        }
        MoveFileExW(tmp.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING);
        KLOG_E("crash report: %s", to_utf8(file.wstring()).c_str());
    } catch (...) {
        KLOG_E("crash report could not be written");
    }
}

}  // namespace kiln::crash
