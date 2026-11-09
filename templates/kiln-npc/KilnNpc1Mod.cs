using Kiln;

namespace KilnNpc1;

/// <summary>A friendly NPC from building blocks. blocks/npcs.json holds one in data: it is there at the first start. The one below is written in C#:
/// Kiln keeps it and the game builds it at the next start. Both talk with a dialogue of quests/talk.json.</summary>
public sealed class KilnNpc1Mod : Mod
{
    public override void OnLoad()
    {
        Blocks.Define("rowan", new NpcBlock
        {
            Name = "Rowan the Peddler",
            Body = Bodies.Villager,
            TalkName = "peddler-talk",
            // a vendor: one plant fibre buys one twig, at the body's workshop
            Vendor = new VendorBlock { Sells = { [new ItemRef("Material_T1_Twigs")] = Price.Of(new ItemRef("Material_T1_Plant_Fibre"), 1) } },
            Roster = new RosterBlock { SummonTool = "Rowan's Bell", Description = "A peddler of small things." },
            Place = new NpcPlace { Distance = [6, 10] },
        });
    }
}
