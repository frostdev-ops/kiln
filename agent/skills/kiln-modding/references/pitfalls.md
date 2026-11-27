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
| Code-defined game-tree nodes on a dedicated server, before dev.27 | not in the tree until the next start | `skills/*.json`; dev.27+ the build bakes them |
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
| Treating gear `EntityData` as saved item payload | no stable positional identity; data is session-only | `InstanceData` (dev.26+), `Affixes` (dev.27+) |
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

## dev.26+ additions (not seen by a player)

| Mistake | Result | Use instead |
|---|---|---|
| A managed service's capability (`items.moves`) in the manifest's `features` | refused: the service registers its row only after manifests are checked | sigdb features and `kiln.<service>` only; `Capabilities.Is(name)` at run time for the rest |
| Trusting `[RequiresCapability]` to guard a call | it checks nothing at run time; the member's own code throws `KilnUnavailableException` | `Capabilities.Is` / `Why` before the call (dev.27+: KILN121 says where) |
| Taking `EntityRef.IsAlive` as "the same entity" for a world entity loaded from the save | no `SpawnTime`, serial 0: `IsAlive` only says the id exists | check `HasSerial` |
| `EntityRef.Of` / `PlayerRef.Of` in `OnLoad` | the identity sources start at the first tick: unavailable | `ServerStartedEvent` or later |
| `x == EntityRef.None` | false for a none id with a serial (record equality) | `x.IsNone` |
| Catching only `KilnUnavailableException` on a dev.26 runtime | dev.26 services still threw their own exception for an off capability | catch `NotSupportedException` (its base) when the mod must run on dev.26 |
| `Mods.ApiVersion` to learn the running Kiln's API | it is the value your mod was built with | `Mods.RuntimeApiVersion` |
| Calling `Rules.Simulate`, `QuestInput`, `GameTree.Lab*` and other lab helpers | `KILNX001` build error; they may change without notice | test kit or a lab mod with `KILNX001` in `NoWarn` |
| `OriginalCode`, `Keep`, `Arsenals.JobHandler`, `Is.Job`/`Act.Job`, `Abilities.Programs.Register`, `NativeCall.Bytes` without `"advanced": true` | `InvalidOperationException` | `"advanced": true`, or a typed service call |
| Writing a `Kiln.Ecs` struct you read and expecting the game to change | it is a copy | `World.Write` / `World.Update` (dev.27+) |
| An enum value of `Kiln.Ecs` used only as a constant | folded into a number: a renumbering after a game update is not caught at load | check `Ecs.Supports<T>()` for the type that holds it |
| A test that passes under `dotnet test` taken as proof for the server | most native services are unavailable offline; cached lookups can hide that | `kiln test` on a test server too |
| Relying on `SteamGuard` for `/questadmin` or rewards paid at a later join | not covered | a throwaway character; experimental, never run in a real client |
| `Persist.Carried` for item data that travels with a character | `KilnUnavailableException("saves.instance_data.carried")` | `Persist.World`; not in this release |
| Treating a set-aside item data record as lost | it waits in `InstanceData.Parked(key)`; Kiln never moves it to another item by itself | read `Parked`, `/instances`; decide in the mod |
| `InstanceData` on a plain stack (wood, arrows) | no item entity: nothing to keep data on | data on the holder (`EntityData`, `PlayerData`) |
| Changing `assets/player-states.json` and hot-reloading | states stay as they were at boot | restart the game and the server |
| Player states on a server that did not cook the mod, or a mod without `"side": "both"`, `"clients": "mod"` | states refused; clients that cooked are refused at join (graph hash differs) | cook on the server too; state both |
| `Assets.Override` / `Register` on the player graph's key, or an `assets.json` entry for it | `ArgumentException`; the entry is refused at boot | `assets/player-states.json` |
| A second cast of a mod's wand state before the first ended | the clip continues, it does not restart | design the clip for that, or a longer cooldown |
| A move's `animation` by name on a build without `anim.player_states` | the move keeps its `like` state; the name is reported by block and slot | check the report; a GUID or `0x` id needs no lookup |
| `blocks.weapon_moves` or `items.*` in a manifest's `features` | refused: the rows appear after manifests are checked | `Capabilities.Is` at run time |
| Moving Kiln's data folder, then building a native mod | CMake keeps the old SDK path | delete the project's `build` folder once |

## dev.27+ additions (test servers; not seen by a player)

| Mistake | Result | Use instead |
|---|---|---|
| Not reading a Kiln result (`Items.Equip`, `NoClip.Set`) | KILN120; a refusal goes unnoticed | check `result.Ok`, pass `Reason` on; `_ = ...` to drop it on purpose |
| A `[RequiresCapability]` member with nothing asking first | KILN121; `KilnUnavailableException` at run time on a build without it | `Capabilities.Is(row)` or the service's guard property in the same method, or `catch (KilnUnavailableException)` |
| The capability check in a helper of your own | KILN121 still warns: the helper is not followed | check where the call is, catch at the command, or mark the caller `[RequiresCapability]` |
| Writing the same value again inside the handler of its change event and expecting a nested event | the event for that write arrives at the start of the next step | read the state after the step, or do the follow-up outside the handler |
| `World.TryWrite` for a change that must be saved | obsolete (`World.WriteRaw`): the game neither saves nor sends it | `World.Write` / `Update` / `WriteField`, the type listed under `"writes"` |
| Taking a tracked write's call as done | it runs in the next sim step and can answer `Dropped` | read the result; ask again next step |
| `World.WriteField` / `WriteAt` without listing the component under `"writes"` | `NotDeclared`: the build adds only the types passed to `World.Write` / `Update` | list it in kiln.mod.json by hand |
| Writing a container's `Fuel` (a watering can) while a water refill covers it (`Items.Fuel.KeepFull`, a `Water` rule, Creative's rule set) | the refill writes over it one step later: `Kept` false, `WrittenOverBy` names the refill | end or narrow the refill first, or change the water through `Items.Fuel` |
| `EventFilter.ForEntity(creature)` on hit events taken as "hits on the creature" | the creature's own attacks come too (it is the `Attacker`) | test `e.Target == creature` in the handler |
| `Events.On<T>(handler, default)` | does not compile: the argument can be a priority or a filter | `EventPriority.Normal`, or leave it out |
| Casting a ray from `PlayerStates.AimOf(p).Origin` as the character's eyes | it is the camera, behind the character | fine for "what the player looks at"; for the character use its position |
| A mouse key name (`Mouse4`, `Ctrl+WheelUp`) as the only default for a mod that must load on an older Kiln | `KeyBindings.Register` throws there (`KeyChord.Parse` does not know the name before dev.27) | check `KeyChord.TryParse` first and fall back to a keyboard key |
| Relying on a `Ui.Menus.Add` entry being drawn in the main menu | with more than 3 mods' entries a menu that cannot scroll leaves them to Kiln's Mods page | make the entry's action reachable another way too (a command, a key) |
| Shipping `assets/cooked` in a mod | no receipt on the player's PC: a warning now, refused in a later release | ship the recipe and the sources; the launcher cooks on Play |
| A recipe step that names a source file the mod does not ship (a glTF kept outside the mod folder, in a folder the pack leaves out, or only on your PC) | your own cook works, so it looks done; on every other PC the launcher's cook fails at each Play, takes its time again at the next one, and the models or clips of that step are missing | keep every file a recipe names inside the mod (`assets/src/...`); before a release unpack the `kiln pack` zip into an empty folder and run `kiln cook <that folder> --game <game>`: a recipe is complete only when it cooks from the zip alone |
| Changing a baked definition in code without rebuilding the mod | the baked file wins at every start; kiln.log warns | rebuild (or `KilnBake=false`) |
| Expecting an affix on an item to act when the item lies in the backpack | affixes act while the item is in hand or worn | equip it; `AffixesAppliedEvent` says what a wearer holds |
| `NoClip.Set` on a server that did not allow it | refused with a reason | `"allowNoClip": true` in `kiln/config/rules.json`; read the result |
| Expecting NoClip to last through a death or a teleport | it ends (`PlayerNoClipEndedEvent`, `Death` / `Teleport`) and stays off | ask again: `NoClip.Set(player, true)` or `Update` on your rule set |
| Code definitions (quests, game-tree nodes, VFX) that depend on settings, other mods or the game's data, with baking on | the build bakes what the code defined under the test kit, and that file wins at start | `KilnBake=false` for such a mod |

## dev.28+ additions

| Mistake | Result | Use instead |
|---|---|---|
| Changing data the game reads once at start (Flame Altars per level, experience per level, a template's components) in `OnLoad` or a data pack | too late: the game already read it | a resource patch in `OnBoot` or `blocks/*.json` with `"boot": true` ([boot.md](boot.md)) |
| `Blocks.Define` (the run-time one) in `OnBoot` | KILN034 (error) | `BootDefinitions.For(boot).Define(...)` |
| Editing a boot mod's files or the settings its `OnBoot` read, then hot-reloading | refused: `RestartRequiredBoot` | restart the game or the server |
| `OnBoot` content that depends on a setting only the server or only the player has | the join finds the two sides' boot content differ (dev.29+) | read only shared settings; stage the same for every role |
| `MoveTo` taken as pathfinding | it walks straight at the point | `Queries.FindPath`, then walk the points |
| A voxel ray as "can it see the creature" | voxel rays see terrain and buildings, not props or creatures | combine with an entity check; `BudgetExceeded` means ask next step |
| A world query on a world a player hosts on dev.28 | held (`game test pending`) | dev.29+, or `"preview"` on a test PC only |

## dev.29+ additions

| Mistake | Result | Use instead |
|---|---|---|
| A tick-thread API in a `Systems.Wrap` stage, a `Systems.Define` handler or a same-step native | throws: they run on the game's workers once a player is in the world (a lab without players can hide it) | copy values, `slice.Post` / `NativeContext.Post` |
| `Spawn` or `Destroy` inside a wrap stage or a Kiln ECS program | refused | `slice.Post(...)` or `Context.Scheduler.Post(...)` |
| A second `Replace` on one program | refused, naming the holder | `Before` / `After`, or agree with the other mod |
| Changing a `[Query]` and hot-reloading | `Systems.Define` answers `Ok = false` | rebuild and restart the server |
| Binding a client-world program off the game's main thread | `InvalidOperationException` (KILN109 warns) | `OnLoad` or `Client.Post` |
| Expecting a client program's write to be saved or seen by others | client-world writes are marked for nothing | do it on the server, or accept it is local |
| A `RegisterSync` native or attachment that blocks | holds the game's program and the mod's unload | return at once; post the rest |
| More than 19 natives across all mods | refused with the reason | fewer natives, or one native with a mode argument |
| Many buff clones defined from code while a world runs | each gets a table of its own | a data pack, or define them before the world loads |
| Assuming a refused death keeps the entity at its old health | one or two steps at 0 health, then `SurvivalHealth` | set `SurvivalHealth`; the screen during those steps needs a client check |
| An equipment set with several direct swaps | can be refused before anything changes (`UnsupportedSwaps`) | split it into two sets |
| A resource-patch path like `components[3].isActive` | refused when a patch adds or removes components | the component's type name: `Gravity.isActive` |

## dev.29.x additions

| Mistake | Result | Use instead |
|---|---|---|
| Keeping per-base data under the base id (dev.29.14+) | the id is the smallest altar id: it changes when altars merge or split | key it by an altar's id; `BasesChangedEvent` |
| Counting `ItemCraftedEvent` twice for an automated station (from dev.29.14) | it now comes once, at the cycle's end | count once |
| `Blocks` in `KilnServices` for a mod that only automates stations (dev.29.15+) | players without Kiln cannot join its server | list `Stations` instead |
| Expecting a mod window to keep Kiln's ember look (dev.29.16+) | the default is the player's look (`UiStyle.Player`) | `Style = UiStyle.Kiln` on the options, or a theme of your own |
| Counting on Kiln's "Unknown command" reply (dev.29.17+) | an unknown `/` line now passes through to the game and other mods | handle your own commands; servers can set `"unknownCommands": "reply"` |
| `.Result`, `.Wait()` or `Thread.Sleep` in a command handler (dev.29.18+) | KILN105 warns: commands run on the tick, the server freezes | `await`, or a Kiln task |
| `KilnTask.Wait()` or `.Result` before it ended | `Wait()` is refused on game threads and in tasks; `.Result` throws | `await task` |
| `ConfigureAwait(false)` inside a task | the code after it has left the task: `WorldConcurrent` refuses it (KILN105) | drop it; `await ctx.NextGateWindow()` before the next read |
| `ctx.ToTick();` without `await` | nothing hops: the rest still runs on the worker (KILN105) | `await ctx.ToTick();` |
| Moving items or spawning inside a task | tick-only APIs throw there; an unload between take and give loses items | return a plan, carry it out in tick code |
| Keeping `KILNX003` in `NoWarn` for task-thread world API | harmless but unnecessary with dev.30.7 / mod API 0.2.16 | remove only that id; keep unrelated opt-ins |
| `WorldConcurrent` with no tick fallback | `Unavailable` on every game build without an exact profile | `WorldConcurrent.OrOnTick(ctx, offTick, onTick)` |
| `Content.Text` on a dedicated server, or off the client's main thread | null on a server | `Texts.Get` with a client catalog on a server; call it on the main thread |
| A server wrap that changes what a player's own character does, with no prediction stage | that player's own screen does not show it | the same stage with `WorldKind.Prediction` (dev.29.18+) |
| `Hooks.Mid(creativeBase + 0x15F5F7, ...)`: an absolute address inside another mod's DLL (dev.29.19+) | refused (invalid argument): hooks go into the game's code only | `Hooks.Mid(Hooks.Module("creative.dll").At(0x15F5F7), ...)`, better `Hooks.FindInModule(name, pattern)` |
| `Hooks.Module` in `OnLoad` for a DLL that loads later | `KeyNotFoundException` | `Hooks.TryModule`, again from a later tick |
| Reading Kiln's internals by reflection (`KilnText.Language`, `UiNative`, `KeyNames.PressCode`, `ModContext.Owner`) | breaks when Kiln changes them; not covered by the API promise | `KilnLanguage`, `KeyInput.Listen`, `KeyInput.PressCode` (dev.29.19+) |
| `KeyInput.Listen` for an action players should rebind | it is not on the Keys page | `KeyBindings.Register`; `KeyInput` for your own capture field or state |
| Slow work in a `KeyInput` handler or `KilnLanguage.Changed` | runs on the game's window or render thread: the game stalls | copy the press, post the rest |
| `KILN_TASK_RESIDENT` alone, or a second resident task per owner (native) | `INVALID_ARGUMENT` | with `KILN_TASK_LONG_RUNNING`, one per owner; retry without the flag on an older Kiln |
| `File.ReadAllText` on the harness's kiln.log (Kiln.TestKit) | fails on Windows while the harness writes it | `kiln.ReadLogFile()` (dev.29.19+) |
| `read_original` / `code_state` on an address in an opened module (native) | `KILN_ERR_INVALID_ARGUMENT`: they cover the exe's `.text` only | `module_scan` for the module; compare its bytes yourself |

## dev.32 additions

Pets, taming, NPC dialogue, the new registries and exact costs are on a test build and have not been seen in a real game: report what
only a player can see as "needs a client check". Server and client, edit caps and timing are where these bite.

| Mistake | Result | Use instead |
|---|---|---|
| `Pets.*` (except `Define`, `Definition`, `Definitions`) or `Taming.*` on a joined client, off the tick thread, or in a Kiln task | `KilnUnavailableException` (a joined client has no server rows) or a wrong-thread error | server mod, tick thread; ask `Capabilities.Is("pets.records")` / `Is(Taming.Capability)` first; `await` the `Task<PetResult>` from a handler |
| One `PetRequest` or `TamingRequest` reused for a new action | `RequestConflict`; or the first answer given back for 10 minutes | `PetRequest.New()` and `TamingRequest.New(progress.Token)` for each action you mean; the same one only to resend a lost request |
| A pet whose creature is wildlife, a farm animal, a flyer, a template name or a GUID | `CreatureUnsupported` / `CreatureUnknown`; or `ArgumentException` | a creatures-registry id (`game:enemy_...`) or your creature block; only enemies that walk are pets |
| A pet or taming id with capitals or without your lower-case mod id | `ArgumentException` naming the field | `<mod id>:<name>`, lower case letters, digits, `-`, `_`, `.` |
| Expecting a pet to fight, assist or defend by itself | it only follows: Kiln never attacks by itself | `Pets.Command(..., PetOrder.Attack, target, ...)` when your rule says so; read `TargetProtected` |
| `Pvp = ByModRule` and nothing else | players stay protected: no pet fights another player | `Pets.SetHostile(a, b, true)` per character pair, when both agreed; `false` ends it |
| Keeping a pet's body `EntityRef` | the body is made and retired by summon, down, dismiss and revive | keep the `PetId`; read `PetSnapshot.Body` when you need it (none unless Live) |
| A revive cost of gear, an item the game lacks, or a count split over two stacks | `CostUnavailable` or nothing paid; the revive is refused with nothing taken | one plain stack item (1..999) and a stack that holds all of it |
| Defining a taming with `game:` item ids in `OnLoad` of a dedicated server | the definition is not made (`DefinitionUnknown`, or an `ArgumentException` naming `$.steps[1].item`) | a `TamingBlock` (Kiln waits for the catalog), or define from a tick once `Items.Available` |
| A client or UI that names a slot id for a cost | there is no such call: the server must read the slot | `Taming.Choices(...)` rows or `ItemSelection.Of` of an `Items.Read` slot, built on the server |
| Calling `Advance` with an old token | `StaleStep`, nothing taken | keep the last `TamingResult.Progress` and make the next request on its token: `TamingRequest.New(progress.Token)` |
| Matching a pet or taming refusal by its text (`Reason.StartsWith("Cap")`) | words change between releases; a new word reads as nothing you know | `r.Refusal == PetRefusal.Capacity` (`TamingRefusal` for taming): the same words in both services, `Other` for a word a later Kiln adds |
| Resubmitting a cost whose receipt is `Uncertain`, or refunding it | it may be paid twice; `Refund` never refunds an uncertain receipt | read `CostReceipt.State`; treat uncertain as paid until you know |
| Promising a player who left a refund | it is not settled for an owner who is gone | tell players refunds are for players in the game |
| Reading `NativeTamingState.TamedBy` | always null: the game records no tamer | decide credit in your mod |
| `Npcs.*` guarded by `Npcs.Available` or `Capabilities.Is("npcspeech.bubble")` | false on a dedicated server, so a guarded mod shows nothing to anyone | call it; the server still sends to Kiln players |
| A line over 200 characters, 2048 bytes for all languages of one line, 33 `Replace` lines | `ArgumentException` at the call | shorter lines; keep names and timers that change often out of texts (4,096 distinct texts per world) |
| `SpeechPick.By` reading state that changed, with no `Refresh()` | players see the answer given one open ago | `Refresh()` after the state changes; the callback is on the host's tick thread |
| Silencing a farewell or the menu line, a topic with `Close` and no `Reply`, 17 topics | there is no `Suppress` on `NpcLines` (it does not compile); `Ok: false`; the extra topics are left off until there is room | `Replace`/`Clear`; give `Close` a `Reply`; at most 16 topics per NPC across all mods |
| An NPC mod that decides on both host and client | two answers, one per side | decide on the host (`Lifecycle.IsServerRunning`); players need Kiln 0.2.0-dev.32 for the menu parts |
| `Registries.Reauthor()` from inside `OnRegistry`, or without a `WorldLoad` point | the result is not Ok (`Reason`, also in kiln.log): nothing re-authored | ask from the handler that saw the setting change, with `[RegistryEntryPoints(RegistryEntryPoint.WorldLoad)]`, and read the result |
| A tick-thread API (`Game.AssertTickThread`, a service's own check) inside a re-authored `OnRegistry` | `InvalidOperationException` ("runs off it"); the mod's passes then run on the tick, slower | read settings and game state before asking, in fields; `[AnyThread]` APIs (`Players.All`, `Progression.Get`) are fine |
| More than the cap of edits of one registry in one call (4,096; `randomLoot` 16,384) | the whole call is refused (kiln.log, `registry.json`), never thrown; your other calls still apply | count fields not entries; split over `Alter` / `Final` or several `Registry.Change` calls; read `RegistrySchema.EditsPerCall` |
| `world`, `difficulty`, `limits` or `balance` edits with `"clients": "none"` or a server-only mod | joined players' boot values differ from the server's; the join compares and warns | `GameStart` only, `"clients": "mod"`, `"side": "both"`; `kiln lint` says KL213 |
| A relative edit (`mul`) over creatures with `game:*` | refused whole: most creatures lack sight or an arsenal | select `"has": ["enemy"]` or ids; in C# tell creatures by `Kind`, never by a 0 (`Resist.Fire == 0` means immune) |
| A random-loot table id copied from another build, or one `game:world_*` id | a table whose first chest changed has a new id; edits naming the old one are refused | select by prefix (`game:labels_*`) and by values (`table.Rarity.Legendary.Mode == RandomLootMode.Set`) |
| `RegistryViews` first read in a tick | 4 to 11 s for a registry's first read, 0.2 s to list an advanced view | read off the game's thread (`Task.Run`); views never write |
| Editing the `gear` section through registry fields to "port" it | `levels`, a relative power with `levels`, save checks and pack order can change what the rule means | keep `gear` a data pack; `gearRules` keys are the `balance` fields and Kiln already writes them there |
| Promising custom terrain tiles | no API: dev.32's tile work is tools only and `terrain.baked` stays off | `Terrain.Apply` overlays |
| `"native": true` in the manifest of a mod meant for older Kilns | before dev.32 an unknown key warns and the mod is not opted in | keep `"advanced": true` (still works, no removal date) |
| `LabBots` (Kiln.Players, experimental KILNX001) in a shipped mod | lab API: may change or go in any release | use it on your own test server only |
