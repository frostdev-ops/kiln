// SPDX-License-Identifier: MIT
using System.Text;

namespace Kiln;

/// <summary>A buff on an entity: a player, a creature or an NPC (the game keeps them in the entity's BuffContainer, on
/// the server).</summary>
/// <param name="Id">The game's BuffTypeId: FNV-1a of the buff's GUID (<c>GameBuffs.Ids.X</c>, <see cref="Abilities.IdOf"/>).</param>
/// <param name="Slot">The game's BuffSlotType name: "Food", "Buff", "Debuff", "Rested", "TerrainDebuff".</param>
/// <param name="Remaining">Time left, when known.</param>
/// <param name="Level">Its level, when known.</param>
public sealed record EntityBuff(uint Id, string Slot, TimeSpan? Remaining = null, int Level = 0)
{
    /// <summary>True for this buff id.</summary>
    public bool Is(uint id) => Id == id;

    /// <summary>True for this buff's GUID (its id is FNV-1a of the GUID).</summary>
    public bool Is(Guid buff) => Id == Abilities.IdOf(buff);
}

/// <summary>The answer to <see cref="Abilities.RemoveBuff(EntityId, Guid)"/>: whether the game's removeBuff took the
/// buff off. <see cref="Ok"/> is false with a <see cref="Reason"/> when the entity did not have it (the game's own
/// answer) or when the removal did not run.</summary>
[ServerOnly]
public sealed record BuffRemoveResult : IKilnResult
{
    internal BuffRemoveResult(EntityId target, uint buff, bool ok, string? reason)
    {
        Target = target;
        Buff = buff;
        Ok = ok;
        Reason = ok ? null : reason;
    }

    /// <summary>The entity the buff was taken from.</summary>
    public EntityId Target { get; }

    /// <summary>The buff's id (FNV-1a of its GUID).</summary>
    public uint Buff { get; }

    /// <summary>True when the game removed the buff.</summary>
    public bool Ok { get; }

    /// <summary>Why nothing was removed; null when <see cref="Ok"/>.</summary>
    public string? Reason { get; }

    /// <summary>"removed 0x… from …", or the reason.</summary>
    public override string ToString() => Ok ? $"removed buff 0x{Buff:x8} from {Target}" : $"buff 0x{Buff:x8} not removed from {Target}: {Reason}";
}

public static partial class Abilities
{
    /// <summary>The program behind <see cref="ApplyBuff"/>: applyBuff(Buff, Target, -, -, Duration), then DSELF. Built
    /// here (a keen::ImpactProgram blob) and registered at first use.</summary>
    internal static readonly Guid BuffProgram = new("6b696c6e-6275-4666-8000-000000000001");

    /// <summary>The program behind <see cref="RemoveBuff(EntityId, Guid)"/>: removeBuff(WasRemoved, Buff, Target), Done =
    /// 1, then it waits (<see cref="RemoveWaitSteps"/> steps at most) for Kiln to read its two variables and destroy its
    /// entity, and destroys itself if nobody came. Kiln polls the instance, so the answer does not depend on the native
    /// event queue (which drops events when it is full).</summary>
    internal static readonly Guid RemoveBuffProgram = new("6b696c6e-6275-4666-8000-000000000002");

    /// <summary>The remove program's variables: the buff reference, the target, the game's answer, and the done flag.</summary>
    internal const uint VarBuff = 0, VarTarget = 1, VarWasRemoved = 2, VarDone = 3;

    /// <summary>Steps a removal waits for its program's answer before it gives up (about 5 s on a dedicated server).</summary>
    internal const ulong RemoveTimeoutSteps = 120;

    /// <summary>Steps the remove program keeps its answer readable before it destroys itself. Kiln reads it at the next
    /// step; the rest is slack, and the end of it keeps an entity from waiting for good when Kiln's managed side is gone.</summary>
    internal const int RemoveWaitSteps = 30;

    internal const string NotRemovedText = "the entity has no such buff (the game's removeBuff removed nothing)";

    private static bool _buffProgramReady, _removeProgramReady;

    /// <summary>Applies a buff or debuff to an entity through the game's own applyBuff native (the path the game's
    /// programs use: the buff entity, its program, BuffNetworkData for clients). <paramref name="buff"/> is the
    /// BuffType's GUID (its id is FNV-1a of it: Abilities.IdOf(buff)); <paramref name="duration"/> null = the buff's
    /// default lifetime. <paramref name="source"/> becomes the impact's root (credit, faction checks). Returns the
    /// short-lived impact entity that applies it (it destroys itself). Tick thread.</summary>
    public static Task<EntityId> ApplyBuff(EntityId target, Guid buff, TimeSpan? duration = null, EntityId source = default)
    {
        try
        {
            Game.AssertTickThread();
            if (target.IsNone) throw new ArgumentException("no target", nameof(target));
            EnsureBuffProgram();
            var at = World.Position(target) ?? throw new ArgumentException($"entity {target} has no position", nameof(target));
            return RunProgram(BuffProgram, at, new ImpactOptions
            {
                Root = source.IsNone ? target : source,
                Configs = new ImpactConfigs { ["Buff"] = buff, ["Target"] = target, ["Duration"] = duration ?? TimeSpan.Zero },
                Tag = "abilities-buff",
            });
        }
        catch (Exception e)
        {
            return Task.FromException<EntityId>(e);
        }
    }

    private static void EnsureBuffProgram()
    {
        if (_buffProgramReady && Programs.Info(Programs.IdOf(BuffProgram)) is not null) return;
        if (Programs.Info(Programs.IdOf(BuffProgram)) is null)
        {
            Programs.RegisterCore(BuildBuffProgram(), out var waiting);  // Kiln's program: not the gated mod call
            if (waiting) throw new InvalidOperationException("ApplyBuff: the game's ImpactRegistry is not loaded yet");
        }
        _buffProgramReady = true;
    }

    private static byte[] BuildBuffProgram()
    {
        const uint hBuff = 0xFFFF0000, hTarget = 0xFFFF0001, hDuration = 0xFFFF0002;
        uint[] code =
        [
            11, hDuration, 12, 12, 11, hTarget, 11, hBuff,  // args[4] Duration, [3] 0, [2] 0 (no damage), [1] Target, [0] Buff
            19, 0x6b696c01, 0x91f30b26,                      // ECALL applyBuff
            27, 29, 30,                                      // POP, DSELF, HALT
        ];
        // keen types: BuffTypeReference 0x305916d9 (16 B), ecs EntityId 0x737c8f86 (4 B), Time 0xdfe4e404 (8 B)
        (string Name, uint Type, int Size)[] vars = [("Buff::value", 0x305916d9, 16), ("Target::value", 0x737c8f86, 4),
            ("Duration::value", 0xdfe4e404, 8)];
        return ProgramBlob.Build(BuffProgram, code, [30], vars);
    }

    // ---------------------------------------------------------------------------------------------- remove ---------

    private sealed class Removal
    {
        public required EntityId Impact;
        public required EntityId Target;
        public required uint Buff;
        public required ulong Since;
        public required ModContext? Mod;
        public required TaskCompletionSource<BuffRemoveResult> Done;
    }

    // impact entity -> removal waiting for its program; polled by a service tick every step, and ended when the server
    // world closes (ServerStoppingEvent comes on the destroying thread on a client: hence the lock)
    private static readonly Dictionary<uint, Removal> Removals = new();
    private static int _closeHooked;
    private static bool _closed;  // the server world is closing: no step will read a removal's answer (under the Removals lock)

    /// <summary>The server world closed and no step has run since: a removal asked for now would never be answered.</summary>
    internal static bool RemovalsClosed
    {
        get { lock (Removals) return _closed; }
    }

    internal const string ClosingText = "the server world is closing: the removal was not started";

    private static readonly Dictionary<uint, Guid> GuidsById = new();
    private static IDisposable? _removalTick;
    private static ulong _removalStep;

    /// <summary>True while a removal waits for its program.</summary>
    internal static bool RemovalsPending
    {
        get { lock (Removals) return Removals.Count > 0; }
    }

    /// <summary>Takes a buff off an entity (a player, a creature, an NPC) through the game's own removeBuff native, the
    /// call the game's programs use: the buff entity goes, and with it the buff's effect and its icon on the clients.
    /// <paramref name="buff"/> is the BuffType's GUID (its id is FNV-1a of it). The removal runs in a small program Kiln
    /// builds and registers at first use; it waits one step, so the result comes about two steps later. The result is
    /// the game's own answer: <see cref="BuffRemoveResult.Ok"/> false when the entity did not have the buff. On success
    /// <see cref="BuffRemovedEvent"/> is posted. Tick thread, server.</summary>
    /// <exception cref="ArgumentException"><paramref name="target"/> is none or not in the world.</exception>
    /// <exception cref="KilnUnavailableException"><c>abilities.buffs</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the game's ImpactRegistry is not loaded yet.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(AbilitiesCaps.Buffs)]
    public static Task<BuffRemoveResult> RemoveBuff(EntityId target, Guid buff) => Remove(target, buff, IdOf(buff));

    /// <summary>Takes a buff off an entity by its id (<c>GameBuffs.Ids.X</c>, <see cref="EntityBuff.Id"/>): the game's
    /// removeBuff only hashes the GUID, so Kiln passes 16 bytes with this id's hash. Otherwise as
    /// <see cref="RemoveBuff(EntityId, Guid)"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="target"/> is none or not in the world, or
    /// <paramref name="buffId"/> is 0.</exception>
    /// <exception cref="KilnUnavailableException"><c>abilities.buffs</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or the game's ImpactRegistry is not loaded yet.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(AbilitiesCaps.Buffs)]
    public static Task<BuffRemoveResult> RemoveBuff(EntityId target, uint buffId)
    {
        if (buffId == 0) throw new ArgumentException("buff id 0 names no buff", nameof(buffId));
        Guid g;
        lock (GuidsById)
            if (!GuidsById.TryGetValue(buffId, out g))
                GuidsById[buffId] = g = GuidWithId(buffId);
        return Remove(target, g, buffId);
    }

    /// <summary>Every buff on an entity (a player, a creature, an NPC), from the game's buff slots. The game keeps them
    /// on the server only: on a joined client the list is empty. Tick thread.</summary>
    /// <exception cref="KilnUnavailableException"><c>abilities.buff_list</c> is off.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(AbilitiesCaps.BuffList)]
    public static IReadOnlyList<EntityBuff> BuffsOf(EntityId entity)
    {
        Game.AssertTickThread();  // off the thread kiln.players answers "wrong thread", which would read as "no buffs"
        AbilitiesCaps.Require(AbilitiesCaps.BuffList);
        if (entity.IsNone) return [];
        var list = PlayerStates.BuffsOf(entity);  // kiln.players reads any entity's BuffContainer
        var r = new EntityBuff[list.Count];
        for (var i = 0; i < r.Length; i++) r[i] = new EntityBuff(list[i].Id, list[i].Slot, list[i].Remaining, list[i].Level);
        return r;
    }

    private static Task<BuffRemoveResult> Remove(EntityId target, Guid buff, uint id)
    {
        Game.AssertTickThread();
        if (target.IsNone) throw new ArgumentException("no target", nameof(target));
        AbilitiesCaps.Require(AbilitiesCaps.Buffs);
        var at = World.Position(target) ?? throw new ArgumentException($"entity {target} is not in the world (no position)", nameof(target));
        HookWorldClose();
        // Asked for while the server world closes (a handler of the stop, a mod's OnUnload): the world is still there
        // and would take the helper, but no step runs any more to read its answer. A result, not a task that never ends.
        if (RemovalsClosed) return Task.FromResult(new BuffRemoveResult(target, id, false, ClosingText));
        EnsureRemoveProgram();
        EntityId e;
        try
        {
            e = RunProgram(RemoveBuffProgram, at, new ImpactOptions
            {
                Root = target,
                Configs = new ImpactConfigs { ["Buff"] = buff, ["Target"] = target },
                Tag = "abilities-unbuff",
            }).GetAwaiter().GetResult();
        }
        catch (SpawnException x)
        {
            // the game refused the removal's impact entity: a refusal, not a bad argument
            return Task.FromResult(new BuffRemoveResult(target, id, false, $"the game refused the removal's impact: {x.Message}"));
        }
        var r = new Removal
        {
            Impact = e, Target = target, Buff = id, Since = _removalStep, Mod = ModContext.CurrentMod,
            Done = new TaskCompletionSource<BuffRemoveResult>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        _removalTick ??= ServiceTick.Add("kiln.abilities removals", static (in TickInfo t) => PollRemovals());
        lock (Removals)
        {
            if (!_closed)
            {
                Removals[e.Value] = r;
                return r.Done.Task;
            }
        }
        // the world began to close between the check above and here
        try { Spawn.Destroy(e); }
        catch (Exception x) when (x is InvalidOperationException or NotSupportedException or SpawnException) { }
        return Task.FromResult(new BuffRemoveResult(target, id, false, ClosingText));
    }

    // Kiln's own subscription (not the calling mod's, which would end with that mod): when the server world closes, no
    // step polls any more (a client is back in its menu), so the waiting removals end here.
    private static void HookWorldClose()
    {
        if (Interlocked.Exchange(ref _closeHooked, 1) == 1) return;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try
        {
            Events.On<ServerStoppingEvent>(_ => CloseRemovals());
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    /// <summary>The server world closes: every waiting removal ends, not removed, with that reason.</summary>
    internal static void CloseRemovals()
    {
        List<Removal> open;
        lock (Removals)
        {
            _closed = true;
            open = Removals.Values.ToList();
            Removals.Clear();
        }
        foreach (var r in open) Finish(r, false, ReasonOf(RemovalState.WorldClosed));
    }

    /// <summary>What a pending removal's program says now. Pure, for the tests.</summary>
    internal enum RemovalState { Waiting, Removed, NotRemoved, Gone, WorldClosed, TimedOut }

    /// <summary>The removal's state from what was read this step: a server world running, the impact entity there, its
    /// done flag and the game's answer (null: not readable yet), and its age in steps.</summary>
    internal static RemovalState Judge(bool worldRunning, bool impactExists, bool? done, bool wasRemoved, ulong age) =>
        !worldRunning ? RemovalState.WorldClosed
        : done == true ? (wasRemoved ? RemovalState.Removed : RemovalState.NotRemoved)
        : !impactExists ? RemovalState.Gone
        : age > RemoveTimeoutSteps ? RemovalState.TimedOut
        : RemovalState.Waiting;

    internal static string? ReasonOf(RemovalState s) => s switch
    {
        RemovalState.NotRemoved => NotRemovedText,
        RemovalState.Gone => "the removal's impact went away before its answer was read (canceled or destroyed)",
        RemovalState.WorldClosed => "the server world closed before the removal ran",
        RemovalState.TimedOut => $"the removal program did not answer within {RemoveTimeoutSteps} steps",
        _ => null,
    };

    // every step while removals wait: read each program's done flag and answer from its live instance
    internal static unsafe void PollRemovals()
    {
        _removalStep++;
        List<Removal> open;
        lock (Removals)
        {
            _closed = false;  // a step runs: answers are read again (a new session after a closed one)
            if (Removals.Count == 0) return;
            open = Removals.Values.ToList();
        }
        var running = Spawn.WorldRunning;
        foreach (var r in open)
        {
            bool exists = false, removed = false;
            bool? done = null;
            if (running && World.Available && World.TryRead<ImpactComponentRaw>(r.Impact, "Impact", out var c))
            {
                exists = true;
                var t = AbilitiesNative.T;
                byte d = 0, w = 0;
                uint len;
                if (t != null && c.ImpactId != 0 && t->InstanceRead(c.ImpactId, VarDone, &d, 1, &len) == AbilitiesNative.Ok)
                {
                    // done only counts with the answer beside it: a read that fails is asked again at the next step
                    if (d == 0) done = false;
                    else if (t->InstanceRead(c.ImpactId, VarWasRemoved, &w, 1, &len) == AbilitiesNative.Ok)
                    {
                        done = true;
                        removed = w != 0;
                    }
                }
            }
            var state = Judge(running, exists, done, removed, _removalStep - r.Since);
            if (state == RemovalState.Waiting) continue;
            lock (Removals)
                if (!Removals.Remove(r.Impact.Value)) continue;  // the world closed meanwhile and ended it
            if (exists)
            {
                try { Spawn.Destroy(r.Impact); }
                catch (Exception x) when (x is InvalidOperationException or NotSupportedException or SpawnException) { }
            }
            Finish(r, state == RemovalState.Removed, ReasonOf(state));
        }
    }

    private static void Finish(Removal r, bool removed, string? reason)
    {
        if (removed && Events.HasSubscribers<BuffRemovedEvent>())
        {
            try
            {
                Events.Post(new BuffRemovedEvent(r.Target, r.Buff, r.Mod?.Info.Id));
            }
            catch (Exception ex)
            {
                ModLog.Error($"abilities: BuffRemovedEvent: {ex.Message}");
            }
        }
        r.Done.TrySetResult(new BuffRemoveResult(r.Target, r.Buff, removed, reason));
    }

    private static void EnsureRemoveProgram()
    {
        if (_removeProgramReady && Programs.Info(Programs.IdOf(RemoveBuffProgram)) is not null) return;
        if (Programs.Info(Programs.IdOf(RemoveBuffProgram)) is null)
        {
            Programs.RegisterCore(BuildRemoveProgram(), out var waiting);  // Kiln's program: not the gated mod call
            if (waiting) throw new InvalidOperationException("RemoveBuff: the game's ImpactRegistry is not loaded yet");
        }
        _removeProgramReady = true;
    }

    internal static byte[] BuildRemoveProgram()
    {
        const uint hBuff = 0xFFFF0000 | VarBuff, hTarget = 0xFFFF0000 | VarTarget, hWas = 0xFFFF0000 | VarWasRemoved,
            hDone = 0xFFFF0000 | VarDone;
        uint[] code =
        [
            11, hTarget, 11, hBuff, 11, hWas,            // 0: args[2] entity, [1] buffType, [0] wasRemoved (out)
            19, 0x6b696c02, 0xca687904,                  // 6: ECALL removeBuff
            27,                                          // 9: POP
            13, 24, hDone,                               // 10: Done = 1 (ICONST1, GSTORE)
            .. Enumerable.Repeat(28u, RemoveWaitSteps),  // 13: RVM, one a step: Kiln reads the answer and destroys the entity
            29, 30,                                      // DSELF, HALT: nobody came
        ];
        // keen types: BuffTypeReference 0x305916d9 (16 B), ecs EntityId 0x737c8f86 (4 B), bool 0xc894953d (1 B)
        (string Name, uint Type, int Size)[] vars = [("Buff::value", 0x305916d9, 16), ("Target::value", 0x737c8f86, 4),
            ("WasRemoved::value", 0xc894953d, 1), ("Done::value", 0xc894953d, 1)];
        return ProgramBlob.Build(RemoveBuffProgram, code, [30], vars);
    }

    /// <summary>16 bytes whose FNV-1a is <paramref name="id"/> (the engine only hashes a buff reference). Each byte step
    /// of FNV-1a can be undone, so the last four bytes are found by meeting in the middle.</summary>
    internal static Guid GuidWithId(uint id)
    {
        const uint Prime = 0x01000193, Inverse = 0x359c449b;
        for (byte salt = 0; salt < 255; salt++)
        {
            var bytes = new byte[16];
            for (var i = 0; i < 12; i++) bytes[i] = (byte)(0x4b + i * 7 + salt);
            var a = 0x811c9dc5u;
            for (var i = 0; i < 12; i++) a = (a ^ bytes[i]) * Prime;
            var forward = new Dictionary<uint, ushort>(65536);
            for (var x = 0; x < 65536; x++)
                forward[(((a ^ (uint)(x & 0xff)) * Prime) ^ (uint)(x >> 8)) * Prime] = (ushort)x;
            for (var y = 0; y < 65536; y++)
            {
                var back = ((id * Inverse) ^ (uint)(y >> 8)) * Inverse ^ (uint)(y & 0xff);
                if (!forward.TryGetValue(back, out var x)) continue;
                bytes[12] = (byte)x; bytes[13] = (byte)(x >> 8); bytes[14] = (byte)y; bytes[15] = (byte)(y >> 8);
                var guid = new Guid(bytes);
                if (IdOf(guid) == id) return guid;
            }
        }
        throw new InvalidOperationException($"no 16 bytes with the hash 0x{id:x8}");
    }
}

/// <summary>Writes keen::ImpactProgram blobs (docs/kiln/services/abilities.md, "contract").</summary>
internal static class ProgramBlob
{
    public static byte[] Build(Guid guid, uint[] code, uint[] shutdown, (string Name, uint Type, int Size)[] vars)
    {
        var names = vars.Select(v => Encoding.UTF8.GetBytes(v.Name)).ToArray();
        var dataSize = vars.Sum(v => v.Size);
        var codeOff = 64;
        var shutOff = codeOff + code.Length * 4;
        var layoutOff = shutOff + shutdown.Length * 4;
        var namesOff = layoutOff + vars.Length * 24;
        var dataOff = namesOff + names.Sum(n => n.Length);
        var b = new byte[(dataOff + dataSize + 3) & ~3];
        var g = guid.ToByteArray();
        BitConverter.GetBytes(AbilitiesNative.Fnv(g)).CopyTo(b, 0);
        g.CopyTo(b, 4);
        BitConverter.GetBytes((ushort)256).CopyTo(b, 0x14);
        void Arr(int field, int target, int count)
        {
            BitConverter.GetBytes(count == 0 ? 0 : target - field).CopyTo(b, field);
            BitConverter.GetBytes(count).CopyTo(b, field + 4);
        }
        Arr(0x18, codeOff, code.Length);
        Arr(0x20, shutOff, shutdown.Length);
        Arr(0x28, layoutOff, vars.Length);
        Arr(0x30, dataOff, dataSize);
        for (var i = 0; i < code.Length; i++) BitConverter.GetBytes(code[i]).CopyTo(b, codeOff + i * 4);
        for (var i = 0; i < shutdown.Length; i++) BitConverter.GetBytes(shutdown[i]).CopyTo(b, shutOff + i * 4);
        int nameAt = namesOff, dataAt = 0;
        for (var i = 0; i < vars.Length; i++)
        {
            var e = layoutOff + i * 24;
            BitConverter.GetBytes(AbilitiesNative.Fnv(names[i])).CopyTo(b, e);
            BitConverter.GetBytes(vars[i].Type).CopyTo(b, e + 8);
            BitConverter.GetBytes((ushort)vars[i].Size).CopyTo(b, e + 12);
            BitConverter.GetBytes((ushort)dataAt).CopyTo(b, e + 14);
            BitConverter.GetBytes(nameAt - (e + 16)).CopyTo(b, e + 16);
            BitConverter.GetBytes(names[i].Length).CopyTo(b, e + 20);
            names[i].CopyTo(b, nameAt);
            nameAt += names[i].Length;
            dataAt += vars[i].Size;
        }
        return b;
    }
}
