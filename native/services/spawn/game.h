// SPDX-License-Identifier: MIT
// kiln.spawn: engine access (svn 1285601 layouts, resolved through sigdb; see docs/re/spawn.md).
// Everything here runs on the tick thread in Pre or Post (no world job running). Raw memory walks are guarded with
// structured exception handling: a wrong layout turns into "unavailable", never a crash.
#pragma once
#include <cstdint>
#include <string>
#include <vector>

#include "kiln_api.h"

namespace kspawn {

struct Guid {
    uint8_t b[16]{};
    bool zero() const;
    bool operator==(const Guid& o) const;
    bool operator<(const Guid& o) const;
    std::string text() const;                   // Windows text form, lower case
    static bool parse(std::string_view s, Guid* out);  // accepts with or without dashes/braces
};

#pragma pack(push, 8)
struct WorldTransform {  // keen::ecs::CurrentTransform, 56 bytes
    int64_t pos[3];      // 32.32 fixed point metres
    float rot[4];        // quaternion x, y, z, w
    float scale[3];
    uint32_t pad;
};
struct SpawnComponent {  // createEntity spawn component, 0x18 bytes
    uint32_t hash;       // qualifiedHash = FNV-1a-32("keen::ecs::<Name>")
    uint32_t pad;
    const void* data;
    uint64_t size;       // must equal the engine's component size
};
struct SpawnSlice {
    const SpawnComponent* data;
    uint64_t count;
};
struct PickupItem {      // keen::ecs::PickupItem, 28 bytes (spawnPickupItem RVA 0x166760)
    uint32_t item_id;
    uint8_t item_ref[16];  // ItemReference: the game's drop code leaves it zero
    uint32_t level;
    uint8_t rarity;
    uint8_t pad;
    uint16_t amount;
};
#pragma pack(pop)
static_assert(sizeof(WorldTransform) == 56 && sizeof(SpawnComponent) == 0x18 && sizeof(PickupItem) == 28);

constexpr uint32_t fnv1a(const char* s) {
    uint32_t h = 0x811c9dc5u;
    for (; *s; ++s) h = (h ^ static_cast<uint8_t>(*s)) * 0x01000193u;
    return h;
}
constexpr uint32_t kCurrentTransform = fnv1a("keen::ecs::CurrentTransform");  // 0x027ac564
constexpr uint32_t kPickupItem = fnv1a("keen::ecs::PickupItem");              // 0x08775a90
constexpr uint32_t kSpawnTime = fnv1a("keen::ecs::SpawnTime");                // 0x583963a3
constexpr uint32_t kDoNotSave = fnv1a("keen::ecs::DoNotSave");                // 0x41e678ea
constexpr uint32_t kOverrideLevel = fnv1a("keen::ecs::OverrideLevel");        // 0xa21e7ee7
static_assert(kCurrentTransform == 0x027ac564u && kPickupItem == 0x08775a90u && kSpawnTime == 0x583963a3u);

struct TemplateRow {
    Guid guid;
    std::string name;
    uint8_t family = 0;
    bool flags_known = false;  // the per-template component bits below were read
    bool do_not_save = false;  // the template itself carries DoNotSave: its entities are never saved
    bool lifetime = false;     // LifeTime: the game removes its entities after a while
    bool transform = false;    // CurrentTransform (sanity check: almost every spawnable template has one)
};

struct EntityRow {
    uint32_t id = 0;
    uint32_t parent = 0;
    Guid tmpl;
    bool has_tmpl = false;
};

namespace game {

// Resolves symbols and constants and installs the world-capture hook. False = spawn unavailable (why says why).
bool init(const KilnApi* api, std::string& why);
bool can_destroy();
bool can_list_templates();
bool can_list_entities();

// True once the server world has stepped at least once (its pointer is known).
bool world_ready();
bool world_from_core();  // the core's KilnApi.server_world (else Kiln's own fallback hook on SimStep)

// ECS time now (ns): tick period x tick. tick_period: the period (16,666,667 ns at 60 Hz), 0 if unknown.
int64_t ecs_now();
int64_t tick_period();

// createEntity through the game's spawn wrapper. 0 = refused (the game logs why) or faulted (*fault != 0).
uint32_t create(const Guid& tmpl, const SpawnComponent* comps, size_t count, unsigned long* fault);
// destroyEntity(storage, id, 1, 0): removes it now (grid, map) and queues the client update. False = no entity/fault.
bool destroy(uint32_t id, unsigned long* fault);

// The entity's CurrentTransform (false: no such entity, or it has none).
bool transform(uint32_t id, WorldTransform* out);
// A component of the entity (GetComponent). False if absent.
bool component(uint32_t id, uint32_t hash, void** data, uint64_t* size);
// The entity's template (entity map record -> template info). False if the entity does not exist.
bool entity_template(uint32_t id, Guid* out, uint32_t* parent = nullptr);

// Every template the engine knows (template info map). False if unreadable.
bool templates(std::vector<TemplateRow>& out);
// Every live server-world entity with its template. False if unreadable.
bool entities(std::vector<EntityRow>& out);
// Diagnostics of the entity map: capacity, the map's own count field, occupied slots, key/record mismatches.
std::string entity_map_stats();

}  // namespace game

double fixed_to_m(int64_t v);
int64_t m_to_fixed(double m);

}  // namespace kspawn
