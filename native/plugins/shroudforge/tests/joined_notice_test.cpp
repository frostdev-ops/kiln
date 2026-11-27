// SPDX-License-Identifier: MIT
// src/joined_notice.h: what kiln-shroudforge says when a ShroudForge mod tries to change blocks in a game that joined a
// server (kiln.log's reason, the mod's name from ShroudForge's own line, once per joined session).
//   tests/test-joined-notice.sh (g++/clang++, ASan/UBSan; no game, no Windows)
#include "../src/joined_notice.h"

#include <cstdio>
#include <string>

namespace {
int total = 0, failed = 0;
void check(bool ok, const char* what) {
    ++total;
    if (!ok) {
        ++failed;
        std::printf("FAIL %s\n", what);
    }
}
bool has(const std::string& s, const char* part) { return s.find(part) != std::string::npos; }

// netjunk's client, Kiln 0.2.0-dev.31.19 (2026-11-27 support report, kiln.log 1308/1328): Altar Cleanup joined to a
// dedicated server, as ShroudForge's log has the lines (kiln.log mirrors them after "[sf] ")
const char* kGridLine =
    "[I 2026-11-27 01:36:36,153 UTC] [altar-cleanup] [altar-cleanup] waiting: voxel grid: original registered voxel grid "
    "contract is unavailable";
const char* kGaveUp =
    "[W 2026-11-27 01:37:06,157 UTC] [altar-cleanup] [altar-cleanup] gave up after 30 s: voxel grid: original registered "
    "voxel grid contract is unavailable";
const char* kWriteLine =
    "[W 2026-11-26 14:59:37,826 UTC] [sf-voxel-probe] voxel-probe: box 1 (4096 cells) write: ok=false reason=voxel write "
    "was rejected before changing the world (try 1)";
const char* kPending = "[I 2026-11-27 01:39:55,419 UTC] [altar-cleanup] [altar-cleanup] waiting: voxel grid: voxel-grid-pending";
const char* kNetwork =
    "[W 2026-11-27 01:34:20,247 UTC] [network] Network service is waiting for Steam Networking Messages available=false";
}  // namespace

int main() {
    using namespace ksf::joined;
    const Worlds joined_1{false, true, 1}, joined_2{false, true, 2}, hosting{true, true, 2}, menu{false, false, 1};

    {  // the reason Kiln gives, word for word (the brief's text), and when it applies
        check(std::string(kReason) ==
                  "this game joined a server, and only the server or the player hosting can change the world's blocks; a "
                  "ShroudForge mod that changes blocks has to run there (ShroudForge works the same way)",
              "the joined reason, word for word");
        check(refusal_reason(joined_1) == kReason, "a joined game: the joined reason");
        check(refusal_reason(menu) == kNoWorld && !has(kNoWorld, "joined"), "no world at all: not the joined reason");
        check(joined(joined_1) && !joined(hosting) && !joined(menu), "joined = a client world and no server world");
    }
    {  // kiln-core's session answer (host_voxels.cpp's session_worlds)
        const auto session = [](uint32_t side, uintptr_t server, uintptr_t client, uint32_t state, uint32_t lifecycle) {
            KilnSessionInfo s{};
            s.size = sizeof s;
            s.side = side;
            s.state = state;
            s.lifecycle = lifecycle;
            s.worlds[KILN_WORLD_SERVER] = server;
            s.worlds[KILN_WORLD_CLIENT] = client;
            s.sessions[KILN_WORLD_SERVER] = 2;
            s.sessions[KILN_WORLD_CLIENT] = 4;
            return from_session(s);
        };
        const Worlds remote = session(KILN_SIDE_CLIENT, 0, 0x10, KILN_SESSION_REMOTE, 1);
        check(joined(remote) && remote.client_session == 4, "a joined game, with the client world's session");
        check(!joined(session(KILN_SIDE_CLIENT, 0x20, 0x10, KILN_SESSION_HOST, 1)), "a game this client hosts");
        check(!joined(session(KILN_SIDE_CLIENT, 0, 0, KILN_SESSION_NONE, 1)), "the main menu");
        check(!joined(session(KILN_SIDE_SERVER, 0x20, 0, KILN_SESSION_DEDICATED, 1)), "a dedicated server");
        // Without the core's world destroy hook (lifecycle 0) a world's pointer outlives the world, and the core's state
        // counts a world only while it stepped in the last 3 s (tick.cpp session()).
        check(!joined(session(KILN_SIDE_CLIENT, 0, 0x10, KILN_SESSION_NONE, 0)),
              "back at the main menu after a joined game, without the destroy hook: not joined (the stale client world)");
        check(joined(session(KILN_SIDE_CLIENT, 0, 0x10, KILN_SESSION_REMOTE, 0)), "a joined game without the destroy hook");
        check(!joined(session(KILN_SIDE_CLIENT, 0x20, 0x10, KILN_SESSION_REMOTE, 0)),
              "a hosted world's stale pointer (no destroy hook, or a host's world that stalls 3 s): never called joined");
    }
    {  // ShroudForge's line: its source is the mod, its message the rest
        check(line_source(kGridLine) == "altar-cleanup", "the mod of a ShroudForge line");
        check(line_source(kNetwork) == "network", "a runtime line's source");
        check(line_source("no tag at all") == "" && line_source("[I 2026] [unclosed") == "", "a line of another shape");
        check(world_edit_refused(line_message(kGridLine)), "the grid contract a joined client lacks (status 5)");
        check(world_edit_refused(line_message(kGaveUp)), "the mod's give-up line with that reason");
        check(world_edit_refused(line_message(kWriteLine)), "ShroudForge's outcome 1 text");
        check(!world_edit_refused(line_message(kPending)), "a pending grid is not a refusal (a hosted world loading)");
        check(!world_edit_refused(line_message(kNetwork)), "an unrelated line");
    }
    {  // once per joined session, naming the mod from ShroudForge's line
        Notice n;
        auto f = n.line(kGridLine, joined_1, 1);
        check(f && f->session == 1 && f->mod == "altar-cleanup", "a joined client's refusal fires, naming the mod");
        check(f && has(f->log, "altar-cleanup") && has(f->log, kReason), "its kiln.log line names the mod and the reason");
        check(!n.line(kGaveUp, joined_1, 2), "the same session: no second notice");
        check(!n.line(kWriteLine, joined_1, 3), "another mod in the same session: no second notice");
        check(!n.flush(joined_1, 9), "nothing pending");
        auto again = n.line(kGaveUp, joined_2, 10);
        check(again && again->session == 2, "a new joined session tells again");
    }
    {  // not joined: nothing (a hosted world, the main menu)
        Notice n;
        check(!n.line(kGridLine, hosting, 1), "a hosting game: no notice");
        check(!n.line(kGridLine, menu, 1), "the main menu: no notice");
        n.write_refused(menu, 1);
        check(!n.flush(menu, 5), "a refused write with no client world: no notice");
        check(!n.line(kPending, joined_1, 1), "a pending grid on a joined client: no notice");
    }
    {  // Kiln's own refusal of a write (host_voxels.cpp): the mod's following line names it, else it fires unnamed
        Notice n;
        n.write_refused(joined_1, 4);
        check(!n.flush(joined_1, 4) && !n.flush(joined_1, 5), "waits for the mod's line (two mirror polls)");
        auto f = n.line(kWriteLine, joined_1, 5);
        check(f && f->mod == "sf-voxel-probe", "the mod's line names it");
        check(!n.flush(joined_1, 9), "and it is not told twice");

        Notice m;
        m.write_refused(joined_1, 4);
        auto u = m.flush(joined_1, 6);
        check(u && u->mod.empty() && has(u->log, kReason) && has(u->log, "A ShroudForge mod"), "no line from the mod: unnamed");
        check(!m.line(kWriteLine, joined_1, 7), "and then once only");

        Notice s;  // the session ended before the flush: the pending write is dropped
        s.write_refused(joined_1, 4);
        check(!s.flush(hosting, 6), "the game hosts by then: no notice");
    }
    {  // a mod's name is shown as words only (it comes from a file anyone can write)
        Notice n;
        auto f = n.line("[I 2026-11-27 01:36:36,153 UTC] [evil\x1b[31m] voxel write was rejected before changing the world",
                        joined_1, 1);
        check(f && f->mod.find('\x1b') == std::string::npos, "control characters are left out of the mod's name");
    }
    std::printf("joined notice: %d passed, %d failed\n", total - failed, failed);
    return failed ? 1 : 0;
}
