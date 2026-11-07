// SPDX-License-Identifier: MIT
// Lab test: GameTypes, Reflected (checked writes), GameSystems (switch, profile), GameEvents (post, read back).
using Kiln;

namespace TypesTest;

public sealed class TypesTestMod : Mod
{
    private ulong _start;
    private int _posted = -1;

    private void Check(string name, bool ok, object? detail = null) =>
        Log.Info($"{(ok ? "PASS" : "FAIL")} {name}{(detail is null ? "" : ": " + detail)}");

    public override void OnLoad()
    {
        KilnDevTools.Start();
        Context.Tick.Post += OnPost;
    }

    private void OnPost(in TickInfo tick)
    {
        // Start once the world has loaded its entities (a fresh server steps for a while before it does).
        if (_start == 0)
        {
            if (tick.Step % 25 != 0 || Reflected.All("Health").Count < 10) return;
            _start = tick.Step - 39;
        }
        var n = tick.Step - _start;
        try
        {
            if (n == 40) Basics();
            if (n == 41) _posted = GameEvents.Post("TestEvent1", new Dictionary<string, object?> { ["data"] = 7 }) ? 1 : 0;
            if (n == 42)
            {
                var seen = 0;
                GameEvents.Each("TestEvent1", e => { if (Convert.ToInt64(e["data"]) == 7) seen++; });
                Check("GameEvents.Post then Each", _posted == 1 && seen == 1, $"posted {_posted}, seen {seen}");
            }
            if (n == 160)
            {
                var top = GameSystems.Stats();
                Check("GameSystems.Stats", top.Count > 0 && GameSystems.ProfileSeconds > 0, $"{top.Count} systems, top {top.FirstOrDefault()?.Name}");
                GameSystems.Profile(false);
                Log.Info("types-test: done");
            }
        }
        catch (Exception e)
        {
            Check($"step {n}", false, e.Message);
        }
    }

    private void Basics()
    {
        var health = GameTypes.Find("Health");
        Check("GameTypes.Find Health", health is { Kind: GameTypeKind.Struct }, $"{health?.QualifiedName}, {GameTypes.Count} types");
        Check("base class", health?.Base?.QualifiedName == "keen::ecs::SInt32Attribute", health?.Base);
        Check("enum values", GameTypes.Find("ItemRarity")?.EnumValues.Any(v => v.Name == "Legendary") == true);
        Check("components list", GameTypes.Components.Count > 600, GameTypes.Components.Count);

        var ids = Reflected.All("Health");
        var id = ids.FirstOrDefault(e => Reflected.Component(e, "Health")?.Get<long>("dataStorage[2]") > 1);
        Check("Reflected.All Health", ids.Count > 0, ids.Count);
        var h = Reflected.Component(id, "Health");
        if (h != null)
        {
            var max = h.Get<long>("dataStorage[2]");
            var before = h.Get<long>("dataStorage[0]");
            h["dataStorage[0]"] = max - 1;
            var hurt = Reflected.Component(id, "Health")!.Get<long>("dataStorage[0]");
            h["dataStorage[0]"] = Math.Max(before, max);
            Check("Reflected write reads back", hurt == max - 1 && h.Get<long>("dataStorage[0]") == Math.Max(before, max), $"entity {id}: {before} -> {hurt} -> {h["dataStorage[0]"]}");
            var refused = false;
            try { h["dataStorage[0]"] = 1L << 40; } catch (ArgumentException) { refused = true; }
            Check("Reflected write is checked", refused && h.Get<long>("dataStorage[0]") == Math.Max(before, max));
        }

        var sys = GameSystems.Find("stamina_depletion_sprint");
        Check("GameSystems.Find", sys != null && GameSystems.All.Count > 500, $"{GameSystems.All.Count} systems");
        GameSystems.SetEnabled("stamina_depletion_sprint", false);
        var off = GameSystems.Find("stamina_depletion_sprint")!.Enabled == false;
        GameSystems.SetEnabled("stamina_depletion_sprint", true);
        Check("GameSystems.SetEnabled", off && GameSystems.Find("stamina_depletion_sprint")!.Enabled);
        GameSystems.Profile(true);
    }
}
