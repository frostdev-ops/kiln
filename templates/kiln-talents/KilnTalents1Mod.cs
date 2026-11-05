using Kiln;

namespace KilnTalents1;

/// <summary>The tree is data (skills/talents.json): Kiln.Skills loads every loaded mod's skills/*.json by itself (kiln.log:
/// "tree kiln-mod-id:talents registered"). This code only watches the players' choices.</summary>
public sealed class KilnTalents1Mod : Mod
{
    [Subscribe]
    private void OnChanged(TalentChangedEvent e)
    {
        if (e.Change.Tree.Id != "kiln-mod-id:talents") return;
        Log.Info($"{e.Change.Holder} {e.Change.Kind} {e.Change.Node} -> level {e.Change.NewLevel}, {e.Change.Points} point(s) left");
    }
}
