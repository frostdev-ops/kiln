# Common mistakes

| Mistake | What happens | Do instead |
|---|---|---|
| Calling a service from `Task.Run`, a timer or a socket callback | `InvalidOperationException` ("wrong thread") or a race with the game's jobs | `Context.Scheduler.Post(...)` or `await Context.Scheduler.NextTick()` first |
| `.Result` / `.Wait()` / `Thread.Sleep` on the tick thread | the server's step stalls; a blocked test cannot time out | `await` (continuations come back to the tick thread) |
| Changing content in `OnLoad` directly | the registries may not be ready, and the change is not applied again at the next world load or pack reload | `Content.OnReady(_ => ...)`; register new items in `OnLoad` |
| Setting the time of day or touching world state in `OnLoad` | the save load overwrites it; on a client there may be no world yet | do it in `ServerStartedEvent` (or `Client.Tick` on a client) |
| A new GUID on every build, or changing a released GUID | saved items disappear or become `[Missing: …]` placeholders | generate GUIDs once and hard-code them |
| Shipping `Kiln.Api.dll` or `Kiln.<Service>.dll` in the mod folder | type identity breaks; the mod may not load | let Kiln.Sdk reference them (`ExcludeAssets=runtime`) |
| Guessing field paths or item names | the pack logs "unknown field" / "selector matched nothing" and does nothing | `kiln catalog search`, or `Content.Dump()` (`kiln/data/content/dump.json`); `kiln lint` |
| Static caches, threads, timers or event handlers on non-Kiln objects | the old load context never unloads; `reload-test` reports leaks | keep state on the mod instance; stop what you start in `OnUnload` |
| `RegisterJson<T>()` with reflection for network messages | keeps an unloaded mod in memory after a hot reload for a while | source-generated `JsonTypeInfo<T>` or `INetMessage<T>` |
| `PlaySequence` of a cast to make an NPC or player cast a spell | the animation plays, the spell does not fire | `Abilities.Cast` / `RunImpact` for the effect |
| Expecting vanilla clients to see new items, icons or models | refused; with `modSync: off`, let in without new entities, and a Kiln item can crash them | state `clients` as `kiln` or `mod`; a server modpack delivers `mod` mods |
| Leaving `clients` unstated on a `both` mod that vanilla clients can't use | vanilla clients aren't refused: the default doesn't count | state `kiln` or `mod` |
| `"clients": "kiln"` for quests, clones or roster NPCs | the game's journal stays empty: the client builds them from the mod's files at boot | `"clients": "mod"`, `"side": "both"` (KL210) |
| Spawning a named NPC the world hasn't unlocked | the game destroys it within seconds | `place`, grant its unlock, or a `test_Townsfolk_*` villager |
| Reordering options, or renaming quests, steps or nodes, in native dialogue | ids change; saves drop what they held | keep names; add options at the end |
| A journal quest with no `name`, a step with no `text` or `title`, `roster` with no `clone` | KL312; that mod gets none of the game's UI, and its quests run with chat lines | fix what lint reports. Any `if` is fine: what one knowledge check can't say is about a second behind |
| Code-defined game-tree nodes on a dedicated server | not in the tree until the next start | `skills/*.json` |
| Comments or trailing commas in `assets/cook.json` | the cook fails (KL002, KL003) | plain JSON |
| A voice-over with an empty language slot | silence for players with that voice language | `"fallback": "en"`, if that's what you want |
| A Release build under the debugger | breakpoints never bind | deploy a Debug build |
| `kill -9` on netcoredbg while attached | the server dies with no save | disconnect from the editor |
| Raising max stacks, then removing the mod | after a restart without it, stacks above the vanilla size are cut on their next move | tell users; this is the game's own rule |
| Shipping cooked models, Impact listings or other game-derived files | redistributes game data | ship sources and a `cook.json` recipe; `assets/cooked/` is gitignored |
| Replacing a registered Impact program in place | refused: running instances read program code live | clone it to a new GUID |
| A Kiln impact without damage configs | it hits for 0 (no used item) | pass `Damage1` (e.g. `DamageSetup.FireOnly(10f)`) in the configs |
| `Abilities.PlaySequence` on a sequence that consumes the used item (every potion and food) | it has no used item, and the game reads one with no check: the server dies | Kiln refuses it and logs why: play a copy with the event's `consume` off, or let the player use the item |
| An Impact native's argument left unconnected | a native that reads it with no check kills the server (`hasAttackCategory`'s `matchAll`) | the linter says `E_UNCONNECTED`, and kiln.abilities refuses the program |
| An Impact loop without an exit | hangs the server's simulation step | the linter warns (`W_LOOP`): add an exit |
| Many hits on one target in one step | the engine applies at most 4 impacts per target per step | Kiln defers the rest; don't rely on them landing in the same step |
| Two mods detouring the same very hot function (advanced tier) | under Wine a hot reload of one can stall | one mod per hot function |
| `_ReturnAddress()` in a native hook handler (advanced tier) | another link in front of yours hides the game's call site | `RawHook::caller()`, read before calling the original |
| `Hooks.Detour`/`Hooks.Mid` per command, or a native hook whose label or user pointer changes each time | each such install takes a new link slot; links are never freed, and after 65,536 hooking fails | install once, switch a flag; an identical native re-install reuses its slot |
| Testing against the live server | players see your half-done mod; a fault disables it in front of them | a separate local test server |
| Claiming a client-side effect works after a server-only test | a dedicated server draws nothing, has no UI and no sound | list it as "needs a client check" |

## dev.20+ additions

| Mistake | Result | Use instead |
|---|---|---|
| Take then give to move gear | makes a new item entity, losing instance state | `Items.Move` |
| Treating gear `EntityData` as saved item payload | no stable positional identity; data is session-only | check `IsPersistent`; persistent item data/affixes are not supplied |
| Registering an ECS scheduler declaration in ordinary `OnLoad` | world registration has already closed | ship `kiln.programs.json`, then `EcsPrograms.Bind` in `OnLoad` |
| Calling game services from an ECS worker callback | wrong thread and undeclared access | declared context access, or post tick work |
| A UI block `command` intended for a joined server | runs only in this game's command registry | `castAbility` / `applyEffect` with the mod on the server |
| A factory recipe with `NeedsProps` | refused: no nearby-prop context on that path | use a manual station |

## dev.21+ additions

| Mistake | Result | Use instead |
|---|---|---|
| A mid hook that sets `TrampolineRsp` to anything but `Rsp` to return early | the stub resumes with `rsp = TrampolineRsp; ret`, so any other value returns to the wrong address | at a function's first instruction: `r.Rax = value; r.TrampolineRsp = r.Rsp;` |
| Installing the same raw handler twice on one function for two links | the second install replaces the first; one handler has one live link per function | one handler function per link |
| Expecting a priority-0 link to run before an EMF or Shroudtopia mod's hook | their links run first (priority 1, newest first) | a higher priority, or `before` their owner (`emf:<id>`) |
| An `early` or `boot` native mod that `requires` a mod whose DLL is in `chain.ini [late]` | it fails at startup (the late DLL loads after all plugins) | a `world`-phase mod, or load the DLL early |
| `Overrides.Claim` (or a write through a resource got there) from `Task.Run` or a timer | `InvalidOperationException`; before dev.21 the change counted as Kiln's and was never given back | `Context.Scheduler.Post`; get handles in `OnLoad` |
| Reading a `null` from `Texts.Get` as "no such text" | it also means another thread is loading the catalog | check `Texts.Status`; `await Texts.Preload()` first |
| A native transfer handler that counts or logs every call | dry-run moves call it too (`KILN_TRANSFER_DRY_RUN`) | skip side effects when the flag is set |
| File or folder names that end in a dot or a space in a mod zip | the launcher refuses the zip: Windows would turn them into other names | rename them |
