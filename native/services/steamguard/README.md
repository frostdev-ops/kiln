# kiln.steamguard: the achievement guard

`kiln.steamguard` (client only) drops the game client's own call that reports an achievement to Steam while the guard
is on, so a character or world changed by a test or an admin command cannot unlock an achievement on the player's
Steam account. An unlocked Steam achievement cannot be taken back, so every doubt reads as "not protecting".

- Contract: `native/core/abi/services/kiln_steamguard.h`
- Signatures: `sigdb/symbols/client.d/steamguard.json` (feature `steam.achievement_guard`)
- Managed face: `managed/services/Kiln.Client/SteamGuard.cs` (`Kiln.SteamGuard`)
- Host test: `steamguard_test.cpp` + `test-steamguard.sh` (no game, no Wine)

All addresses below are RVAs in the game client of Steam build 26430625 (optional-header image base `0x140000000`;
subtract it from the strings' `.rdata` positions). They were read from the client executable with a PE reader and
capstone; nothing read from the executable is committed except the pattern, offsets and short descriptions sigdb
already carries for every other symbol.

## The target

`steamguard/ReportAchievements` at `0x1088440` is the Steam-achievement provider's apply method (the game's own
function, provider vtable slot 6 at `.rdata 0x1dd0c78`). It is reached only through that vtable (two call sites,
`0x1086352` and `0x1086436`, both in the provider-slot wrappers `0x1086310` / `0x1086400`). It walks the queued
achievement/stat operations (`r8` = `{array ptr, count}`) and is the one place that calls, through the cached
`ISteamUserStats` interface at provider+`0x78`:

- `SetAchievement` (interface vtable +`0x30`) at `0x1088b3e`, and
- `StoreStats` (interface vtable +`0x48`) at `0x1088c29`.

So dropping this one function drops every achievement and stat write the game would send to Steam for the op queue.

The `ISteamUserStats` interface is `STEAMUSERSTATS_INTERFACE_VERSION013` (string `.rdata 0x1532098`), fetched by
`SteamInternal_FindOrCreateUserInterface` at `0x4b9d30` and kept in the game's Steam wrapper at wrapper+`0x48`; the
provider caches it at provider+`0x78` in its init `0x1085710`.

The success log the real game logs, `[online] Achievement '%s' unlocked.` (`.rdata 0x1dd1130`), is emitted by a
different, per-achievement notifier `0x1086d90` that runs **after** Steam has stored the achievement. That is too
late to protect an account, which is why the guard hooks the write at `0x1088440`, not the notifier.

### How the hook goes in, and why it is safe to place

The entry of `0x1088440` is `40 55 53 41 55 48 8d ac 24 90 fe ff ff 48 81 ec 70 02 00 00 …` (push rbp; push rbx;
push r13; lea rbp; sub rsp): five relocatable bytes of plain pushes at the entry, so kiln.hooks (safetyhook) can
place an inline detour there. kiln.steamguard installs a `kiln::RawHook` on the first `latch` or `hold`, never at
boot. While the guard is on the handler returns without running the chain or the real function; while it is off it
passes the call through and counts it.

### What a dropped call returns

The handler returns `0`. `ReportAchievements` itself returns the accumulator byte it builds while walking the batch;
on its normal path, when no achievement newly unlocks, that byte is `0`. Returning `0` is therefore the function's own
"ran, nothing to report" value: it does not take the failure-log paths (`[online] Achievement '%k' couldn't be
unlocked.`, `[online] Attempt to store changes failed.`) and it does not take the provider-not-ready retry path, which
returns `0x18`. **What only a real client can confirm** is that the game then drops the queued operation instead of
re-submitting it; the first-run watch (below) covers it.

## Target verification (the three signs)

`TargetVerified` is true only when, at boot and before anything is installed:

1. `steamguard/ReportAchievements` resolved to exactly one match (the core's uniqueness rule);
2. the live bytes at the target are the ones the pattern names (the service re-reads them; if another engine hooked
   the function before Kiln, the live bytes are its jump and the check fails — fail closed); and
3. the second, independent sign holds: `steamguard/AchievementUnlockText` resolves to the `[online] Achievement '%k'
   couldn't be unlocked.` string (`.rdata 0x1dd0f10`), captured off the `SetAchievement` call site
   (`48 8B 01 FF 50 30`), and the service reads it back and confirms it begins `"[online] Achievement"`. This proves
   the function both calls Steam's achievement slot and is the achievement reporter.

The second sign is re-checked on every `Status` read. After the detour is in, the target's first bytes are Kiln's own
jump, so the byte check is a boot-time check; its "still holds" part is the foreign-over test: `HookInstalled` is true
only when kiln.hooks reports our link in place and the hook map shows no `foreignOver` on the target.

## Answers to the reverse-engineering questions

**Is this the only path from the game to Steam's achievement and stats calls?**
For achievement and stat **writes**: yes. I scanned every virtual call made through the stats interface pointer
(provider+`0x78`) in the provider module: `SetAchievement` (+`0x30`) and `StoreStats` (+`0x48`) are called only inside
`ReportAchievements` (`0x1088440`). Every op producer (the knowledge collector `0x86f580`, the pending-op vector at
image+`0x47d570`) funnels through the provider queue and then this one apply method. *Read from the code.* The other
Steam-stats touches I found: (a) the `onUserStatsReceived` callback `0x1086e20` calls `ResetAllStats` (interface
+`0xa0`), but only when a developer "VERSION" stat is set, and it is a reset, not an unlock — the guard does **not**
cover it and does not need to, to protect against an unwanted unlock; (b) provider init and the callback **read**
current stats (`RequestCurrentStats`, `GetAchievement`/`GetStat`) — read-only. A `+0x78`/`+0x48` pair at `0x10232f4`
and `0x1024bad` is an unrelated stream object (int results, seek arguments), not the stats interface — verified by its
different field. *The negative ("no other path") is read from a full scan of the provider module's calls through the
stats interface; that some other module inside the exe reaches Steam by a path I did not enumerate can only be fully
ruled out in a running client — inferred.*

**Does the game evaluate achievements from the loaded character/world when it loads, or only when something happens?**
On load. The achievement ops are produced by the knowledge collector `0x86f580`, which reads the player's knowledge
and submits unlock ops for reached achievements whenever the knowledge "dirty" flag (knowledge-context +`0xd3`) is set.
That flag is set by the knowledge-mutation paths (`0x121c320`, `0x121fef8`), which also run as a character's saved
knowledge is populated on load; `docs/re/world-status.md` and `docs/re/quests.md` confirm the `Achievement_*` queries
read **saved** knowledge, and a client log shows an unlock 0.2 s after entering the game. **So a character whose saved
knowledge already satisfies an achievement can report it to Steam when the character is loaded later, with nothing
else happening — therefore a character that a `cheats` check changed is dangerous to load later without the guard.**
*Read from the code for the collector and the dirty-flag path; inferred for the exact load-time trigger — the
definitive proof is a client (see the first-run watch).*

**Does the game ask Steam whether an achievement is already unlocked before it reports one?**
It keeps a local mirror of Steam's state (`RequestCurrentStats` on init) and consults it for stat **increments** (the
`[online] Couldn't increase value … because the current status is not known` path), but for a plain achievement
**unlock** op it calls `SetAchievement` regardless of the mirror. Steam's own `SetAchievement` is idempotent: setting
an already-unlocked achievement is a no-op and fires no new unlock. *Read from the code for the local-mirror path;
Steam's idempotency is Steam behaviour, inferred.* This is why a level whose achievement the account already has makes
a harmless first run.

**What a game update does to the symbol, and the cost of a second layer on Steam's interface.**
The entry pattern keeps the prologue literal and wildcards only the two frame displacements, so a recompile that only
moves the function or resizes its frame still matches; sigport re-derives it when the prologue or the provider-ready
call changes, and a whole rewrite of the achievement provider needs a fresh signature. The second sign (the
`SetAchievement` call site plus the `[online] Achievement` string) is tied to Steam's vtable slot `0x30` and the log
text and survives most updates. A second layer on Steam's interface would instead hook the imported
`SteamInternal_FindOrCreateUserInterface` (creation already located at `0x4b9d30`) or the `ISteamUserStats` vtable
slots once the interface exists, and drop `SetAchievement`, `StoreStats` and `ResetAllStats`; it is about a day of
work, is more robust across game updates (the interface version changes rarely) and would also catch the `ResetAllStats`
path this layer does not, but it hooks Steam's DLL boundary, which the brief deprioritises. It is not built.

## The first real run

You cannot prove the guard in a client from here: a dedicated server has no Steam user, so the host test (with a
stand-in game function and hook registry) is the most that runs offline. The first real run must show, with the guard
latched (`SteamGuard.Latch` returned true and `SteamGuard.Protecting` is true):

- **no new `[online] Achievement '…' unlocked.` line** in the game log for the change, and
- the **Dropped** counter rising (the kiln.log line `steamguard: dropped a Steam achievement/stat report …`).

Make that first run harmless: pick a level or change whose achievement the account **already has**. Steam no-ops an
already-unlocked achievement, so even if the drop did nothing, nothing new is unlocked. Only after that run passes
should a `cheats` check that could unlock a *new* achievement be allowed.

## What the guard does not cover

- An achievement already reported before the guard went on (it cannot be taken back).
- `ResetAllStats` (interface +`0xa0`, developer-gated), and any path a mod or another loader makes into Steam itself.
- A game build whose `ReportAchievements` does not match the signature — then the guard reports not available.
- A client with no Kiln, or with the guard off: if a changed character's saved state reaches an achievement, the game
  can report it. On a dedicated server the affected player's own client must latch (the `Kiln.Network` relay); a
  client without Kiln cannot be protected, and the admin command's reply says so.
