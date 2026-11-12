// SPDX-License-Identifier: MIT
// kiln.playeranim, portable part: a mod's player-state definitions, <mod>/assets/player-states.json format 1, read as
// tools/animcook/anicook/playerstates.py reads them, verdict for verdict and word for word:
//   { "format": 1, "states": { "<name>": { "clip": ..., "like": ..., "blendIn": s, "loop": b, "speed": x } } }
// Strict JSON as Python's json.loads takes it (no comments, no trailing commas, no NaN, no byte order mark), a key
// twice and an unknown key are errors. A wrong state is refused with its reason; the others stay. Whether `like` may
// be a neighbour and whether the clip is served is the merge's (merge.h). No Windows, no game, no Kiln API.
#pragma once
#include <cstdint>
#include <optional>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#include "nlohmann/json.hpp"

namespace kpa {

constexpr int kFormat = 1;
constexpr const char* kFileName = "player-states.json";
constexpr size_t kNameMax = 64;  // code points
constexpr double kBlendMax = 5.0;

// The file as a whole cannot be read: no state of it loads.
struct DefinitionError : std::runtime_error {
    using std::runtime_error::runtime_error;
};

// Test seams (tests replace the id rules to make ids collide, as the Python tests do with mock).
struct IdHooks {
    uint32_t (*state_id)(const std::string& guid) = nullptr;           // FNV-1a-32 of the GUID's bytes
    uint32_t (*node_id)(const std::vector<std::string>& parts) = nullptr;  // FNV-1a-32 of "kiln.player.<parts>"
};

// UUIDv5 of "<mod>:<kind>:<name>" in Kiln.Blocks' namespace, as text (graphgen.derive, BlockIds.Derive).
std::string derive(const std::string& mod, const std::string& kind, const std::string& name);
// FNV-1a-32 of a GUID text's 16 bytes in the game's order (kfc.animgraph.fnv_guid); 0 when it is no GUID.
uint32_t fnv_guid_text(std::string_view guid);
// A clip of this mod's cook by name, or a GUID written out (8-4-4-4-12, any case; lower case returned).
std::string clip_guid(const std::string& mod, const std::string& clip);
// Why a mod's player states cannot load at all, or nullopt: the manifest must state "side": "both" and "clients": "mod".
std::optional<std::string> manifest_reason(const std::string& mod, const std::optional<std::string>& side, const std::optional<std::string>& clients);

// One definition. `status` is empty while nothing has refused it, "refused" with `reason` after, "live" once merged.
struct State {
    std::string mod, name;
    std::string guid;  // derive(mod, "state", name)
    uint32_t id = 0;   // its FNV-1a-32
    std::optional<std::string> clip_ref, clip, like;
    std::optional<double> blend_in, speed;  // float32 values
    std::optional<bool> loop;
    std::optional<std::string> kind, machine, status, reason;

    // Refuse with `why` (the text after "mod <mod>, state <name>: "). The first reason stays.
    void refuse(const std::string& why);
    // The report's line: mod, name, guid, id, kind, like, machine, status, reason.
    nlohmann::ordered_json entry() const;
};

struct ModStates {
    std::string mod;
    std::optional<std::string> side, clients;  // as the manifest writes them (nullopt: not written, or not a string)
    std::string source = kFileName;
    std::vector<State> states;
};

// The text of a player-states.json of mod `mod`. Throws DefinitionError when the file cannot be read at all.
ModStates parse(std::string_view text, const std::string& mod, const std::optional<std::string>& side, const std::optional<std::string>& clients,
                const std::string& source = kFileName, const IdHooks* hooks = nullptr);
// The file's bytes: not UTF-8 or a byte order mark is a DefinitionError (playerstates.load), then parse().
ModStates load_bytes(std::string_view bytes, const std::string& mod, const std::optional<std::string>& side, const std::optional<std::string>& clients,
                     const std::string& file_name = kFileName, const IdHooks* hooks = nullptr);

// re.sub(r"^\s*//.*$", "", text, flags=re.M): a line whose first non-space characters are // goes, with the white space
// before it (playerstates.read_manifest, playergraph.ClipFacts' assets.json).
std::string strip_line_comments(std::string_view text);

// kiln.mod.json as playerstates.read_manifest reads it (whole-line // comments and trailing commas removed, then JSON):
// its "id", "side" and "clients" (nullopt when absent or not a string). Throws DefinitionError("<where>: <why>").
struct ManifestFields {
    std::optional<std::string> id, side, clients;
};
ManifestFields read_manifest_text(std::string_view text, const std::string& where);

// ---- the JSON reader, exposed for the tests ----

// A value as Python's json.loads(..., object_pairs_hook=list) gives it: objects keep their pairs in order, repeats
// included. Strings are WTF-8 (a \ud800 escape is a lone surrogate, as in a Python str).
struct Json {
    enum T : uint8_t { Null, True, False, Int, Float, Str, Arr, Obj } t = Null;
    std::string s;      // Str: the text; Int: the digits as written
    double f = 0;       // Float; Int: its value rounded to a double
    bool big = false;   // Int: too large for a double (float() raises OverflowError)
    std::vector<Json> items;        // Arr items, Obj values
    std::vector<std::string> keys;  // Obj keys
    const Json* find(std::string_view key) const;  // the last pair of that key, as dict() keeps it
};
// Python's json.loads. `strict_constants`: NaN, Infinity and -Infinity raise DefinitionError("<where>: <name> is not
// JSON") as playerstates' parse_constant does; otherwise they are numbers. A syntax error throws
// DefinitionError("<where>: <prefix><Python's message>").
Json parse_json(std::string_view text, const std::string& where, bool strict_constants, const std::string& prefix);
// Python's repr() of a JSON value as playerstates' parse leaves it (an object is its list of pairs).
std::string py_repr(const Json& v);

}  // namespace kpa
