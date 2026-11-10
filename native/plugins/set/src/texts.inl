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
// (Res.text then answers nil, as before) with one line in the console; another attempt is made after 60 s. A file that
// was read and refused (no loca section, not a catalog, too large) is not read again until its size or time changes,
// and the same reason is not printed again. Paths are wide on Windows (a home folder with non-ASCII letters).
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
std::string gSaid;                                           // gMx: the last reason printed
// Files read and refused, with their size and time then: skipped (no re-parse) while both are unchanged. gMx.
struct Miss {
    uintmax_t size;
    std::filesystem::file_time_type time;
    std::string why;
};
std::map<std::filesystem::path, Miss> gMisses;
size_t gParses = 0;                                          // gMx: files parsed (tests)

// A path as UTF-8 for messages (path::string() throws on Windows for names outside the ANSI code page).
std::string u8(const std::filesystem::path& p) {
    const auto s = p.u8string();
    return std::string(s.begin(), s.end());
}

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

// An environment variable as a path, empty when unset. Wide on Windows: the narrow CRT copy loses letters outside the
// ANSI code page.
std::filesystem::path envPath(const char* name) {
#ifdef _WIN32
    std::wstring w(name, name + std::strlen(name));
    const wchar_t* v = _wgetenv(w.c_str());
    return v ? std::filesystem::path(v) : std::filesystem::path();
#else
    const char* v = std::getenv(name);
    return v ? std::filesystem::path(v) : std::filesystem::path();
#endif
}

// The files a catalog may be in, in the order kiln-manage looks (docs/kiln/modding/catalogs.md) plus the server's own Kiln
// folder. Wine renames HOME and XDG_DATA_HOME to WINE_HOST_<name> and gives the Linux home as WINEHOMEDIR; a Linux path
// is also tried through the Z: drive.
std::vector<std::filesystem::path> candidates() {
    std::vector<std::filesystem::path> places;
    auto add = [&](const std::filesystem::path& p) {
        if (p.empty()) return;
        places.push_back(p);
#ifdef _WIN32
        const std::wstring& n = p.native();
        if (n[0] == L'/') {
            std::wstring z = L"Z:" + n;
            std::replace(z.begin(), z.end(), L'/', L'\\');
            places.emplace_back(z);
        }
#endif
    };
    add(envPath("KILN_CATALOG"));
    if (gKApi && gKApi->path) {
        char buf[1024];
        size_t n = 0;
        if (gKApi->path(KILN_PATH_KILN, buf, sizeof buf, &n) == KILN_OK && n && n < sizeof buf)
            places.push_back(std::filesystem::path(std::u8string(buf, buf + n)) / "catalog");  // the core's paths are UTF-8
    }
    // generic (forward-slash) form, as before, so a Linux value from Wine's host variables is also tried through Z:
    auto gen = [](const std::filesystem::path& p) { return std::filesystem::path(p.generic_u8string()); };
    if (auto d = envPath("KILN_DATA_DIR"); !d.empty()) add(gen(d / "catalog"));
    for (const char* name : {"XDG_DATA_HOME", "WINE_HOST_XDG_DATA_HOME"})
        if (auto x = envPath(name); !x.empty()) add(gen(x / "kiln" / "catalog"));
    for (const char* name : {"HOME", "WINE_HOST_HOME"})
        if (auto h = envPath(name); !h.empty()) add(gen(h / ".local" / "share" / "kiln" / "catalog"));
    if (auto w = envPath("WINEHOMEDIR"); !w.empty()) {
        auto n = w.native();
        const decltype(n) prefix(std::filesystem::path("\\??\\").native());
        if (n.rfind(prefix, 0) == 0) n.erase(0, 4);
        places.push_back(std::filesystem::path(n) / ".local" / "share" / "kiln" / "catalog");
    }
    if (auto l = envPath("LOCALAPPDATA"); !l.empty()) places.push_back(l / "Kiln" / "catalog");
    std::vector<std::filesystem::path> files;
    for (const auto& p : places) {
        auto f = p.extension() == ".json" ? p : p / "client.json";
        if (std::find(files.begin(), files.end(), f) == files.end()) files.push_back(std::move(f));
    }
    return files;
}

// A catalog file's loca table, or null with `why`.
// `lasting` (optional): the refusal stays until the file changes (not set for a file that could not be opened).
std::shared_ptr<Table> parseFile(const std::filesystem::path& file, std::string& why, bool* lasting = nullptr) {
    if (lasting) *lasting = true;
    std::error_code ec;
    const auto size = std::filesystem::file_size(file, ec);
    if (ec) return nullptr;  // not there: the caller tries the next place
    if (size > kMaxFile) {
        why = u8(file) + " is larger than 512 MB";
        return nullptr;
    }
    std::ifstream in(file, std::ios::binary);
    if (!in) {
        why = u8(file) + " cannot be opened";
        if (lasting) *lasting = false;
        return nullptr;
    }
    auto table = std::make_shared<Table>();
    table->text.reserve(20000);
    Sax sax;
    sax.out = table.get();
    const bool ok = nlohmann::json::sax_parse(in, &sax);
    if (!ok) {
        why = u8(file) + " is not a readable catalog";
        return nullptr;
    }
    if (table->text.empty()) {
        why = u8(file) + " has no 'loca' section (a server catalog has none: use the client catalog, `kiln catalog build --side client`)";
        return nullptr;
    }
    table->source = u8(file);
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
        std::error_code ec;
        const auto size = std::filesystem::file_size(file, ec);
        const auto time = ec ? std::filesystem::file_time_type{} : std::filesystem::last_write_time(file, ec);
        if (!ec) {
            // read and refused before, unchanged since: not parsed again (a server catalog can be hundreds of MB)
            if (auto m = gMisses.find(file); m != gMisses.end() && m->second.size == size && m->second.time == time) {
                why = m->second.why;
                tried += (tried.empty() ? "" : "; ") + u8(file);
                continue;
            }
        }
        std::string w;
        bool lasting = false;
        if (!ec) ++gParses;
        if (auto t = ec ? nullptr : parseFile(file, w, &lasting)) {
            gTable = t;
            gMisses.clear();
            gSink((LV_OK "Res.text: " + std::to_string(t->text.size()) + " texts from " + t->source + " (game build " + t->build + ")").c_str());
            return gTable;
        }
        if (!w.empty()) {
            why = w;
            if (!ec && lasting) gMisses[file] = Miss{size, time, w};
        }
        tried += (tried.empty() ? "" : "; ") + u8(file);
    }
    gFailedAt = now;
    gReason = !why.empty() ? why : "no client catalog found (run `kiln catalog build --side client`, or set KILN_CATALOG; looked at: " + tried + ")";
    if (gReason != gSaid) {  // the same reason is said once, not every 60 s
        gSaid = gReason;
        gSink((LV_WARN "Res.text: no texts for tags beyond items and templates: " + gReason).c_str());
    }
    return nullptr;
}

void reset() {
    std::lock_guard lock(gMx);
    gTable.reset();
    gFailedAt = {};
    gReason = "not loaded yet";
    gSaid.clear();
    gMisses.clear();
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
