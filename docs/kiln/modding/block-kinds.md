# Block kinds a mod adds

A mod can add a kind of block: a record of its own and a compiler that says what Kiln's blocks it becomes. Other mods
then write that kind like any block, in C# and in `blocks/*.json`. A kind is made of what Kiln already compiles: **one
primary block** (an item, a weapon, a spell, a status or an effect) and up to 32 effects the primary names. It adds no
new engine pieces: for those there is no public way in this version.

Two mods take part:

- the **provider** registers the kind in its `OnLoad`;
- the **consumer** lists the provider in its `dependencies` (so the provider loads first) and defines blocks of the kind.

Players need nothing extra: a Kiln client gets the items and statuses the kind became from the server, as for any
block. It never runs the provider's code.

## The provider

```csharp
public sealed record LanternBlock : IBlock
{
    public string Name { get; init; } = "";
    public double Seconds { get; init; }
}

public sealed class LanternKind : IBlockKind<LanternBlock>
{
    public BlockPlan Compile(BlockCompileContext context, LanternBlock b) =>
        context.Plan("light",
        [
            context.Item("light", new ItemBlock
            {
                Name = b.Name,
                Tool = new ToolBlock { Harvests = ToolClass.Torch, Durability = b.Seconds },
                Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 2 } },
            }),
        ]);
}

public override void OnLoad()
{
    var result = Blocks.RegisterKind(new BlockKindDescriptor
    {
        Id = "lanterns:lantern",            // "<your mod id>:<kind>"
        JsonSection = "lanterns:lanterns",  // the section of blocks/*.json
        SchemaJson = """{"type":"object","properties":{"name":{"type":"string"},"seconds":{"type":"number"}}}""",
        Clients = ClientsLevel.Kiln,
    }, new LanternKind());
    if (!result.Ok) Context.Log.Warn(result.Reason);
}
```

Rules for `Compile`:

- It is **pure**. It reads the block and the context and returns `context.Plan(...)`. It does not call the game, a
  service, or `Blocks.Compile`, `Blocks.RegisterKind`, `Blocks.Apply` (those throw from inside a compile).
- It runs **once** per compile, on a copy of the author's record. Kiln never calls it again for a plan that exists,
  also not in a new world or after the provider was unloaded.
- The context is valid only during the call.
- Products are frozen when you make them (`context.Item`, `Weapon`, `Spell`, `Status`, `Effect`): changing the block
  you passed changes nothing. A product is plain data, as `blocks/*.json` writes it: no delegates, and no inline block
  of another kind (an inline screen effect, for one; name a block that is defined already instead).
- One product is the primary (`Plan`'s first argument); every other product is an effect. A product names another by
  `context.ProductName("key")` in a field that takes an effect by name. `context.Resolve(new BlockRef<T>("name"))`
  gives the ids of a block that was declared before the compile; `context.GuidOf(domain, name)` a stable GUID.

`RegisterKind` answers `Ok = false` (`AlreadyRegistered`, `TooManyKinds`) when the id, the section or the record is
taken; a descriptor that is wrong throws `ArgumentException`. Disposing `result.Registration` takes the kind away:
plans that are applied stay, nothing new compiles.

## The consumer

```csharp
var camp = Blocks.Define("camp", new LanternBlock { Name = "Camp lantern", Seconds = 600 });
```

```json
{ "format": 1,
  "lanterns:lanterns": {
    "camp": { "format": 1, "name": "Camp lantern", "seconds": 600 }
  } }
```

Every block of an external kind says `"format": 1`. Both ways give the same plan.

`Blocks.Define` compiles the block (what is wrong throws `BlockError` there) and returns a handle at once. The handle
is **provisional** until the plan is applied: its `Guid`, `Id` and `GameName` are final, but `Blocks.Find` gives the
block only once it is applied. `Blocks.Application(handle)` says where it stands:

| State | Meaning |
|---|---|
| `PendingContent` | compiled; waits for the game's content to be ready |
| `Applied` | every block of the plan is registered |
| `Refused` | nothing of the plan is registered; `Reason` starts with the cause |
| `Removed` | it went with its mod |

A plan is applied whole or not at all: its blocks are registered under an owner of the plan's own, and when the game
or a service refuses one of them, the others are taken back. `BlockApplicationChangedEvent` is posted once per change.

`Blocks.Compile(name, block)` and `Blocks.Apply(plan)` are the two halves of `Define`, for a mod that wants the plan
(`plan.Products`, `plan.InputHash`) before it is applied. A plan belongs to the mod instance that compiled it: another
mod, or the same mod after a reload, cannot apply it (`ArgumentException`). The same plan applied again answers the
same and does nothing. Another plan for a name that is applied is refused (`NameAlreadyApplied`): reload the mod to
change a block.

## Limits

| | |
|---|---|
| Products per plan | 1 primary and at most 32 effects |
| One product | 64 KiB of JSON, nested at most 32 deep |
| One plan | 4096 objects and lists, 1 MiB of JSON, 16 MiB of generated programs and definitions |
| One consumer mod | 64 MiB of generated programs and definitions |
| Kinds per process | 128 |
| Names | block name 1 to 64, product key 1 to 32 characters of letters, digits, `.`, `_`, `-` |
| Schema | 64 KiB, only local `#` references |

## Capabilities

| Row | Says |
|---|---|
| `blocks.kinds` | kinds can be registered |
| `blocks.compile` | blocks of a kind compile to a plan |
| `blocks.apply` | plans are applied |
| `blocks.apply.item`, `.weapon`, `.spell`, `.status`, `.effect` | a plan with this primary is applied |

All are off (with the reason) where Kiln has nothing to compile blocks to. A member whose row is off throws
`KilnUnavailableException` naming the row.

Kiln.Blocks reports its rows at its first use, not at the start of the game. So in `OnLoad`, before your mod's first
call into `Blocks`, `Capabilities.Is("blocks.kinds")` still answers false (no row of that name yet). Do not guard
your first call with it: make the call and catch `KilnUnavailableException`, as the sample does. After the first call
the rows read as they are.

## Not in this version

A kind cannot make VFX, materials, NPCs, quests, UI or anything read by the game before mods load, cannot register
raw game data, cannot run code when content is ready, and cannot be made of blocks of another external kind.

The sample `mods/samples/BlockKinds/` has both halves: `Lanterns` (the provider, with its record in `Lanterns.Api`,
which it exports) and `CampLanterns` (a consumer in C# and in `blocks/camp.json`).
