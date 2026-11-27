# kiln.mod.json (manifest v2)

Wiki: [manifest](https://frostdev.io/kiln/wiki/manifest). Usually Kiln.Sdk writes it from project properties; a hand-written file in
the project folder is validated and copied. JSON with `//` comments and trailing commas.

```jsonc
{
  "id": "my-mod",            // required: [a-z0-9][a-z0-9._-]{0,63}; not kiln, kiln.api, game, enshrouded, native, permissions
  "name": "My Mod",
  "version": "1.4.0",        // required, semver
  "description": "…", "authors": ["…"], "license": "MIT", "url": "https://…",
  "apiVersion": "0.2",       // required
  "side": "server",          // server (default) | client | both
  "clients": "none",         // none | kiln | mod (default: server -> none, client/both -> mod)
  "dependencies": {
    "sample-lib": "^1.2",
    "kiln": ">=0.2",
    "extras": { "version": "*", "optional": true }
  },
  "loadAfter": ["some-mod"], "loadBefore": ["other-mod"],   // ordering only
  "incompatible": { "rival": "<3" },
  "features": ["tick"],      // Kiln features this game build must have; dev.29.18+ "tasks.pool" for Kiln tasks
  "entryAssembly": "MyMod.dll",
  "entryType": "MyMod.MyModClass",   // default: the only public Mod subclass
  "exports": ["MyMod.Api.dll"],      // assemblies dependents compile against
  "advanced": false,         // the Native tier (raw hooks, symbols, memory); dev.32+ also accepted as "native": true (the same setting; give one, or both with the same value). A Kiln before dev.32 warns about "native" and does not opt in: keep "advanced" for older Kilns
  "steamMessages": false,    // dev.30.3+: true = the mod opens its own Steam peer message leases (SteamMessages.Open throws without it)
  "tests": { "autoRun": true, "timeoutSeconds": 20 },
  "writes": ["Fuel"],        // dev.27+: component types written with World.Write/Update/WriteField/WriteAt (max 64)
  "boot": false,             // dev.28+: true | "required": Mod.OnBoot runs before the game starts (boot.md)
  "updates": { "provider": "github", "repository": "me/my-mod" },   // dev.28+: where new versions are published
  "kilnBuild": { "version": "0.2.0-dev.29.19+…", "created": "…" }  // written by the build: the Kiln.Api it compiled against
}
```
`updates` (dev.28+): `github` (`repository`, `tagPattern` default `v{version}`, `asset`, `channel` `stable` or
`prerelease`) or `nexus` (`gameId` 5811, `modId`, `fileChainId`). The launcher and `kiln updates check` show a notice;
nothing is downloaded or installed, and an unreadable value is a warning, never a load failure. Kiln.Sdk:
`KilnUpdatesRepository` and friends ([setup.md](setup.md)).

`boot` (dev.28+): `required` stops the start (exit code 0x4B01) when the mod's boot content or one of its own data files
fails. Dedicated servers since dev.28, game clients since dev.29. A boot mod enabled or changed after the start loads only
after a restart (`RestartRequiredBoot`).

`features`: only capabilities fixed at boot: sigdb features, `kiln.<service>` rows and core rows such as
`anim.player_states` and `tasks.pool`. A row a managed service registers later, or one that depends on the session, is
refused there: check it with `Capabilities.Is` at run time.

dev.29+: `"systems": { "replaces": [...] }` is a declaration for tools; nothing reads it yet. `kilnBuild` lets Kiln and
the launcher tell a player whose Kiln is older than the mod to update it.
`writes`: Kiln.Sdk adds the types your code passes to `World.Write` / `World.Update`; list by hand what `WriteField` and
`WriteAt` name as a string. Read when the server starts (a hot-loaded change counts from the next start). A list Kiln
cannot read loads the mod with a warning and refuses only its tracked writes.

## Clients
| `clients` | A joining player needs | Use for |
|---|---|---|
| `none` | nothing | server logic, chat commands, world edits, creature and loot changes, `"journal": "chat"` quests |
| `kiln` | Kiln: the server sends definitions | new items, recipes, template clones from existing resources |
| `mod` | this mod, from the server's modpack | icons, models, sounds with your audio, talents, quests, NPC clones, client code, key-bound abilities, boot mods the players need (resource patches), material pools |

A stated `kiln` or `mod` makes the server refuse vanilla clients, unless `network.json` has `modSync: off`. A value from the `side`
default doesn't, so state it. A Kiln client without a stated `mod` mod is refused by default and told which; `modSync: warn` or `off` lets it in (passes on test servers, not yet in a real game).

`side: both` mods load on server and client. A `client` mod is skipped on a dedicated server (not an error).

## Version ranges
`*` any · `^1.2.3` / `1.2` compatible (for 0.x the minor is breaking: `^0.2` = `>=0.2.0 <0.3.0`) · `~1.2` patch updates · `=1.2.3` exact ·
`1.x` · `>=1.0 <2` · `1.0 - 2.0` · `[1.0,2.0)` · `^1 || ^3`.

## Load order and failures
Dependencies first, then `loadAfter`/`loadBefore`, ties by id. A failed mod never stops the others. Reasons are in kiln.log, `Mods.All`,
the control command `mods` and `kiln/logs/mods.txt`:
```
[E] [host] mod x 1.0.0 not loaded: missing dependency y ^1.0
[E] [host] mod x 1.0.0 not loaded: needs y [2.0,3.0), found y 1.2.0
[I] [host] mod x 1.0.0 skipped: side 'client': not loaded on a server
```
Unknown keys are kept and warned about. `kiln lint` catches them first.
