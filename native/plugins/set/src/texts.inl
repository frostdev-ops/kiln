// SPDX-License-Identifier: MIT
// kiln-set: Res.text for any loca tag, included by lua_api.cpp before admin.inl.
//
// The game's texts are in the loca collection only the game CLIENT ships; a dedicated server's data has neither the
// resource nor its blobs (docs/re/world-status.md section 12). What a server machine can have is Kiln's catalog
// (`kiln catalog build --side client`): its `loca` section is the client's en-us table. This reads it, once, on the first
// Res.text that items and templates did not answer, and keeps the English text of every tag in memory (about 17,000 texts).
// Kiln.Content's Texts.Get (managed) reads the same file the same way.
//
// Bounded: at most 512 MB of file, 2,000,000 texts, 64 KiB each. A catalog that is missing or unreadable means "no texts"
// (Res.text then answers nil, as before) with one line in the console per attempt; another attempt is made after 60 s.
// (The headers it uses, <fstream>, <memory>, <unordered_map> and nlohmann/json.hpp, are included at the top of lua_api.cpp: this
// file is part of an anonymous namespace.)

namespace settexts {

struct Table {
    std::unordered_map<uint32_t, std::string> text;
    std::string source, build;
};

std::mutex gMx;
std::shared_ptr<const Table> gTable;                         // gMx
std::chrono::steady_clock::time_point gFailedAt{};           // gMx; time_since_epoch 0 = no failure yet
std::string gReason = "not loaded yet";                      // gMx

constexpr uintmax_t kMaxFile = 512ull << 20;
constexpr size_t kMaxTexts = 2'000'000, kMaxText = 1u << 16;

// SAX reader for `sections.loca[] {id: "0x...", text: "..."}` and `game.steamBuild / svn`; everything else is skipped
// without being kept.
struct LocaSax {
    Table* out;
    int lvl = 0;
    std::string curKey, parent;  // the latest key, and the key of the object we are in (lvl 2)
    bool inLoca = false;
    uint32_t id = 0;
    std::string text, steam, svn;
    bool haveText = false;

    bool null() { return true; }
    bool boolean(bool) { return true; }
    bool number_integer(nlohmann::json::number_integer_t) { return true; }
    bool number_unsigned(nlohmann::json::number_unsigned_t) { return true; }
    bool number_float(nlohmann::json::number_float_t, const std::string&) { return true; }
    bool binary(nlohmann::json::binary_t&) { return true; }
    bool string(std::string& v) {
        if (inLoca && lvl == 4) {
            if (curKey == "id" && v.size() > 2 && v.size() <= 10 && v[0] == '0' && (v[1] == 'x' || v[1] == 'X') &&
                v.find_first_not_of("0123456789abcdefABCDEF", 2) == std::string::npos) {
                char* end = nullptr;
                const unsigned long n = std::strtoul(v.c_str() + 2, &end, 16);
                id = (end && !*end) ? static_cast<uint32_t>(n) : 0;
            } else if (curKey == "text" && v.size() <= kMaxText) {
                text = std::move(v);
                haveText = true;
            }
        } else if (lvl == 2 && parent == "game") {
            if (curKey == "steamBuild") steam = v;
            else if (curKey == "svn") svn = v;
        }
        return true;
    }
    bool start_object(std::size_t) {
        ++lvl;
        if (lvl == 2) parent = curKey;
        if (inLoca && lvl == 4) id = 0, haveText = false, text.clear();
        return lvl <= 64;
    }
    bool end_object() {
        if (inLoca && lvl == 4 && id && haveText && out->text.size() < kMaxTexts) out->text[id] = std::move(text);
        --lvl;
        return true;
    }
    bool start_array(std::size_t) {
        ++lvl;
        if (lvl == 3 && parent == "sections" && curKey == "loca") inLoca = true;
        return lvl <= 64;
    }
    bool end_array() {
        if (inLoca && lvl == 3) inLoca = false;
        --lvl;
        return true;
    }
    bool storeKey(std::string& k) {
        curKey = k;
        if (inLoca && lvl == 4) {
            if (k == "id") id = 0;
            if (k == "text") haveText = false, text.clear();
        }
        return true;
    }
    bool parse_error(std::size_t, const std::string&, const nlohmann::json::exception&) { return false; }
};

// nlohmann's SAX interface calls the key callback `key`.
struct Sax : LocaSax {
    bool key(std::string& k) { return storeKey(k); }
};

std::string getenv_s(const char* name) {
    const char* v = std::getenv(name);
    return v ? v : "";
}

// The files a catalog may be in, in the order kiln-manage looks (docs/kiln/modding/catalogs.md) plus the server's own Kiln
// folder. Wine renames HOME and XDG_DATA_HOME to WINE_HOST_<name> and gives the Linux home as WINEHOMEDIR; a Linux path
// is also tried through the Z: drive.
std::vector<std::filesystem::path> candidates() {
    std::vector<std::filesystem::path> places;
    auto add = [&](const std::string& p) {
        if (p.empty()) return;
        places.emplace_back(p);
#ifdef _WIN32
        if (p[0] == '/') {
            std::string z = "Z:" + p;
            std::replace(z.begin(), z.end(), '/', '\\');
            places.emplace_back(z);
        }
#endif
    };
    add(getenv_s("KILN_CATALOG"));
    if (gKApi && gKApi->path) {
        char buf[1024];
        size_t n = 0;
        if (gKApi->path(KILN_PATH_KILN, buf, sizeof buf, &n) == KILN_OK && n && n < sizeof buf)
            places.emplace_back((std::filesystem::path(std::string(buf, n)) / "catalog"));
    }
    if (auto d = getenv_s("KILN_DATA_DIR"); !d.empty()) add((std::filesystem::path(d) / "catalog").generic_string());
    for (const char* name : {"XDG_DATA_HOME", "WINE_HOST_XDG_DATA_HOME"})
        if (auto x = getenv_s(name); !x.empty()) add((std::filesystem::path(x) / "kiln" / "catalog").generic_string());
    for (const char* name : {"HOME", "WINE_HOST_HOME"})
        if (auto h = getenv_s(name); !h.empty()) add((std::filesystem::path(h) / ".local" / "share" / "kiln" / "catalog").generic_string());
    if (auto w = getenv_s("WINEHOMEDIR"); !w.empty()) {
        if (w.rfind("\\??\\", 0) == 0) w.erase(0, 4);
        places.emplace_back(std::filesystem::path(w) / ".local" / "share" / "kiln" / "catalog");
    }
    if (auto l = getenv_s("LOCALAPPDATA"); !l.empty()) places.emplace_back(std::filesystem::path(l) / "Kiln" / "catalog");
    std::vector<std::filesystem::path> files;
    for (const auto& p : places) {
        auto f = p.extension() == ".json" ? p : p / "client.json";
        if (std::find(files.begin(), files.end(), f) == files.end()) files.push_back(std::move(f));
    }
    return files;
}

// A catalog file's loca table, or null with `why`.
std::shared_ptr<Table> parseFile(const std::filesystem::path& file, std::string& why) {
    std::error_code ec;
    const auto size = std::filesystem::file_size(file, ec);
    if (ec) return nullptr;  // not there: the caller tries the next place
    if (size > kMaxFile) {
        why = file.string() + " is larger than 512 MB";
        return nullptr;
    }
    std::ifstream in(file, std::ios::binary);
    if (!in) {
        why = file.string() + " cannot be opened";
        return nullptr;
    }
    auto table = std::make_shared<Table>();
    table->text.reserve(20000);
    Sax sax;
    sax.out = table.get();
    const bool ok = nlohmann::json::sax_parse(in, &sax);
    if (!ok) {
        why = file.string() + " is not a readable catalog";
        return nullptr;
    }
    if (table->text.empty()) {
        why = file.string() + " has no 'loca' section (a server catalog has none: use the client catalog, `kiln catalog build --side client`)";
        return nullptr;
    }
    table->source = file.string();
    table->build = sax.steam.empty() ? sax.svn : sax.svn.empty() ? sax.steam : sax.steam + " (svn " + sax.svn + ")";
    return table;
}

// The table, loaded on the first call (about a quarter of a second once); null when there is none. Call-driven only.
std::shared_ptr<const Table> table() {
    std::lock_guard lock(gMx);
    if (gTable) return gTable;
    const auto now = std::chrono::steady_clock::now();
    if (gFailedAt.time_since_epoch().count() != 0 && now - gFailedAt < std::chrono::seconds(60)) return nullptr;
    std::string why;
    std::string tried;
    for (const auto& file : candidates()) {
        std::string w;
        if (auto t = parseFile(file, w)) {
            gTable = t;
            gSink((LV_OK "Res.text: " + std::to_string(t->text.size()) + " texts from " + t->source + " (game build " + t->build + ")").c_str());
            return gTable;
        }
        if (!w.empty()) why = w;
        tried += (tried.empty() ? "" : "; ") + file.string();
    }
    gFailedAt = now;
    gReason = !why.empty() ? why : "no client catalog found (run `kiln catalog build --side client`, or set KILN_CATALOG; looked at: " + tried + ")";
    gSink((LV_WARN "Res.text: no texts for tags beyond items and templates: " + gReason).c_str());
    return nullptr;
}

void reset() {
    std::lock_guard lock(gMx);
    gTable.reset();
    gFailedAt = {};
    gReason = "not loaded yet";
}

// The text of a LocaTag reference (the tag id is the FNV-1a of its 16 GUID bytes), or empty.
std::string textOf(const uint8_t g[16]) {
    bool zero = true;
    for (int i = 0; i < 16; ++i) zero &= g[i] == 0;
    if (zero) return {};
    const auto t = table();
    if (!t) return {};
    const uint32_t id = refl::fnv(std::string_view(reinterpret_cast<const char*>(g), 16));
    auto it = t->text.find(id);
    return it == t->text.end() ? std::string() : it->second;
}

}  // namespace settexts
