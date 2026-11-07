# Wiki note: shroudlink and Ember (agent rustsdk, 2026-11-07)

What changed for the public guide (frostdev.io/kiln/wiki). Details: `docs/kiln/engines/shroudlink.md`,
`docs/kiln/engines/ember.md`.

## Suggested text: "Other mod loaders" page, new rows

**shroudlink (server mods: chestsort, assistants, storage, meter, gemforge).** Kiln runs shroudlink's own server core
as a plugin, so you no longer need its `steam_api64.dll` (the file a Steam check or game update puts back). Put the
mods in `shroudlink_mods/` next to `enshrouded_server.exe` as before, or add them to a Kiln profile. The players' MODS
tab still comes from shroudlink's optional client addon, which Kiln chains like any `dinput8.dll` mod. Bigger stacks
and bigger chests (its EML data mods) are applied by Kiln's EML runner before the world loads, also after a game update.
Moving an existing server: stop it, delete `steam_api64.dll`, rename `steam_api64_o.dll` back to `steam_api64.dll`,
keep `shroudlink_mods/`, install Kiln. If you keep shroudlink's proxy, Kiln notices and lets it run the mods
(`kiln-shroudlink steps aside` in kiln.log).

**Ember (server SDK by zachthedev).** Ember has not released its loader or mod format yet. Kiln already recognises
Ember mods in `ember/mods/<mod>/` and lists them in kiln.log and the status file, but cannot start them until Ember
publishes how. Not to be confused with Nexus mod 79 "Ember", an EML mod that Kiln runs today.

## For mod authors (Kiln C#)

New in `Kiln.World`: `Nearby.Entities(center, radius, component)` and `Nearby.Within(list, center, radius)`, nearest
first, e.g. `Nearby.Within(Items.FindInventories(), player, 40)` for the chests around a player.
