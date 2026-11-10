# kiln.mod.json (manifest v2)

One file per mod folder (`kiln/mods/<folder>/kiln.mod.json`). JSON with `//` comments and trailing commas allowed.
Schema: `schema/kiln.mod.schema.json`. Reference implementation: `managed/Kiln.Host/Manifest.cs` (the build task uses
the same file). v1 manifests (id, name, version, apiVersion, entryAssembly, entryType, side) load unchanged.

```jsonc
{
  "id": "my-mod",                       // required: [a-z0-9][a-z0-9._-]{0,63}; not kiln, kiln.api, game, enshrouded, native, permissions
  "name": "My Mod",                     // default: the id
  "version": "1.4.0",                   // required: semver (1 / 1.2 are read as 1.0.0 / 1.2.0)
  "description": "…", "authors": ["…"], "license": "MIT", "url": "https://…",
  "apiVersion": "0.2",                  // required: Kiln mod API (see below)
  "side": "server",                     // server (default) | client | both
  "clients": "none",                    // what joining players need: none | kiln | mod (default: server -> none,
                                        //   client/both -> mod; see "Clients" below)
  "dependencies": {
    "sample-lib": "^1.2",               // required, version range
    "kiln": ">=0.1",                    // the Kiln core itself (a dev build 0.1.0-dev counts as 0.1.0)
    "extras": { "version": "*", "optional": true }
  },
  "loadAfter": ["some-mod"],            // ordering only: when present, load after / before it
  "loadBefore": ["other-mod"],
  "incompatible": { "rival": "<3" },    // or ["rival"] (any version): this mod does not load when it is present
  "features": ["tick", "log.mirror"],   // Kiln features this game build must have (Game.HasFeature)
  "entryAssembly": "MyMod.dll",         // a file in the folder; leave it out for a mod of data only (see below)
  "entryType": "MyMod.MyModClass",      // default: the only public Kiln.Mod subclass
  "exports": ["MyMod.Api.dll"],         // assemblies dependents compile against (shared at run time)
  "advanced": false,                    // opt into the advanced tier (ModInfo.Advanced)
  "tests": { "autoRun": true, "timeoutSeconds": 20 },  // Kiln.Testing (testing.md)
  "kilnBuild": { "version": "0.2.0-dev+20261106.0f0a3f6", "created": "2026-11-06T12:12:16Z" }  // written by the build
}
```
Unknown keys are kept but logged as warnings (`mod my-mod: unknown key 'dependecies' (ignored)`).

## Built against which Kiln (`kilnBuild`)
Kiln.Sdk writes `kilnBuild` when it builds a mod (also into a project's own kiln.mod.json): the Kiln build of the
Kiln.Api it compiled against, named as the runtime bundle is (`<semver>+<yyyymmdd>.<git>`, the commit's time in
`created`). Don't write it by hand. Two dev builds both say `0.2.0-dev`, so semver can't tell a mod that uses something
newer than the player's Kiln; the stamp can:
- **The launcher** compares the active profile's mods with the game's runtime (its receipt, and that bundle's `created`)
  and shows "Update Kiln" on Play, naming the mods built for a newer Kiln.
- **Kiln.Host** compares with its own Kiln.Api's stamp before the mod loads:
  `[W] [host] mod item-blocks 0.1.1 was built against Kiln 0.2.0-dev+20261106.0f0a3f6, newer than this runtime
  (0.2.0-dev+20261106.10a0d37): if it fails, update Kiln on the launcher's Runtime page`. It still loads: the API only
  grows, so most such mods work.
- **When it fails** on a Kiln member the runtime lacks (MissingFieldException, MissingMethodException, TypeLoadException
  or a missing `Kiln.*` assembly, in `OnLoad`, as its types load, or later), kiln.log and `mods.txt` say so plainly:
  `item-blocks needs a newer Kiln runtime (it uses Kiln.StatusLook.Fire, which runtime 0.2.0-dev+20261106.10a0d37 doesn't
  have): update Kiln on the launcher's Runtime page`. This works for mods without a stamp too.

Order: the semantic version, then `created` when both sides have it, else the day. A runtime or mod built from a tree
without git has no stamp, and nothing is compared before the load.

Reserved ids: `kiln` (the core, usable as a dependency), `kiln.api`, `game`, `enshrouded`, `native`
and `permissions` (they would collide with the config files `<configDir>/native.json` and `permissions.json`).

## Clients
`clients` tells servers and the launcher what a player's game needs to join a server that runs the mod:

| Value | Player needs | Example |
|---|---|---|
| `none` | nothing: vanilla clients join | server logic, chat commands, world edits through the game's own build paths |
| `kiln` | Kiln on the client, not this mod: the server sends the mod's definitions (ContentSync) | new items cloned from existing ones |
| `mod` | this mod on the client: the server's modpack delivers it (checked by its `kdh1` hash) | icons, retextures, models, client code |

Without the key: `side: "server"` means `none`, `client` and `both` mean `mod`. Every value is valid with every side.
The loader itself does not act on it; `Kiln.Network` (REJECT/WELCOME `required[]`) and the launcher (modpacks) do.
A loaded mod that states `kiln` or `mod` itself makes the server refuse vanilla clients, unless `network.json` has
`modSync: off`. A value that only comes from the `side` default doesn't, because many `both` mods work with vanilla
clients. A Kiln client without a mod that states `mod` is refused too, with a message naming the mods, unless `network.json` sets `modSync` to
`warn` or `off`. It needs `side: both` to be checked, and `optionalMods` exempts one. The refusal carries the server's modpack, so the launcher can offer it.
A client let in anyway (`modSync: off`) doesn't get entities of new templates, but a new item it meets can still crash
it: see [Clients without Kiln](../network.md#clients-without-kiln).

## Mods of data only
A mod needs no code. Without `entryAssembly` (and `entryType`) Kiln.Host loads the folder as a mod with nothing to run: it has
its id, version, dependencies, side and a place in the load order, a line in the mod list (`kiln/logs/mods.txt`, the launcher's
`state/mods.json`) and unloads and reloads like any mod. What the folder holds belongs to it: data packs for kiln.content
(`content/*.json`, [content.md](../services/content.md#data-packs)), assets for kiln.assets.
```jsonc
// kiln/mods/pack-only/kiln.mod.json, with kiln/mods/pack-only/content/items.json beside it
{ "id": "pack-only", "version": "1.0.0", "apiVersion": "0.2", "side": "both", "clients": "kiln" }
```
```text
[host] mod pack-only 1.0.0 loaded in 0.4 ms (data only: no entryAssembly)
```
A key that is present must still be a `.dll` file name (an empty `entryAssembly` is refused). Before dev.22 Kiln.Host refused a manifest
without it (`kiln.mod.json: 'entryAssembly' is required`) and the folder's data packs were skipped. Kiln.Sdk, which builds code, still
requires the key to name the assembly it builds (KILN001).

## Versions and ranges
| Range | Means |
|---|---|
| `*`, empty | any version |
| `1.2.3`, `^1.2.3`, `^1.2`, `1.2` | compatible: `>=1.2.3 <2.0.0`; for 0.x the minor is breaking (`^0.2` = `>=0.2.0 <0.3.0`), as in Cargo/npm |
| `~1.2` | patch updates: `>=1.2.0 <1.3.0` |
| `=1.2.3` | exactly; `=1.2` = `1.2.x` |
| `1.x`, `1.2.*` | wildcards |
| `>=1.0 <2`, `>1.2`, `<=2.1` | comparators; space or comma = AND |
| `1.0 - 2.0` | inclusive hyphen range (`2.0` partial: up to `<2.1.0`) |
| `[1.0,2.0)`, `[1.0,)`, `(,2.0]`, `[1.2.3]` | Maven/Forge intervals; `[1,2),[3,4)` unions |
| `^1 \|\| ^3` | either |

An exclusive upper bound `<X.Y.Z` also excludes the pre-releases of X.Y.Z (`<2` rejects `2.0.0-beta`); pre-releases
inside a range match. **apiVersion** is special: a bare version means "this API or a later one with the same
major" (the mod API stays additive within a major, 0.x included), so `"0.1"` mods load on API 0.2. Today's API:
`Kiln.Mods.ApiVersion` = 0.2.0.

## What the loader does (Forge's ModSorter + dependency checks)
At the first server-world step (a client: in the menu; its side "server" mods at each hosted world's first step, see
docs/kiln/client.md), for every folder with a `kiln.mod.json` (a `.disabled` file skips the folder):
1. Read and validate the manifest. Errors name the field; the folder is reported as `folder:<name>`.
2. Reject duplicates (the first folder in name order keeps the id), mods for the other **side** (skipped, not an
   error), an `apiVersion` this Kiln does not provide, missing `features`.
3. Check dependencies and incompatibilities until nothing changes (a failure can fail its dependents): missing
   required dependency, version mismatch (also for a present optional one), `kiln` version, declared incompatibility.
4. Order: dependencies (required and present optional), `loadAfter` and `loadBefore` edges; ties by id. Mods in a
   **cycle** fail together, with the cycle in the message; their dependents fail next round; bystanders load.
5. Load in that order. A mod whose load throws (bad entry type, `OnLoad` exception) fails, and so do the mods that
   require it.

A rejected mod never stops the others. `kiln.log` says why, for example:
```
[E] [host] mod sample-missing-dep 1.0.0 not loaded: missing dependency sample-nowhere ^1.0
[E] [host] mod sample-needs-lib2 1.0.0 not loaded: needs sample-lib [2.0,3.0), found sample-lib 1.2.0
[E] [host] mod sample-cycle-a 1.0.0 not loaded: dependency cycle: sample-cycle-a -> sample-cycle-b -> sample-cycle-a (each must load after the next)
[E] [host] mod sample-after-cycle 1.0.0 not loaded: dependency sample-cycle-a * did not load (failed: dependency cycle: …)
[I] [host] mod sample-client-only 1.0.0 skipped: side 'client': not loaded on a server
[W] [host] mods: 13 loaded, 9 failed, 1 skipped; load order: …
```
The same reasons are in `Mods.All` (`ModEntry.State`, `.Error`), the control command `mods` and `kiln/logs/mods.txt`.
A later `load <folder>` or `reload <id>` retries mods that failed for want of that mod.

## Exports: shared mod APIs
Each mod has its own collectible load context. Assemblies resolve in this order: Kiln.Api (shared), Kiln's shared
assemblies in `kiln/host` (`Kiln.*`), the **exports of the mod's dependencies** (transitively; loaded in the
providing mod's context, so a type is the same type on both sides and static state is shared), then the mod's own
folder. A mod only sees exports of mods it depends on.

Pattern: put the API in its own assembly (`MyLib.Api.dll`), ship it in the provider's folder, list it in `exports`,
and have dependents reference it with `Private="false"` (compile only; see `mods/samples/SampleConsumer`). The entry
assembly can be exported too.

Because a dependent holds the provider's types, **reloading or unloading a provider cascades**: its dependents
(transitively, in reverse load order) unload first, then the provider, then everything loads again in order.
