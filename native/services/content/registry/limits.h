// SPDX-License-Identifier: MIT
// kiln.content registry stage: the limits registry's back-end (dev.32 registry-r3b; regmap/common/limits.json, entries kind
// hooks, backend hook). Limits the game's code holds as fixed numbers, not data: Kiln answers the game's own check with the
// registry's value through a hook of its own, installed only when the value is not the game's (no edit: no hook, nothing of
// the game's code touched).
//
// Each hook value has a name (a regmap row's path), the game's own value read from its code at start, and an answer:
//   altarCap   sigdb limits/AltarLimit: min(BalancingTable.altarsPerFlameLevel[level], 16), the 16 an immediate
//              (limits/altars.cap). Kiln's hook answers min(the level's table value, cap): at or below the game's 16 that is
//              min(the game's own answer, cap); above it Kiln reads the level's value as the game does (limits/altars.table).
//   giftRange  sigdb limits/GiftRange: the gifting distance getter, `movss xmm0,[rip+d]; ret`, the squared distance (2500 =
//              50 m) the host's gift check and the players' UI compare with. Kiln's value is in metres; the hook answers its
//              square. The literal is shared with another reader, so the getter is hooked, never the literal.
// The values are restart fields: the stage composes them at start and apply() takes them for the process (a later entry point
// is RestartRequired, as for the boot back-end). Both sides need the same values (clientNeeds; the join compares them).
// The pure parts (the answers, the game's value in Kiln units) are host-tested; the code reads and the hooks come through Env.
#pragma once
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"
#include "stage.h"

namespace kc::registry::limits {

using json = nlohmann::json;

// The hook values, by the name a row's path gives (tools/registrygen/regmap_check.py HOOK_VALUES has the same list).
struct HookValue {
    const char* name;
    const char* kind;  // "int" or "float": the Kiln value
};
inline constexpr HookValue kValues[] = {{"altarCap", "int"}, {"giftRange", "float"}};
bool known(const std::string& name);

// ---- the answers (pure) ----
// The altar limit the game's code gets: `original` is the game's own answer (min(value, game_cap)); `value` the level's table
// value read as the game reads it (nullopt: not readable). At or below game_cap: min(original, cap). Above it: min(value, cap),
// and the game's own answer when the value cannot be read (never more than the game allows by guesswork).
uint32_t altar_answer(uint32_t original, int64_t game_cap, int64_t cap, std::optional<uint32_t> value);
// The squared distance the gift getter gives for a range in metres (float, as the game keeps it).
float gift_answer(double metres);
// The game's gifting range in metres from the getter's literal (the squared distance); nullopt: not a usable distance.
std::optional<double> gift_range_of(float squared);

// ---- kiln-content.dll (or a host test's fakes) ----
struct Env {
    std::function<bool(const char* name, uintptr_t* out)> symbol;    // sigdb symbol, absolute address
    std::function<bool(const char* name, int64_t* out)> constant;    // sigdb constant
    std::function<bool(uintptr_t addr, void* out, size_t n)> read;   // a guarded read of the game's memory
    // the exe file's bytes of the game's code at addr (kiln.hooks read_original): a hook another module put in before Kiln's
    // first read does not hide the game's own code; unset or false: the live bytes (read)
    std::function<bool(uintptr_t addr, void* out, size_t n)> read_code;
    // installs the hook of one value (its answer is read from answers() at each call); false + *why: not installed
    std::function<bool(const std::string& name, std::string* why)> install;
    std::function<void(int level, const std::string& line)> log;     // 0 info, 1 warn, 2 error
};
void bind(Env env);
void reset();  // host tests: forget the values, the answers and the hooks

// The game's own value of a hook value, from its code (model::set_hook_values). false + *why: not found on this build.
bool base_value(const std::string& name, json* out, std::string* why);

// The answers the hooks give now (read by the hooks at each call, lock-free).
struct Answers {
    int64_t altar_cap = 0;     // 0: the game's own
    int64_t game_altar_cap = 0;
    int64_t altar_table = 0;   // limits/altars.table
    float gift_squared = 0;
    bool gift_answers = false; // false: the game's own distance
};
Answers answers();

// The limits registry's restart fields (boothook::Hooks::hooks): each composed over the game's value and taken; a value that
// is not the game's installs its hook (once). One outcome per field; *notes for kiln.log. Returns how many values were taken.
size_t apply(const stage::BootResource& rb, std::vector<stage::BootOutcome>* outcomes, std::vector<std::string>* notes);

// registry.json / kilnctl status: per value the game's own, the value taken, and whether its hook is in.
json state();

// ---- the lab's proof (proof.cpp) ----
// One value set as an answer for a moment (the producer's probe: no registry layer), and taken back. The hook is installed
// for it when it is not in yet. false + *why: the hook cannot answer on this build.
bool probe_set(const std::string& name, double value, std::string* why);
void probe_clear(const std::string& name);
// What the game's code gets back from the hooked function now, in the value's Kiln unit (calls the game's function: the
// gift getter as it is, the altar limit over a scratch balancing table whose levels all hold `table_value`).
std::optional<double> game_answer(const std::string& name, uint32_t table_value, std::string* why);

// ---- what the hooks answer (kiln-content.dll's handlers call these on the game's threads; lock-free) ----
// The altar limit for the game's call (registry, level) whose own answer is `original`.
uint32_t altar_hook_answer(void* registry, uint32_t level, uint32_t original);
// The gift getter's answer while Kiln answers (true, *out: the squared distance); false: the game's own.
bool gift_hook_answer(float* out);
// The level's value of a balancing registry's altar table, read as the game reads it (guarded); nullopt: not readable.
std::optional<uint32_t> table_value_at(void* registry, uint32_t level);

}  // namespace kc::registry::limits
