// SPDX-License-Identifier: MIT
#include "image.h"

#include <bcrypt.h>

#include <cstdio>
#include <cstring>
#include <vector>

#include "log.h"

namespace kiln {
namespace {

ImageInfo g_image;

std::string sha256_file(const std::filesystem::path& file) {
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    std::string hex;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) return hex;
    if (BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) == 0) {
        HANDLE h = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                               FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
        if (h != INVALID_HANDLE_VALUE) {
            std::vector<uint8_t> buf(1 << 20);
            DWORD got = 0;
            bool ok = true;
            while (ReadFile(h, buf.data(), static_cast<DWORD>(buf.size()), &got, nullptr) && got)
                ok = ok && BCryptHashData(hash, buf.data(), got, 0) == 0;
            CloseHandle(h);
            uint8_t digest[32];
            if (ok && BCryptFinishHash(hash, digest, sizeof digest, 0) == 0) {
                char b[3];
                for (uint8_t d : digest) {
                    std::snprintf(b, sizeof b, "%02x", d);
                    hex += b;
                }
            }
        }
        BCryptDestroyHash(hash);
    }
    BCryptCloseAlgorithmProvider(alg, 0);
    return hex;
}

struct CvInfo {
    DWORD signature;  // 'RSDS'
    GUID guid;
    DWORD age;
    char path[1];     // NUL-terminated PDB path
};

std::string lower(std::string s) {
    for (char& c : s) c = (c >= 'A' && c <= 'Z') ? static_cast<char>(c - 'A' + 'a') : c;
    return s;
}

// Server or client: the PDB name the build machine wrote into the exe (game38_server\...\enshrouded_server.pdb vs
// game38_client\...\enshrouded.pdb) survives renames of the exe; the exe name is the fallback.
void detect_side() {
    const std::string pdb = lower(g_image.pdb);
    const std::string exe = lower(to_utf8(paths().exe.filename().wstring()));
    if (pdb == "enshrouded_server.pdb") {
        g_image.side = KILN_SIDE_SERVER, g_image.side_source = "pdb " + g_image.pdb;
    } else if (pdb == "enshrouded.pdb") {
        g_image.side = KILN_SIDE_CLIENT, g_image.side_source = "pdb " + g_image.pdb;
    } else if (exe == "enshrouded_server.exe") {
        g_image.side = KILN_SIDE_SERVER, g_image.side_source = "exe name " + exe;
    } else if (exe == "enshrouded.exe") {
        g_image.side = KILN_SIDE_CLIENT, g_image.side_source = "exe name " + exe;
    } else {
        g_image.side = KILN_SIDE_SERVER, g_image.side_source = "default (unknown exe " + exe + ")";
    }
}

}  // namespace

const ImageInfo& image() { return g_image; }

const char* side_name(uint32_t side) { return side == KILN_SIDE_CLIENT ? "client" : "server"; }

bool init_image() {
    auto* base = reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (!base || dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64) return false;
    g_image.base = reinterpret_cast<uintptr_t>(base);
    g_image.size = nt->OptionalHeader.SizeOfImage;
    g_image.timestamp = nt->FileHeader.TimeDateStamp;
    auto* sec = IMAGE_FIRST_SECTION(nt);
    for (int i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++sec) {
        if (std::memcmp(sec->Name, ".text", 6) == 0) {
            g_image.text = base + sec->VirtualAddress;
            g_image.text_size = sec->Misc.VirtualSize;
        }
    }
    const auto& dbg = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DEBUG];
    if (dbg.VirtualAddress && dbg.Size) {
        auto* d = reinterpret_cast<IMAGE_DEBUG_DIRECTORY*>(base + dbg.VirtualAddress);
        for (size_t i = 0; i < dbg.Size / sizeof(IMAGE_DEBUG_DIRECTORY); ++i, ++d) {
            if (d->Type != IMAGE_DEBUG_TYPE_CODEVIEW || !d->AddressOfRawData) continue;
            auto* cv = reinterpret_cast<CvInfo*>(base + d->AddressOfRawData);
            if (cv->signature != 0x53445352) continue;  // RSDS
            char b[64];
            const GUID& g = cv->guid;
            std::snprintf(b, sizeof b, "%08lX%04X%04X%02X%02X%02X%02X%02X%02X%02X%02X-%lu", g.Data1, g.Data2, g.Data3,
                          g.Data4[0], g.Data4[1], g.Data4[2], g.Data4[3], g.Data4[4], g.Data4[5], g.Data4[6],
                          g.Data4[7], cv->age);
            g_image.codeview = b;
            const size_t max = d->SizeOfData > offsetof(CvInfo, path) ? d->SizeOfData - offsetof(CvInfo, path) : 0;
            const std::string path(cv->path, strnlen(cv->path, max));
            const size_t slash = path.find_last_of("\\/");
            g_image.pdb = slash == std::string::npos ? path : path.substr(slash + 1);
        }
    }
    detect_side();
    const double t0 = now_ms();
    g_image.sha256 = sha256_file(paths().exe);
    KLOG_I("game image: base=%p size=0x%zx .text=0x%zx bytes sha256=%s codeview=%s (hashed in %.0f ms)",
           reinterpret_cast<void*>(g_image.base), g_image.size, g_image.text_size, g_image.sha256.c_str(),
           g_image.codeview.empty() ? "-" : g_image.codeview.c_str(), now_ms() - t0);
    KLOG_I("side: %s (%s)", side_name(g_image.side), g_image.side_source.c_str());
    return g_image.text != nullptr;
}

}  // namespace kiln
