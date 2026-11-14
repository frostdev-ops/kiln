// SPDX-License-Identifier: MIT
// kiln-fptool: the core's ABI fingerprint pass (native/core/src/services/hooks_fingerprint.cpp) on an exe file, for
// tools/sigport/sigdb_check.py. Reads the exe as data (it is never run), lays its sections out at their RVAs and
// answers a request file:
//   kiln-fptool <exe> <request.json>
//   request:  {"structs": {...}, "sigs": {"<symbol>": {"rva": 1234, "sig": {...}}}, "rvas": [1234, ...]}
//   output:   {"sigs": {"<symbol>": {"verdict": "match|mismatch|unknown|bad", "text": "...", "fingerprint": {...}}},
//              "rvas": {"0x4d2": {...fingerprint...}}}
// Build: tools/sigport/fptool/build.sh (host compiler, the vendored Zydis).
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"
#include "services/hooks_fingerprint.h"

namespace fp = kiln::hooks::fp;
using json = nlohmann::ordered_json;

namespace {

struct Image {
    std::vector<uint8_t> mem;  // sections at their RVAs
    uint64_t text_lo = 0, text_hi = 0;
    fp::Pdata pdata;
    bool has_pdata = false;
};

template <class T>
T rd(const std::vector<uint8_t>& v, size_t at) {
    T x{};
    if (at + sizeof(T) <= v.size()) std::memcpy(&x, v.data() + at, sizeof(T));
    return x;
}

bool load(const char* path, Image* img, std::string* err) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return *err = "cannot open the exe", false;
    const std::vector<uint8_t> file((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    if (file.size() < 0x200 || rd<uint16_t>(file, 0) != 0x5A4D) return *err = "not a PE file", false;
    const uint32_t nt = rd<uint32_t>(file, 0x3C);
    if (rd<uint32_t>(file, nt) != 0x00004550 || rd<uint16_t>(file, nt + 24) != 0x20B) return *err = "not a PE32+ file", false;
    const uint16_t nsec = rd<uint16_t>(file, nt + 6), optsize = rd<uint16_t>(file, nt + 20);
    const uint32_t size_of_image = rd<uint32_t>(file, nt + 24 + 56), size_of_headers = rd<uint32_t>(file, nt + 24 + 60);
    if (size_of_image < 0x1000 || size_of_image > (1u << 30)) return *err = "implausible image size", false;
    img->mem.assign(size_of_image, 0);
    std::memcpy(img->mem.data(), file.data(), std::min<size_t>(size_of_headers, file.size()));
    const size_t sec = nt + 24 + optsize;
    for (uint16_t i = 0; i < nsec; ++i) {
        const size_t s = sec + i * 40u;
        const uint32_t vsize = rd<uint32_t>(file, s + 8), rva = rd<uint32_t>(file, s + 12), rsize = rd<uint32_t>(file, s + 16),
                       raw = rd<uint32_t>(file, s + 20), flags = rd<uint32_t>(file, s + 36);
        const size_t n = std::min<size_t>(std::min(rsize, vsize ? vsize : rsize), file.size() > raw ? file.size() - raw : 0);
        if (static_cast<uint64_t>(rva) + n > img->mem.size()) return *err = "a section lies outside the image", false;
        std::memcpy(img->mem.data() + rva, file.data() + raw, n);
        if ((flags & 0x20000000) && !img->text_hi) img->text_lo = rva, img->text_hi = rva + std::max(vsize, rsize);  // first executable section
    }
    if (!img->text_hi) return *err = "no code section", false;
    img->has_pdata = fp::pdata_init(&img->pdata, img->mem.data(), img->mem.size());
    return true;
}

// The pass works on RVAs here (no relocation is needed: it never follows absolute addresses).
size_t bytes(void* ctx, uint64_t addr, uint8_t* out, size_t n) {
    const Image& img = *static_cast<const Image*>(ctx);
    if (addr < img.text_lo || addr >= img.text_hi) return 0;
    n = static_cast<size_t>(std::min<uint64_t>(n, img.text_hi - addr));
    n = static_cast<size_t>(std::min<uint64_t>(n, img.mem.size() - addr));
    std::memcpy(out, img.mem.data() + addr, n);
    return n;
}
bool function(void* ctx, uint64_t addr, uint64_t* primary, uint64_t* next) {
    return fp::pdata_lookup(static_cast<const Image*>(ctx)->pdata, addr, primary, next);
}

}  // namespace

int main(int argc, char** argv) {
    if (argc != 3) {
        std::fprintf(stderr, "usage: kiln-fptool <exe> <request.json>\n");
        return 2;
    }
    Image img;
    std::string err;
    if (!load(argv[1], &img, &err)) {
        std::fprintf(stderr, "kiln-fptool: %s: %s\n", argv[1], err.c_str());
        return 2;
    }
    json req;
    try {
        std::ifstream in(argv[2]);
        req = json::parse(in);
    } catch (const std::exception& e) {
        std::fprintf(stderr, "kiln-fptool: %s: %s\n", argv[2], e.what());
        return 2;
    }
    fp::Reader reader;
    reader.ctx = &img;
    reader.bytes = &bytes;
    reader.function = img.has_pdata ? &function : nullptr;
    json out;
    out["sigs"] = json::object();
    out["rvas"] = json::object();
    fp::Structs structs;
    if (req.contains("structs") && !fp::parse_structs(req["structs"].dump(), &structs, &err)) {
        std::fprintf(stderr, "kiln-fptool: %s\n", err.c_str());
        return 2;
    }
    if (req.contains("sigs"))
        for (auto& [name, e] : req["sigs"].items()) {
            json r;
            fp::Sig sig;
            if (!fp::parse_sig(e["sig"].dump(), structs, e.value("trust_allowed", false), &sig, &err)) {
                r["verdict"] = "bad";
                r["text"] = err;
            } else {
                const fp::Fingerprint f = fp::compute(reader, e["rva"].get<uint64_t>());
                std::string text;
                r["verdict"] = fp::verdict_name(fp::check_sig(sig, f, &text));
                r["text"] = text;
                r["fingerprint"] = json::parse(fp::to_json(f));
                r["positions"] = sig.positions();
            }
            out["sigs"][name] = r;
        }
    if (req.contains("rvas"))
        for (auto& v : req["rvas"]) {
            char key[32];
            std::snprintf(key, sizeof key, "0x%llx", static_cast<unsigned long long>(v.get<uint64_t>()));
            out["rvas"][key] = json::parse(fp::to_json(fp::compute(reader, v.get<uint64_t>())));
        }
    std::cout << out.dump(2) << "\n";
    return 0;
}
