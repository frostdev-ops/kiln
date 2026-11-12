# kiln.steamguard: the achievement guard

`kiln.steamguard` (game client only) drops the game client's own call that reports achievements to Steam while the
guard is on, so a character or world changed by a test or an admin command cannot unlock an achievement on the
player's Steam account. An unlocked Steam achievement cannot be taken back, so every doubt reads as "not protecting".

- Contract: `native/core/abi/services/kiln_steamguard.h`
- Signatures: `sigdb/symbols/client.d/steamguard.json` (feature `steam.achievement_guard`)
- Managed face: `managed/services/Kiln.Client/SteamGuard.cs` (`Kiln.SteamGuard`)
- Host test: `steamguard_test.cpp` + `test-steamguard.sh` (no game, no Wine)

Addresses are RVAs in the game client of Steam build 26430625 (image base `0x140000000`). They were read from the
client executable with a PE reader and capstone. Each answer below says whether it was **read from the code** or is
**inferred**.

## How the client reports an achievement

1. **Collect** (`0x86f580`, called from the client's update `0x876880`; inferred: the main thread). It runs when the
   local knowledge context's "changed" byte (+`0xd3`) is set (`0x121eb30` reads and clears it). It walks the
   knowledge resource's achievement list (8-byte entries: achievement id, knowledge id; `0x121ab20`) and takes every
   achievement whose knowledge value is set. Read from the code.
2. **Submit** (`0x1087b60`, its only call site is in the collector): one unlock operation per id (`{id, type 0,
   value}`), queued as a task on the achievement system's task queue (system+`0x60`). Read from the code.
3. **Apply**: the task (`0x1086310`) calls the provider's slot 6 (vtable `.rdata 0x1dd0c48`, slot at `0x1dd0c78`):
   **`steamguard/ReportAchievements` at `0x1088440`**, `uint8 (provider*, uint32 user, {ops*, count}*)`. It runs on
   the task queue, not the main thread (inferred from the queue; the function waits up to 10 s for Steam's callbacks,
   which the main thread could not afford). For each operation it looks the id up in the provider's table and calls,
   through `ISteamUserStats` (provider+`0x78`): `SetStat` (+`0x18`) for stat-backed ones, `GetAchievement` (+`0x28`),
   and `SetAchievement` (+`0x30`) only when `GetAchievement` said "not unlocked". Then `StoreStats` (+`0x48`) once
   if anything changed, and it waits for the store callbacks. Read from the code.
4. **Result**: the task stores the returned byte with the operation (`request+8`); the client's poll (`0x871850` via
   `0x10866e0`) frees each finished operation and never reads that byte. Read from the code.
5. **Log**: `[online] Achievement '%s' unlocked.` comes from the `UserAchievementStored_t` callback (`0x1086d90`)
   **after** Steam stored the unlock, and `[online] User stats stored.` from the stats-stored callback. Both are too
   late to protect anything, which is why the guard hooks step 3. Read from the code.

The dedicated server cannot unlock a player's achievements: it asks Steam only for `SteamGameServer015`, networking,
utils and matchmaking interfaces. `SteamGameServerStats001` appears only in the interface version list passed at
init and is never requested. Read from the server executable's code.

### What a dropped call returns

`0`. The function's result byte is its first error code, `0` when every operation went through. The game's null
provider (vtable `0x1dd0c80`, used when no online provider exists) answers the same slot with `xor al, al; ret`.
Because the poll never reads the byte (step 4), no value makes the game retry; `0` also keeps the game off its error
paths. A dropped batch is not re-queued by the game. The collector submits again the next time the knowledge context
changes, and while the guard is on that batch is dropped and counted too.

## Target verification

`TargetVerified` is true only when all of these held at boot (before anything is installed) and the last one still
holds when asked:

1. `steamguard/ReportAchievements` resolved, by the core's rule of exactly one match;
2. the live bytes at the target are the bytes the pattern names (the service reads them; an engine that hooked the
   function before Kiln leaves a jump there, and the check fails);
3. the second, independent sign: `@steamguard.sign` matches inside the function at its `SetAchievement` call
   (`49 8B 4D 78 … 48 8B 01 FF 50 30`, interface at +`0x78`, slot +`0x30`) followed by the failure log's string, and
   captures that string as `steamguard/AchievementUnlockText`. The service reads it back and requires the text to
   begin with `[online] Achievement`. Re-checked on every status read.

`HookInstalled` is true only when the guard's own install succeeded, the target still begins with the jump the
install left, and kiln.hooks' hook map lists a live link labelled `steamguard/ReportAchievements` on the target with
neither `foreignBefore` nor `foreignOver`. A map that cannot be read counts as "not installed". A lost link or a
foreign hook fails the guard until the game exits. The map is re-read at most every 500 ms; a negative answer is never
cached.

## Answers

**Only path to Steam's achievement and stats calls?** For writes, yes in this exe: every call through the stats
interface in the provider's code is listed above. `SetAchievement`, `SetStat` and `StoreStats` occur only in
`ReportAchievements`. Read from the code (a scan of every virtual call through provider+`0x78`, and of the whole
code section for those slots through +`0x78` and +`0x48` holders; the two other hits, at `0x10232f4` and `0x1024bad`,
are a stream object with integer results). The interface is fetched once (`0x4b9d30`, version 013) and its getter
`0x4ba750` has one caller, the provider's init. Other calls into the stats interface:
- `RequestUserStats` (+`0x78`) in the provider's init `0x1085710`: a read; not covered, nothing to cover.
- `GetStat` (+`0x8`) and **`ResetAllStats(true)`** (+`0xa0`) in the stats-received callback `0x1086e20`: when the
  account's `VERSION` stat is non-zero the game resets all stats and achievements. It removes, never unlocks, and does
  not depend on a save; the guard does not cover it.
- There is no `IndicateAchievementProgress`, `ClearAchievement` or `UpdateAvgRateStat` call. The game's own progress
  stats go through `SetStat` in `ReportAchievements`, which the guard drops.

Not ruled out: code that reaches Steam by an indirect path these scans do not see (an interface copied into another
object and called through a register). Inferred: none. Other mods and loaders calling Steam are outside the guard.

**Evaluated from the loaded character, or only at the moment something happens?** From the state. The collector
takes every achievement whose knowledge is set, whatever set it and whenever, each time the knowledge context changes.
Knowledge replication marks the context changed (`0x41e2b0` → `0x121c320`; `0x121fe00` when a value changes). Read
from the code. That this runs once when a character is loaded or joins a world is inferred: the synced knowledge
arrives then, and an older client log shows an unlock 0.2 s after `toggleHudScreen to InGame` with nothing else
happening. **So a character changed by a `cheats` check is dangerous to load later without the guard**: if its saved
knowledge reaches an achievement, the next game that loads it reports it.

**Does the game ask Steam whether an achievement is already unlocked?** Yes: `GetAchievement` (+`0x28`) before every
`SetAchievement`, and an unlocked one is skipped (`0x1088a94`, `0x1088b2d`). It reads the Steam client's copy of the
account's achievements. Read from the code. An achievement the account already has is never set again.

**A game update, and a second layer on Steam's interface.** The entry pattern keeps the prologue and the
provider-ready check literal (`mov rcx,[rcx+0x70]; call [rax+8]; … mov al,0x18`) and wildcards the frame sizes and one
branch, so a rebuild that only moves the function or resizes its frame still matches. A change to the provider class
(field offsets, vtable slots) or to the function's first instructions breaks it; then the symbol does not resolve, the
feature is off and the guard reports "not available", never "protecting". sigport ports it like any other symbol. The
second sign depends on the interface slot `+0x30`, the provider's +`0x78` and the failure string, which are stable
unless Steam's interface version changes. A second layer on Steam's side would wrap the `ISteamUserStats` object the
game receives from `SteamInternal_FindOrCreateUserInterface` (one import, called through the context-init callback
`0x4b9d30`) and filter `SetAchievement`, `SetStat`, `StoreStats` and `ResetAllStats`. That is a hook on steam_api64's
interface, about a day of work with its own test, independent of the game's code (so it survives game updates that
move this function), but it touches Steam's DLL boundary and its vtable layout per SDK version; it is not built.

## The first real run

The guard cannot be proven from here: a dedicated server has no Steam user, and the host test uses a stand-in game
function and a stand-in registry. The first run in a real client must show, with `SteamGuard.Latch` returned true and
`SteamGuard.Protecting` true:

- no new `[online] Achievement '…' unlocked.` line in the game log for the change, and
- the `steamguard: dropped a Steam achievement/stat report …` line in kiln.log, with `Dropped` going up.

Make that first run harmless with a change that can only reach achievements the account already has: because the game
calls `GetAchievement` first and skips unlocked achievements, such a change reports nothing new even if the guard did
nothing, yet the batch still reaches `ReportAchievements`, so `Dropped` must rise. For example, a level one up on a
character whose level the account has passed before, below the next level achievement the account lacks. A single
level jump can reach several achievements at once, so check the account's list before choosing. Grant no knowledge in
that run.

## What the guard does not cover

- An achievement reported before the guard went on.
- `ResetAllStats` in the stats-received callback, and every call a mod or another loader makes into Steam.
- A build whose function does not match the signature: the guard is then not available.
- A changed character or world loaded later in a game where the guard is off: its saved knowledge can report
  achievements then.
- A player whose game has no Kiln. A dedicated server can only ask a player's own client to latch (Kiln.Network's
  `SteamGuardRelay`); a client without Kiln cannot be protected. The admin command still runs and its reply says
  "this player's game has no Kiln achievement guard: Steam achievements may unlock".
- Kiln.Quests' own `/questadmin` commands are **not covered**: complete, advance and step can pay quest rewards
  (experience or knowledge); start/fail actions and signals can do so too. Do not use these on an account that needs
  protection. The routed Kiln.Admin bridge patch latches before its `modquest_set` and `modquest_signal`, but does not
  change the service library's commands. Deferred rewards paid to a player who joins later are not covered.
