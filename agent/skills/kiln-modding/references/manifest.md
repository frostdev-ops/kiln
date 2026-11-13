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
  "features": ["tick"],      // Kiln features this game build must have
  "entryAssembly": "MyMod.dll",
  "entryType": "MyMod.MyModClass",   // default: the only public Mod subclass
  "exports": ["MyMod.Api.dll"],      // assemblies dependents compile against
  "advanced": false,
  "tests": { "autoRun": true, "timeoutSeconds": 20 },
  "writes": ["Fuel"]         // dev.27+: component types written with World.Write/Update/WriteField/WriteAt (max 64)
}
```
`writes`: Kiln.Sdk adds the types your code passes to `World.Write` / `World.Update`; list by hand what `WriteField` and
`WriteAt` name as a string. Read when the server starts (a hot-loaded change counts from the next start). A list Kiln
cannot read loads the mod with a warning and refuses only its tracked writes.

## Clients
| `clients` | A joining player needs | Use for |
|---|---|---|
| `none` | nothing | server logic, chat commands, world edits, creature and loot changes, `"journal": "chat"` quests |
| `kiln` | Kiln: the server sends definitions | new items, recipes, template clones from existing resources |
| `mod` | this mod, from the server's modpack | icons, models, sounds with your audio, talents, quests, NPC clones, client code, key-bound abilities |

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
