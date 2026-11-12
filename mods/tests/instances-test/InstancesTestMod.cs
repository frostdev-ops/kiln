// SPDX-License-Identifier: MIT
using System.Reflection;
using Kiln;

namespace InstancesTest;

/// <summary>A check of this lab test: a method of <see cref="InstancesTestMod"/> (any partial file) returning
/// <see cref="Task"/>, run in <see cref="Order"/> once the world runs and kiln.items is ready.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LabCheckAttribute(int order, string name) : Attribute
{
    public int Order { get; } = order;
    public string Name { get; } = name;

    /// <summary>0: every boot; 1: the first boot only; 2: only after a restart (the first boot left
    /// <c>boot1.done</c> in the mod's data folder).</summary>
    public int Boot { get; init; }
}

/// <summary>A check that runs in <see cref="Mod.OnLoad"/> (before any tick): a void method of
/// <see cref="InstancesTestMod"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LabOnLoadAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Lab test of the package <c>instances</c>, no player connected. Each check logs PASS or FAIL lines into kiln.log and a
/// summary line "instances-test done: N PASS, M FAIL". The checks are the <see cref="LabCheckAttribute"/> methods of
/// this partial class: one file per task (IdentityChecks.cs, TrackingChecks.cs, ...), so a task adds its checks without
/// editing another's file. A file <c>only</c> in the mod's data folder (check names, one per line) runs just those.
/// </summary>
public sealed partial class InstancesTestMod : Mod
{
    private int _pass, _fail;
    private bool _stop;

    /// <summary>True after a restart: the first boot wrote <c>boot1.done</c>.</summary>
    internal bool SecondBoot => File.Exists(Path.Combine(Context.DataDirectory, "boot1.done"));

    public override void OnLoad()
    {
        OnLoadChecks();
        _ = RunAsync();
    }

    public override void OnUnload() => _stop = true;

    private void OnLoadChecks()
    {
        foreach (var m in GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (m.GetCustomAttribute<LabOnLoadAttribute>() is not { } a) continue;
            try
            {
                m.Invoke(this, null);
            }
            catch (Exception e)
            {
                Check(false, $"{a.Name} threw in OnLoad: {(e is TargetInvocationException { InnerException: { } i } ? i : e)}");
            }
        }
    }

    internal void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    internal void Info(string what) => Log.Info($"INFO {what}");

    internal async Task Steps(int n)
    {
        for (var i = 0; i < n && !_stop; i++) await Context.Scheduler.NextTick();
    }

    /// <summary>Waits until <paramref name="ready"/> or <paramref name="maxSteps"/>; true when ready.</summary>
    internal async Task<bool> Until(Func<bool> ready, int maxSteps)
    {
        for (var i = 0; i < maxSteps && !_stop; i++)
        {
            if (ready()) return true;
            await Steps(1);
        }
        return ready();
    }

    private async Task RunAsync()
    {
        try
        {
            await Run();
        }
        catch (Exception e)
        {
            Check(false, $"test aborted: {e}");
        }
        Log.Info($"instances-test done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Run()
    {
        var ready = await Until(() => Spawn.WorldRunning && Items.Status() is { Transactions: true, Catalog: true }, 25 * 240);
        var st = Items.Status();
        Check(ready, $"world running and kiln.items ready: {(st == null ? "kiln.items not loaded" : $"program {st.Program}, {st.Items} items, '{st.Detail}'")}");
        if (!ready) return;
        await Steps(50);  // let the world settle (stations, villagers) before choosing containers
        var only = Path.Combine(Context.DataDirectory, "only");
        var filter = File.Exists(only)
            ? File.ReadAllLines(only).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet()
            : null;
        var checks = GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<LabCheckAttribute>()))
            .Where(x => x.Attr != null)
            .OrderBy(x => x.Attr!.Order)
            .ToList();
        var boot = SecondBoot ? 2 : 1;
        foreach (var (m, a) in checks)
        {
            if (_stop) return;
            if (a!.Boot != 0 && a.Boot != boot) continue;
            if (filter != null && !filter.Contains(a.Name)) continue;
            Log.Info($"---- {a.Name} (boot {boot})");
            try
            {
                if (m.Invoke(this, null) is Task t) await t;
            }
            catch (Exception e)
            {
                Check(false, $"{a.Name} threw: {(e is TargetInvocationException { InnerException: { } i } ? i : e)}");
            }
        }
    }

    // ---- shared helpers for the checks ----

    /// <summary>Containers of the world with at least <paramref name="free"/> free Generic slots whose content does not
    /// change over 50 steps (not a station or a villager's stock in use).</summary>
    internal async Task<List<EntityId>> QuietContainers(int free, int max)
    {
        var candidates = new List<(EntityId E, string Snap)>();
        foreach (var e in Items.FindInventories(InventoryKinds.Containers))
            if (Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= free)
                candidates.Add((e, Snap(c)));
        await Steps(50);
        var quiet = new List<EntityId>();
        foreach (var (e, snap) in candidates)
        {
            if (Items.Read(e) is { } c && Snap(c) == snap) quiet.Add(e);
            if (quiet.Count >= max) break;
        }
        return quiet;
    }

    internal static string Snap(InventoryContents c) =>
        string.Join(" ", c.Slots.Select(s => s.IsEmpty ? "-" : $"{s.Item.Value:x}x{s.Count}#{s.ItemEntity}"));

    /// <summary>A gear weapon (every copy has its own item entity).</summary>
    internal static ItemInfo? Sword() =>
        Items.Lookup("Weapon_T1_1H_SwordEpic_01") ??
        Items.All.FirstOrDefault(i => i.IsGear && i.Category == ItemCategory.Weapons && i.LevelMin > 0);

    /// <summary>The first slot of <paramref name="holder"/>'s inventories holding item entity <paramref name="pide"/>.</summary>
    internal static InventorySlot? SlotOf(EntityId holder, EntityId pide) =>
        Items.Read(holder)?.Slots.FirstOrDefault(s => !s.IsEmpty && s.ItemEntity == pide);

    /// <summary>The place of a serial (bits 60-63): 1 Players, 2 Items, 3 Spawn, 0 none.</summary>
    internal static int Place(EntityRef r) => (int)(r.Serial >> 60);
}
