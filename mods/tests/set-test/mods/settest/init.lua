-- Kiln's conformance mod for the SET Lua API on a server (no player needed). Prints PASS/FAIL lines.
local u = require("util")
local check = u.check

check("GetVersion", GetVersion() == "0.1.1", GetVersion())
check("GetGameBuild", type(GetGameBuild()) == "number" and GetGameBuild() > 0, GetGameBuild())
check("json roundtrip", json.decode(json.encode({a = 1, b = {2, 3}})).b[2] == 3)
local f = io.open("state.json", "w")
f:write(json.encode({runs = 1}))
f:close()
check("io in own folder", io.open("state.json") ~= nil)
check("io sandbox", not pcall(io.open, "../escape.txt", "w"))
check("shared API frozen", not pcall(function() Ecs.player = nil end))

-- Types (kiln.types): the game's reflection
local hf = Types.fields("Health")
check("Types.fields Health", #hf > 0, #hf .. " fields")
local rarity = Types.enum("ItemRarity")
check("Types.enum ItemRarity", rarity and rarity.Legendary ~= nil, rarity and rarity.Legendary)

-- Systems (kiln.types): the game's ECS systems
local all = Systems.list()
check("Systems.list", #all > 500, #all .. " systems")
check("Systems.list search", #Systems.list("stamina") > 0)

local ticks, healed, profiled, observed = 0, false, false, 0
Observe("EntityDiedEvent", function(e) observed = observed + 1 end)

-- Events: post through the game's own queue, observe it after the next step. TestEvent1 is registered but a server
-- world has no stream for it; DebugEnemyProjectileEvent has one, and nothing on a server acts on it (debug drawing).
local posted, seen = false, nil
Observe("DebugEnemyProjectileEvent", function(e) seen = e.targetPosition.z end)

-- Hooks.observe: the server's event_tracking system update (server build 26430253, rva 0x2190f0; rcx = the job). It
-- runs every server step with no player; actor_animation, used before, only runs with players.
local hookCalls, hookDropped = 0, 0
local HOOK = {[1793791038] = "48 89 5C 24 10 48 89 6C 24 18 56 57 41 56 48 81 EC E0 00 00 00 41 B8 88"}
local okHook, hookErr = pcall(Hooks.observe, {name = "anim", pattern = HOOK[GetGameBuild()] or "00 11 22 33",
  args = {"ptrsize"}, queue = 64, fn = function(ev)
    hookCalls = hookCalls + 1
    hookDropped = hookDropped + ev.dropped
    if hookCalls == 1 then print(("settest: hook seq %d job 0x%x"):format(ev.seq, ev.args[1])) end
  end})
check("Hooks.observe registers", okHook, okHook and ("id " .. tostring(hookErr)) or hookErr)

registerForEvent("onInit", function() check("onInit", true) end)
registerForEvent("onWorldEnter", function()
  print("settest: entered the world, " .. Systems.list()[1])
  -- Res (kiln.assets + reflection)
  local g, logs = Res.find("ItemInfo", "Material_T1_Wood_Logs")
  check("Res.find ItemInfo", g ~= nil and logs ~= nil, g)
  if logs then
    check("Res field read", logs.maxStackSize ~= nil and logs.maxStackSize > 0, logs.maxStackSize)
    check("Res.get by GUID", Res.get(g, "ItemInfo") ~= nil)
    -- English names (Kiln's item catalog and kiln.spawn's template names)
    check("Res.text item", Res.text(g) == "Wood Logs", Res.text(g))
    check("Res.text item name ref", Res.text(logs.name) == "Wood Logs", Res.text(logs.name))
  end
  local tg = Res.find("TemplateResource", "Animal_Wildlife_T1_Goat_AG2")
  check("Res.text template", tg ~= nil and Res.text(tg) == "Common Goat", tg and Res.text(tg))
  check("Res.text unknown is nil", Res.text("00000000-0000-0000-0000-000000000001") == nil)
  -- Systems switch and profiler
  Systems.enable("stamina_depletion_sprint", false)
  check("Systems.enable off", Systems.enabled("stamina_depletion_sprint") == false)
  Systems.enable("stamina_depletion_sprint", true)
  check("Systems.enable on", Systems.enabled("stamina_depletion_sprint") == true)
  Systems.profile(true)
  -- Admin (kiln.settings)
  local s = Admin.settings()
  check("Admin.settings", s ~= nil and s.enemyDamageFactor ~= nil, s and s.enemyDamageFactor)
end)

Tick.add(function(dt)
  ticks = ticks + 1
  if ticks == 50 then
    local n, id, view = 0, nil, nil
    for e, h in Ecs.each("Health") do
      n = n + 1
      if not id and h.dataStorage[2] > 0 then id, view = e, h end
    end
    check("Ecs.each Health", n > 0, n .. " entities")
    if view then
      local before = view.dataStorage[0]
      local max = view.dataStorage[2]
      check("Ecs.get Health", Ecs.get(id, "Health") ~= nil, ("entity %d hp %d/%d"):format(id, before, max))
      -- a checked write: a value that does not fit the field is refused
      check("checked write refuses", not pcall(function() view.dataStorage[0] = 2^40 end))
      view.dataStorage[0] = max - 1                  -- a real write, read back through a fresh lookup
      local hurt = Ecs.get(id, "Health").dataStorage[0]
      view.dataStorage[0] = math.max(before, max)    -- then heal to full, as the guide's example does
      local now = Ecs.get(id, "Health").dataStorage[0]
      check("Ecs write heals", hurt == max - 1 and now == math.max(before, max), hurt .. " -> " .. now)
      healed = true
    end
    check("Ecs.player on a server without players", Ecs.player() == nil)
    local okLevel, levelErr = pcall(Admin.setLevel, 20)
    check("Admin.setLevel refuses with no player", not okLevel and tostring(levelErr):find("no player") ~= nil, levelErr)
    local okGive, giveErr = pcall(Admin.give, "wood logs", 1)
    check("Admin.give by English name resolves", okGive or not tostring(giveErr):find("no ItemInfo"), okGive or giveErr)
  end
  if ticks == 60 then
    local ok, r = pcall(Events.post, "DebugEnemyProjectileEvent", {targetPosition = {x = 1, y = 2, z = 42}})
    posted = ok and r
    check("Events.post", posted, ok and tostring(r) or r)
  end
  if ticks == 70 then
    check("Observe gets the posted event", seen == 42, seen)
    check("Hooks.observe delivers", hookCalls > 0, hookCalls .. " calls, " .. hookDropped .. " dropped")
  end
  if ticks == 120 and not profiled then
    profiled = true
    local list, secs = Systems.stats()
    check("Systems.stats", #list > 0 and secs > 0, #list .. " systems over " .. string.format("%.2f s", secs))
    if list[1] then print(("settest: costliest system %s %.3f ms, %d calls, %s"):format(list[1].name, list[1].ms, list[1].calls, list[1].world)) end
    Systems.profile(false)
    print("settest: done (EntityDiedEvent seen " .. observed .. ")")
  end
end)

return {ready = true}
