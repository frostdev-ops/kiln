// SPDX-License-Identifier: MIT
// kiln-emf: the parts of EmfApi v22-v26 that need no game and no Windows, so host tests (tests/run.sh) run them with
// fakes: the table layout per version, mod windows (v24/v26 definitions as EMF copies them), the component policy and
// the change queue (v23), E-prompt offers (v25) and picture paths (v26). The glue onto Kiln's services is in
// api_level.cpp, api_components.cpp and api_windows.cpp.
//
// Ported from EMF 0.5.1 (MIT, EMF contributors; licenses/EMF-LICENSE.txt): src/core/api_sizes.h,
// src/core/overlay/window_model.cpp, src/core/components_api.cpp, src/core/component_service.h and src/core/offers.cpp.
// The rules and the refusal texts follow EMF's, so a mod sees the same answers on Kiln.
#pragma once
#include <cstddef>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <tuple>
#include <utility>
#include <vector>

#include "emf/emf_api.h"

namespace kemf::v26 {

// ---------------------------------------------------------------------------------------------- the table -------
template <size_t Offset, size_t Size>
constexpr uint32_t field_end() {
    return static_cast<uint32_t>(Offset + Size);
}
#define KEMF_FIELD_END(T, f) ::kemf::v26::field_end<offsetof(T, f), sizeof(((T*)nullptr)->f)>()

constexpr uint32_t kApiV21Size = KEMF_FIELD_END(EmfApi, world_setting_info);
constexpr uint32_t kApiV22Size = KEMF_FIELD_END(EmfApi, player_level_set);
constexpr uint32_t kApiV23Size = KEMF_FIELD_END(EmfApi, change_component);
constexpr uint32_t kApiV24Size = KEMF_FIELD_END(EmfApi, window_close);
constexpr uint32_t kApiV25Size = KEMF_FIELD_END(EmfApi, offer_remove);
constexpr uint32_t kApiV26Size = KEMF_FIELD_END(EmfApi, offer_active);
constexpr uint32_t kComponentChangeV23Size = KEMF_FIELD_END(EmfComponentChange, user);
constexpr uint32_t kWindowDefV24Size = KEMF_FIELD_END(EmfWindowDef, user);
constexpr uint32_t kUiWidgetV24Size = KEMF_FIELD_END(EmfUiWidget, pad);
constexpr uint32_t kOfferV25Size = KEMF_FIELD_END(EmfOffer, reserved);
constexpr uint32_t kWidgetMinSize = KEMF_FIELD_END(EmfUiWidget, rgba);  // the fields a v24 window draws

// The x64 layouts EMF mods are built with (EMF's own asserts): a field that moves would break every built mod.
static_assert(kApiV21Size == 592, "EmfApi v21 size");
static_assert(kApiV22Size == 616 && kApiV23Size == 624 && kApiV24Size == 648 && kApiV25Size == 672,
              "EmfApi v22-v25 sizes");
static_assert(kApiV26Size == 688 && sizeof(EmfApi) == kApiV26Size, "EmfApi v26 size");
static_assert(kComponentChangeV23Size == 48 && sizeof(EmfComponentChange) == 48, "EmfComponentChange layout (v23)");
static_assert(kWindowDefV24Size == 72 && sizeof(EmfWindowDef) == 72 && offsetof(EmfWindowDef, cb) == 56,
              "EmfWindowDef layout (v24)");
static_assert(kUiWidgetV24Size == 56 && sizeof(EmfUiWidget) == 64 && offsetof(EmfUiWidget, fps) == 56,
              "EmfUiWidget layout (v24 + v26)");
static_assert(sizeof(EmfUiEvent) == 40 && sizeof(EmfOffer) == 24 && offsetof(EmfOffer, range_m) == 20,
              "EmfUiEvent / EmfOffer layout");

// The table size a mod built against API `version` checks (api->size >= this); 0 for an unknown version.
uint32_t api_size_for(uint32_t version);
// Saturate manifest requirements so a huge future version cannot wrap into a supported version.
uint32_t api_requirement(uint64_t version);

// ---------------------------------------------------------------------------------------------- v22 ------------
// What attribute_add may do with an id (EMF's attribute_write_rule): XP, its requirement and the level are kept
// consistent by the game (read-only); pending XP is only given (positive amounts).
enum class AttrWrite { Clamped, GiveOnly, ReadOnly };
AttrWrite attribute_write_rule(uint32_t id);
bool is_progression_attribute(uint32_t id);  // EMF_ATTR_XP_PENDING / _XP / _XP_REQUIRED / _LEVEL
// player_level_set's XP: cut to below the level's requirement (EMF: min(xp, need - 1)).
uint32_t level_set_xp(uint32_t xp, uint32_t need);
// Why player_level_set refuses `level` ("" when it does not), from the level cap (0 = unknown).
std::string level_set_refusal(uint32_t level, uint32_t cap, bool curve_known);

// ---------------------------------------------------------------------------------------------- v24 / v26 -------
struct WindowWidget {
    uint32_t type = EMF_UI_TEXT;
    uint32_t flags = 0;
    std::string key, text, tooltip;
    uint32_t count = 0;
    uint32_t value = 0;  // bar: 0..1000
    uint32_t rgba = 0;
    uint32_t icon = 0;  // v26: a picture handle from image_load
    uint32_t fps = 0;
    float height = 0;  // EMF_UI_IMAGE: text lines (0 = 6)
};

struct WindowRequest {
    std::string id, title, mod;
    std::vector<WindowWidget> widgets;
    uint16_t close_vk = 0;
    uint32_t place = EMF_UI_PLACE_CENTRE;
    float width = 0;  // text heights
    uint8_t world_kind = EMF_WORLD_SERVER;
    EmfUiCallback cb = nullptr;
    void* user = nullptr;
};

constexpr size_t kMaxWindowWidgets = 256;
constexpr size_t kMaxWindowIdLen = 48;
constexpr size_t kMaxWindowTitleLen = 64;
constexpr size_t kMaxWidgetKeyLen = 48;
constexpr size_t kMaxWidgetTextLen = 256;
constexpr size_t kMaxWidgetTooltipLen = 512;
constexpr float kDefaultWindowWidth = 28.0f;

// Copies a mod's definition as EMF does (every string owned; widgets read through the mod's widget_size, fields
// past it as 0). False and *why (EMF's words) when refused. for_update: no callback needed, cb/user not taken.
bool copy_window_def(const EmfWindowDef* def, bool for_update, WindowRequest* out, std::string* why);
bool widget_takes_clicks(uint32_t type);
bool valid_window_id(std::string_view id);
std::vector<std::string> describe_window(const WindowRequest& w);

// image_load: a portrait-cache key (1-64 of A-Z a-z 0-9 _ . -, no "..", no device names).
bool portrait_key_ok(const std::string& key);
constexpr uint32_t kMaxImages = 128;
constexpr uint32_t kMaxImageSide = 512;
constexpr uint32_t kMaxImageFrames = 64;
constexpr uint64_t kMaxImageBytes = 64ull << 20;  // RGBA pixels of every picture together
// The width and height in a PNG's IHDR (false when it is not a PNG).
bool png_size(const uint8_t* data, size_t n, uint32_t* w, uint32_t* h);

// ---------------------------------------------------------------------------------------------- v23 ------------
// Component hashes (keen reflection qualified hashes: FNV-1a-32 of "keen::ecs::<Name>"; EMF's generated headers).
constexpr uint32_t kScaleComponent = 0x2A0C9602u;
constexpr uint32_t kInteractionPause = 0xA64742EDu;
constexpr uint32_t kIsTame = 0x1399E0BAu;
constexpr uint32_t kLifeTime = 0xF22C253Cu;
constexpr uint32_t kDoNotSave = 0x41E678EAu;
constexpr uint32_t kForceSave = 0x5AEC6CBFu;
constexpr uint32_t kConditionalDespawn = 0xF2433191u;
constexpr uint32_t kDespawnLink = 0x7A10128Fu;
constexpr uint32_t kInteractionOfferRuntime = 0x78EE0F23u;
constexpr uint32_t kClientInteractionOffer = 0x8598AD86u;
constexpr uint32_t kCheckPlayerInRange = 0x50311280u;
constexpr uint32_t kInteractionAcceptedEventSize = 24;  // guestEntityId +8, hostEntityId +12

struct ComponentRule {
    const char* name;
    uint32_t hash;
    uint16_t size;
    bool savable, add, remove, checked;
};
const ComponentRule* component_rule(uint32_t hash);
std::string component_name(uint32_t hash);  // the rule's name, else "0x%08x"
// EMF's policy: "" = allowed, else why not. added_by_this_mod: the mod added it in this session.
std::string check_change(bool add, uint32_t hash, uint32_t data_size, bool data_given, uint32_t flags,
                         bool added_by_this_mod);
std::string check_data(uint32_t hash, const void* data, uint32_t size);

constexpr size_t kMaxComponentChanges = 256;
constexpr int kComponentPushRetries = 120;
constexpr int kComponentVerifySteps = 3;

using ComponentDone = std::function<void(EmfWorld* world, uint32_t status, const std::string& why)>;
struct ComponentRequest {
    EmfWorld* world = nullptr;
    uint32_t session = 0;
    uint32_t entity = 0;
    uint32_t hash = 0;
    bool add = true;
    std::vector<uint8_t> data;  // add: the bytes (empty: zeros of the rule's size)
    uint32_t flags = 0;
    std::string owner;      // the mod (what it added in this session, it may remove)
    bool internal = false;  // kiln-emf's own (E prompts): no policy, an add brings its bytes
    ComponentDone done;
    std::function<bool()> still_owned; // internal removals: revalidate before pushing into the game
};

// What the queue needs to know about the server world (the glue reads kiln.world; tests pass fakes).
struct ComponentEnv {
    std::function<bool(uint32_t entity)> exists;
    std::function<bool(uint32_t entity, uint32_t hash)> has;
    std::function<bool(uint32_t entity)> is_player;
};
// Pushes a kind-3 (add) or kind-4 (remove) record inside the program; false = the command buffer is full.
using ComponentPush = std::function<bool(uint32_t entity, uint32_t hash, bool add, const std::vector<uint8_t>& data)>;

struct ComponentAnswer {
    ComponentDone done;
    EmfWorld* world;
    uint32_t status;
    std::string why;
};

// The v23 change queue: request (any thread) -> check after the next server step -> push inside the step after
// (the program) -> verify after it. Answers are returned, to be called outside the lock on the server world's thread.
class ComponentQueue {
public:
    bool request(ComponentRequest r, std::string* why);
    void after_step(EmfWorld* world, uint32_t session, const ComponentEnv& env, std::vector<ComponentAnswer>* out);
    void run(EmfWorld* world, uint32_t session, const ComponentEnv& env, const ComponentPush& push);
    void end_all(const std::string& why, std::vector<ComponentAnswer>* out);  // FAILED with a NULL world
    size_t pending() const;
    bool enabled = true;

private:
    struct Job {
        ComponentRequest req;
        int phase = 0;  // 0 check after the next step, 1 push in the program, 2 pushed (verify after the step)
        int tries = 0;
        int verify = 0;
        uint32_t status = EMF_COMPONENT_FAILED;
        std::string why;
    };
    mutable std::mutex mu_;
    std::vector<Job> jobs_;
    uint32_t ledger_session_ = 0;
    std::set<std::tuple<std::string, uint32_t, uint32_t>> added_;  // (owner, entity, hash) added this session
};

// ---------------------------------------------------------------------------------------------- v25 ------------
constexpr uint32_t kOfferDefaultSequence = EMF_SEQUENCE_TOUCH;
constexpr uint32_t kOfferDefaultVerb = EMF_VERB_USE;
constexpr uint8_t kOfferDefaultRange = 32;
constexpr int kOfferUpkeepSteps = 30;  // how often offers are checked for a gone entity

struct OfferSpec {
    uint32_t verb = 0;
    std::string text;
    uint32_t sequence = 0;
    uint8_t range_m = 0;
    uint8_t flags = 0;
};
// EMF's checks of an EmfOffer ("" = fine, else why) and its copy.
std::string offer_spec_from(const EmfOffer* o, OfferSpec* out);
uint32_t custom_verb_id(std::string_view text);  // FNV-1a-32 of "emf.verb:" + text
uint32_t offer_verb(const OfferSpec& s);
std::vector<uint8_t> offer_runtime_bytes(const OfferSpec& s);  // InteractionOfferRuntime, 56
std::vector<uint8_t> offer_client_bytes();                     // ClientInteractionOffer, 12 zeros
std::vector<uint8_t> offer_range_bytes(const OfferSpec& s);    // CheckPlayerInRange, 1

using OfferCallback = std::function<void(EmfWorld* world, uint32_t player, uint32_t entity)>;

struct OfferDeps {
    std::function<bool(uint32_t entity, uint32_t hash)> has;
    // InteractionOfferRuntime's (defaultSequence, defaultVerbId) on the entity, or nothing.
    std::function<std::optional<std::pair<uint32_t, uint32_t>>(uint32_t entity)> read_offer;
    std::function<bool(ComponentRequest& r, std::string* why)> change;
    std::function<void(const std::string&)> log;
};

// The offers of the running server session (EMF's OfferService, single session, std::mutex).
class OfferService {
public:
    explicit OfferService(OfferDeps deps) : deps_(std::move(deps)) {}
    bool offer(EmfWorld* w, uint32_t session, uint32_t entity, const OfferSpec& spec, const std::string& owner,
               OfferCallback cb, std::string* why);
    bool remove(EmfWorld* w, uint32_t session, uint32_t entity, const std::string& owner, std::string* why);
    bool active(uint32_t session, uint32_t entity, const std::string& owner) const;
    void on_accept(EmfWorld* w, uint32_t session, const void* event, uint32_t size);
    void after_step(EmfWorld* w, uint32_t session);
    std::vector<std::string> list() const;  // "entity owner verb accepts"

private:
    struct Entry {
        std::string owner;
        OfferSpec spec;
        OfferCallback cb;
        uint32_t accepts = 0;
    };
    struct State {
        int pending = 0;
        OfferSpec spec;
        bool runtime_added = false, client_added = false, range_added = false;
        bool removing = false, removes_sent = false;
    };
    ComponentRequest change_of(EmfWorld* w, uint32_t session, uint32_t entity, uint32_t hash, bool add,
                               std::vector<uint8_t> bytes, bool first);
    std::vector<ComponentRequest> removes_for(EmfWorld* w, uint32_t session, uint32_t entity, const State& state);
    bool still_ours(uint32_t entity, const OfferSpec& spec) const;
    void submit(std::vector<ComponentRequest>& reqs);
    void new_session_locked(uint32_t session);

    OfferDeps deps_;
    mutable std::mutex mu_;
    uint32_t session_ = 0;
    int steps_ = 0;
    std::map<uint32_t, Entry> entries_;
    std::map<uint32_t, State> state_;
    struct Dropped { std::string owner; OfferSpec spec; State state; };
    std::map<uint32_t, Dropped> dropped_;
};

}  // namespace kemf::v26
