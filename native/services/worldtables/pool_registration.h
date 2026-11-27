// SPDX-License-Identifier: MIT
#pragma once
#include "merge.h"
#include "../../core/abi/services/kiln_capacity_util.hpp"
#include <functional>
#include <algorithm>
#include <cstring>

namespace kwt {
// Shared receipt digest, identical to buffs and VFX.
inline std::array<uint8_t,32> pool_sha256(const std::string& text) { return kiln::capacity::sha256(text); }
inline std::string pool_hex(const uint8_t* bytes,size_t count) {
    std::string out;kiln::capacity::detail::hex(out,bytes,count);return out;
}
struct PoolReceipt {
    PoolDefinition definition;
    std::array<uint8_t,16> id{};
    KilnDefinitionReply reply{};
    std::string reason;
    std::string report() const { return nlohmann::json{{"reason",reason.empty()?nlohmann::json(nullptr):nlohmann::json(reason)},{"warnings",nlohmann::json::array()}}.dump(); }
    std::string key() const {return pool_hex(id.data(),id.size());}
    std::string envelope() const {
        return nlohmann::json{{"format",1},{"mod",definition.mod},{"block",definition.block},{"definition",definition.guid.text()},
            {"registration",key()},{"canonical",nlohmann::json::parse(definition.definition)}}.dump();
    }
};
struct PoolOwnerLease {KilnOwner owner=0;uint64_t step=0;uint32_t misses=0;bool seen=false;};
inline bool restore_pruned_pool(std::map<Guid,std::string>& pruned,const PoolReceipt& receipt,
                               const std::function<bool(const PoolReceipt&)>& persist) {
    if(receipt.reply.disposition==KILN_DEFINITION_REGISTERED && pruned.count(receipt.definition.guid)) {
        if(!persist(receipt))return false;
        pruned.erase(receipt.definition.guid);
    }
    return true;
}
// At a world's Post: whether the lease's owner is gone, so its registrations become Retained. A Pre this step settles it;
// a missed Pre alone does not (a step the owner sat out, a subscription made after this step's Pre): the core's own
// liveness (`alive`: post_world's answer for the owner, KILN_ERR_REVOKED once it is revoked) decides.
inline bool pool_lease_gone(PoolOwnerLease& lease,uint64_t step,const std::function<KilnResult()>& alive) {
    if(lease.seen && lease.step==step)return false;
    return alive()==KILN_ERR_REVOKED;
}
inline size_t pool_define_report_size(const PoolReceipt& preview) {
    auto inactive=preview;inactive.reason="owner is no longer active";
    size_t required=std::max(preview.report().size(),inactive.report().size());
    if(preview.reply.disposition==KILN_DEFINITION_ACCEPTED_NEXT_START) {
        auto failure=preview;failure.reason="could not persist the pool definition and receipt";
        required=std::max(required,failure.report().size());
    }
    return required;
}
class PoolRegistrations {
public:
    uint64_t revision=1;
    std::map<std::array<uint8_t,16>,PoolReceipt> receipts;
    PoolReceipt prepare(const PoolDefinition& definition,uint32_t timing,bool served,uint32_t refusal=KILN_DEFINE_NONE,const std::string& why="") const {
        PoolReceipt r;r.definition=definition;const auto digest=pool_sha256(definition.definition);
        kiln::capacity::registration_id(definition.mod,KILN_DEFINITION_BUILDING_MATERIAL_POOL,definition.guid.b.data(),digest.data(),r.id.data());
        r.reply.size=sizeof r.reply;kiln::capacity::reply_init(&r.reply,KILN_DEFINITION_BUILDING_MATERIAL_POOL,r.id.data(),definition.guid.b.data());
        if(refusal) {r.reason=why.empty()?"pool admission refused":why;kiln::capacity::reply_refused(&r.reply,refusal,revision,served?KILN_DEFINITION_RETAINED:KILN_DEFINITION_REFUSED,served?uint32_t(definition.entries.size()):0);}
        else if(served) kiln::capacity::reply_ok(&r.reply,KILN_DEFINITION_REGISTERED,uint32_t(definition.entries.size()),revision);
        else if(timing==KILN_DEFINE_ALLOW_NEXT_START) kiln::capacity::reply_ok(&r.reply,KILN_DEFINITION_ACCEPTED_NEXT_START,0,revision);
        else {r.reason="the boot material-pool registry is closed";kiln::capacity::reply_refused(&r.reply,KILN_DEFINE_REGISTRY_CLOSED,revision);}
        return r;
    }
    PoolReceipt commit(PoolReceipt r,const std::function<bool(const PoolReceipt&)>& persist) {
        auto it=receipts.find(r.id);
        const auto same=[&]{return it!=receipts.end() && it->second.reply.disposition==r.reply.disposition && it->second.reply.reason_code==r.reply.reason_code && it->second.reply.slots_consumed==r.reply.slots_consumed && it->second.reason==r.reason;};
        if(same()) return it->second;
        // A stricter later call cannot revoke an already-durable next-start request.
        if(it!=receipts.end() && it->second.reply.disposition==KILN_DEFINITION_ACCEPTED_NEXT_START && r.reply.reason_code==KILN_DEFINE_REGISTRY_CLOSED) {
            r.reply.snapshot_revision=revision;return r;
        }
        if(r.reply.disposition==KILN_DEFINITION_ACCEPTED_NEXT_START && !persist(r)) {
            r.reason="could not persist the pool definition and receipt";
            kiln::capacity::reply_refused(&r.reply,KILN_DEFINE_STORAGE_FAILURE,revision);
            if(same()) return it->second;
        }
        if(r.reply.ok)for(auto& [id,old]:receipts) {
            if(id!=r.id && old.definition.mod==r.definition.mod && old.definition.guid==r.definition.guid && old.reply.disposition==KILN_DEFINITION_ACCEPTED_NEXT_START) {
                old.reason="a newer definition superseded this pending pool";
                kiln::capacity::reply_refused(&old.reply,KILN_DEFINE_SUPERSEDED,++revision);
            }
        }
        r.reply.snapshot_revision=++revision;receipts[r.id]=r;return r;
    }
    void owner_inactive(const Guid& definition) {
        for(auto& [id,r]:receipts)if(r.definition.guid==definition && r.reply.disposition==KILN_DEFINITION_REGISTERED) {
            r.reason="owner is no longer active; the boot row remains in the game";
            kiln::capacity::reply_refused(&r.reply,KILN_DEFINE_OWNER_INACTIVE,++revision,KILN_DEFINITION_RETAINED,r.reply.slots_consumed);
        }
    }
    // Called only after the envelope has been successfully removed from durable storage.
    void remove_pending(const Guid& definition) {
        for(auto& [id,r]:receipts)if(r.definition.guid==definition && r.reply.disposition==KILN_DEFINITION_ACCEPTED_NEXT_START) {
            r.reason="the code no longer defines this pending pool";
            kiln::capacity::reply_refused(&r.reply,KILN_DEFINE_REMOVED,++revision);
        }
    }
    PoolReceipt query(const std::array<uint8_t,16>& id) const {
        auto it=receipts.find(id);if(it!=receipts.end())return it->second;
        PoolReceipt r;r.id=id;r.reason="no material-pool registration of that id";
        r.reply.size=sizeof r.reply;uint8_t empty[16]{};kiln::capacity::reply_init(&r.reply,KILN_DEFINITION_BUILDING_MATERIAL_POOL,id.data(),empty);
        kiln::capacity::reply_refused(&r.reply,KILN_DEFINE_NOT_FOUND,revision);return r;
    }
};
// The blocks document a replayed pool is read back from at the next start (worldtables.cpp read_mod): the section it was
// defined in, which pool_envelope read it from (a tool pool's canonical lists "entries", a building pool's "materials").
// A ToolMaterialPoolBlock wrapped as buildingMaterialPools was refused there at every start and never registered (kiln#152).
inline ojson pool_replay_document(const PoolDefinition& definition) {
    return ojson{{definition.tool?"toolMaterialPools":"buildingMaterialPools",{{definition.block,ojson::parse(definition.definition)}}}};
}
inline bool pool_envelope(const ojson& envelope,const std::string& mod,PoolDefinition* out,std::string* why) {
    try {
        if(!envelope.is_object() || envelope.value("format",0)!=1 || envelope.value("mod","")!=mod)throw std::runtime_error("invalid envelope owner/format");
        ModFile mf;mf.mod=mod;mf.clients="mod";
        const auto block=envelope.at("block").get<std::string>();
        const auto& canonical = envelope.at("canonical");
        const char* section = canonical.is_object() && canonical.contains("entries") ? "toolMaterialPools" : "buildingMaterialPools";
        read_blocks(ojson{{section,{{block,canonical}}}},&mf);
        if(!mf.errors.empty() || mf.pools.size()!=1 || mf.pools[0].guid.text()!=envelope.at("definition").get<std::string>())throw std::runtime_error("invalid canonical pool");
        PoolRegistrations ledger;auto r=ledger.prepare(mf.pools[0],KILN_DEFINE_ALLOW_NEXT_START,false);
        if(r.key()!=envelope.at("registration").get<std::string>())throw std::runtime_error("pool receipt checksum mismatch");
        *out=std::move(mf.pools[0]);return true;
    } catch(const std::exception& e) {*why=e.what();return false;}
}
} // namespace kwt
