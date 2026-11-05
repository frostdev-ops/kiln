using Xunit;
namespace Kiln.Api.Tests;

public sealed class SelectorCommands
{
    [Command("one")] public string One(Player who) => who.Name;
    [Command("many")] public string Many(IReadOnlyList<Player> who) => string.Join(",", who.Select(p => p.Name));
    [Command("ent")] public string Ent(EntityId e) => e.Value.ToString();
    [Command("ents")] public string Ents(IReadOnlyList<EntityId> e) => string.Join(",", e.Select(x => x.Value));
}

public sealed class SelectorTests : KilnTest
{
    static Player P(uint id, string name) => new() { Entity = new EntityId(id), Name = name, State = PlayerState.InGame };
    static readonly Player Alder = P(1, "Alder"), Alan = P(2, "Alan"), Bob = P(3, "Bob");
    static readonly Dictionary<uint, WorldPosition> Pos = new()
    {
        [1] = new(0, 0, 0), [2] = new(10, 0, 0), [3] = new(50, 0, 0),
        [100] = new(5, 0, 0), [101] = new(20, 0, 0), [102] = new(3, 0, 0), [103] = new(200, 0, 0),
    };
    public SelectorTests()
    {
        Arg.OnlinePlayers = () => [Alder, Alan, Bob];
        Arg.PositionOf = e => Pos.TryGetValue(e.Value, out var p) ? p : null;
        Arg.Random = () => 0.5;
        Arg.FindEntities = (hint, near, radius, max) =>
        {
            var all = new List<SelectedEntity>
            {
                new(new EntityId(100), Pos[100], "Enemy_Vukah_Melee", "Vukah", TemplateKind.Creature, null),
                new(new EntityId(101), Pos[101], "Enemy_Vukah_Slinger", "Vukah Slinger", TemplateKind.Creature, null),
                new(new EntityId(102), Pos[102], "Prop_Chest_Wood", null, TemplateKind.Prop, null),
                new(new EntityId(103), Pos[103], "Npc_Villager_Blacksmith", "Blacksmith", TemplateKind.Npc, null),
                new(new EntityId(1), Pos[1], "Player", null, TemplateKind.Other, null),
            };
            return all.Where(e => hint is null || e.TemplateName!.Contains(hint, StringComparison.OrdinalIgnoreCase)).ToList();
        };
        var mod = NewMod("sel");
        As(mod, () => Commands.RegisterAll(new SelectorCommands()));
    }
    static CommandResult Run(string line, CommandSource? s = null) => Commands.Execute(s ?? CommandSource.Console, line);
    static CommandSource Me => CommandSource.ForPlayer(new EntityId(1), "Alder");
    static string Out(CommandResult r) => r.Ok ? r.Output.Single() : "ERR " + r.Error;

    [Fact] public void Nearest_and_random()
    {
        Assert.Equal("Alder", Out(Run("one @p", Me)));
        Assert.Equal("Alan", Out(Run("one @p[name=!Alder]", Me)));
        Assert.StartsWith("ERR", Out(Run("one @p")));            // console: no position
        Assert.Equal("Alan", Out(Run("one @p[x=12,y=0,z=0]")));
        Assert.Equal("Alder,Alan", Out(Run("many @a[distance=..15]", Me)));
        Assert.Equal("Bob", Out(Run("many @a[distance=20..]", Me)));
        Assert.Equal("Bob,Alan", Out(Run("many @a[sort=furthest,limit=2]", Me)));
        Assert.Single(Run("many @r", Me).Output.Single().Split(','));
        Assert.Equal(2, Run("many @r[limit=2]", Me).Output.Single().Split(',').Length);
        Assert.StartsWith("ERR", Out(Run("one @a", Me)));
        Assert.Equal("Alan", Out(Run("one @a[name=Al*,sort=furthest,limit=1]", Me)));
        Assert.StartsWith("ERR", Out(Run("one @e[type=creature]", Me)));
    }
    [Fact] public void Entities()
    {
        Assert.Equal("100,101", Out(Run("ents @e[type=creature]", Me)));
        Assert.Equal("100", Out(Run("ents @e[type=Enemy_Vukah*,distance=..10]", Me)));
        Assert.Equal("102", Out(Run("ent @e[type=!player, sort=nearest, limit=1]", Me)));
        Assert.Equal("1,2", Out(Run("ents @e[type=player,distance=..12]", Me)));
        Assert.Equal("103", Out(Run("ents @e[name=Blacksmith]")));
        Assert.Equal("103", Out(Run("ents @e[type=npc]")));
        Assert.Equal("1", Out(Run("ent @s", Me)));
        Assert.Equal("7,8", Out(Run("ents 7,8")));
        Assert.Equal("2", Out(Run("ents Alan")));
        Assert.StartsWith("ERR", Out(Run("ents @e[type=creature,distance=..10]")));   // console: no origin
        Assert.Equal("100", Out(Run("ents @e[type=creature,x=0,y=0,z=0,distance=..10]")));
        Assert.Contains("unknown filter", Out(Run("ents @e[colour=red]")));
        Assert.Contains("nothing matched", Out(Run("ents @e[type=dragon]")));
        Assert.Contains("only selects players", Out(Run("many @a[type=creature]", Me)));
        Assert.Equal("100,101,102,103", Out(Run("ents @e[type=!player]")));
    }
}
