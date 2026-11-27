// SPDX-License-Identifier: MIT
// kiln.content registry views: the service behind kiln.registry.view (native/core/abi/services/kiln_registry_view.h).
//
// One catalog per process (the game's types do not change while it runs), built at the first call after the stage (resources
// are resident from then on): every registry of the schema as a stable view, every struct type that has resources as an
// advanced one (views.h). Entries of an advanced view are listed from kiln.assets when first needed and again when the type's
// resources change (a mod's new resources); reads decode the resident bytes through the game's reflection under kiln.content's store
// lock (rbytes::member), so a read never sees a half-written slot. Stable views read through the registry (kiln.registry's
// resolved view), so what they give is what the registry gives, mapped and proven per build.
// Every refusal names the view, the entry and the field in Kiln words; kiln.log gets each distinct one once (at most 256).
// The game is reached only through the Env: the service and its host test have no game memory and no service table.
#pragma once
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <vector>

#include "../../../core/abi/kiln_api.h"
#include "services/kiln_assets.h"
#include "services/kiln_registry_view.h"
#include "views.h"

namespace kc::registry::views {

struct ServiceEnv {
    // The game's resources are resident (the registry stage ran); false + *why while they are not.
    std::function<bool(std::string* why)> ready;
    // Every struct type of the game's reflection that has at least one resource: qualified name and hash.
    std::function<std::vector<std::pair<std::string, uint32_t>>(std::string* why)> resource_types;
    // One type, depth-limited (rbytes describe); nullopt: not a struct type of this build.
    std::function<std::optional<TypeDesc>(const std::string& type)> describe;
    // The resources of a type, in the archive's order (parts other than 0 left out).
    std::function<bool(uint32_t type_hash, std::vector<KilnAssetKey>* out)> list;
    // One member of a resource (path "" = the whole resource) as rbytes::member gives it; false + *why.
    std::function<bool(const KilnAssetKey& key, const std::string& type, const std::string& path, json* out, std::string* why)> member;
    // The regmap of the running build (the registries' rows: coveredBy) and the alias table (regmap/views.json; missing: none).
    std::function<bool(map::RegMap* out)> regmap;
    std::function<bool(Aliases* out, std::vector<std::string>* errs)> aliases;
    std::optional<Schema> schema;  // unset: the generated one
    // Stable views: the registry's resolved view (kiln.registry read/select) and a field's mapping on this build.
    std::function<KilnResult(const std::string& registry, const std::string& id, const std::string& field, std::string* out)> registry_read;
    std::function<KilnResult(const std::string& registry, const std::string& selector, std::string* out)> registry_select;
    std::function<bool(const std::string& qualified, std::string* state, std::string* reason)> registry_mapping;
    std::function<void(int level, const std::string& line)> log;  // 0 info, 1 warn
};

class Service {
   public:
    explicit Service(ServiceEnv env);
    // kiln.registry.view's members. On a refusal (KILN_ERR_UNAVAILABLE, KILN_ERR_NOT_FOUND, KILN_ERR_INVALID_ARGUMENT) *out is
    // the reason in Kiln words; on KILN_OK it is the JSON the header describes. Any thread: the service lock is held only to
    // build the catalog (once) and to take or publish an entry listing, never while the game's bytes are read, so a long
    // listing of one view (thousands of entries: seconds) holds back no other call.
    KilnResult views(std::string* out);
    KilnResult fields(const std::string& view, std::string* out);
    KilnResult read(const std::string& view, const std::string& id, const std::string& field, std::string* out);
    KilnResult select(const std::string& view, const std::string& selector, std::string* out);
    KilnResult entry(const std::string& view, const std::string& id, std::string* out);

   private:
    // One listing of an advanced view's entries, never changed once published: a call keeps the one it started on (a later
    // listing replaces it in entries_, never in place).
    struct Entries {
        std::vector<KilnAssetKey> keys;  // the type's resources they were made from
        std::vector<std::string> ids, names;
        std::map<std::string, size_t> by_id, by_guid;
    };
    using Listing = std::shared_ptr<const Entries>;
    // The other views' listings one call has taken (an id field resolves each view's GUIDs from one listing per call).
    using Taken = std::map<std::string, Listing>;
    KilnResult prepare(std::string* why);  // the catalog (under mu_); KILN_OK: catalog_ is built and never changes again
    Listing entries(const View& v, std::string* why);  // the current listing (listed again when the type's resources changed)
    KilnResult read_one(const View& v, const Listing& es, size_t i, const std::string& field, Taken* taken, json* out, std::string* why);
    KilnResult find_entry(const View& v, const std::string& id, Listing* es, size_t* i, std::string* why);
    KilnResult read_stable(const View& v, const std::string& id, const std::string& field, std::string* out);
    void note(const std::string& line);

    ServiceEnv env_;
    std::recursive_mutex mu_;
    bool built_ = false;
    std::string unbuilt_;  // why the catalog has no advanced views (no type list on this build), "" when it has
    Catalog catalog_;
    Aliases aliases_;
    std::map<std::string, Listing> entries_;
    std::map<std::string, std::shared_ptr<std::mutex>> listing_;  // one listing of a view at a time
    std::set<std::string> noted_;
};

// The C table (kiln_registry_view.h) over a service: argument checks, the copies out (a refusal's reason too). Binds the one
// service the table serves (once, before it is registered; a later call replaces it).
const KilnRegistryViewApi* view_table(Service* service);

// Registers kiln.registry.view over the game (view_table.cpp; kiln-content.dll only). stage_service.cpp's bind_service calls it.
void bind_view_service();

// The game's reflection as views.h's plain types (view_types.cpp, over gamedata's type graph); nullopt: not a struct type.
std::optional<TypeDesc> describe_type(const std::string& qualified);

}  // namespace kc::registry::views
