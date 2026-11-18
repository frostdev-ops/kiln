# Gem Forge candidates

`Ui.Values.GemForgeCandidates()` observes the game's returned **GEMS** list for the local player's inventories.
It includes rows outside the visible scroll area. It does not search nearby Magic Chests. Membership does not say
whether an action is allowed, compatible, enabled, or will succeed. These client identities are not authority for
server actions.

```csharp
if (Capabilities.Is(Ui.Values.GemForgeCandidatesCapability))
{
    var result = Ui.Values.GemForgeCandidates();
    if (result.Ok)
        foreach (var row in result.Snapshot!.Candidates)
            Log.Info($"{row.Index}: {row.DisplayedGem.Item}");
}
```

The API requires `ui.gemforge.inspect`, is client only, and may be called from any thread. Missing capability,
service or appended native table throws `KilnUnavailableException`. A closed or undrawn GEMS grid returns
`Ok = false`, a reason, and no snapshot. An empty returned list is a successful snapshot with zero candidates.

**Which gems are listed.** The GEMS grid lists the gems in your backpack and action bars, then the weapons there that
hold a gem (shown with the gem), in every forge mode; on a Kiln client where a mod allowed sockets on other gear
(`Gems.AllowSockets`), also the rings, armour pieces and shields there that hold a gem, as carrier rows like a weapon
([services/gems-sockets.md](services/gems-sockets.md)). Gems in chests and in worn items are not in the game's list (since
0.2.0-dev.29.15 a server can offer chest gems, which Kiln adds to the grid: [below](#chest-gems-in-the-gems-grid)). (The game's
list builder has one caller, and it always asks for socketed weapons too: there is no mode that hides them. A Recycle
removes a row for good; an Insert keeps the count, the loose gem becomes its weapon's row.) More on gems:
[services/gems.md](services/gems.md).

Each immutable `GemForgeCandidate` distinguishes:

- `SourceRow`: the returned row view and its selection key (page and slot).
- `DisplayedGem`: the displayed gem and its actual source page and slot.
- `WeaponCarrier`: the weapon view when the gem is already socketed; otherwise null. Its row key is the weapon's
  inventory slot, while the gem source is the weapon's client entity with slot zero.

The snapshot includes the selected row (`SlotId?`), associated selected weapon (`UiItemView?`), the native mode byte
(0 through 4), local player, session, frame, capture time, and a Kiln generation. All snapshots describe the GEMS role;
the WEAPONS grid is not captured. Selection describes the context at grid entry. Eligibility and disabled state
are unknown. Native producer allocation failures are not exposed by the game, so this is not a certified complete
inventory enumeration.

`SubscribeGemForgeCandidates(onFrame)` returns a `UiValueRegistration`. It calls `onFrame` on the client main thread
after each completed UI frame, including unavailable frames. Keep callbacks short. The copied snapshot is yours to
keep. Dispose the registration when finished; mod unload also removes it.

Kiln retains two copied frames for a sizing retry. A mode, selected identity, list, screen or session change invalidates
old generations. A selection change in either grid or a later menu action discards that frame; the next coherent draw can replace it.
Closing the selector, an absent GEMS draw, capability loss, a newer completed frame without a matching capture, or an
age over 1000 ms makes the current result unavailable. Saved snapshots remain historical values.

This feature initially supports only measured client build 26430625
(SHA-256 `5d9ef2f140b8ed49e30dbee655841b89d08baec20f799a1817e171a18a850c77`). It needs `ui.screens` and Kiln's immutable
item catalog to distinguish loose gems from weapon carriers. Hooks install on first read or subscription. Unsupported
signatures, layouts, call edges or hook contracts leave the feature off. An unreadable or incoherent list disables
only this inspection feature for the process and records a reason in `kiln.caps` and `kiln/state/capabilities.json`.
The snapshot is the game's own list: chest gems a server offered ([below](#chest-gems-in-the-gems-grid)) are not in it.
This read API itself has not been checked in a real game client yet (the chest-gem test below did not call it).

## Chest gems in the GEMS grid
Since 0.2.0-dev.29.15 (GitHub issue 61), capability `ui.gemforge.sources` (client). A server mod offers a player the
loose gems of containers the player may use ([services/gems-forge.md](services/gems-forge.md#chest-gems-in-the-forges-own-gems-list-since-020-dev2915));
Kiln's server half sends the offers to the player's Kiln client, and Kiln lists them in the game's own GEMS grid, after
the player's own gems, drawn as the game draws a gem (icon, level, rarity). There is no client API to call: a client
needs Kiln, not the offering mod.

What the client does, on measured client build 26430625 only (static analysis; see docs/re/gems-forge.md):
- The GEMS grid draws a copy of the game's list with the offered rows appended; the game's own array is not touched,
  and a slot the player's list holds already is not added twice.
- A selected chest row resolves to the offered gem's view (the game would find no view for a key that is not the
  player's), so the selection, the detail panel and the action buttons work as for a gem of the player's.
- When an offer's payer is not the player (a chest, base storage), the Insert and Upgrade price tests add what that
  payer holds of the forge's own upgrade material, as the server last said (the only material payers other than the
  player pay in): the button shows what the server will be able to charge. Picture only: the server pays from the
  payers or refuses.
- A row is listed only when the client's own item catalog knows its item as a loose gem (WeaponGem); a row of an
  unknown item (or without a catalog) is dropped.
- The action sends the game's own request; it names the chest gem's real slot. The server checks the offer, the slot,
  the gem and its level, the payer and the transaction; a refusal leaves the screen as it was and Kiln shows a toast with
  the reason.

Safety: the four hooks (a link on the GEMS grid, a mid hook on the selected-row resolve, two on the price tests) go in
only when a server sent this player rows, after each site's instruction bytes were compared with the measured ones; a
mismatch (another game build, another tool's hook there) turns the row off for the process and the forge stays the
game's. While no row is live every hook returns at once. Kiln's client does no per-frame work for this until the forge
screen opens (it watches screen changes; the frame watch for the 5-second keep-alive runs only while the screen is open). Rows belong to the client world they came in (a new session or
local player drops them) and lapse with their offer (30 s by default; the server sends them again every 5 s while the
screen is open). A client without Kiln, an older Kiln, or a Kiln client on a server that never offers sees the game's
own forge.

## Real-client checks
**Seen in a real game.** bisdat ran the chest gems in a real game client on 2026-11-17 (GitHub issue 61): Kiln
0.2.0-dev.29.15, Bazzite Linux with Steam's Proton, a world hosted on that PC, and their server mod "Gem Forge Magic
Storage" v1.0.0 offering a base's Magic chests. Passed: the 28 loose gems of one Magic chest listed as rows in the game's
own GEMS grid; a chest gem selected and socketed into a weapon the player held, with the game's controls and without
the gem passing through the backpack; unsocketing it to the backpack; an upgrade of a chest gem (43 to 44) with the
172 Archaic Essence VII paid from the Magic chests and none in the backpack; a refused upgrade when the essence was
short, with nothing lost; recycling a chest gem; and withdrawn offers leaving the grid while the player's own gems stayed.
Server side: [services/gems-forge.md](services/gems-forge.md#seen-in-a-real-game-020-dev2915).

**Not seen in a real game yet:** an action on a stale row (step 6 below), two players at one forge with offers at the
same time, the cross-base permission cases, a Kiln client joined to a dedicated server (rows sent over the network to a
remote client), and the vanilla forge on a server that offers nothing (step 7).

The owner's check, with `mods/tests/gemforge-chest-check`
on the server (or the hosting client) and on the Kiln client:
1. A storage chest within 20 m of a Shroud Forge; `/gemchest setup` (a sword with an empty socket, two gems and 300
   essence into the nearest chest). kiln.log: `gemchest: ui.gemforge.sources = on`, `gems.sources = on`, `gems.payers = on`.
2. Open the forge (Insert). The GEMS list shows your own gems first, then the chest's two gems with their icons, levels
   (2 and 3) and rarity frames, looking like your own. kiln.log: `ui.gemforge.sources hooks installed` once.
3. Select a chest gem: the detail panel shows that gem; choose the sword in WEAPONS; the Insert button is enabled with no
   essence in the backpack (the chest pays). Insert: the sword holds the gem, the gem is gone from the chest, the chest's
   essence went down, the backpack's did not. `gemchest: event: gem crafted ...` (not code).
4. Upgrade the other chest gem: its level goes up in the chest; the chest pays. `/gemchest pay player` and reopen: with
   no essence in the backpack the Upgrade button is disabled, as the game's.
5. Recycle a chest gem: it is gone, the chest got the essence (`/gemchest return player` and reopen: the backpack gets it).
6. Take a chest gem out by hand while its row is still listed, then act on the stale row: nothing happens on the screen,
   a toast says why, nothing was paid. `/gemchest off`, reopen after 30 s: only your own gems are listed.
7. The vanilla forge: on a server without the mod (or a vanilla server) the forge looks and works as the game's.
