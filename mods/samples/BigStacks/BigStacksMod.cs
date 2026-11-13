// SPDX-License-Identifier: MIT
// Big Stacks: a C# port of the idea of EMF 0.3.3's big_stacks mod (MIT): every stackable item (max stack > 1) gets
// max stack x multiplier, at most `max`, never below its own size. Through kiln.content the change is a layer owned
// by this mod: it comes off when the mod unloads or reloads, and the stack guard (switched on by kiln.content) keeps
// bigger stacks from being cut down meanwhile. Nothing is saved; the change is re-applied at every world load.
using System.Globalization;
using Kiln;

namespace BigStacks;

public sealed class BigStacksConfig
{
    [Comment("Max stack size multiplier for every item that stacks (max stack > 1).")]
    [Range(1, 1000)]
    public double Multiplier { get; set; } = 4;

    [Comment("Largest stack size (the game stores it as 16 bits: at most 65535).")]
    [Range(2, 65535)]
    public int Max { get; set; } = 9999;
}

public sealed class BigStacksMod : Mod
{
    private ModConfig<BigStacksConfig>? _config;

    public override void OnLoad()
    {
        _config = Config.Register<BigStacksConfig>();
        if (!Content.Available)
        {
            Log.Warn("kiln.content is not available on this server: Big Stacks does nothing");
            return;
        }
        Content.OnReady(_ => Apply());
    }

    [Subscribe]
    private void OnConfigChanged(ConfigChangedEvent e)
    {
        if (e.ModId != Context.Info.Id || !Content.Available || !Content.Ready) return;
        Content.Revert();
        Apply();
    }

    private void Apply()
    {
        var c = _config!.Value;
        var m = c.Multiplier.ToString(CultureInfo.InvariantCulture);
        // The same document works as a data pack (content/bigstacks.json) on the server and, later, on clients.
        var pack = $$"""
            { "items": { "*": { "@where": { "maxStackSize": ">1" },
                                "maxStackSize": { "mul": {{m}}, "max": {{c.Max}}, "raiseOnly": true } } } }
            """;
        var report = Content.Apply(pack, $"bigstacks x{m} max {c.Max}");
        foreach (var err in report.Errors) Log.Warn(err);
        int raised = 0, stackable = 0;
        foreach (var item in Content.Items)
        {
            var orig = item.Original("maxStackSize").Integer;
            if (orig <= 1) continue;
            stackable++;
            if (item.MaxStack > orig) raised++;
        }
        var wood = Content.Items.Get("Material_T1_Wood_Logs");
        Log.Info($"raised the max stack size of {raised} of {stackable} stackable items (x{m}, at most {c.Max}); " +
                 $"wood logs {wood?.Original("maxStackSize")} -> {wood?.MaxStack}; stack guard {(Content.StackGuard ? "on" : "off")}" +
                 (report.Conflicts.Count > 0 ? $"; {report.Conflicts.Count} conflict(s) with other mods, e.g. {report.Conflicts[0]}" : ""));
    }
}
