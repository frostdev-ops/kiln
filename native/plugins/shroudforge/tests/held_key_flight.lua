-- SPDX-License-Identifier: MIT
-- Held W/S and SF Unlimited Flight under Kiln-hosted ShroudForge without ShroudForge's key helper.
-- (docs/kiln/shroudforge-compat.md 5.5.) Kiln's bundled runtime ships no shroudforge.exe, so SF's keybind queries
-- (shroudforge.input.*, fed by `shroudforge.exe --keyboard-input` over a pipe) answer "not held". This check runs the
-- mod's real Lua with SF's own override helpers and synthetic flight fixture (loaded from the SF source tree at run
-- time; nothing of it is copied here) and shows where its steering input comes from instead. No game, no build.
--   lua5.4 native/plugins/shroudforge/tests/held_key_flight.lua <SF source root> [<staged component with mods/>]
local src_root = assert(arg[1], "usage: held_key_flight.lua <SF source root> [<component dir>]")
local component = arg[2] or src_root
local function read(path)
    local f = assert(io.open(path, "rb"))
    local data = f:read("a")
    f:close()
    return data
end

flight_source = read(component .. "/mods/sf-unlimited-flight/src/mod.lua")
local manifest = read(component .. "/mods/sf-unlimited-flight/extended.mod.json")
overrides_source = read(src_root .. "/modules/mod-runtime/lua/ingame/overrides/track_runtime_overrides.lua")
override_targets_factory = assert(load(read(src_root .. "/modules/mod-runtime/lua/ingame/overrides/create_override_targets.lua"),
    "override targets"))()
-- SF's fixture is a list of `do` blocks after its local fixture() builder: take the builder only.
local fixture_text = read(src_root .. "/modules/mod-runtime/tests/unlimited_flight_lifecycle.lua")
local cut = assert(fixture_text:find("\ndo\n", 1, true), "SF's flight fixture changed layout")
local fixture = assert(load(fixture_text:sub(1, cut) .. "return fixture\n", "SF flight fixture"))()

local checks = 0
local function check(ok, label)
    checks = checks + 1
    if not ok then error("FAIL " .. label, 2) end
end

-- 1. The mod names no key API and declares no keybind setting, so SF never even starts its key reader for it
--    (create_mod_api.rs starts it only for a mod with a `control: "keybind"` setting).
check(not flight_source:find("shroudforge%.input"), "mod.lua does not call shroudforge.input")
check(not flight_source:find("keybind", 1, true), "mod.lua names no keybind")
check(not manifest:find('"keybind"', 1, true), "extended.mod.json declares no keybind control")
check(flight_source:find('"keen::ecs::ActorInput", {"desiredLocalMoveInput.z"}', 1, true) ~= nil,
    "steering reads the game's ActorInput.desiredLocalMoveInput.z")

-- 2. shroudforge.input as the hosted runtime answers it without the helper (observe_keyboard.rs: the SDL reader never
--    becomes ACTIVE, so is_down is false and press_revision 0). Every access to it is counted.
local input_access = {}
local helperless_input = setmetatable({}, {__index = function(_, name)
    input_access[#input_access + 1] = name
    return function() return false end
end})
local function host_without_helper()
    local settings = shroudforge.settings
    shroudforge = setmetatable({settings = settings}, {__index = function(_, name)
        if name == "input" then
            input_access[#input_access + 1] = "input"
            return helperless_input
        end
        error("unexpected shroudforge." .. tostring(name))
    end})
end

-- Hold S for 2 s, release for 0.3 s, hold W for 2 s, at the mod's 100 ms update interval.
local function run(label, mode, reaches, players)
    local f = fixture()
    host_without_helper()
    f.settings.unlimitedFlying = true
    f.active_item, f.pitch_mode, f.delta_seconds = f.item_id, mode, 0.1
    if players then f.players = players end
    local seen = {}
    for _, phase in ipairs({{key = "S", z = -1, n = 20}, {key = "none", z = 0, n = 3}, {key = "W", z = 1, n = 20}}) do
        local first, steady
        for _ = 1, phase.n do
            -- reaches: the held key is in the ActorInput the mod reads; otherwise that component stays at 0
            f.pitch_input = reaches and phase.z or 0
            f:tick()
            local rate = f.glider.equipment.gliderConfig.pitchAngleSpeed
            if first == nil then first, steady = rate, true else steady = steady and rate == first end
        end
        seen[phase.key] = {rate = first, steady = steady}
    end
    print(string.format("%-38s S held %6s | released %6s | W held %6s | effect %s: %s", label, tostring(seen.S.rate),
        tostring(seen.none.rate), tostring(seen.W.rate), tostring(f.state), tostring(f.detail)))
    f:unload()
    return seen, f
end

-- 2a. The key is in the game's ActorInput: steering follows it with no key helper at all.
local ok, okf = run("pitch-speed, key in ActorInput", "YawSpeedPitchSpeed", true)
check(ok.S.steady and ok.S.rate == -70, "S held 2 s: inverted native rate (climb) on every update")
check(ok.none.steady and ok.none.rate == 0, "released: pitch held")
check(ok.W.steady and ok.W.rate == 70, "W held 2 s: native rate (dive) on every update")

-- 2b. What the report's symptom needs: the ActorInput the mod reads never shows the key. Neither key does anything,
--     and the effect report reads exactly as it does when steering works.
local stuck, stuckf = run("pitch-speed, ActorInput stays 0", "YawSpeedPitchSpeed", false)
check(stuck.S.rate == 0 and stuck.none.rate == 0 and stuck.W.rate == 0, "stuck input: neither S nor W changes the rate")
check(stuckf.state == okf.state and stuckf.detail == okf.detail, "stuck input: effect report identical to working steering")

-- 2c. Target-angle input mode: the mod leaves the game's rate (W dives, S does not climb) and says why.
local target, targetf = run("target-angle mode, key in ActorInput", "YawSpeedPitchTargetAngle", true)
check(target.S.rate == 70 and target.W.rate == 70, "target-angle: game's own rate, no upward tilt")
check(targetf.state == "waiting" and tostring(targetf.detail):find("pitch-speed input controls", 1, true) ~= nil,
    "target-angle: effect detail names the mode")

-- 2d. Two PlayerInput entities in the authoritative world: the same, with its own reason.
local two, twof = run("two players, key in ActorInput", "YawSpeedPitchSpeed", true, {11, 12})
check(two.S.rate == 70 and two.W.rate == 70, "two players: game's own rate, no upward tilt")
check(twof.state == "waiting" and tostring(twof.detail):find("one authoritative player", 1, true) ~= nil,
    "two players: effect detail names the reason")

check(#input_access == 0, "no shroudforge.input access in any case (" .. table.concat(input_access, ",") .. ")")
print(string.format("PASS held_key_flight (%d checks): the steering never asks ShroudForge's key helper; "
    .. "a held key reaches it only through the game's ActorInput", checks))
