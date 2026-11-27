// SPDX-License-Identifier: MIT
#pragma warning disable KILNX031
using Kiln;
using Kiln.Registry;
using DataRegistry = Kiln.Registry.Registries;

namespace RegistryManagedTest;

[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.ModLoad | RegistryEntryPoint.WorldLoad | RegistryEntryPoint.HotReload)]
public sealed class RegistryManagedTestMod : Mod
{
    private const string Wood = "game:material_t1_wood_logs";
    private bool _ran, _worldRan;
    private int _pass, _fail, _hotReload, _ready;
    private RegistryContext? _retained;
    public sealed class Settings { public int Stack { get; set; } = 271; }
    public sealed class WorldSettings { public int Extra { get; set; } = 2; }
    private bool Drop => File.Exists(Path.Combine(Context.DataDirectory, "drop"));
    private void Check(bool ok, string message)
    {
        if (ok) ++_pass; else ++_fail;
        Log.Info($"registry-managed-test: {(ok ? "PASS" : "FAIL")} {message}");
    }
    public override void OnRegistry(RegistryContext context)
    {
        _retained = context;
        Log.Info($"registry-managed-test: POINT {context.EntryPoint} {context.Phase}");
        var registry = DataRegistry.For(context);
        if (context.EntryPoint is RegistryEntryPoint.GameStart or RegistryEntryPoint.ModLoad)
        {
            try { _ = WorldConfig<WorldSettings>().Value; throw new InvalidOperationException("early world settings succeeded"); }
            catch (RegistryContextException e) when (e.Code == RegistryContextError.WorldSettingsUnavailable) { }
        }
        if (context.EntryPoint == RegistryEntryPoint.GameStart)
        {
            var before = context.Identity;
            var stack = Config.Register<Settings>().Value.Stack;
            if (before == context.Identity) throw new InvalidOperationException("settings missing from identity");
            registry.Alter(view => view.Items[Wood].MaxStack = stack);
        }
        else if (context.EntryPoint == RegistryEntryPoint.ModLoad)
        {
            if (!Drop) registry.Alter(view => view.Items[Wood].MaxStack = 272);
        }
        else if (context.EntryPoint == RegistryEntryPoint.WorldLoad)
        {
            if (context.World is not { Token: not 0 } world) throw new InvalidOperationException("native world identity missing");
            if (world.Id is null)
            {
                if (!Throws(RegistryContextError.WorldSettingsUnavailable, () => _ = WorldConfig<WorldSettings>().Value))
                    throw new InvalidOperationException("id-less world settings succeeded");
                Log.Info("registry-managed-test: id-less world accepted without save settings");
                return; // adoption reauthors this point before the settings-dependent assertions below
            }
            var extra = WorldConfig<WorldSettings>().Value.Extra;
            if (!Drop)
            {
                registry.Alter(view => view.Items[Wood].MaxStack = 271 + extra);
                registry.Final(view => view.Items[Wood].MaxStack = 272 + extra);
            }
            if (context.Phase == RegistryPhase.Final) _worldRan = true;
        }
        else if (context.EntryPoint == RegistryEntryPoint.HotReload)
        {
            ++_hotReload;
            // review4 NS5: a runtime pass reads the pre-pass values without the mod's own layers, its GameStart edit included
            if (Drop && registry.Items[Wood].MaxStack == 271) throw new InvalidOperationException("replacement pass read its own GameStart layer");
        }
    }
    public override void OnLoad()
    {
        DataRegistry.Ready += _ => ++_ready;
        Context.Tick.Pre += (in TickInfo _) =>
        {
            if (_ran || !_worldRan) return;
            _ran = true;
            try { Run(); }
            catch (Exception e) { Check(false, e.ToString()); }
            finally { Log.Info($"registry-managed-test: {(Drop ? "RELOADED" : "DONE")} pass={_pass} fail={_fail}"); }
        };
    }
    private static int Stack()
    {
        var stack = 0; DataRegistry.Read(view => stack = view.Items[Wood].MaxStack); return stack;
    }
    private void Run()
    {
        Check(DataRegistry.Available, "stage and live transports available");
        Check(_ready > 0, "owner-bound copied ready notification delivered");
        Check(_retained is not null && Throws(RegistryContextError.Closed, () => DataRegistry.For(_retained)), "retained automatic context expired");
        if (Drop)
        {
            Check(_hotReload == 3, "replacement ran all three HotReload passes");
            Check(Stack() == 271, "dropping all runtime edits restores retained GameStart base");
            return;
        }
        Check(Stack() == 274, "WorldLoad Alter and Final mapped writes completed");
        ItemEntry? retained = null;
        var change = DataRegistry.Change(view => { retained = view.Items[Wood]; retained.MaxStack = 281; });
        Check(change.Result.Ok && change.Result.Accepted == 1, "manual change returns Applied with one accepted edit");
        Check(Stack() == 281, "resolved typed mapped read sees manual bytes");
        Check(Throws(RegistryContextError.Closed, () => _ = retained!.MaxStack), "retained manual cell expired");
        var reverted = change.Revert();
        Check(reverted.Ok && Stack() == 274, "revert recomposes prior automatic layers");
        var restart = DataRegistry.Change(view => view.Balance.LevelCap = 60);
        Check(restart.Result.Outcome == RegistryChangeOutcome.RestartRequired && !restart.Result.Ok && restart.Result.Reason is not null,
            "later restart-only write has a typed refusal and reason");
        try { DataRegistry.Change(view => { view.Items[Wood].MaxStack = 999; throw new InvalidOperationException("expected"); }); }
        catch (InvalidOperationException e) when (e.Message == "expected") { }
        Check(Stack() == 274, "throwing manual turn submits no buffered edits");
        DataRegistry.Read(view => Check(Throws(RegistryContextError.ReadOnly, () => view.Items[Wood].MaxStack = 99), "resolved typed view refuses writes"));
    }
    private static bool Throws(RegistryContextError error, Action action)
    {
        try { action(); return false; } catch (RegistryContextException e) { return e.Code == error; }
    }
}
