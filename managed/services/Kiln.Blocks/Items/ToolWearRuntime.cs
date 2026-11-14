// SPDX-License-Identifier: MIT
// ToolBlock.WearPerDebit while the game runs: what a block asks for is an intent, set as a rule of kiln.toolwear
// (Kiln.Items) where an authoritative server world runs, and nowhere else.
//  - A dedicated server and a hosted game set it, on the tick, once the item and a server world are there; until then
//    it is tried again (every 25 steps), also after a host starts a world later.
//  - A game that only joined a server sets nothing and reports nothing: it has no world of its own to rule.
//  - What the game cannot do (the item is not a tool with an item entity and durability) and a capability that is off
//    on a server are the block's error, once, by name.
// The rule is the mod's (its owner): it goes when the mod unloads or reloads.
namespace Kiln;

internal static class ToolWearRuntime
{
    internal const int MaxCost = 32767;
    private const int UnknownTries = 40;   // about 1000 steps
    private const string Capability = "items.tool_wear_debit";

    private sealed class Intent(ModBlocks mod, string block, uint item, int cost)
    {
        public readonly ModBlocks Mod = mod;
        public readonly string Block = block;
        public readonly uint Item = item;
        public readonly int Cost = cost;
        public ToolWearRule? Rule;
        public bool Done;
        public int Tries;
        public bool Said;
    }

    private static readonly object Gate = new();
    private static readonly List<Intent> Intents = new();
    private static IDisposable? _tick;

    /// <summary>A server world may run in this process: a dedicated server, or a game that hosts (tests set it).</summary>
    internal static Func<bool> ServerHere = () => !BlocksBridge.IsClient || Client.IsHosting;
    /// <summary>The capability is held by the client gate here (a hosted game before its game test): the intent waits, it is no error (tests set it).</summary>
    internal static Func<bool> Held = () => Capabilities.IsHeld(Capability);
    /// <summary>Sets the rule as the mod's (tests set it).</summary>
    internal static Func<ModBlocks, uint, int, ToolWearResult> Set = (mod, item, cost) => Items.ToolWear.Set(new ItemId(item), cost, null, mod.Owner, mod.Id);
    internal static Action<ToolWearRule> Remove = rule => Items.ToolWear.Remove(rule, fromOwner: false);
    /// <summary>How the retry gets its tick (tests set it and call <see cref="Step"/> themselves).</summary>
    internal static Func<Action<ulong>, IDisposable?> AddTick = step => BlocksBridge.AddTick("kiln blocks tool wear", step);

    public static void Add(ModBlocks mod, string block, uint item, int cost)
    {
        lock (Gate)
        {
            Intents.Add(new Intent(mod, block, item, cost));
            _tick ??= AddTick(Step);
        }
    }

    /// <summary>The mod reloads or is gone: its rules go, its intents are forgotten.</summary>
    public static void Forget(ModBlocks mod)
    {
        List<Intent> gone;
        lock (Gate)
        {
            gone = Intents.Where(i => ReferenceEquals(i.Mod, mod)).ToList();
            Intents.RemoveAll(i => ReferenceEquals(i.Mod, mod));
        }
        foreach (var i in gone)
            if (i.Rule is { } rule)
            {
                try { Remove(rule); }
                catch (Exception e) when (e is InvalidOperationException or KilnUnavailableException) { }   // the service went first: its owner's rules went with it
            }
    }

    /// <summary>For tests: forget everything.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Intents.Clear();
            _tick?.Dispose();
            _tick = null;
        }
    }

    internal static int Waiting
    {
        get { lock (Gate) return Intents.Count(i => !i.Done); }
    }

    internal static void Step(ulong step)
    {
        if (step % 25 != 1) return;
        List<Intent> todo;
        lock (Gate) todo = Intents.Where(i => !i.Done).ToList();
        if (todo.Count == 0 || !ServerHere()) return;      // a joined game: nothing to rule, nothing to say
        if (Held())
        {
            // switched off until its game test: said once per block, as information; the intent waits ("preview" lifts the gate)
            foreach (var i in todo.Where(i => !i.Said))
            {
                i.Said = true;
                BlocksBridge.Log(LogLevel.Info, $"blocks: {i.Mod.Id}: block {i.Block}: tool.wearPerDebit waits: {Capability} is switched off here until its game test");
            }
            return;
        }
        foreach (var i in todo)
        {
            ToolWearResult r;
            try { r = Set(i.Mod, i.Item, i.Cost); }
            catch (KilnUnavailableException e)
            {
                // off on a game that could run a server world: the author asked for something this server does not do
                Problem(i, e.Message);
                continue;
            }
            if (r.Ok)
            {
                i.Rule = r.Rule;
                i.Done = true;
                BlocksBridge.Log(LogLevel.Info, $"blocks: {i.Mod.Id}: block {i.Block}: each wear debit costs {i.Cost}");
            }
            else if (r.Reason.StartsWith("UnsupportedTool", StringComparison.Ordinal)) Problem(i, r.Reason);
            else if (r.Reason == "UnknownItem" && ++i.Tries == UnknownTries)
            {
                // the block's own item should be in the game within moments of its commit: say so once, and keep trying
                var text = $"block {i.Block}, tool.wearPerDebit: the game still has no item for this block after {UnknownTries} tries; the rule is set when it has";
                lock (i.Mod.Warnings) i.Mod.Warnings.Add(text);
                BlocksBridge.Log(LogLevel.Warn, $"blocks: {i.Mod.Id}: {text}");
            }
            else if (r.Reason is not ("NoAuthoritativeWorld" or "PendingContent" or "UnknownItem")) Problem(i, r.Reason);
            // NoAuthoritativeWorld (a host with no world yet), PendingContent, UnknownItem (the item is registered a little later): tried again
        }
    }

    private static void Problem(Intent i, string why)
    {
        i.Done = true;
        var text = $"block {i.Block}, tool.wearPerDebit: {why}";
        lock (i.Mod.Errors) i.Mod.Errors.Add(text);
        i.Mod.ReportDirty = true;
        BlocksBridge.Log(LogLevel.Error, $"blocks: {i.Mod.Id}: {text}");
    }
}
