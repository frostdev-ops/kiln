// SPDX-License-Identifier: MIT
using Kiln;

namespace TkBoot;

/// <summary>Reads one record of the game's data in OnBoot and stages a status and an item (its stack from the record); OnLoad defines
/// nothing and logs the committed event.</summary>
public sealed class TkBootMod : Mod
{
    public override void OnBoot(BootContext boot)
    {
        var wood = boot.GameData.Read("item:Material_T1_Wood_Logs");
        if (!wood.Ok) throw new InvalidOperationException(wood.Reason);
        var d = BootDefinitions.For(boot);
        var status = d.Define("tk-cold", new StatusBlock { Name = "Kit Cold", Seconds = 3 });
        if (!status.Ok) throw new InvalidOperationException(status.Reason);
        var item = d.Define("tk-token", new ItemBlock { Name = "Kit Token", Stack = wood.Data!.Value.GetProperty("maxStackSize").GetInt32() });
        if (!item.Ok) throw new InvalidOperationException(item.Reason);
    }

    public override void OnLoad() =>
        Events.On<BootDefinitionsCommittedEvent>(e => Context.Log.Info($"tk-boot: committed {string.Join(", ", e.Definitions)} in generation {e.Generation}"));
}
