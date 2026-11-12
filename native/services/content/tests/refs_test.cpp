// SPDX-License-Identifier: MIT
// Compile the real manifest reader and ownership decision on the host; discard unused game-memory paths at link time.
#include "../refs.cpp"
#include <cstdarg>
#include <stdexcept>

namespace kc {
const KilnApi* g_api = nullptr;
std::string test_mods_dir;
std::vector<std::string> test_warnings;
std::string mods_dir() { return test_mods_dir; }
void logf(int, const char* fmt, ...) {
    char text[2048];
    va_list args;
    va_start(args, fmt);
    std::vsnprintf(text, sizeof text, fmt, args);
    va_end(args);
    test_warnings.emplace_back(text);
}
std::string lower(std::string s) {
    for (char& c : s) if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
    return s;
}
// Host fixture GUID decoder (same Windows byte layout as game.cpp; no game memory involved).
bool guid_parse(const std::string& s, uint8_t out[16]) {
    unsigned a, b, c, d[8];
    if (s.size() != 36 || std::sscanf(s.c_str(), "%8x-%4x-%4x-%2x%2x-%2x%2x%2x%2x%2x%2x", &a, &b, &c,
        &d[0], &d[1], &d[2], &d[3], &d[4], &d[5], &d[6], &d[7]) != 11) return false;
    const uint32_t aa = a;
    const uint16_t bb = b, cc = c;
    std::memcpy(out, &aa, 4);
    std::memcpy(out + 4, &bb, 2);
    std::memcpy(out + 6, &cc, 2);
    for (int i = 0; i < 8; ++i) out[8 + i] = static_cast<uint8_t>(d[i]);
    return true;
}
}

namespace {
int checks = 0;
void check(bool yes, const char* name) {
    if (!yes) throw std::runtime_error(name);
    ++checks;
}
void malformed_entries() {
    using namespace kc;
    for (const char* key : {"kind", "type", "guid"}) {
        for (const json& bad : {json(nullptr), json(42), json(false), json::array(), json::object()}) {
            json entry = {{"name", "bad-model"}, {"kind", "resource"}, {"type", "keen::RenderModel"}, {"guid", "11111111-2222-4333-8444-555555555555"}};
            entry[key] = bad;
            std::unordered_set<G, GH> models;
            const size_t before = test_warnings.size();
            try { declared_models(json{{"entries", json::array({entry})}}, &models); }
            catch (const json::exception& e) { throw std::runtime_error(std::string("malformed ") + key + " threw: " + e.what()); }
            check(models.empty(), "malformed entry must not declare a model");
            check(test_warnings.size() == before + 1, "malformed entry must be logged once");
            check(test_warnings.back().find("bad-model") != std::string::npos, "warning must name the entry");
            check(test_warnings.back().find(key) != std::string::npos, "warning must name the invalid field");
        }
    }
}
}

int main() {
    try {
        malformed_entries();
        std::printf("refs-test: %d checks passed, 0 failed\n", checks);
        return 0;
    } catch (const std::exception& e) {
        std::fprintf(stderr, "refs-test: FAIL: %s\n", e.what());
        return 1;
    }
}
