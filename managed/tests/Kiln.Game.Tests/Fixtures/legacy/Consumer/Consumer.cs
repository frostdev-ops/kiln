// SPDX-License-Identifier: MIT
// Touches every member of the nine dev.24 types the way a mod built against dev.24 does: each field (fixed buffers
// through their element), both constants of each struct, ComponentTypes, and every Components method. Run against
// another Kiln.Game, a missing or retyped member fails to bind (MissingFieldException, MissingMethodException,
// TypeLoadException) when the method is compiled. With no game, Components reads throw NotSupportedException from
// kiln.world; that is caught: the call bound and ran.
extern alias dev24;
using G = dev24::Kiln.GameTypes;

namespace Kiln.Game.LegacyConsumer;

public static unsafe class Probe
{
    public static List<string> Run()
    {
        var log = new List<string>();
        Structs(log);
        Constants(log);
        ComponentsApi<G.NetworkHealth>(log);
        ComponentsApi<G.Health>(log);
        ComponentsApi<G.Mana>(log);
        ComponentsApi<G.Stamina>(log);
        ComponentsApi<G.CurrentTransform>(log);
        ComponentsApi<G.LastTransform>(log);
        ComponentsApi<G.Velocity>(log);
        ComponentsApi<G.TeleportCount>(log);
        ComponentsApi<G.NpcCommon>(log);
        return log;
    }

    static void Structs(List<string> log)
    {
        var nh = new G.NetworkHealth { health = 3, healthMax = 7 };
        log.Add($"NetworkHealth {nh.health} {nh.healthMax} {sizeof(G.NetworkHealth)}");

        var h = new G.Health();
        h.dataStorage[0] = 1; h.dataStorage[31] = 2;
        log.Add($"Health {h.dataStorage[0] + h.dataStorage[31]} {sizeof(G.Health)}");
        var m = new G.Mana();
        m.dataStorage[31] = 3;
        log.Add($"Mana {m.dataStorage[31]} {sizeof(G.Mana)}");
        var s = new G.Stamina();
        s.dataStorage[31] = 4;
        log.Add($"Stamina {s.dataStorage[31]} {sizeof(G.Stamina)}");

        var ct = new G.CurrentTransform();
        ct.transform[55] = 5;
        log.Add($"CurrentTransform {ct.transform[55]} {sizeof(G.CurrentTransform)}");
        var lt = new G.LastTransform();
        lt.transform[55] = 6; lt.velocity[11] = 7;
        log.Add($"LastTransform {lt.transform[55] + lt.velocity[11]} {sizeof(G.LastTransform)}");
        var v = new G.Velocity();
        v.velocity[11] = 8;
        log.Add($"Velocity {v.velocity[11]} {sizeof(G.Velocity)}");
        var tc = new G.TeleportCount { counter = 9 };
        tc.lastTeleportTime[7] = 1;
        log.Add($"TeleportCount {tc.counter + tc.lastTeleportTime[7]} {sizeof(G.TeleportCount)}");

        var n = new G.NpcCommon
        {
            interactionStates = 1, targetSearchStates = 1, movementMode = 1,
            sleepTime = 1, wakeUpTime = 1, closestPlayerDistance = 1, squaredDistanceToTarget = 1, yDistanceToTarget = 1,
            historyIndex = 1, useReducedRoamingSpace = 1, shouldBeMoving = 1, taskFailures = 1, unstuckTries = 1,
            goToTargetTries = 1,
        };
        n.lastStateChange[7] = 1; n.stateTimer[7] = 1; n.currentStateDuration[7] = 1; n.stuckTime[7] = 1;
        n.stuckTimeOut[7] = 1; n.resumeUpdateTimeOffset[7] = 1; n.queryId[3] = 1; n.closestPlayerId[3] = 1;
        n.currentTargetId[3] = 1; n.closestBuildingCursorId[3] = 1; n.bedId[3] = 1; n.previousTargetId[3] = 1;
        n.currentTargetPosition[11] = 1; n.closestPlayerPosition[23] = 1; n.interactionTarget[63] = 1;
        n.avoidWaterData[15] = 1; n.history[1439] = 1; n.currentSequenceId[15] = 1;
        float floats = n.sleepTime + n.wakeUpTime + n.closestPlayerDistance + n.squaredDistanceToTarget + n.yDistanceToTarget;
        int bytes = n.interactionStates + n.targetSearchStates + n.movementMode + n.historyIndex
            + n.useReducedRoamingSpace + n.shouldBeMoving + n.taskFailures + n.unstuckTries + n.goToTargetTries
            + n.lastStateChange[7] + n.stateTimer[7] + n.currentStateDuration[7] + n.stuckTime[7] + n.stuckTimeOut[7]
            + n.resumeUpdateTimeOffset[7] + n.queryId[3] + n.closestPlayerId[3] + n.currentTargetId[3]
            + n.closestBuildingCursorId[3] + n.bedId[3] + n.previousTargetId[3] + n.currentTargetPosition[11]
            + n.closestPlayerPosition[23] + n.interactionTarget[63] + n.avoidWaterData[15] + n.history[1439]
            + n.currentSequenceId[15];
        log.Add($"NpcCommon {floats} {bytes} {sizeof(G.NpcCommon)}");
    }

    static void Constants(List<string> log)
    {
        // Constants are copied into this assembly at compile time: these bind to nothing, but a mod has them.
        log.Add($"Hash {G.NetworkHealth.Hash:X8} {G.Health.Hash:X8} {G.Mana.Hash:X8} {G.Stamina.Hash:X8} "
            + $"{G.CurrentTransform.Hash:X8} {G.LastTransform.Hash:X8} {G.Velocity.Hash:X8} {G.TeleportCount.Hash:X8} "
            + $"{G.NpcCommon.Hash:X8}");
        log.Add($"Component {G.NetworkHealth.Component} {G.Health.Component} {G.Mana.Component} {G.Stamina.Component} "
            + $"{G.CurrentTransform.Component} {G.LastTransform.Component} {G.Velocity.Component} "
            + $"{G.TeleportCount.Component} {G.NpcCommon.Component}");
        log.Add($"ComponentTypes {G.ComponentTypes.NetworkHealth:X8} {G.ComponentTypes.Health:X8} "
            + $"{G.ComponentTypes.Mana:X8} {G.ComponentTypes.Stamina:X8} {G.ComponentTypes.CurrentTransform:X8} "
            + $"{G.ComponentTypes.LastTransform:X8} {G.ComponentTypes.Velocity:X8} "
            + $"{G.ComponentTypes.TeleportCount:X8} {G.ComponentTypes.NpcCommon:X8}");
    }

    static void ComponentsApi<T>(List<string> log) where T : unmanaged
    {
        var e = default(EntityId);
        var parts = new List<string> { typeof(T).Name, G.Components.NameOf<T>() ?? "null", G.Components.Available.ToString() };
        parts.Add(Try(() => G.Components.TryGet<T>(e, out _).ToString()));
        parts.Add(Try(() => G.Components.Get<T>(e).HasValue.ToString()));
        parts.Add(Try(() => G.Components.Has<T>(e).ToString()));
        parts.Add(Try(() => G.Components.All<T>().Count.ToString()));
        log.Add("Components " + string.Join(' ', parts));
    }

    static string Try(Func<string> call)
    {
        try { return call(); }
        catch (NotSupportedException) { return "unavailable"; }
    }
}
