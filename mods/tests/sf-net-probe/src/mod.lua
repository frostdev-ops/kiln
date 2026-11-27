-- SPDX-License-Identifier: MIT
-- Kiln test probe for ShroudForge's runtime.network.send_mod between a joined game and the game it joined (MIT). Uses only
-- ShroudForge's public Lua API (runtime.network, shroudforge.log), as an author would for a world edit in multiplayer:
-- the joined game asks, the server (a dedicated server, or the player hosting) checks the sender's permissions and acts.
-- Install it on both sides. Every line starts with "net-probe:"; kiln.log mirrors ShroudForge's log ("[sf] ...").
--   joined game: "send ping N to <id> (<where the id came from>): ok" every 10 s, "got pong N ... from <id>"
--   server/host: "got ping N from <peer> (mod <from_mod>): permissions CanEditWorld,..." and its answer
local MOD = "kiln-sf-net-probe"
local net = runtime.network
local function say(fmt, ...) shroudforge.log.warn("net-probe: " .. string.format(fmt, ...)) end

local last_status, last_peers, waited, sent, told_lab, told_alone = nil, nil, 0, 0, false, false
local send_results = {}

local function text(v) return v == nil and "-" or tostring(v) end

local function status()
    local ok, s = pcall(net.status)
    if not ok then return nil, "status raised: " .. tostring(s) end
    return s
end

-- the joined game's target: the game it joined (its own session log), else the dedicated server its health probe named
local function target(s)
    if s.session_host_steam_id then return s.session_host_steam_id, "session host" end
    if s.remote_dedicated_server_steam_id then return s.remote_dedicated_server_steam_id, "server probe" end
    if s.local_dedicated_server_steam_id then return s.local_dedicated_server_steam_id, "server on this PC" end
    if s.configured_server_steam_id then return s.configured_server_steam_id, "Network setting" end
    return nil
end

local function permissions(peer)
    local p, why = net.player_permissions(peer)
    if type(p) ~= "table" then return "none (" .. text(why) .. ")" end
    local names = {}
    for name, on in pairs(p) do if on then names[#names + 1] = name end end
    table.sort(names)
    return #names > 0 and table.concat(names, ",") or "none listed"
end

local function hosting(s)
    if s.role == "server" then return true end
    local peers = net.connected_peers()
    return type(peers) == "table"
end

local function report_status(s)
    local key = table.concat({ text(s.available), text(s.role), text(s.local_steam_id), text(s.session_host_steam_id),
        text(s.remote_dedicated_server_steam_id), text(s.local_dedicated_server_steam_id), text(s.service_ready), text(s.reason) }, " ")
    if key == last_status then return end
    last_status = key
    say("status available=%s role=%s local=%s session_host=%s remote_server=%s local_server=%s service_ready=%s reason=%s",
        text(s.available), text(s.role), text(s.local_steam_id), text(s.session_host_steam_id),
        text(s.remote_dedicated_server_steam_id), text(s.local_dedicated_server_steam_id), text(s.service_ready), text(s.reason))
end

local function report_peers()
    local peers, why = net.connected_peers()
    local key = type(peers) == "table" and table.concat(peers, ",") or ("none: " .. text(why))
    if key == last_peers then return end
    last_peers = key
    say("connected peers: %s", key)
end

-- what a lab with no player can show: each call answers, nothing raises (once). Nothing goes to a Steam account outside
-- this game: send_mod gets peer "0", which ShroudForge refuses before Steam, and player_permissions (which only reads
-- this game's own log) asks for the lowest individual Steam ID, account 0, which is nobody's.
local function lab_calls()
    if told_lab then return end
    told_lab = true
    local ok, why = net.send_mod("0", MOD, "lab ping")
    say("lab: send_mod to peer 0 (refused before Steam): ok=%s reason=%s", text(ok), text(why))
    say("lab: player_permissions of a player not in this game: %s", permissions("76561197960265728"))
    local got, rwhy = net.receive_mod(4)
    say("lab: receive_mod answers %s (%s), reason=%s", type(got), type(got) == "table" and #got or 0, text(rwhy))
    say("NET PROBE READY")
end

local function receive(s, host)
    local got, why = net.receive_mod(16)
    if type(got) ~= "table" then
        if why then say("receive_mod: %s", text(why)) end
        return
    end
    for _, m in ipairs(got) do
        if host then
            say("got %s from %s (mod %s, reliable %s): permissions %s", m.payload, m.peer_steam_id, text(m.from_mod),
                text(m.reliable), permissions(m.peer_steam_id))
            local ok, swhy = net.send_mod(m.peer_steam_id, MOD, "pong for " .. m.payload)
            say("answer to %s: ok=%s reason=%s", m.peer_steam_id, text(ok), text(swhy))
        else
            say("got %s from %s (mod %s)", m.payload, m.peer_steam_id, text(m.from_mod))
        end
    end
end

-- seconds since the last round: the clock when Lua has one, else the update interval (0.5 s). Not the runtime's delta:
-- on a Kiln lab server it was about 0.026 s a call, its own tick's, so 10 s of deltas took 3 minutes.
local clock = os and os.time
local last_round
local function elapsed()
    local now = clock and clock()
    if not now then return 0.5 end
    last_round = last_round or now
    local d = now - last_round
    last_round = now
    return d
end

local function on_update()
    waited = waited + elapsed()
    local s, why = status()
    if not s then say("%s", why) return end
    report_status(s)
    if not s.available then return end
    local host = hosting(s)
    receive(s, host)
    if waited < 10 then return end
    waited = 0
    if host then
        report_peers()
        lab_calls()
        return
    end
    local peer, from = target(s)
    if not peer then
        if not told_alone then say("no server to send to yet (no session host, no server probe)") end
        told_alone = true
        return
    end
    told_alone = false
    sent = sent + 1
    local ok, swhy = net.send_mod(peer, MOD, "ping " .. sent)
    local key = text(ok) .. text(swhy)
    if sent <= 3 or send_results[key] == nil then
        say("send ping %d to %s (%s): ok=%s reason=%s", sent, peer, from, text(ok), text(swhy))
    end
    send_results[key] = true
end

return {
    update_interval_ms = 500,
    on_load = function() say("loaded") end,
    on_update = on_update,
}
