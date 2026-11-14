// SPDX-License-Identifier: MIT
// Host test of the shared capacity and registration contract (abi/services/kiln_capacity.h, kiln_capacity_util.hpp).
//   native/core/tests/test-capacity.sh
#include <cstddef>
#include <cstdio>
#include <string>

#include "services/kiln_capacity_util.hpp"

namespace cap = kiln::capacity;

namespace {
int g_fail = 0, g_pass = 0;
void check(bool ok, const char* what) {
    std::printf("%s %s\n", ok ? "PASS" : "FAIL", what);
    ok ? ++g_pass : ++g_fail;
}
KilnCapacityInfo fresh() {
    KilnCapacityInfo i;
    std::memset(&i, 0xAB, sizeof i);  // garbage: a helper must write every field
    i.size = sizeof i;
    return i;
}
bool counts_zero(const KilnCapacityInfo& i) { return !i.capacity && !i.game_used && !i.kiln_used && !i.reserved; }
}  // namespace

int main(int argc, char** argv) {
    check(sizeof(KilnCapacityInfo) == 48 && offsetof(KilnCapacityInfo, revision) == 40 && alignof(KilnCapacityInfo) == 8,
          "KilnCapacityInfo is 48 bytes, revision at +40");
    check(sizeof(KilnDefinitionReply) == 64 && offsetof(KilnDefinitionReply, snapshot_revision) == 48 &&
              offsetof(KilnDefinitionReply, registration_id) == 16 && offsetof(KilnDefinitionReply, definition_guid) == 32 &&
              offsetof(KilnDefinitionReply, reason_code) == 56 && offsetof(KilnDefinitionReply, kind) == 60,
          "KilnDefinitionReply is 64 bytes, revision at +48");

    {
        auto i = fresh();
        check(cap::publish(&i, 4096, 0, 12, 3, cap::Scope::process(), 7, true) && i.size == sizeof i && i.capacity == 4096 &&
                  i.game_used == 0 && i.kiln_used == 12 && i.reserved == 3 && i.readiness == KILN_CAPACITY_VALID &&
                  i.flags == (KILN_CAPACITY_READY | KILN_CAPACITY_PROCESS | KILN_CAPACITY_EXPANDED) &&
                  i.world_kind == KILN_CAPACITY_NO_WORLD_KIND && i.generation == 0 && i.reserved_zero == 0 && i.revision == 7,
              "a ready process budget: READY, PROCESS, EXPANDED; no world kind, generation 0");
    }
    {
        auto i = fresh();
        check(cap::publish(&i, 896, 817, 20, 0, cap::Scope::world(KILN_WORLD_CLIENT, 3), 9, false) &&
                  i.flags == (KILN_CAPACITY_READY | KILN_CAPACITY_WORLD) && i.world_kind == 2 && i.generation == 3,
              "a ready world budget: exactly the WORLD scope flag, the kind and the generation");
    }
    {
        auto i = fresh();
        check(!cap::publish(&i, 64, 35, 20, 10, cap::Scope::process(), 4, false) && counts_zero(i) &&
                  i.readiness == KILN_CAPACITY_ACCOUNTING_MISMATCH && !(i.flags & KILN_CAPACITY_READY) &&
                  (i.flags & KILN_CAPACITY_PROCESS) && i.revision == 4,
              "counts over the capacity: ACCOUNTING_MISMATCH, counts 0, never a clamped budget");
        i = fresh();
        check(!cap::publish(&i, 100, UINT64_MAX, 2, 0, cap::Scope::process(), 4, false) && counts_zero(i) &&
                  i.readiness == KILN_CAPACITY_ACCOUNTING_MISMATCH,
              "a sum that wraps in 64 bits is a mismatch too");
        i = fresh();
        check(!cap::publish(&i, uint64_t{1} << 33, 1, 1, 1, cap::Scope::process(), 4, false) &&
                  i.readiness == KILN_CAPACITY_ACCOUNTING_MISMATCH,
              "a capacity that does not fit 32 bits is a mismatch");
        i = fresh();
        check(!cap::publish(&i, 64, 1, 1, 1, cap::Scope::world(KILN_WORLD_SERVER, 0), 4, false) &&
                  i.readiness == KILN_CAPACITY_NO_WORLD && counts_zero(i) && (i.flags & KILN_CAPACITY_WORLD),
              "a world budget with generation 0 is NO_WORLD");
    }
    for (uint32_t r : {KILN_CAPACITY_NOT_READY, KILN_CAPACITY_NO_WORLD, KILN_CAPACITY_WORLD_ENDED, KILN_CAPACITY_UNKNOWN_EXTERNAL,
                       KILN_CAPACITY_ACCOUNTING_MISMATCH, KILN_CAPACITY_NOT_FOUND}) {
        auto i = fresh();
        cap::not_ready(&i, r, cap::Scope::world(KILN_WORLD_SERVER, 5), 11);
        const bool ext = (i.flags & KILN_CAPACITY_EXTERNAL_UNKNOWN) != 0;
        check(i.size == sizeof i && counts_zero(i) && i.readiness == r && !(i.flags & KILN_CAPACITY_READY) &&
                  ext == (r == KILN_CAPACITY_UNKNOWN_EXTERNAL) && (i.flags & KILN_CAPACITY_WORLD) && !(i.flags & KILN_CAPACITY_PROCESS) &&
                  i.world_kind == 0 && i.generation == 5 && i.revision == 11 && i.reserved_zero == 0,
              "not ready: counts 0, READY clear, EXTERNAL_UNKNOWN only with UNKNOWN_EXTERNAL, one scope flag");
    }
    {
        auto i = fresh();
        cap::not_ready(&i, KILN_CAPACITY_VALID, cap::Scope::process(), 1);
        check(i.readiness == KILN_CAPACITY_NOT_READY && !(i.flags & KILN_CAPACITY_READY), "not_ready never says VALID");
        KilnCapacityInfo small{};
        small.size = 40;
        check(!cap::valid_out(&small) && !cap::valid_out(nullptr) && cap::valid_out(&i), "valid_out checks the caller's size");
    }
    {
        cap::Revision rev;
        check(rev.current() == 1 && rev.next() == 2 && rev.next() == 3 && rev.current() == 3, "a revision starts at 1 and rises by one");
    }
    {
        // the vector the managed test (CapacityMappingTests) holds too
        uint8_t guid[16], sha[32], id[16], id2[16];
        for (int k = 0; k < 16; ++k) guid[k] = static_cast<uint8_t>(k + 1);
        for (int k = 0; k < 32; ++k) sha[k] = static_cast<uint8_t>(0xA0 + k);
        std::string hex;
        check(cap::registration_id("my-mod", KILN_DEFINITION_BUFF, guid, sha, id), "a registration id is derived");
        cap::detail::hex(hex, id, 16);
        std::printf("     registration id vector: %s\n", hex.c_str());
        check((id[6] >> 4) == 5 && (id[8] >> 6) == 2, "it is a version 5 UUID");
        check(argc < 2 || hex == argv[1], "it equals the vector the managed test holds");
        cap::registration_id("my-mod", KILN_DEFINITION_BUFF, guid, sha, id2);
        check(!std::memcmp(id, id2, 16), "it is stable");
        cap::registration_id("my-mod", KILN_DEFINITION_VFX, guid, sha, id2);
        const bool kind_differs = std::memcmp(id, id2, 16) != 0;
        cap::registration_id("my-mod2", KILN_DEFINITION_BUFF, guid, sha, id2);
        const bool mod_differs = std::memcmp(id, id2, 16) != 0;
        sha[0] ^= 1;
        cap::registration_id("my-mod", KILN_DEFINITION_BUFF, guid, sha, id2);
        check(kind_differs && mod_differs && std::memcmp(id, id2, 16) != 0, "kind, mod and definition hash each change it");
        check(!cap::registration_id("my-mod", 99, guid, sha, id2), "an unknown kind has no id");
        const auto abc = cap::detail::sha1(reinterpret_cast<const uint8_t*>("abc"), 3);
        std::string abc_hex;
        cap::detail::hex(abc_hex, abc.data(), 20);
        check(abc_hex == "a9993e364706816aba3e25717850c26c9cd0d89d", "SHA-1 of \"abc\" is the standard's");

        KilnDefinitionReply r;
        std::memset(&r, 0xCD, sizeof r);
        r.size = sizeof r;
        cap::reply_init(&r, KILN_DEFINITION_BUFF, id, guid);
        cap::reply_ok(&r, KILN_DEFINITION_REGISTERED, 1, 21);
        check(r.size == sizeof r && r.ok == 1 && r.disposition == KILN_DEFINITION_REGISTERED && r.slots_consumed == 1 &&
                  r.reason_code == KILN_DEFINE_NONE && r.snapshot_revision == 21 && r.kind == KILN_DEFINITION_BUFF &&
                  !std::memcmp(r.registration_id, id, 16) && !std::memcmp(r.definition_guid, guid, 16),
              "a registered reply");
        cap::reply_ok(&r, KILN_DEFINITION_ACCEPTED_NEXT_START, 1, 22);
        check(r.ok == 1 && r.slots_consumed == 0, "accepted for the next start consumes no slot now");
        cap::reply_refused(&r, KILN_DEFINE_CAPACITY_EXCEEDED, 23);
        check(r.ok == 0 && r.disposition == KILN_DEFINITION_REFUSED && r.slots_consumed == 0 &&
                  r.reason_code == KILN_DEFINE_CAPACITY_EXCEEDED,
              "a refusal: ok 0, its code, no slot");
        cap::reply_refused(&r, KILN_DEFINE_REGISTRATION_FAILED_RETAINED, 24, KILN_DEFINITION_RETAINED, 1);
        check(r.ok == 0 && r.disposition == KILN_DEFINITION_RETAINED && r.slots_consumed == 1, "a retained record keeps its slot");
        cap::reply_refused(&r, KILN_DEFINE_NONE, 25);
        check(r.reason_code != KILN_DEFINE_NONE, "code 0 only with ok 1");
    }
    {
        namespace fs = std::filesystem;
        const fs::path dir = fs::temp_directory_path() / ("kiln-capacity-test-" + std::to_string(std::rand()));
        check(cap::pending_files(dir / "pending").empty(), "no pending folder: nothing to replay");
        check(cap::write_atomic(dir / "pending" / "zeta" / "b.json", "{\"b\":1}") &&
                  cap::write_atomic(dir / "pending" / "zeta" / "a.json", "{\"a\":1}") &&
                  cap::write_atomic(dir / "pending" / "alpha" / "c.json", "{\"c\":1}") &&
                  cap::write_atomic(dir / "pending" / "zeta" / "a.json", "{\"a\":2}"),
              "pending files are written (one of them twice)");
        std::ofstream(dir / "pending" / "alpha" / "d.json.tmp") << "half";
        fs::create_directories(dir / "pending" / "alpha" / "nested");
        const auto files = cap::pending_files(dir / "pending");
        check(files.size() == 3 && files[0].mod == "alpha" && files[0].name == "c.json" && files[1].mod == "zeta" &&
                  files[1].name == "a.json" && files[2].name == "b.json",
              "replay order is by mod, then file name; temporary files and folders are skipped");
        std::ifstream in(files[1].path);
        std::string text((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
        check(text == "{\"a\":2}" && !fs::exists(dir / "pending" / "zeta" / "a.json.tmp"), "a rewrite replaces the file whole; no temporary file stays");
        check(!cap::write_atomic(dir / "pending" / "alpha" / "nested", "x"), "a write that cannot replace its target fails");
        std::error_code ec;
        fs::remove_all(dir, ec);
    }
    std::printf("%d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
