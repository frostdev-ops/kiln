# Wiki note: Shrouded Engine Tweaks mods, and the game's types and systems

Two additions for the public guide.

## 1. "Other mod frameworks": add Shrouded Engine Tweaks
Suggested text:

> **Shrouded Engine Tweaks (SET) Lua mods** run on Kiln unchanged, on dedicated servers too. Put each mod where SET
> wants it, `<game>/shrouded_engine_tweaks/mods/<name>/init.lua`, and start the game or server; `kiln/logs/kiln.log`
> lists them (`[set] Mod loaded: <name>`). You don't need SET's `powrprof.dll`. If you do run SET itself, Kiln steps
> aside and lets SET run its mods. Not yet supported: `GameOptions`, English names (`Res.text`), `Admin.send`,
> `Admin.setLevel`, `Admin.setFlameLevel`, and SET's overlay windows other than Kiln's own Systems and Types windows.
> To run a line of Lua, put it in a file in `kiln/control/set/` (`echo 'print(#Systems.list())' >
> kiln/control/set/x.lua`).

Link: docs/kiln/set.md.

## 2. "For mod authors": the game's types, systems and components by name
Suggested text:

> Kiln reads the game's own description of its data: every type's fields, offsets, base classes, enum values and
> default values (`GameTypes.Find("Health")`). With it you can read and write any component by name, checked against
> its type (`Reflected.Component(entity, "Health")["dataStorage[0]"] = 50`; a value that doesn't fit is refused). You
> can also switch off any of the game's systems (`GameSystems.SetEnabled("fall_damage_infliction", false)`), time
> them all (`GameSystems.Profile(true)`, `GameSystems.Stats()`), and read or post the game's ECS events
> (`GameEvents.Each`, `GameEvents.Post`).
>
> Admins get the same from commands: `/types`, `/type`, `/systems`, `/system <name> on|off`, `/systems profile on`,
> `/systems top`, `/component <entity> <Component> [path] [value]`. In the game, `/devtools systems` and
> `/devtools types` open the Systems and Types windows (bindable in the Kiln menu's Keys tab).

Link: docs/kiln/services/types.md.
