# kiln.progression: levels, experience, skill tree, skill points

| Layer | File |
|---|---|
| C table | `native/services/players/kiln_progression.h` (`kiln.progression` v1, published by kiln-players.dll) |
| Native | `native/services/players/progression.cpp` (next to kiln.players) |
| Symbols | `sigdb/symbols/server.d/progression.json` (features `players.progression`, `players.skills`, `players.skill_knowledge`, `players.progression_events`) |
| Managed | `managed/services/Kiln.Players/Progression.cs`, `ProgressionEvents.cs` (`Kiln.Progression`, `Player.Progression`) |
| Admin | `mods/Kiln.Admin/Ops.Progression.cs` (ops + `/level`, `/xp`, `/skillpoints`, `/skill`), launcher Players page |
| Test mod | `mods/tests/progression-test/` |
| Evidence | `docs/re/progression.md` |

## What the game has, in short
- **Level** and **experience** are attributes of the player entity. Experience has three values: experience into the
  level, *granted* experience the game counts in the next step, and what the level *needs*. The game's `player_level`
  counts granted experience every step and raises the level by one per step while the experience reaches what is
  needed, up to the **level cap 50** (balancing table). At the cap, new experience is discarded.
- **Skill points are not stored.** Total = 2 × (level − 1) + the player's **skill-point knowledge** (Elixir wells +3,
  "additional skill point" rewards +1; 114 points in all on this build); unspent = total − the costs of the unlocked
  nodes, shadow nodes included (skills.md). A native tree that costs more than the total, or has nodes not linked to a root, is
  reset (free) by the game when the player logs in next.
- **The tree**: 243 nodes (12 roots, 65 attributes, 43 active skills, 123 passives), 91 with levels. A node can be
  unlocked when a linked node is unlocked (or a root is next to it) and the points suffice.
- Characters live on the player's machine: the server sends the changed character back to the client, which saves it.

## C#
Reference Kiln.Players as for `Players` (docs/kiln/services/players.md).

```csharp
using Kiln;

[Subscribe] private void OnLevel(PlayerLevelChangedEvent e) => Log.Info($"{e.Player?.Name} {e.OldLevel} -> {e.NewLevel}");

async Task Reward(Player p)
{
    var prog = p.Progression;                    // PlayerProgression for this player
    Log.Info($"{p.Name}: {prog.Read()}");        // level 12/50, experience 3000/7437, skill points 4 of 30 (11 nodes)
    var r = await prog.AddExperience(5000, ExperienceSource.Quest);   // the game levels them up
    if (!r.Ok) Log.Warn(r.Reason);               // e.g. "level 50 is the level cap: the game discards new experience"
    await prog.GiveSkillPoints(2);               // two +1 skill-point knowledge flags
    await prog.Unlock(0xffd5f940);               // the double jump node, if linked and affordable
}
```

| Member | Threads | Notes |
|---|---|---|
| `Progression.Available`, `Status` | any | what works on this build (`Levels`, `Skills`, `SkillKnowledge`, `ExperienceEvents`, `Program`), counters, `Reason` |
| `Progression.Get(EntityId)` / `player.Progression.Read()` | tick thread: live; elsewhere: the snapshot taken every 8 steps (`IsSnapshot`) | `ProgressionSnapshot`: `Level`, `LevelCap`, `Experience`, `ExperienceNeeded`, `ExperiencePending`, `SkillPoints` (unspent), `SkillPointsTotal/Spent/FromLevel/FromKnowledge/PerLevel`, `UnlockedNodes`, `GivableSkillPoints`, `AtLevelCap`, `OverBudget`; null for a player not in game |
| `player.Progression.Level`, `Experience`, `ExperienceNeeded`, `SkillPoints`, `UnlockedNodes` | as `Read()` | shortcuts |
| `SetLevel(level)` | any (completes on the tick thread) | 1..cap (clamped, `Reason` says so). As the game's admin command: experience 0, granted 0; needed as for a level reached by playing |
| `AddExperience(amount, source)` | any | granted += amount like a quest reward, `XPGainedEvent` to the client (source shown there). One level-up per step: big amounts settle over several steps |
| `SetExperience(n)` | any | experience into the level; at or above what is needed the game levels up next step |
| `GiveSkillPoints(n)` | any | unowned +1 skill-point knowledge, up to `GivableSkillPoints` (≤ 60); more points: raise the level |
| `Unlock(node, force)` / `Lock(node, force)` | any | one node, or one level of a node with levels, also a shadow node. Without force as in the game's tree: linked and affordable / the rest stays connected. Lock is free (no cost item). A force that breaks the game's rules still applies and says so in `Reason`: "forced: this tree is not linked to a root, so the game resets it at this character's next join", or its cost |
| `Respec()` | any | every node back, free (the game's own reset), and the shadow table: kiln.log `(respec)` per node |
| `Progression.Tree`, `FindNode(id)`, `ParseNode("0x…")` | any (after the world's first step) | `SkillNode`: `Id`, `Type`, `Cost`, `MaxLevel`, `ClassAlignment` (0 Warrior, 1 Mage, 2 Ranger, 3 Neutral), `ActiveSkillItem`, links, UI position |
| `Progression.UnlockedNodes(EntityId)` | tick thread | `UnlockedSkillNode`: `Id`, `Level`, `MaxLevel`, `Cost` (now), native and shadow nodes |
| `PlayerLevelChangedEvent`, `PlayerSkillsChangedEvent` | posted on the tick thread | seen in the snapshots (every 8 steps) and after Kiln's changes (`Request` = its id, 0 = the game). Not cancelable. Several level-ups between two snapshots are one event |
| `Progression.Diagnostics` | tick thread | `ExperienceForLevel(level)` (the game's table and cap), `Simulate(steps)` (the game's skill functions on a scratch tree), `ProbeAttributes(...)` (attribute lookup + formulas on a scratch component) |

Changes return `Task<ProgressionResult>` (`Ok`, `Status`, `Applied`, `Reason`, `Before`, `After`). The change is applied
in the next server-world step and the task completes two steps later (~80 ms) with what the game made of it; game
reasons (player left, cap, unknown or unlinked node, nothing left to give) come back as `Ok = false`, never as an
exception. Arguments out of range throw `ArgumentException`; an unavailable part completes with `Status
"unavailable"`.

## C
```c
#include "kiln_progression.h"   /* native/services/players/ */
const KilnProgressionApi* pr = api->service((KilnStr){KILN_PROGRESSION_SERVICE, 16}, 1);
KilnProgression p = {sizeof p};
if (pr && pr->get(entity, &p) == KILN_OK) { /* p.level, p.experience, p.points_available ... */ }
KilnProgressionRequest rq = {sizeof rq, KILN_PROG_OP_ADD_EXPERIENCE, entity, 500};
rq.source = 3; rq.callback = &done; uint64_t id; pr->submit(owner, &rq, &id);   /* done(result) on the tick thread */
```
`result(id, &out)` polls instead of a callback (the last 64 are kept); `events(after, ...)` reads the event ring.

## How it works
- **Reads** go through the game's own code: the attribute lookup (`AttrFind`) on the player's Level / Experience,
  `SkillPointsTotal` and `SkillPointsSpent` over masks rebuilt from `UnlockedSkillNodes` with the game's rebuild, the
  knowledge blocks the game's sum reads.
- **Changes** are queued and applied by Kiln's program `kiln_players_progression` (kiln.world; writes Experience,
  Level, UnlockedSkillNodes; with PlayerState; host only) for that player, after MarkWritten, so they replicate
  (`NetworkPlayerAttributes`, `NetworkLevel`) and go into the character like the game's own writes:
  - experience: granted += n, formulas re-run, `XPGainedEvent` (exactly the quest reward path);
  - level: level, experience 0, granted 0, needed = the table's value, formulas re-run;
  - nodes: the game's `SkillNodeSet`, then `changeCount + 1` so the game's `player_skilltree` re-derives the masks and
    `handle_skill_impacts` adds or removes the node effects;
  - points: `{knowledge id, Bool, 1}` + the player's context pushed into `KnowledgeTriggerQueue`, the queue the game's
    quest rewards and the client's knowledge input use.
- **Fail soft**: each part is a sigdb feature with runtime checks (singletons found, the tree's 243 nodes match the
  game's lookup, the level cap and table read). A missing part answers `unavailable`; with nothing working the service
  is not published. Every call into the game is guarded.

## Shadow nodes (kiln.skills)
Nodes past the game's caps live in kiln.skills' table, not in `UnlockedSkillNodes`. The service looks kiln.skills up at first use, and
every read and change goes through it: `SkillPointsSpent`, `SkillPoints` and `UnlockedNodes` count them (the sums run on scratch
masks, under `shadow_scope`, so the shadow hooks know whose they are), Unlock and Lock act on the table through the game's NodeSet, a
lock is refused while another shadow node hangs on it, Respec clears the table. Without kiln.skills or its shadow hooks nothing changes.

**On a real client in tests: no `/level`, `/xp` or `/skillpoints`.** Real Steam achievements fire, even on LOCAL copies.

## Kiln.Admin
| Op | Args | Answer |
|---|---|---|
| `progression_status` | | what works, cap, points per level, counters |
| `players_progression` / `players_progression_get` | `entity?` | `{entity, name, progression{level, levelCap, experience, experienceNeeded, experiencePending, skillPoints, skillPointsTotal, skillPointsSpent, skillPointsFromLevel, skillPointsFromKnowledge, givableSkillPoints, unlockedNodes, atLevelCap, overBudget, ...}}`, all players without `entity` |
| `players_progression_set` | `entity`, any of `level`, `experience`, `skillPoints` (give), `respec: true` | `{steps: [result...], progression}` (applied in that order) |
| `give_xp` | `entity`, `amount`, `source?` (`Quest`, `Combat`, ...) | result `{ok, op, status, applied, steps, reason, before, after}` |
| `give_skill_points` | `entity`, `amount` | result |
| `skill_unlock` / `skill_lock` | `entity`, `node` (`"0x…"` or a number), `force?` | result |
| `skill_respec` | `entity` | result |
| `skill_nodes` | `entity` | `[{id, name, level, maxLevel, cost}]` |
| `skill_tree` | | `[{id, name, type, class, cost, maxLevel, x, y}]` (names: the ability item of active nodes, else type and class) |

Chat (permission level Admin, nodes `kiln-admin.command.<name>`): `/level <player> [level]`, `/xp <player> <amount>`,
`/skillpoints <player> [amount]`, `/skill <player> list | unlock <node> [force] | lock <node> [force] | respec`.
The launcher's admin Players page shows level, experience and skill points per player with Set level, Give XP, Give
points and Respec.

## Testing
```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/progression-test -c Release -o $KILN_OUT/mods/progression-test --artifacts-path $KILN_OUT/dotnet
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any; tools/lab/lab.sh start <lab>
grep progression-test ~/srv/enshrouded/labs/<lab>/kiln/logs/kiln.log      # 45 PASS, 0 FAIL without a player
touch ~/srv/enshrouded/labs/<lab>/kiln/control/progression-test.live       # with a player in game: the live run
```
Without a player the mod checks the service, the program, the game's experience table against the balancing
resource, the tree against kiln.gamedata, the game's skill functions on a scratch tree, the attribute layout, the
knowledge list, refusals and argument checks. The live run (throwaway character only) adds experience, sets the level
and experience, unlocks and locks a node, gives one skill point, respecs and puts level, experience and nodes back
(the given skill-point knowledge stays). Leak check: `reload-test progression-test 3` (0 leaked contexts).

## In a vanilla client joined to a lab as Admin
All seen in the game's own UI (details in docs/re/progression.md section 6): the XP bar follows grants at once; a
Combat grant shows "Enemy slain XP +300"; a grant across a level shows "LEVEL UP!" with +2 skill points; set level,
given skill points, unlock / lock / respec show in the skill tree at once (attribute effects follow); an over-budget
tree shows Available 0; after leaving and rejoining, Kiln's writes (experience, the knowledge point, nodes) were still
in the character. Scripted live run: 59 PASS, 0 FAIL.

Still open: the label the client shows for `None` / `Quest` grants; whether the world grants an
`additional_skillpoint_*` flag again after Kiln gave it (it does not, by design of knowledge flags); the launcher's
Players-page row against a live server (typechecked, not clicked through).
