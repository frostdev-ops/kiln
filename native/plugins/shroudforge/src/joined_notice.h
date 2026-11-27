// SPDX-License-Identifier: MIT
// kiln-shroudforge: what Kiln says when a ShroudForge mod tries to change the world's blocks in a game that joined a
// server. No Windows code, so the host test (tests/test-joined-notice.sh) builds it on Linux.
//
// Why: a joined client's world is the server's to change, in ShroudForge as under Kiln. ShroudForge 1.8.1 (and the fork
// pin 6cf01443) answers a joined client's runtime.world.voxel.get_grid_spec with status 5, "original registered voxel
// grid contract is unavailable" (read_registered_voxel_grid.cpp GetSpecForSession: the entity session guard takes only
// a LocalAuthority world), and Kiln's writer (host_voxels.cpp) refuses a write there, which the mod sees as ShroudForge's
// outcome 1, "voxel write was rejected before changing the world". Those texts are ShroudForge's and stay as they are;
// but they do not say why, and a player sees nothing at all. netjunk, 2026-11-27: Altar Cleanup (Nexus 417) on a client
// joined to a dedicated server waited 30 s on the grid contract and gave up.
//
// What Kiln adds, once per joined session (the client world's count of sessions): one kiln.log line that names the mod
// (from ShroudForge's own line of it, "[<time> UTC] [<mod>] ...") and says why, and a Kiln toast in the game (Kiln.Ui
// reads shroudforge.json "joinedWorldEdit", which plugin.cpp writes). Joined = a client world runs and no server world:
// a world this game hosts, or none at all (the main menu), is never "joined".
#pragma once
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

#include "kiln_api.h"

namespace ksf::joined {

// kiln.log's reason for a joined client (host_voxels.cpp's refusal, and the notice line)
inline constexpr const char* kReason =
    "this game joined a server, and only the server or the player hosting can change the world's blocks; a ShroudForge mod "
    "that changes blocks has to run there (ShroudForge works the same way)";
// ... and with no world at all (the main menu, a world loading)
inline constexpr const char* kNoWorld = "no world runs in this game as its host yet";

// The worlds Kiln's core reports (KilnSessionInfo): a server world, a client world, the client world's session count.
struct Worlds {
    bool server = false;
    bool client = false;
    uint32_t client_session = 0;
};
inline bool joined(const Worlds& w) { return w.client && !w.server; }
inline const char* refusal_reason(const Worlds& w) { return joined(w) ? kReason : kNoWorld; }

// The worlds of kiln-core's session answer (host_voxels.cpp's session_worlds). Without the core's world destroy hook
// (lifecycle 0) a world's pointer outlives the world; the core's state counts a world only while it stepped in the last
// 3 s. So the client world counts while the state says a world runs (back at the main menu after a joined game it says
// none), and any server world pointer, live or not, keeps the game from being called joined: never a wrong notice.
inline Worlds from_session(const KilnSessionInfo& s) {
    Worlds w;
    w.server = s.worlds[KILN_WORLD_SERVER] != 0;
    w.client = s.worlds[KILN_WORLD_CLIENT] != 0 && s.state != KILN_SESSION_NONE;
    w.client_session = s.sessions[KILN_WORLD_CLIENT];
    return w;
}

// One line of ShroudForge's log: "[<L> <date> <time> UTC] [<source>] <message>" (log_level.h). The source of a mod's
// line is the mod's id. Empty for a line of another shape.
inline std::string_view line_source(std::string_view line) {
    if (line.size() < 4 || line[0] != '[') return {};
    const size_t head = line.find("] [");
    if (head == std::string_view::npos) return {};
    const size_t start = head + 3, end = line.find(']', start);
    if (end == std::string_view::npos || end == start) return {};
    return line.substr(start, end - start);
}
inline std::string_view line_message(std::string_view line) {
    const std::string_view source = line_source(line);
    if (source.empty()) return line;
    const size_t at = static_cast<size_t>(source.data() - line.data()) + source.size() + 1;
    return at < line.size() ? line.substr(at) : std::string_view{};
}

// ShroudForge's own answers to a world edit that a joined client cannot make, as a mod logs them: the grid contract
// (status 5), the write's outcome 1, and the write operation that is not available. "voxel-grid-pending" (a world still
// loading) is not one.
inline bool world_edit_refused(std::string_view message) {
    return message.find("original registered voxel grid contract is unavailable") != std::string_view::npos ||
           message.find("voxel write was rejected before changing the world") != std::string_view::npos ||
           message.find("world operation is not ready: runtime.world.voxel.write") != std::string_view::npos;
}

// What to say: the session it is for, the mod ("" when no line named it) and kiln.log's line.
struct Fire {
    uint32_t session = 0;
    std::string mod;
    std::string log;
};

// A mod's id as words: at most 96 printable ASCII characters (ShroudForge's ids are shorter and plain; a log line is
// anyone's text).
inline std::string plain_mod(std::string_view mod) {
    std::string out;
    for (const char c : mod)
        if (c > 0x20 && c < 0x7f && c != '[' && out.size() < 96) out += c;
    return out;
}

inline std::string notice_line(const std::string& mod) {
    return (mod.empty() ? std::string("A ShroudForge mod") : "ShroudForge mod " + mod) +
           " tried to change the world's blocks and was refused: " + kReason;
}

// Not thread-safe: one caller (plugin.cpp's log mirror holds its lock). `poll`: the mirror's count of polls so far.
class Notice {
public:
    // host_voxels.cpp refused a write in a joined game: the mod's following line names it; when none does within two
    // mirror polls, flush tells it unnamed.
    void write_refused(const Worlds& w, uint64_t poll) {
        if (!joined(w) || told(w.client_session) || pending_) return;
        pending_ = true;
        pending_session_ = w.client_session;
        pending_poll_ = poll;
    }

    // One of ShroudForge's log lines, as the mirror reads it.
    std::optional<Fire> line(std::string_view text, const Worlds& w, uint64_t /*poll*/) {
        if (!joined(w) || told(w.client_session)) return std::nullopt;
        const std::string_view source = line_source(text);
        if (source.empty() || !world_edit_refused(line_message(text))) return std::nullopt;
        return fire(w.client_session, plain_mod(source));
    }

    // After each mirror poll.
    std::optional<Fire> flush(const Worlds& w, uint64_t poll) {
        if (!pending_ || poll < pending_poll_ + 2) return std::nullopt;
        pending_ = false;
        if (!joined(w) || w.client_session != pending_session_ || told(pending_session_)) return std::nullopt;
        return fire(pending_session_, {});
    }

private:
    bool told(uint32_t session) const { return told_ && told_session_ == session; }
    Fire fire(uint32_t session, std::string mod) {
        told_ = true;
        told_session_ = session;
        pending_ = false;
        Fire f;
        f.session = session;
        f.log = notice_line(mod);
        f.mod = std::move(mod);
        return f;
    }
    bool told_ = false, pending_ = false;
    uint32_t told_session_ = 0, pending_session_ = 0;
    uint64_t pending_poll_ = 0;
};

}  // namespace ksf::joined
