// SPDX-License-Identifier: MIT
// kiln-emf: EmfApi v22-v26 logic without the game (see v26_model.h). Ported from EMF 0.5.1 (MIT, EMF contributors).
#include "v26_model.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>

namespace kemf::v26 {

uint32_t api_requirement(uint64_t version) {
    return static_cast<uint32_t>(std::clamp<uint64_t>(version, 1, UINT32_MAX));
}

uint32_t api_size_for(uint32_t version) {
    switch (version) {
        case 21: return kApiV21Size;
        case 22: return kApiV22Size;
        case 23: return kApiV23Size;
        case 24: return kApiV24Size;
        case 25: return kApiV25Size;
        case 26: return kApiV26Size;
        case 27: return kApiV27Size;
        case 28: return kApiV28Size;
        case 29: return kApiV29Size;
        case 30: return kApiV30Size;
        case 31: return kApiV31Size;
        case 32: return kApiV32Size;
        case 33: return kApiV33Size;
        case 34: return kApiV34Size;
        case 35: return kApiV35Size;
        case 36: return kApiV36Size;
        case 37: return kApiV37Size;
        case 38: return kApiV38Size;
        case 39: return kApiV39Size;
        case 40: return kApiV40Size;
        default: return 0;
    }
}

std::vector<std::string> note_rows(std::string_view text, float max_width, size_t max_rows,
                                   const std::function<float(std::string_view)>& width) {
    std::vector<std::string> rows;
    if (max_rows == 0) return rows;
    // a line break, a tab or any other control character separates words as a space does: the rows are ours to make
    const auto is_gap = [](char c) { return static_cast<unsigned char>(c) <= ' '; };
    std::string row;
    bool cut = false;
    size_t i = 0;
    while (i < text.size()) {
        while (i < text.size() && is_gap(text[i])) ++i;
        size_t j = i;
        while (j < text.size() && !is_gap(text[j])) ++j;
        if (j == i) break;
        const std::string_view word = text.substr(i, j - i);
        const std::string joined = row.empty() ? std::string(word) : row + " " + std::string(word);
        if (row.empty() || width(joined) <= max_width) {
            row = joined;
        } else if (rows.size() + 1 < max_rows) {
            rows.push_back(std::move(row));
            row = std::string(word);
        } else {
            cut = true;
            break;
        }
        i = j;
    }
    if (cut) {
        // the last row ends with the dots and still fits: words go from its end until it does, all of them if need be
        // (the dots alone are the least a cut note shows, even on a screen narrower than they are)
        while (!row.empty() && width(row + " ...") > max_width) {
            const size_t sp = row.rfind(' ');
            row.resize(sp == std::string::npos ? 0 : sp);
        }
        row = row.empty() ? "..." : row + " ...";
    }
    if (!row.empty()) rows.push_back(std::move(row));
    return rows;
}

// ============================================================================================= v22 =============
bool is_progression_attribute(uint32_t id) {
    return id == EMF_ATTR_XP_PENDING || id == EMF_ATTR_XP || id == EMF_ATTR_XP_REQUIRED || id == EMF_ATTR_LEVEL;
}

AttrWrite attribute_write_rule(uint32_t id) {
    if (id == EMF_ATTR_XP_PENDING) return AttrWrite::GiveOnly;
    if (id == EMF_ATTR_XP || id == EMF_ATTR_XP_REQUIRED || id == EMF_ATTR_LEVEL) return AttrWrite::ReadOnly;
    return AttrWrite::Clamped;
}

uint32_t level_set_xp(uint32_t xp, uint32_t need) { return need == 0 ? 0 : std::min(xp, need - 1); }

std::string level_set_refusal(uint32_t level, uint32_t cap, bool curve_known) {
    if (!curve_known) return "the game's xpRequired is not found on this build";
    if (!cap) return "the game's level cap is not found on this build";
    if (level < 1 || level > cap) return "the level must be 1.." + std::to_string(cap);
    return {};
}

// ============================================================================================= v24 / v26 =======
namespace {

bool copy_text(const char* p, size_t max, std::string* out) {
    if (!p) {
        out->clear();
        return true;
    }
    size_t n = 0;
    while (n <= max && p[n]) ++n;
    if (n > max) return false;
    out->assign(p, n);
    return true;
}

EmfUiWidget widget_at(const EmfWindowDef& def, uint32_t i) {
    EmfUiWidget w{};
    const auto* base = reinterpret_cast<const uint8_t*>(def.widgets) + static_cast<size_t>(i) * def.widget_size;
    std::memcpy(&w, base, std::min<size_t>(def.widget_size, offsetof(EmfUiWidget, fps)));
    if (def.widget_size >= KEMF_FIELD_END(EmfUiWidget, fps)) std::memcpy(&w.fps, base + offsetof(EmfUiWidget, fps), sizeof w.fps);
    if (def.widget_size >= KEMF_FIELD_END(EmfUiWidget, height)) std::memcpy(&w.height, base + offsetof(EmfUiWidget, height), sizeof w.height);
    if (def.widget_size < sizeof w) w.icon = 0;  // a v24/v25 mod: icon was reserved there, whatever it holds
    return w;
}

}  // namespace

bool valid_window_id(std::string_view id) {
    if (id.empty() || id.size() > kMaxWindowIdLen) return false;
    for (char c : id)
        if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-'))
            return false;
    return true;
}

bool copy_window_def(const EmfWindowDef* def, bool for_update, WindowRequest* out, std::string* why) {
    const auto refuse = [why](const std::string& reason) {
        if (why) *why = reason;
        return false;
    };
    if (!def || def->size < kWindowDefV24Size) return refuse("no window definition, or its size field is too small");
    if (def->flags) return refuse("unknown window flags");
    if (def->widget_size > 4096) return refuse("widget_size is too large");
    if (!def->id || !valid_window_id(std::string_view(def->id, strnlen(def->id, kMaxWindowIdLen + 1))))
        return refuse("id must be 1-48 of A-Z a-z 0-9 _ . -");
    if (def->count > kMaxWindowWidgets) return refuse("at most 256 widgets");
    if (def->count && !def->widgets) return refuse("widgets is NULL with a count");
    if (def->count && def->widget_size < kWidgetMinSize)
        return refuse("widget_size is smaller than an EmfUiWidget (pass sizeof(EmfUiWidget))");
    if (def->world_kind != EMF_WORLD_SERVER && def->world_kind != EMF_WORLD_CLIENT)
        return refuse("world_kind must be EMF_WORLD_SERVER or EMF_WORLD_CLIENT");
    if (def->place > EMF_UI_PLACE_RIGHT) return refuse("place must be EMF_UI_PLACE_CENTRE, _LEFT or _RIGHT");
    if (!std::isfinite(def->width) || def->width < 0 || def->width > 200)
        return refuse("width must be 0 (the default) to 200 text heights");
    const uint32_t ck = def->close_key;
    // VK_LBUTTON..VK_XBUTTON2 = 0x01..0x06, VK_LSHIFT..VK_RMENU = 0xA0..0xA5
    if (ck > 0xFF || (ck >= 0x01 && ck <= 0x06) || (ck >= 0xA0 && ck <= 0xA5))
        return refuse("close_key must be 0 or a keyboard key's generic virtual-key code (no mouse buttons)");
    if (!for_update && !def->cb) return refuse("no callback");

    WindowRequest r;
    r.mod = out->mod;
    r.id = def->id;
    if (!copy_text(def->title, kMaxWindowTitleLen, &r.title)) return refuse("title is longer than 64 bytes");
    r.widgets.reserve(def->count);
    for (uint32_t i = 0; i < def->count; ++i) {
        const EmfUiWidget w = widget_at(*def, i);
        const std::string at = "widget " + std::to_string(i) + ": ";
        if (w.type > EMF_UI_IMAGE) return refuse(at + "unknown type " + std::to_string(w.type));
        if (!std::isfinite(w.height) || w.height < 0 || w.height > 40)
            return refuse(at + "height must be 0 (the default) to 40 text lines");
        if (w.fps > 60) return refuse(at + "fps must be 0 to 60");
        WindowWidget c;
        c.type = w.type;
        c.flags = w.flags;
        if (!copy_text(w.key, kMaxWidgetKeyLen, &c.key)) return refuse(at + "key is longer than 48 bytes");
        if (!copy_text(w.text, kMaxWidgetTextLen, &c.text)) return refuse(at + "text is longer than 256 bytes");
        if (!copy_text(w.tooltip, kMaxWidgetTooltipLen, &c.tooltip)) return refuse(at + "tooltip is longer than 512 bytes");
        c.count = w.count;
        c.value = w.value > 1000 ? 1000 : w.value;
        c.rgba = w.rgba;
        c.icon = w.icon;
        c.fps = w.fps;
        c.height = w.height;
        r.widgets.push_back(std::move(c));
    }
    r.close_vk = static_cast<uint16_t>(ck);
    r.place = def->place;
    r.width = def->width > 0 ? def->width : kDefaultWindowWidth;
    r.world_kind = def->world_kind;
    if (!for_update) {
        r.cb = def->cb;
        r.user = def->user;
    }
    *out = std::move(r);
    return true;
}

bool widget_takes_clicks(uint32_t type) {
    return type == EMF_UI_BUTTON || type == EMF_UI_ROW || type == EMF_UI_SLOT || type == EMF_UI_IMAGE;
}

std::vector<std::string> describe_window(const WindowRequest& w) {
    static const char* const kTypes[] = {"text", "heading", "button", "row", "slot", "bar", "separator", "image"};
    std::vector<std::string> out;
    out.push_back("window " + w.id + " \"" + w.title + "\": " + std::to_string(w.widgets.size()) + " widgets, events on the " +
                  (w.world_kind == EMF_WORLD_CLIENT ? "client" : "server") + " world");
    for (size_t i = 0; i < w.widgets.size(); ++i) {
        const WindowWidget& x = w.widgets[i];
        std::string line = "  " + std::to_string(i) + " " + (x.type <= EMF_UI_IMAGE ? kTypes[x.type] : "?");
        if (!x.key.empty()) line += " " + x.key;
        if (!x.text.empty()) line += " \"" + x.text + "\"";
        if (x.icon) line += " picture " + std::to_string(x.icon);
        out.push_back(std::move(line));
    }
    return out;
}

// ============================================================================================= v10 wheel =======
bool valid_hold_key(uint32_t vk) {
    // VK_LBUTTON..VK_XBUTTON2 = 0x01..0x06, VK_LSHIFT..VK_RMENU = 0xA0..0xA5
    return vk <= 0xFF && !(vk >= 0x01 && vk <= 0x06) && !(vk >= 0xA0 && vk <= 0xA5);
}

bool copy_wheel_def(const EmfWheel* def, WheelRequest* out, std::string* why) {
    const auto refuse = [why](const std::string& reason) {
        if (why) *why = reason;
        return false;
    };
    if (!def || def->size < kWheelV10Size) return refuse("no wheel definition, or its size field is too small");
    if (def->count < 1 || def->count > kMaxWheelItems) return refuse("a wheel has 1 to 64 entries");
    if (!def->items) return refuse("items is NULL");
    if (!valid_hold_key(def->hold_key))
        return refuse("hold_key must be 0 or a keyboard key's generic virtual-key code (VK_SHIFT, not VK_LSHIFT; no mouse buttons)");
    if (def->world_kind != EMF_WORLD_SERVER && def->world_kind != EMF_WORLD_CLIENT)
        return refuse("world_kind must be EMF_WORLD_SERVER or EMF_WORLD_CLIENT");
    if (!def->cb) return refuse("no callback");
    const auto cut = [](const char* p, size_t max) {
        if (!p) return std::string();
        size_t n = 0;
        while (n < max && p[n]) ++n;
        while (n > 0 && p[n] && (static_cast<unsigned char>(p[n]) & 0xC0) == 0x80) --n;  // never inside a UTF-8 sequence
        return std::string(p, n);
    };
    WheelRequest r;
    r.title = cut(def->title, kMaxWheelTitleLen);
    r.items.reserve(def->count);
    for (uint32_t i = 0; i < def->count; ++i) r.items.push_back(cut(def->items[i], kMaxWheelLabelLen));  // a NULL label is empty
    r.hold_vk = def->hold_key;
    r.world_kind = def->world_kind;
    r.cb = def->cb;
    r.user = def->user;
    *out = std::move(r);
    return true;
}

bool portrait_key_ok(const std::string& k) {
    if (k.empty() || k.size() > 64 || k == "." || k == "..") return false;
    for (const char c : k)
        if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-'))
            return false;
    if (k.find("..") != std::string::npos || k.back() == '.') return false;
    std::string base = k.substr(0, k.find('.'));
    for (char& c : base) c = static_cast<char>(c >= 'a' && c <= 'z' ? c - 'a' + 'A' : c);
    if (base == "CON" || base == "PRN" || base == "AUX" || base == "NUL") return false;
    const bool port = base.rfind("COM", 0) == 0 || base.rfind("LPT", 0) == 0;
    if (base.size() == 4 && port && base[3] >= '0' && base[3] <= '9') return false;
    return true;
}

bool png_size(const uint8_t* d, size_t n, uint32_t* w, uint32_t* h) {
    static const uint8_t kSig[8] = {0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A};
    if (!d || n < 24 || std::memcmp(d, kSig, 8) != 0 || std::memcmp(d + 12, "IHDR", 4) != 0) return false;
    auto be = [](const uint8_t* p) { return (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | p[3]; };
    *w = be(d + 16);
    *h = be(d + 20);
    return *w > 0 && *h > 0;
}

// ============================================================================================= v23 =============
namespace {
// EMF's curated list (components_api.cpp): the savable ones stay refused until checked in game.
constexpr ComponentRule kRules[] = {
    {"ScaleComponent", kScaleComponent, 4, false, true, false, true},
    {"IsTame", kIsTame, 1, true, true, true, false},
    {"LifeTime", kLifeTime, 24, true, true, true, false},
    {"InteractionPause", kInteractionPause, 8, false, true, false, true},
    {"DoNotSave", kDoNotSave, 1, true, true, true, false},
    {"ForceSave", kForceSave, 1, true, true, true, false},
    {"ConditionalDespawn", kConditionalDespawn, 24, true, false, true, false},
    {"DespawnLink", kDespawnLink, 8, true, false, true, false},
};
}  // namespace

const ComponentRule* component_rule(uint32_t hash) {
    if (!hash) return nullptr;
    for (const auto& r : kRules)
        if (r.hash == hash) return &r;
    return nullptr;
}

std::string component_name(uint32_t hash) {
    if (const ComponentRule* r = component_rule(hash)) return r->name;
    switch (hash) {
        case kInteractionOfferRuntime: return "InteractionOfferRuntime";
        case kClientInteractionOffer: return "ClientInteractionOffer";
        case kCheckPlayerInRange: return "CheckPlayerInRange";
        default: break;
    }
    char b[16];
    std::snprintf(b, sizeof b, "0x%08x", hash);
    return b;
}

std::string check_change(bool add, uint32_t hash, uint32_t data_size, bool data_given, uint32_t flags,
                         bool added_by_this_mod) {
    if (flags & ~(EMF_COMPONENT_PERSISTENT | EMF_COMPONENT_EXPERT)) return "an unknown flag";
    if (flags & EMF_COMPONENT_EXPERT) return "EMF_COMPONENT_EXPERT is reserved: this EMF changes only the components it lists";
    const ComponentRule* r = component_rule(hash);
    if (!r) return "component " + component_name(hash) + " is not one EMF lets mods " + (add ? "add" : "remove");
    if (!r->checked) return std::string(r->name) + " is saved with the world and not checked in game yet: this EMF refuses it";
    if (add) {
        if (!r->add) return std::string(r->name) + " is not one EMF lets mods add";
        if (data_given && data_size != r->size)
            return std::string(r->name) + " has the size " + std::to_string(r->size) + " in this game build, not " +
                   std::to_string(data_size);
    } else if (!r->remove && !added_by_this_mod) {
        return std::string(r->name) + ": removing it is not allowed (only when this mod added it in this session)";
    }
    if (r->savable && !(flags & EMF_COMPONENT_PERSISTENT))
        return std::string(r->name) +
               " is saved with the world: the change outlives the mod (pass EMF_COMPONENT_PERSISTENT to accept that)";
    return {};
}

std::string check_data(uint32_t hash, const void* data, uint32_t size) {
    if (hash != kScaleComponent) return {};
    float v = 0;
    if (data && size == sizeof v) std::memcpy(&v, data, sizeof v);
    if (std::isfinite(v) && v >= 0.1f && v <= 10.0f) return {};
    char b[96];
    std::snprintf(b, sizeof b, "ScaleComponent needs a size from 0.1 to 10, not %g", static_cast<double>(v));
    return b;
}

bool ComponentQueue::request(ComponentRequest r, std::string* why) {
    const auto refuse = [&](const std::string& reason) {
        if (why) *why = reason;
        return false;
    };
    if (!r.world) return refuse("the server world only (a world this game hosts)");
    if (!r.entity) return refuse("no entity");
    std::lock_guard lock(mu_);
    const bool added_before = ledger_session_ == r.session && added_.count({r.owner, r.entity, r.hash}) != 0;
    std::string reason;
    if (!enabled) reason = "changing components is off (see the log)";
    else if (r.internal) {
        if (r.add && (r.data.empty() || r.data.size() > 4096)) reason = "an internal add brings its bytes";
    } else {
        reason = check_change(r.add, r.hash, static_cast<uint32_t>(r.data.size()), !r.data.empty(), r.flags, added_before);
        if (reason.empty() && r.add) reason = check_data(r.hash, r.data.empty() ? nullptr : r.data.data(), static_cast<uint32_t>(r.data.size()));
    }
    if (reason.empty() && jobs_.size() >= kMaxComponentChanges) reason = "too many changes under way";
    if (reason.empty())
        for (const auto& j : jobs_)
            if (j.req.entity == r.entity && j.req.hash == r.hash) {
                reason = "a change of that component on that entity is under way";
                break;
            }
    if (!reason.empty()) return refuse(reason);
    if (r.add && r.data.empty())
        if (const ComponentRule* rule = component_rule(r.hash)) r.data.assign(rule->size, 0);
    Job j;
    j.req = std::move(r);
    jobs_.push_back(std::move(j));
    return true;
}

void ComponentQueue::after_step(EmfWorld* world, uint32_t session, const ComponentEnv& env, std::vector<ComponentAnswer>* out) {
    std::lock_guard lock(mu_);
    if (ledger_session_ != session) { added_.clear(); ledger_session_ = session; }
    for (auto it = added_.begin(); it != added_.end();) {
        if (!env.exists(std::get<1>(*it)) || !env.has(std::get<1>(*it), std::get<2>(*it))) it = added_.erase(it);
        else ++it;
    }
    for (auto it = jobs_.begin(); it != jobs_.end();) {
        Job& j = *it;
        auto answer = [&](uint32_t status, const std::string& why) {
            if (j.req.done) out->push_back({j.req.done, world, status, why});
            it = jobs_.erase(it);
        };
        if (j.req.session != session || j.req.world != world) {
            if (j.req.done) out->push_back({j.req.done, nullptr, EMF_COMPONENT_FAILED, "the world it was meant for ended"});
            it = jobs_.erase(it);
            continue;
        }
        if (j.phase == 3) {
            answer(j.status, j.why);
            continue;
        }
        if (j.phase == 0 && j.req.still_owned && !j.req.still_owned()) {
            answer(EMF_COMPONENT_FAILED, "the offer belongs to the game now");
            continue;
        }
        const bool here = env.exists && env.exists(j.req.entity);
        if (j.phase == 0) {
            if (!here) {
                answer(EMF_COMPONENT_FAILED, "the entity is gone");
                continue;
            }
            if (env.is_player && env.is_player(j.req.entity)) {
                answer(EMF_COMPONENT_FAILED, "never a player");
                continue;
            }
            const bool has = env.has && env.has(j.req.entity, j.req.hash);
            if (has == j.req.add) {
                answer(EMF_COMPONENT_ALREADY, "");
                continue;
            }
            j.phase = 1;
        } else if (j.phase == 1) {
            if (++j.tries > kComponentPushRetries) {
                answer(EMF_COMPONENT_FAILED, "the game's command buffer stayed full");
                continue;
            }
        } else {
            const bool has = here && env.has && env.has(j.req.entity, j.req.hash);
            if (here && has == j.req.add) {
                if (j.req.add && !j.req.internal) added_.insert({j.req.owner, j.req.entity, j.req.hash});
                if (!j.req.add) added_.erase({j.req.owner, j.req.entity, j.req.hash});
                answer(EMF_COMPONENT_DONE, "");
                continue;
            }
            if (!here || ++j.verify >= kComponentVerifySteps) {
                answer(EMF_COMPONENT_FAILED, here ? "the game did not apply it" : "the entity is gone");
                continue;
            }
        }
        ++it;
    }
}

void ComponentQueue::run(EmfWorld* world, uint32_t session, const ComponentEnv& env, const ComponentPush& push) {
    std::lock_guard lock(mu_);
    for (auto& j : jobs_) {
        if (j.phase != 1 || j.req.world != world || j.req.session != session) continue;
        // A different program may have changed this entity since the post-step check. In particular,
        // queue_add replaces an existing component's bytes: never push over data already present.
        const bool here = env.exists && env.exists(j.req.entity);
        const bool player = env.is_player && env.is_player(j.req.entity);
        const bool has = here && env.has && env.has(j.req.entity, j.req.hash);
        const bool ours = !j.req.still_owned || j.req.still_owned();
        if (!here || player || !ours || has == j.req.add) {
            j.phase = 3;
            j.status = here && !player && ours ? EMF_COMPONENT_ALREADY : EMF_COMPONENT_FAILED;
            j.why = !here ? "the entity is gone" : player ? "never a player" : !ours ? "the offer belongs to the game now" : "";
        } else if (push(j.req.entity, j.req.hash, j.req.add, j.req.data)) j.phase = 2;
    }
}

void ComponentQueue::end_all(const std::string& why, std::vector<ComponentAnswer>* out) {
    std::lock_guard lock(mu_);
    for (auto& j : jobs_)
        if (j.req.done) out->push_back({j.req.done, nullptr, EMF_COMPONENT_FAILED, why});
    jobs_.clear();
    added_.clear();
}

size_t ComponentQueue::pending() const {
    std::lock_guard lock(mu_);
    return jobs_.size();
}

// ============================================================================================= v25 =============
std::string offer_spec_from(const EmfOffer* o, OfferSpec* s) {
    if (!o || o->size < kOfferV25Size) return "no offer, or its size field is too small";
    *s = OfferSpec{};
    s->verb = o->verb;
    if (o->text) {
        const size_t n = strnlen(o->text, 64);
        if (n == 0 || n > 63) return "text must be 1 to 63 bytes";
        s->text.assign(o->text, n);
    }
    if ((o->flags & ~(EMF_OFFER_LOW_PRIORITY | EMF_OFFER_BUILD_MODE)) || o->reserved)
        return "unknown flags (this EMF knows EMF_OFFER_LOW_PRIORITY and EMF_OFFER_BUILD_MODE), or reserved is not 0";
    s->sequence = o->sequence;
    s->range_m = o->range_m;
    s->flags = o->flags;
    return {};
}

uint32_t custom_verb_id(std::string_view text) {
    uint32_t h = 2166136261u;
    for (char c : std::string("emf.verb:") + std::string(text)) h = (h ^ static_cast<uint8_t>(c)) * 16777619u;
    return h;
}

uint32_t offer_verb(const OfferSpec& s) {
    if (!s.text.empty()) return custom_verb_id(s.text);
    return s.verb ? s.verb : kOfferDefaultVerb;
}

std::vector<uint8_t> offer_runtime_bytes(const OfferSpec& s) {
    std::vector<uint8_t> b(56, 0);
    const uint32_t sequence = s.sequence ? s.sequence : kOfferDefaultSequence;
    const uint32_t verb = offer_verb(s);
    std::memcpy(b.data() + 8, &sequence, 4);   // defaultSequence
    std::memcpy(b.data() + 16, &verb, 4);      // defaultVerbId
    b[47] = (s.flags & EMF_OFFER_LOW_PRIORITY) ? 1 : 0;  // reduceInteractionWeight
    b[48] = (s.flags & EMF_OFFER_BUILD_MODE) ? 1 : 0;    // allowedAsBuildingSpark
    return b;  // isOffered and offerId stay 0: the game's interaction_offer sets them
}

std::vector<uint8_t> offer_client_bytes() { return std::vector<uint8_t>(12, 0); }

std::vector<uint8_t> offer_range_bytes(const OfferSpec& s) {
    return std::vector<uint8_t>(1, s.range_m ? s.range_m : kOfferDefaultRange);
}

void OfferService::new_session_locked(uint32_t session) {
    if (session == session_) return;
    entries_.clear();
    state_.clear();
    dropped_.clear();
    session_ = session;
    steps_ = 0;
}

ComponentRequest OfferService::change_of(EmfWorld* w, uint32_t session, uint32_t entity, uint32_t hash, bool add,
                                         std::vector<uint8_t> bytes, bool first) {
    ComponentRequest r;
    r.world = w;
    r.session = session;
    r.entity = entity;
    r.hash = hash;
    r.add = add;
    r.data = std::move(bytes);
    r.owner = "emf.offers";
    r.internal = true;
    r.done = [this, session, entity, hash, add, first](EmfWorld* world, uint32_t status, const std::string& why) {
        std::vector<ComponentRequest> later;
        std::string owner;
        bool lost = false;
        {
            std::lock_guard lock(mu_);
            if (session != session_) return;
            auto st = state_.find(entity);
            if (st != state_.end() && st->second.pending > 0) --st->second.pending;
            if (st != state_.end() && add && status == EMF_COMPONENT_DONE) {
                if (hash == kInteractionOfferRuntime) st->second.runtime_added = true;
                if (hash == kClientInteractionOffer) st->second.client_added = true;
                if (hash == kCheckPlayerInRange) st->second.range_added = true;
            }
            lost = add && (status == EMF_COMPONENT_FAILED ||
                          (hash == kInteractionOfferRuntime && first && status == EMF_COMPONENT_ALREADY));
            if (lost && st != state_.end()) st->second.removing = true;
            if (lost) {
                auto e = entries_.find(entity);
                if (e != entries_.end()) {
                    owner = e->second.owner;
                    entries_.erase(e);
                }
            }
            if (st != state_.end() && st->second.pending == 0) {
                if (st->second.removing && !st->second.removes_sent && world) {
                    later = removes_for(world, session, entity, st->second);
                    st->second.removes_sent = true;
                    st->second.pending = static_cast<int>(later.size());
                    if (later.empty()) state_.erase(st);
                } else if (st->second.removing || lost) {
                    state_.erase(st);
                }
            }
        }
        if (lost && !owner.empty() && deps_.log)
            deps_.log("offer on entity " + std::to_string(entity) + " (" + owner + ") did not land: " +
                      (status == EMF_COMPONENT_ALREADY ? std::string("it has an offer of the game's") : why));
        submit(later);
    };
    return r;
}

std::vector<ComponentRequest> OfferService::removes_for(EmfWorld* w, uint32_t session, uint32_t entity, const State& state) {
    std::vector<ComponentRequest> out;
    if (state.runtime_added) out.push_back(change_of(w, session, entity, kInteractionOfferRuntime, false, {}, false));
    if (state.client_added) out.push_back(change_of(w, session, entity, kClientInteractionOffer, false, {}, false));
    if (state.range_added) out.push_back(change_of(w, session, entity, kCheckPlayerInRange, false, {}, false));
    for (auto& r : out) {
        r.still_owned = [this, entity, spec = state.spec] {
            // An absent runtime is fine (our earlier removal may already have landed). A replacement is not.
            return !deps_.read_offer || !deps_.read_offer(entity) || still_ours(entity, spec);
        };
    }
    return out;
}

bool OfferService::still_ours(uint32_t entity, const OfferSpec& spec) const {
    if (!deps_.read_offer) return true;
    const auto got = deps_.read_offer(entity);
    if (!got) return false;
    const uint32_t sequence = spec.sequence ? spec.sequence : kOfferDefaultSequence;
    return got->first == sequence && got->second == offer_verb(spec);
}

void OfferService::submit(std::vector<ComponentRequest>& reqs) {
    for (auto& r : reqs) {
        std::string why;
        if (!deps_.change || !deps_.change(r, &why))
            if (r.done) r.done(r.world, EMF_COMPONENT_FAILED, why.empty() ? "changing components is off" : why);
    }
    reqs.clear();
}

bool OfferService::offer(EmfWorld* w, uint32_t session, uint32_t entity, const OfferSpec& spec, const std::string& owner,
                         OfferCallback cb, std::string* why) {
    const auto refuse = [&](const std::string& reason) {
        if (why) *why = reason;
        return false;
    };
    if (!entity) return refuse("no entity");
    const auto has = [&](uint32_t hash) { return deps_.has && deps_.has(entity, hash); };
    std::vector<ComponentRequest> reqs;
    {
        std::lock_guard lock(mu_);
        new_session_locked(session);
        if (const auto st = state_.find(entity); st != state_.end() && st->second.removing)
            return refuse("an earlier offer is still being taken off it: try again in a moment");
        if (const auto e = entries_.find(entity); e != entries_.end()) {
            if (e->second.owner != owner) return refuse("another mod's offer is on it (" + e->second.owner + ")");
            if (offer_runtime_bytes(e->second.spec) != offer_runtime_bytes(spec) ||
                offer_range_bytes(e->second.spec) != offer_range_bytes(spec))
                return refuse("its offer is there already: remove it first, then offer again");
            e->second.cb = std::move(cb);  // the same offer: a new callback
            return true;
        }
        if (has(kInteractionOfferRuntime)) {
            const auto d = dropped_.find(entity);
            if (d != dropped_.end() && d->second.owner == owner &&
                offer_runtime_bytes(d->second.spec) == offer_runtime_bytes(spec) &&
                offer_range_bytes(d->second.spec) == offer_range_bytes(spec) && still_ours(entity, spec)) {
                State st = d->second.state;
                dropped_.erase(d);
                entries_[entity] = Entry{owner, spec, std::move(cb), 0};
                state_[entity] = st;
                return true;
            }
            return refuse("it has its own offer from the game (a chest, a bench, a bed...)");
        }
        reqs.push_back(change_of(w, session, entity, kInteractionOfferRuntime, true, offer_runtime_bytes(spec), true));
        if (!has(kClientInteractionOffer))
            reqs.push_back(change_of(w, session, entity, kClientInteractionOffer, true, offer_client_bytes(), true));
        if (!has(kCheckPlayerInRange)) {
            reqs.push_back(change_of(w, session, entity, kCheckPlayerInRange, true, offer_range_bytes(spec), true));
        }
        entries_[entity] = Entry{owner, spec, std::move(cb), 0};
        State& st = state_[entity];
        st = State{};
        st.spec = spec;
        st.pending = static_cast<int>(reqs.size());
    }
    std::string refused;
    if (!deps_.change || !deps_.change(reqs.front(), &refused)) {
        std::lock_guard lock(mu_);
        entries_.erase(entity);
        state_.erase(entity);
        return refuse(refused.empty() ? "changing components is off" : refused);
    }
    reqs.erase(reqs.begin());
    submit(reqs);
    return true;
}

bool OfferService::remove(EmfWorld* w, uint32_t session, uint32_t entity, const std::string& owner, std::string* why) {
    std::vector<ComponentRequest> reqs;
    {
        std::lock_guard lock(mu_);
        new_session_locked(session);
        const auto e = entries_.find(entity);
        if (e == entries_.end() || e->second.owner != owner) {
            if (why) *why = "this mod has no offer on it";
            return false;
        }
        State& st = state_[entity];
        if (!st.pending && !still_ours(entity, e->second.spec)) {
            entries_.erase(e);
            state_.erase(entity);
            return true;
        }
        entries_.erase(e);
        st.removing = true;
        if (st.pending == 0) {
            reqs = removes_for(w, session, entity, st);
            st.removes_sent = true;
            st.pending = static_cast<int>(reqs.size());
            if (reqs.empty()) state_.erase(entity);
        }
    }
    submit(reqs);
    return true;
}

bool OfferService::active(uint32_t session, uint32_t entity, const std::string& owner) const {
    std::lock_guard lock(mu_);
    if (session != session_) return false;
    const auto e = entries_.find(entity);
    bool on = e != entries_.end() && e->second.owner == owner;
    if (on)
        if (const auto st = state_.find(entity); st != state_.end() && st->second.removing) on = false;
    return on;
}

void OfferService::on_accept(EmfWorld* w, uint32_t session, const void* event, uint32_t size) {
    if (!event || size < kInteractionAcceptedEventSize) return;
    uint32_t guest = 0, host = 0;
    std::memcpy(&guest, static_cast<const uint8_t*>(event) + 8, 4);
    std::memcpy(&host, static_cast<const uint8_t*>(event) + 12, 4);
    if (!guest || !host) return;
    OfferSpec spec;
    {
        std::lock_guard lock(mu_);
        if (session != session_) return;
        const auto e = entries_.find(host);
        if (e == entries_.end()) return;
        const auto st = state_.find(host);
        if (st == state_.end() || st->second.pending || st->second.removing) return;
        spec = e->second.spec;
    }
    if (!still_ours(host, spec)) return;  // the id is another entity's now
    OfferCallback cb;
    {
        std::lock_guard lock(mu_);
        const auto e = entries_.find(host);
        if (e == entries_.end()) return;
        ++e->second.accepts;
        cb = e->second.cb;
    }
    if (cb) cb(w, guest, host);
}

void OfferService::after_step(EmfWorld*, uint32_t session) {
    std::vector<std::string> gone;
    {
        std::lock_guard lock(mu_);
        new_session_locked(session);
        if (++steps_ < kOfferUpkeepSteps) return;
        steps_ = 0;
        for (auto it = entries_.begin(); it != entries_.end();) {
            const uint32_t entity = it->first;
            const auto st = state_.find(entity);
            if (st != state_.end() && st->second.pending > 0) {
                ++it;
                continue;
            }
            const bool here = deps_.has && deps_.has(entity, 0);  // hash 0: "the entity has a position"
            if (!here || !still_ours(entity, it->second.spec)) {
                gone.push_back(std::to_string(entity) + " (" + it->second.owner + (here ? ", its offer is not EMF's now" : "") + ")");
                if (here) dropped_[entity] = {it->second.owner, it->second.spec, st != state_.end() ? st->second : State{}};
                else dropped_.erase(entity);
                state_.erase(entity);
                it = entries_.erase(it);
            } else {
                ++it;
            }
        }
    }
    if (deps_.log)
        for (const auto& g : gone) deps_.log("offer on entity " + g + " dropped: the entity or its offer is gone");
}

std::vector<std::string> OfferService::list() const {
    std::lock_guard lock(mu_);
    std::vector<std::string> out;
    for (const auto& [id, e] : entries_) {
        char b[64];
        std::snprintf(b, sizeof b, "0x%08x", offer_verb(e.spec));
        out.push_back(std::to_string(id) + " " + e.owner + " verb " + b + " accepts " + std::to_string(e.accepts));
    }
    return out;
}

}  // namespace kemf::v26
