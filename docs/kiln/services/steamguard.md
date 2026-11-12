# kiln.steamguard / SteamGuard: the achievement guard

While the guard is on, the game client's own call that sends achievement unlocks and stat changes to Steam is dropped
and counted, so a test or an admin command cannot unlock achievements on the player's Steam account. An unlocked Steam
achievement cannot be taken back, so every doubt reads as "not protecting".

**Status (dev.26):** experimental (`KILNX002`). The service is tested on the host with no game, the relay in unit
tests; a dedicated lab server skips the service as client-only. **Not run in a real game client yet**. Do not rely on it to protect a Steam account until the release notes say the
guard-proof client check has passed.

| Layer | Where |
|---|---|
| C table | `native/core/abi/services/kiln_steamguard.h` (`kiln.steamguard` v1) |
| Native | `native/services/steamguard/` → `kiln/services/kiln-steamguard.dll`. Game client only: a dedicated server skips it |
| Symbols | `sigdb/symbols/client.d/steamguard.json` (feature `steam.achievement_guard`) |
| Managed | `managed/services/Kiln.Client/SteamGuard.cs` (`SteamGuard`, `SteamGuardStatus`); the server's relay `managed/services/Kiln.Network/SteamGuardRelay.cs` (`SteamGuardRelay`, `GuardOutcome`) |
| Evidence | `native/services/steamguard/README.md`: how the client reports an achievement, what was read from the code and what is inferred |
| Tests | `native/services/steamguard/test-steamguard.sh` (host, no game, no Wine); `managed/tests/Kiln.Network.Tests/SteamGuardRelayTests.cs` |

## C#
A project that calls it opts in with `NoWarn` for `KILNX002`.
```csharp
if (SteamGuard.Latch("my test changes levels")) { /* protecting until the game exits */ }
if (SteamGuard.TryHold("one admin action", out var hold)) using (hold) { /* protecting while held */ }
using (SteamGuard.Hold("must be protected")) { /* throws KilnUnavailableException when it cannot protect */ }
SteamGuardStatus s = SteamGuard.Status;   // Available, HookInstalled, TargetVerified, Active, Dropped, Passed, Reason
bool now = SteamGuard.Protecting;          // all four of Available, HookInstalled, TargetVerified and Active, in one read
```
- `[ClientOnly]`. On a dedicated server there is no Steam user and the guard is never available.
- `Status` and `Protecting` work on any thread and never throw. The first `Latch`, `TryHold` or `Hold` puts the hook in:
  call them on the game tick thread, never the render thread.
- `Latch` and `TryHold` return false when the guard cannot protect (the reason is in `Status.Reason`); on false nothing
  is held. `Hold` throws `KilnUnavailableException` instead: `steam.achievement_guard` when the service is not there
  or its target is not verified, else `steamguard.protecting` (the hook did not go in, or another hook is on the call).
- There is no call that turns the guard off. A latch lasts until the game exits; a hold lasts until it is disposed (two
  holds need two disposals). Another mod cannot end yours. A check or install that failed leaves it not protecting
  until the game exits.

## Players on a server: `SteamGuardRelay`
The guard that matters is the one in the process that talks to Steam: the player's own client. Before a change that
could unlock an achievement, a server asks that client to latch:
```csharp
GuardOutcome g = await SteamGuardRelay.LatchFor(player.Entity, "admin gives xp");   // or LatchForAll(reason)
if (SteamGuardRelay.Note(g) is { } note) reply += " — " + note;
```
- `[ServerOnly]`; start it on the server tick thread. On a hosting client the host's own player is latched locally. A
  joined player's client is asked over kiln.net and given 3 s to latch and answer.
- `Protected`: the client latched and reports protecting. `UnprotectedFailed`: it was asked but could not protect,
  left, or did not answer in time. `UnprotectedVanilla`: it has no Kiln or no guard and cannot be protected.
- The change still runs for an unprotected player (an admin may level players); the reply says so. Kiln.Admin's level,
  XP, skill point, skill, knowledge and mod-quest operations do this.

## Capability rows
- `steam.achievement_guard`: the sigdb feature (static), available when the client's achievement call matched its
  signature on this build.
- `kiln.steamguard`: the service is here.
- `steamguard.protecting` is the name `Hold` throws with when the hook is not protecting. No row of that name is
  registered yet: `Capabilities.Is("steamguard.protecting")` is false. Read `SteamGuard.Protecting` instead.

## What it does not cover
- An achievement already reported.
- The game's own reset of all stats (it removes, never unlocks).
- Calls into Steam that another mod or loader makes itself.
- A game build whose achievement call does not match the signature: then the guard is not available.
- A character or world loaded later in a game where the guard is off. The game reports achievements from saved state,
  so a character changed while the guard was on can unlock them when it is loaded again.
- A player whose client has no Kiln.
- `/questadmin` (Kiln.Quests): its complete, advance, step, start, fail and signal paths can give quest rewards and do
  not ask the players' clients to latch first.
- Rewards delivered later: a reward that waits for an offline recipient runs when that player joins, after any latch
  request made at command time; nothing asks that player's client to latch then.
