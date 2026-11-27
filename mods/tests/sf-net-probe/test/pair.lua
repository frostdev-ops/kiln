-- SPDX-License-Identifier: MIT
-- The probe's two halves (a joined client and a dedicated server) against each other over a fake runtime.network:
-- send_mod on one side lands in the other's receive_mod, as ShroudForge's transport does. No game, no Steam. And a game
-- with no server to send to (single player). The probe must never message a Steam account outside the game it is in.
--   lua5.4 mods/tests/sf-net-probe/test/pair.lua mods/tests/sf-net-probe/src/mod.lua
local path = assert(arg[1])
local CLIENT, SERVER = "76561198000000001", "90298358383607810"
local queues = { [CLIENT] = {}, [SERVER] = {} }
local lines = {}
local outside = {}  -- peers a send_mod went to that are neither side of the pair (a real Steam account, under Steam)
local now = 0  -- both sides' clock (os.time), half a second a round
local clock = true
local function side(me, role, other)
    local env = setmetatable({}, { __index = _G })
    env.os = clock and { time = function() return now end } or false  -- false: not the host Lua's own os
    env.shroudforge = { log = { warn = function(s) lines[#lines + 1] = role .. ": " .. s end, info = function() end } }
    env.runtime = { network = {
        status = function()
            return { available = true, role = role == "server" and "server" or "client", local_steam_id = me,
                session_host_steam_id = role == "client" and other or nil, service_ready = true }
        end,
        connected_peers = function()
            if role == "server" then return { CLIENT } end
            return nil, "this game does not host a multiplayer session"
        end,
        player_permissions = function(peer)
            if role == "server" and peer == CLIENT then return { CanEditWorld = true, CanEditBase = true } end
            return nil, "this player is not in this game or has not logged in yet"
        end,
        send_mod = function(peer, mod, payload)
            -- ShroudForge's own check before Steam (transport/lua/network.rs steam_id)
            if not tostring(peer):match("^%d+$") then return false, "peer Steam ID must be a decimal uint64 string" end
            if tonumber(peer) == 0 then return false, "peer Steam ID must be nonzero" end
            if not queues[peer] then  -- Steam takes a message to anyone: it would reach that account
                outside[#outside + 1] = role .. " -> " .. peer
                return true
            end
            table.insert(queues[peer], { peer_steam_id = me, from_mod = "kiln-sf-net-probe", to_mod = mod, payload = payload, reliable = true })
            return true
        end,
        receive_mod = function(limit)
            local out, q = {}, queues[me] or {}
            while #q > 0 and #out < (limit or 16) do out[#out + 1] = table.remove(q, 1) end
            return out
        end,
    } }
    local chunk = assert(loadfile(path, "t", env))
    return chunk()
end
-- 25 s at the update interval (0.5 s), with and without a Lua clock (os.time); the runtime's delta is its own tick's
-- (about 0.026 s on a lab server), not the time between calls
local function has(p) for _, l in ipairs(lines) do if l:find(p, 1, true) then return true end end end
local all = true
for _, with_clock in ipairs({ true, false }) do
    clock, lines = with_clock, {}
    local c, s = side(CLIENT, "client", SERVER), side(SERVER, "server", CLIENT)
    c.on_load(); s.on_load()
    for _ = 1, 50 do now = now + 0.5; c.on_update(0.026); s.on_update(0.026) end
    print("-- " .. (with_clock and "with" or "without") .. " os.time")
    for _, l in ipairs(lines) do print(l) end
    local ok = has("client: net-probe: send ping 1 to " .. SERVER .. " (session host): ok=true") and
        has("server: net-probe: got ping 1 from " .. CLIENT .. " (mod kiln-sf-net-probe, reliable true): permissions CanEditBase,CanEditWorld") and
        has("client: net-probe: got pong for ping 2 from " .. SERVER) and has("server: net-probe: NET PROBE READY")
    all = all and ok
end
if #outside > 0 then
    print("FAIL the probe sent to Steam accounts outside the game: " .. table.concat(outside, ", "))
    all = false
end
-- a game with no server to send to (single player, or the main menu's network): it says so once, sends nothing
lines, outside = {}, {}
local alone = side("76561198000000002", "alone", nil)
alone.on_load()
for _ = 1, 120 do now = now + 0.5; alone.on_update(0.026) end
local told = 0
for _, l in ipairs(lines) do if l:find("no server to send to", 1, true) then told = told + 1 end end
print("-- alone: " .. told .. " 'no server' line(s) in 60 s, " .. #outside .. " send(s) outside")
if told ~= 1 or #outside > 0 then
    print("FAIL alone: the probe says 'no server to send to' once (said " .. told .. " times) and sends nothing")
    all = false
end
print(all and "PAIR OK" or "PAIR FAILED")
os.exit(all and 0 or 1)
