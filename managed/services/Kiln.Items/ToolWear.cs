// SPDX-License-Identifier: MIT
// Tool wear per game debit (kiln.toolwear, native/core/abi/services/kiln_toolwear.h; docs/kiln/services/items.md,
// "Tool wear"). The game takes 1 from a durable tool's durability in two places a rule reaches: when a hit debits
// the attacker's weapon, and after a projectile was spawned with the tool as the used item. A rule makes each such
// debit cost another number, for one item id. It is per debit of the game: an empty swing spends nothing, and two
// debits in one swing spend twice. The game's own skill roll, clamp, break notice, save and repair stay as they are.
// Only the authoritative server world of this process is acted on.
using System.Collections.Concurrent;

namespace Kiln;

/// <summary>Which of the game's debits changed a tool's durability.</summary>
public enum ToolWearSource
{
    /// <summary>A hit debited the attacker's weapon.</summary>
    WeaponHit = 1,
    /// <summary>A projectile was spawned with the tool as the used item.</summary>
    ProjectileSpawn = 2,
}

/// <summary>A mod's rule for what one debit of a tool costs. Dispose it to take the rule away: the rule before it (another
/// mod's) is in effect again, or the game's own debit of 1. It goes by itself when the mod unloads.</summary>
public sealed class ToolWearRule : IDisposable
{
    internal ToolWearRule(ItemId item, ulong handle, ModContext? mod, ulong owner, string modId)
    {
        Item = item;
        Handle = handle;
        Mod = mod;
        Owner = owner;
        ModId = modId;
    }

    /// <summary>The tool the rule is for.</summary>
    public ItemId Item { get; }
    internal ulong Handle { get; }
    internal ModContext? Mod { get; }
    internal ulong Owner { get; }
    internal string ModId { get; }
    internal bool Gone { get; set; }
    internal Action? Detach { get; set; }

    /// <summary>Takes the rule away. The mod that set it does this, on the tick thread; another mod's call throws.</summary>
    /// <exception cref="InvalidOperationException">Another mod set this rule, or the call is not on the tick thread.</exception>
    [ServerOnly, TickThreadOnly]
    public void Dispose() => Items.ToolWear.Remove(this, fromOwner: true);
}

/// <summary>What <see cref="Items.SetToolWearPerDebit"/> answers.</summary>
public sealed record ToolWearResult : IKilnResult
{
    /// <summary>The rule is set.</summary>
    public bool Ok { get; init; }
    /// <summary>Empty when set; else why not: <c>UnsupportedTool: …</c> (a torch, a build tool, another kind of item), <c>NoAuthoritativeWorld</c>
    /// (no server world in this game now), <c>UnknownItem</c>, <c>PendingContent</c> (the game's items are not read yet), <c>TooManyRules</c>.</summary>
    public string Reason { get; init; } = "";
    /// <summary>The rule, to dispose. Null when not set.</summary>
    public ToolWearRule? Rule { get; init; }
}

/// <summary>What <see cref="Items.ToolWearStatus"/> answers: a copy that does not change.</summary>
public sealed record ToolWearState : IKilnResult
{
    /// <summary>A rule can be set for the tool here.</summary>
    public bool Ok { get; init; }
    /// <summary>Empty when <see cref="Ok"/>; else why not (as <see cref="ToolWearResult.Reason"/>).</summary>
    public string Reason { get; init; } = "";
    /// <summary>What one debit costs under the rule in effect. Null: no rule, the game takes 1.</summary>
    public int? PerDebit { get; init; }
    /// <summary>The mod whose rule is in effect. Null without one.</summary>
    public string? OwnerModId { get; init; }
    /// <summary>The game's items are not read yet: ask again.</summary>
    public bool Pending { get; init; }
    /// <summary>Grows by one with every change of any tool's rule in effect.</summary>
    public ulong Generation { get; init; }
    /// <summary>Durability changes that were not reported because too many came at once, since the game started. The changes themselves happened.</summary>
    public ulong DroppedEvents { get; init; }
}

/// <summary>The rule in effect for a tool changed: set, replaced, taken away, or another mod's rule revealed. Raised on the server tick.</summary>
[ServerOnly]
public sealed class ToolWearRuleChangedEvent : KilnEvent
{
    internal ToolWearRuleChangedEvent(ItemId item, int? before, int? after, string? beforeModId, string? afterModId, ulong generation)
    {
        Item = item;
        Before = before;
        After = after;
        BeforeModId = beforeModId;
        AfterModId = afterModId;
        Generation = generation;
    }

    /// <summary>The tool.</summary>
    public ItemId Item { get; }
    /// <summary>What a debit cost before. Null: the game's 1.</summary>
    public int? Before { get; }
    /// <summary>What it costs now. Null: the game's 1.</summary>
    public int? After { get; }
    /// <summary>The mod whose rule was in effect.</summary>
    public string? BeforeModId { get; }
    /// <summary>The mod whose rule is in effect now.</summary>
    public string? AfterModId { get; }
    /// <summary><see cref="ToolWearState.Generation"/> after the change.</summary>
    public ulong Generation { get; }
}

/// <summary>A tool with a rule lost durability to one of the game's debits. Raised on the server tick after the step it happened in, once per
/// debit that changed the value: a debit the game's skill saved, or a cost of 0, raises none.</summary>
[ServerOnly]
public sealed class ToolDurabilityChangedEvent : KilnEvent
{
    internal ToolDurabilityChangedEvent(ItemInstanceRef item, EntityId actor, ToolWearSource source, ulong step, int before, int after, int maximum,
        int originalDelta, int requestedDelta)
    {
        Item = item;
        Actor = actor;
        Source = source;
        Step = step;
        Before = before;
        After = after;
        Maximum = maximum;
        OriginalDelta = originalDelta;
        RequestedDelta = requestedDelta;
    }

    /// <summary>The tool: the item entity as it was when the debit happened.</summary>
    public ItemInstanceRef Item { get; }
    /// <summary>Whose action it was.</summary>
    public EntityId Actor { get; }
    /// <summary>Which debit.</summary>
    public ToolWearSource Source { get; }
    /// <summary>The server step of the debit.</summary>
    public ulong Step { get; }
    /// <summary>The durability before.</summary>
    public int Before { get; }
    /// <summary>The durability after.</summary>
    public int After { get; }
    /// <summary>The tool's full durability.</summary>
    public int Maximum { get; }
    /// <summary>What the game asked for: -1.</summary>
    public int OriginalDelta { get; }
    /// <summary>What the rule asked for: minus its cost. <see cref="After"/> minus <see cref="Before"/> can be less (the game stops at 0).</summary>
    public int RequestedDelta { get; }
}

public static unsafe partial class Items
{
    /// <summary>Sets what each of the game's debits costs <paramref name="tool"/>: a hit that debits the weapon, a projectile spawned with it.
    /// 1 is the game's own; 0 makes the tool not wear; at most 32767. Per debit, not per swing: no debit, nothing spent; two debits, twice.
    /// The rule is the calling mod's: setting it again replaces it, and across mods the latest set is in effect. Only tools with an item of their own
    /// and durability take a rule (axes, pickaxes): a torch, a build tool or another kind of item answers <c>Ok = false</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="tool"/> is none, or <paramref name="perDebit"/> is outside 0..32767.</exception>
    /// <exception cref="KilnUnavailableException"><c>items.tool_wear_debit</c> is off here.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability("items.tool_wear_debit")]
    public static ToolWearResult SetToolWearPerDebit(ItemId tool, int perDebit)
    {
        Services.RequireTickThread("Items.SetToolWearPerDebit");
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        return ToolWear.Set(tool, perDebit, mod, mod?.Owner ?? 0, mod?.Info.Id ?? "kiln");
    }

    /// <summary>What one debit of <paramref name="tool"/> costs now, whose rule that is, and whether a rule can be set here. Any thread.</summary>
    /// <exception cref="ArgumentException"><paramref name="tool"/> is none.</exception>
    /// <exception cref="KilnUnavailableException"><c>items.tool_wear_debit</c> is off here.</exception>
    [ServerOnly, AnyThread, RequiresCapability("items.tool_wear_debit")]
    public static ToolWearState ToolWearStatus(ItemId tool)
    {
        if (tool.IsNone) throw new ArgumentException("no item", nameof(tool));
        return ToolWear.Read(ToolWear.Require("Items.ToolWearStatus"), tool);
    }

    /// <summary>What the managed side asks of kiln.toolwear: the native table on a game, a made-up one in tests.</summary>
    internal interface IToolWearTable
    {
        int Define(ulong owner, string modId, uint item, uint perDebit, out ulong handle);
        int Remove(ulong owner, ulong handle);
        /// <summary>Drains change records into <paramref name="into"/>; how many.</summary>
        int Changes(Span<ToolWearNative.Change> into);
        bool Status(uint item, out ToolWearNative.State state);
    }

    internal static class ToolWear
    {
        internal const string Capability = "items.tool_wear_debit";
        internal const int MaxCost = 32767;

        /// <summary>The table (tests set it). Null: kiln.toolwear is not here.</summary>
        internal static IToolWearTable? Table;
        private static bool _looked;
        /// <summary>Whether the capability row is on (tests set it).</summary>
        internal static Func<bool> Available = () => Capabilities.Is(Capability);
        /// <summary>Where the events go (tests set it).</summary>
        internal static Action<KilnEvent> Sink = ev => Events.Enqueue(ev);

        private static readonly ConcurrentDictionary<(ulong Owner, uint Item), ToolWearRule> Rules = new();
        private static IDisposable? _tick;

        internal static IToolWearTable? Current
        {
            get
            {
                if (Table != null || _looked) return Table;
                _looked = true;
                var t = Services.Get<ToolWearNative.Table>(ToolWearNative.Service, ToolWearNative.Version);
                if (t != null && t->Header.Size >= (uint)sizeof(ToolWearNative.Table)) Table = new NativeTable(t);
                return Table;
            }
        }

        /// <summary>For tests: forget the table and every rule.</summary>
        internal static void Reset()
        {
            Table = null;
            _looked = false;
            Rules.Clear();
            _tick?.Dispose();
            _tick = null;
        }

        internal static IToolWearTable Require(string api)
        {
            if (!Available() || Current is not { } t) throw ServiceCaps.Unavailable(Capability, $"{api}: tool wear per debit is not available here");
            return t;
        }

        internal static ToolWearState Read(IToolWearTable t, ItemId tool)
        {
            if (!t.Status(tool.Value, out var s)) return new ToolWearState { Reason = "kiln.toolwear did not answer" };
            return new ToolWearState
            {
                Ok = s.Ok != 0, Reason = s.Ok != 0 ? "" : s.ReasonText, Pending = s.Pending != 0,
                PerDebit = s.HasRule != 0 ? (int)s.PerDebit : null, OwnerModId = s.HasRule != 0 ? s.OwnerText : null,
                Generation = s.Generation, DroppedEvents = s.DroppedEvents,
            };
        }

        private static void Changed(ItemId tool, ToolWearState before, ToolWearState after)
        {
            if (before.PerDebit == after.PerDebit && before.OwnerModId == after.OwnerModId) return;
            Sink(new ToolWearRuleChangedEvent(tool, before.PerDebit, after.PerDebit, before.OwnerModId, after.OwnerModId, after.Generation));
        }

        /// <summary>Sets a rule as <paramref name="mod"/> (null: Kiln's own, for a data-only mod's block), reported under <paramref name="modId"/>.</summary>
        internal static ToolWearResult Set(ItemId tool, int perDebit, ModContext? mod, ulong owner, string modId)
        {
            if (tool.IsNone) throw new ArgumentException("no item", nameof(tool));
            if (perDebit is < 0 or > MaxCost) throw new ArgumentOutOfRangeException(nameof(perDebit), perDebit, $"a debit costs 0 to {MaxCost}");
            var t = Require("Items.SetToolWearPerDebit");
            var before = Read(t, tool);
            var rc = t.Define(owner, modId, tool.Value, (uint)perDebit, out var handle);
            switch (rc)
            {
                case ToolWearNative.Ok:
                    break;
                case ToolWearNative.UnsupportedTool:
                    return new ToolWearResult { Reason = before.Reason.Length > 0 ? before.Reason : "UnsupportedTool" };
                case ToolWearNative.NoAuthoritativeWorld:
                    return new ToolWearResult { Reason = "NoAuthoritativeWorld" };
                case ToolWearNative.NotFound:
                    return new ToolWearResult { Reason = "UnknownItem" };
                case ToolWearNative.Pending:
                    return new ToolWearResult { Reason = "PendingContent" };
                case ToolWearNative.TooManyRules:
                    return new ToolWearResult { Reason = "TooManyRules" };
                case ToolWearNative.Unavailable:
                    throw ServiceCaps.Unavailable(Capability, "Items.SetToolWearPerDebit: kiln.toolwear turned it off (see kiln.log)");
                case ToolWearNative.WrongThread:
                    throw new InvalidOperationException("Items.SetToolWearPerDebit must be called on the tick thread");
                default:
                    throw new ArgumentException($"kiln.toolwear refused the rule ({rc})");
            }
            // the same mod and tool again: the same rule object, as the native handle is the same
            var rule = Rules.AddOrUpdate((owner, tool.Value), _ => Track(new ToolWearRule(tool, handle, mod, owner, modId)),
                (_, old) => old.Handle == handle && !old.Gone ? old : Track(new ToolWearRule(tool, handle, mod, owner, modId)));
            Start();
            Changed(tool, before, Read(t, tool));
            return new ToolWearResult { Ok = true, Rule = rule };
        }

        private static ToolWearRule Track(ToolWearRule rule)
        {
            if (rule.Mod is { } mod)
            {
                Action retire = () => Remove(rule, fromOwner: false);
                mod.Retiring += retire;
                rule.Detach = () => mod.Retiring -= retire;
            }
            return rule;
        }

        /// <summary>Takes a rule away. <paramref name="fromOwner"/>: the public Dispose, which only the mod that set it may call, on the tick
        /// thread; false: the host retiring that mod.</summary>
        internal static void Remove(ToolWearRule rule, bool fromOwner)
        {
            if (fromOwner)
            {
                Services.RequireTickThread("ToolWearRule.Dispose");
                if (!ReferenceEquals(ModContext.CurrentMod, rule.Mod))
                    throw new InvalidOperationException($"the tool wear rule for {rule.Item} is {rule.ModId}'s: the mod that set it takes it away");
            }
            if (rule.Gone) return;
            rule.Gone = true;
            rule.Detach?.Invoke();
            Rules.TryRemove(new KeyValuePair<(ulong, uint), ToolWearRule>((rule.Owner, rule.Item.Value), rule));
            if (Current is not { } t) return;
            var before = Read(t, rule.Item);
            t.Remove(rule.Owner, rule.Handle);   // not found: the native side took it when the owner went
            Changed(rule.Item, before, Read(t, rule.Item));
        }

        /// <summary>How the drain gets its tick (tests set it).</summary>
        internal static Func<string, Action<ulong>, IDisposable?> AddTick = (name, step) => ServiceTickAccess.Add(name, step);

        private static void Start() => _tick ??= AddTick("kiln.items tool wear", Drain);

        /// <summary>The service tick: every durability change the hooks queued becomes an event. The instance ref is the item entity and stamp
        /// the hook captured at the debit; nothing is looked up now.</summary>
        internal static void Drain(ulong step)
        {
            if (Current is not { } t) return;
            Span<ToolWearNative.Change> batch = stackalloc ToolWearNative.Change[64];
            for (var rounds = 0; rounds < 64; rounds++)
            {
                var n = t.Changes(batch);
                for (var i = 0; i < n; i++)
                {
                    ref readonly var c = ref batch[i];
                    if (c.Before == c.After) continue;
                    Sink(new ToolDurabilityChangedEvent(ItemRefs.Of(c.ItemPide, c.ItemId, c.InstanceStamp), new EntityId(c.Actor), (ToolWearSource)c.Source,
                        c.Step, c.Before, c.After, c.Maximum, c.OriginalDelta, c.RequestedDelta));
                }
                if (n < batch.Length) break;
            }
        }

        private sealed class NativeTable(ToolWearNative.Table* table) : IToolWearTable
        {
            public int Define(ulong owner, string modId, uint item, uint perDebit, out ulong handle)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(modId);
                ulong h = 0;
                int rc;
                fixed (byte* p = bytes) rc = table->Define(owner, new NativeStr { Ptr = p, Len = (nuint)bytes.Length }, item, perDebit, &h);
                handle = h;
                return rc;
            }

            public int Remove(ulong owner, ulong handle) => table->Remove(owner, handle);

            public int Changes(Span<ToolWearNative.Change> into)
            {
                uint n = 0;
                fixed (ToolWearNative.Change* p = into)
                    return table->Changes(p, (uint)into.Length, (uint)sizeof(ToolWearNative.Change), &n) == ToolWearNative.Ok ? (int)n : 0;
            }

            public bool Status(uint item, out ToolWearNative.State state)
            {
                var s = new ToolWearNative.State { Size = (uint)sizeof(ToolWearNative.State) };
                var ok = table->Status(item, &s) == ToolWearNative.Ok;
                state = s;
                return ok;
            }
        }
    }
}

/// <summary>kiln.toolwear's table and records (native/core/abi/services/kiln_toolwear.h).</summary>
internal static unsafe class ToolWearNative
{
    public const string Service = "kiln.toolwear";
    public const uint Version = 1;
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4;
    public const int UnsupportedTool = 100, NoAuthoritativeWorld = 101, TooManyRules = 102, NotOwner = 103, Pending = 104;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct Change  // 88 bytes
    {
        public uint Size, Source;
        public ulong WorldSession, Step;
        public uint Actor, ItemPide, ItemId, InstanceStamp;
        public int Before, After, Maximum, OriginalDelta, RequestedDelta;
        public uint SequenceId, CallIndex;
        public long TriggerTime;
        public ushort TriggerType, Reserved;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct State  // 360 bytes
    {
        public uint Size, Ok, Pending, HasRule, PerDebit;
        public ulong Generation, DroppedEvents;
        public fixed byte OwnerModId[128];
        public fixed byte Reason[192];

        public readonly string OwnerText
        {
            get { fixed (byte* p = OwnerModId) return Text(p, 128); }
        }

        public readonly string ReasonText
        {
            get { fixed (byte* p = Reason) return Text(p, 192); }
        }

        private static string Text(byte* p, int cap)
        {
            var n = 0;
            while (n < cap && p[n] != 0) n++;
            return System.Text.Encoding.UTF8.GetString(p, n);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, NativeStr, uint, uint, ulong*, int> Define;
        public delegate* unmanaged[Cdecl]<ulong, ulong, int> Remove;
        public delegate* unmanaged[Cdecl]<Change*, uint, uint, uint*, int> Changes;
        public delegate* unmanaged[Cdecl]<uint, State*, int> Status;
    }
}
