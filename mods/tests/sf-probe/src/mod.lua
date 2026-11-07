-- Kiln lab probe for ShroudForge mods hosted by kiln-shroudforge (MIT). Uses only ShroudForge's public Lua API.
local overrides = runtime.overrides.new()
local last

local function report(state, detail)
    local key = state .. "|" .. tostring(detail)
    if key == last then return end
    last = key
    runtime.report_effect(state, detail)
    shroudforge.log.info("probe " .. state .. ": " .. tostring(detail))
end

local function enabled(key) return shroudforge.settings.get(key, false) == true end

local function update()
    if runtime.world.session_id() == 0 then report("waiting", "no authoritative world") return end
    overrides:begin_update()
    overrides:restore_disabled(enabled)
    if overrides.fault then report("write-failed", overrides.fault) return end
    if not enabled("disableDurability") then
        report("no-change", overrides:summary("disableDurability is off")) return
    end
    local service, why = runtime.services.get("ecs::GameSettings")
    if not service then report("waiting", "GameSettings: " .. tostring(why)) return end
    local target, failure = runtime.overrides.service(service, "enableDurability")
    if not target then report("waiting", "enableDurability: " .. tostring(failure)) return end
    overrides:apply(target.key, "disableDurability", target, function() return false end)
    local state = overrides.fault and "write-failed" or
        (overrides.waiting_count > 0 and "waiting" or (overrides.writes > 0 and "write-confirmed" or "no-change"))
    report(state, overrides:summary("enableDurability=false"))
end

return {
    update_interval_ms = 500,
    on_load = function() report("waiting", "probe loaded") end,
    on_update = update,
}
