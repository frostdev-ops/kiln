# Gems: forge actions

Part of the Gems guide ([gems.md](gems.md), section "Forge actions"): socket, unsocket, upgrade and recycle a gem from
code, and the events of the players' own forge actions (`Kiln.Abilities`, `namespace Kiln`).

Capability: **`gems.craft`** (dedicated server, and a game client while it hosts). Since 0.2.0-dev.29.11.

## What it does
A Shroud Forge has four actions. Kiln runs the game's own action for each, the one a player's click at the forge runs,
with all the game's checks and its costs:

| Call | The game's action | Cost (game data) |
|---|---|---|
| `Gems.Socket(player, carrier, gem, forge)` | Insert: the gem goes into the carrier's empty socket | essence, by the forge's tier |
| `Gems.Unsocket(player, carrier, forge)` | Remove: the gem goes back to the player's backpack | free in this game build |
| `Gems.Upgrade(player, gemOrCarrier, forge)` | Upgrade: the gem's level + 1 | essence, by the gem's level and the forge's tier; refused at the gem's own cap or the forge's maximum level |
| `Gems.Recycle(player, gemOrCarrier, forge)` | Recycle: the gem is destroyed | the player gets the forge's essence, by the gem's level |

- **player**: the player character the action runs for. It pays, and gets an unsocketed gem or the essence.
- **carrier**: the item with the socket: a weapon, or a ring, armour piece, shield or other gear a mod gave a socket
  ([gems-sockets.md](gems-sockets.md)).
- **gem / gemOrCarrier**: a loose gem, or (Upgrade, Recycle) the carrier whose socket holds it.
- **forge**: a placed Shroud Forge. The game prices every action with a forge: its tier sets the cost, the maximum level
  and the essence. Kiln needs one even for a free action. You get a forge's entity from `GemCraftingEvent.Forge`, or from
  the world (`Spawn.FindByName("Shroud_Gem_Forge")`).

Each call returns a `Task<GemCraftResult>` that completes on the tick thread one step after the step it ran in. A refusal is
a result, never an exception: `Ok` is false, `Reason` says why, and nothing changed.

```csharp
var sword = Items.Read(player)!.Slots.First(s => s.Item == swordId);
var gem = Items.Read(player)!.Slots.First(s => s.Item == bleedingId);
GemCraftResult r = await Gems.Socket(player, sword.Ref, gem.Ref, forge);
if (!r.Ok) Log.Info($"not socketed: {r.Reason}");
else Log.Info($"socketed, paid {r.Paid}");

var up = await Gems.Upgrade(player, sword.Ref, forge);          // the gem in the sword's socket
Log.Info($"level {up.LevelBefore} -> {up.LevelAfter}, paid {up.Paid}");
```

### Where the items may be
The carrier and the gem must be in the player's own inventories (backpack, action bars, worn gear), or in an entity you
name with `GemCraftOptions.Holder` (for example a chest your mod lets the player use). Since 0.2.0-dev.29.15 the gem and
the carrier may be in two different places: `GemHolder` names where the gem is and `CarrierHolder` where the carrier is
(each defaults to `Holder`, then to the player). Kiln checks before the action that they are there, that each ref still
names the item it was taken from (an `ItemInstanceRef` whose item was destroyed, or whose entity id now names another
item, is `NotFound`), that each holder is the player or a loaded container (not another player, not an NPC), that the
socket is there and empty (Socket) or holds something (Unsocket), and that the item to socket, upgrade or recycle is a
gem. The game itself checks none of that (its action would recycle a sword, or raise a sword's level, if asked), so a
mod should not skip it either: only name a holder the player may use.

```csharp
// a gem in the chest, the sword in the player's backpack: one transaction, the gem never passes the backpack
var gem = Items.Read(chest)!.Slots.First(s => s.Item == bleedingId);
GemCraftResult r = await Gems.Socket(player, sword.Ref, gem.Ref, forge, new GemCraftOptions { GemHolder = chest });
```

The game's own action does the move: its request names the gem's inventory slot (a page entity and an index, the
chest's page here) and the carrier's item entity, and its transaction reads and writes both inventories. A refusal or a
rollback leaves both where they were; nothing is lent to the backpack.

### Who pays and where things go (since 0.2.0-dev.29.15)
`GemCraftOptions.PayFrom` names who pays the game's cost of a Socket or an Upgrade (Unsocket is free on this game build;
Recycle pays nothing): `GemPayer.Player` (the default, as the game), `GemPayer.From(chest)` (a container your mod vouches
for), `GemPayer.BaseStorage` (the crafting-stock containers of the base the player stands in: the Magic chests, in the
game's order, as the game's crafting from base storage takes them; the player needs the server's CanAccessInventories
right) or `GemPayer.PlayerThenBaseStorage` (the player's inventories first, then those). `GemCraftOptions.ReturnTo`
names where an unsocketed gem and Recycle's essence go (none: the player).

```csharp
// upgrade a gem in the chest, paid by the chest
await Gems.Upgrade(player, gem.Ref, forge, new GemCraftOptions { GemHolder = chest, PayFrom = GemPayer.From(chest) });
// take the gem out of a sword into the chest
await Gems.Unsocket(player, sword.Ref, forge, new GemCraftOptions { ReturnTo = chest });
```

The game still decides the cost (by the gem's level and the forge) and takes it inside the action's own transaction:
Kiln points the game's payment at the payers (it takes from the first payers itself on the same transaction and lets
the game's own call take the rest from the last one), and the game's unslot move and Recycle's give at `ReturnTo`. When
the payers cannot pay, nothing is paid and nothing moves (`NotEnough`); outside a base, or without the right,
`BaseStorage` pays nothing (`NotEnough`) and `PlayerThenBaseStorage` is the player alone. `Paid` and `Received` are counted
in the payers and in `ReturnTo`. Other than the player, both need the capability `gems.payers`; a replaced price
(`Cost`, not free) is the player's and is refused together with another payer (`ArgumentException`).

**Does the game's forge take essence from base storage itself?** No (client 26430625, server 26430253, static
analysis): the handler's payment is the inventory transaction's removeItems with the player as the owner, which walks the
player's own InventorySetup links, not `InventoryPay`/base stock as crafting does; and the forge screen's price test counts
the material only in the player's own Generic pages. `PayFrom` is the way to make chests pay.

### Chest gems in the forge's own GEMS list (since 0.2.0-dev.29.15)
A server mod can offer a player the loose gems of a container at the Shroud Forge: on the player's Kiln client they appear
in the game's own GEMS list (after the player's own gems, drawn as the game draws a gem), and the player inserts,
upgrades or recycles one there as any other gem, without a backpack round trip. Capability `gems.sources` (server).

```csharp
Events.On<GemForgeOpenedEvent>(e =>                       // the player's forge screen opened (and every 5 s while open)
{
    foreach (var chest in ChestsThePlayerMayUse(e.Player))  // your policy: the player's base, a permission, a distance
        Gems.OfferChestGems(e.Player, chest, new GemSourceOptions { PayFrom = GemPayer.From(chest), ReturnTo = chest });
});
```

- `GemForgeOpenedEvent` (server, tick thread): a Kiln client says its forge screen opened (`Refresh` false), stays open
  (every 5 s), or an action on an offered gem just ended (`Refresh` true). `Forge` is the nearest Shroud Forge within 12 m
  of the player, or none. A client's hellos post it at most once a second per player (a close and an open count too;
  an opening inside the second is posted when the second is over). Every offer to the player is withdrawn when the
  screen closes, when the player leaves, and when a player joins into the same slot; an offer also belongs to the
  player's session (kiln.players' connection handle): a later player in the same slot (the same entity id) never finds
  it, even before it lapses. An offer to a player slot nobody is in game in is refused.
- `Gems.OfferChestGems(player, holder, options)`: offers every loose gem in `holder` (a gem item, one of it, with its own
  item entity) to `player` for `Lifetime` (real time; default 30 s, 1 ms to 120 s), with `PayFrom` (Insert, Upgrade) and `ReturnTo`
  (Recycle's essence). Each gem gets a token Kiln mints (random, never reused); the result lists them. An offer of the
  same slot replaces the older one; at most 256 live offers per player. `Gems.WithdrawChestGems(player, holder)` takes
  your mod's offers back; your mod's offers also go when it unloads.
- **What the server checks when the player acts.** The player's request is the game's own: it names the gem's real slot
  in the chest. Kiln's hook on the game's gem handler finds the offer by player and slot, consumes the request (the game
  runs nothing for it) and runs the action through the code path above with the offer's holder, payer and return; when
  it runs, Kiln checks again that the slot still holds the offered gem entity and item at the offered level, that the
  carrier is in the player's own inventories, and that the forge is a Shroud Forge, and the payment and the move happen
  in the game's one transaction. Each offer is used once. Payers other than the player pay in the forge's own upgrade
  material (the material a client names counts only when the player pays, as in the game).
- **What is refused once offers are in use.** From the first offer in a world on (before any offer the game's own
  behaviour is unchanged), a player's forge request is consumed and nothing moves when it names:
  - a carrier (the weapon) that is not in the player's own inventories (a chest's or another player's weapon), whatever
    was offered; or
  - a gem slot outside the player's own inventories without a live offer of that slot to that player in the player's
    current session (forged, stale, lapsed, already used, withdrawn, another player's, or made to an earlier player in
    the same slot).

  The player's own inventories are the pages of the player's InventorySetup (backpack, worn bags, action bar, equipment),
  the same pages `Items.Read` lists; a gem socketed in a carrier there is the player's own. A
  `GemCraftingEvent` handler sees and can cancel an offered gem's action like any other forge action of the player;
  `GemCraftedEvent` follows with `ByCode` false.
- **What the player sees.** A refused action changes nothing on the forge screen (the game has no message for it); a
  Kiln client shows a toast with the reason ("Gem Forge: the payers cannot pay the forge's cost").
- **Clients.** The offers travel over Kiln's channel `kiln:gemsources` (not required: a client without it joins and sees
  the game's own list). A client lists them only with `ui.gemforge.sources` ([../ui-gemforge.md](../ui-gemforge.md#chest-gems-in-the-gems-grid)),
  and its hooks go in only when this server sent rows: a vanilla client, an older Kiln, or a server that never offers
  leaves the forge screen exactly as the game's. Nothing a client sends is authority: it says only that the screen is
  open (a material list on the wire is ignored). The payer counts the client gets are of the forge's own upgrade
  material, read by the gem program (`forge_material`).
- Chests in unloaded parts of the world are out of reach (their inventories are not there); offer only what is loaded.

### Seen in a real game (0.2.0-dev.29.15)
bisdat ran the chest-gem offers and the payers in a real game client on 2026-11-17 (GitHub issue 61): Kiln
0.2.0-dev.29.15 (mod API 0.2.7), Bazzite Linux with Steam's Proton, in a world hosted on that PC, with their server mod
"Gem Forge Magic Storage" (v1.0.0, released; it uses `GemForgeOpenedEvent`, `Gems.OfferChestGems` with
`PayFrom = GemPayer.PlayerThenBaseStorage`, and the `Bases` APIs). All passed:
- `GemForgeOpenedEvent` arrived while the forge screen was open, and `Gems.OfferChestGems` offered the loose gems of a
  Magic chest: all 28 appeared as rows in the game's own GEMS grid.
- A chest gem socketed into a weapon the player held, with the game's own controls, without the gem passing through the
  backpack; unsocketing it put it in the player's backpack, as the game's Remove does.
- An upgrade of a chest gem, level 43 to 44, paid from the base's Magic chests: 172 Archaic Essence VII (the forge's shown
  price) came out of the chests while the backpack held none.
- With too little essence the upgrade was refused: the level stayed, no gem or essence was lost.
- A loose chest gem recycled; its essence went to the player's backpack.
- Withdrawn offers: the chest rows left the GEMS grid, and the player's own gems stayed.

Not seen in a real game yet: two players acting on the same offers at once, an action on a stale row (the gem moved
out of the chest while its row was listed), the cross-base permission cases, and a separate Kiln client joined to a
dedicated server (the offers then travel over the network). The server-side checks cover these in labs.

### Free and changed costs
`GemCraftOptions.Cost` changes what the player pays, for creative tools, quest rewards or a mod's own prices:

```csharp
await Gems.Upgrade(player, gemRef, forge, new GemCraftOptions { Cost = GemCost.Free });            // nothing paid
await Gems.Upgrade(player, gemRef, forge, new GemCraftOptions { Cost = new GemCost(goldId, 50) }); // 50 gold instead
await Gems.Recycle(player, gemRef, forge, new GemCraftOptions { Cost = new GemCost(runeId, 1) });  // a rune instead of essence
```
For Recycle the option replaces what the player gets (`GemCost.Free`: nothing). Everything else about the action stays
the game's: the level cap, the forge's maximum level, the socket checks. How it works: Kiln gives the player the game's
price right before the game's action (the game takes it back) and takes your price; afterwards Kiln counts the material
and takes back whatever of its gift the game did not take, and if the action did not happen it gives your price back.
The player needs a free slot for that moment when the game's price is not 0 (otherwise `NoRoom`). A replaced price must
be a plain item (essence, gold, runes): an item with an entity of its own (gear, a gem) throws `ArgumentException`, since
Kiln takes it by item id from anywhere in the inventory and it could be the very carrier or gem of the action. For
Recycle the replacement is given, so any item works there; when the game's essence cannot be swapped (the replacement
does not fit), the player keeps the essence and `Message` says so.

### Results
`GemCraftResult`: `Status`, `Action`, `Player`, `Forge`, `Carrier`, `Gem`, `LevelBefore`, `LevelAfter` (Recycle: 0),
`Paid` (what the payers paid), `Received` (Recycle: what reached `ReturnTo`), `Message`. Statuses:

| Status | Meaning |
|---|---|
| `Done` | it happened |
| `Refused` | the game's own checks refused it; also when 256 actions are waiting (try again in a step) |
| `NotEnough` | the payers (the player by default) cannot pay |
| `NoForge` | the forge entity is no Shroud Forge |
| `NotFound` | the carrier, its socket or the gem is not where the call says, a ref is stale, a holder is no container, or the item is not a gem; an offered gem's slot no longer holds it at the offered level |
| `Occupied` | Socket: the socket holds a gem already; Unsocket: it is empty |
| `NoRoom` | Unsocket: no free slot on the player's Generic pages (a worn backpack's pages count), or in `ReturnTo`; a free or changed cost: no room for the game's price |
| `MaxLevel` | Upgrade: at the gem's own cap or the forge's maximum level |
| `Unavailable`, `Dropped` | `gems.craft` (or, for payers and returns, `gems.payers`) went off, or the player left / the world closed before it ran |

## Events
```csharp
Events.On<GemCraftingEvent>(e =>
{
    // a player is about to use a forge: e.Action, e.Carrier, e.Gem, e.GemLevel, e.Forge, e.Cost (Recycle: e.Yield)
    if (e.Action == GemCraftAction.Recycle && IsQuestGem(e.Gem)) e.Cancel();  // nothing is paid, nothing moves
});
Events.On<GemCraftedEvent>(e =>
    Log.Info($"{e.Player} {e.Action} {e.Gem}: level {e.LevelBefore} -> {e.LevelAfter}, paid {e.Paid}, got {e.Received}, code: {e.ByCode}"));
```

- **`GemCraftingEvent`** (cancelable): a player's own forge action, before the game runs it. While a handler listens,
  the game holds each new forge action for one step (about 40 ms on a dedicated server) and Kiln posts it in the next Pre;
  not canceled, it runs in the next step. `Cost` is the game's own price with that forge (the cost function the game then
  charges). Cancel refuses it: the game treats it as a request without a forge, so nothing is paid and nothing moves. The
  player's game is not told; its forge screen shows the inventory as it is at the next update. A held action nobody
  answers within 8 steps runs. A cancel stands even when the handler's mod stops listening before the game saw the
  answer. Each action is posted once: when the last handler goes, what is still held runs without an event, and an
  action the game leaves unconsumed (its own handler does that when the gem's level cannot be read) is not posted again.
  Only players' actions: `Gems`' own calls are never held or posted here.
- **`GemCraftedEvent`**: after an action changed something: a player's (after the game committed it) or a `Gems` call
  (`ByCode`), including the forgeless unsocket of `Gems.ReturnSocketedGems` (`Unsocket`, `ByCode`, `Forge` none, nothing
  paid). `Paid` and `Received` are counted from the player's inventory before and after. A refused or canceled action
  posts nothing.
- Gems that change any other way (a mod moving items, a socket written directly) are not seen here;
  `GemSocketChangedEvent` sees every change of a socket by polling, a few steps later (gems.md, "Which event for what":
  each forge action that changes a socket shows up in both).

## Capability rows
| Row | Kind | What it says |
|---|---|---|
| `gems.craft` | sigdb feature (server; a hosting client composes it) | the four actions, their events and the forgeless return |
| `gems.payers` | sigdb feature (`server.d/gems-forge.json`, since 0.2.0-dev.29.15) | `PayFrom` and `ReturnTo` other than the player: the five call sites of the handler Kiln's mid hooks sit on (its three payments, its unslot move, Recycle's give), each checked before hooking to be the call the static analysis measured. kiln-items.dll lowers it (`faulted`) when a hook cannot go in; Kiln.Abilities lowers it (`service-older`) with a kiln-items.dll before 0.2.0-dev.29.15 |
| `gems.sources` | managed, session (Kiln.Abilities, since 0.2.0-dev.29.15) | `Gems.OfferChestGems`, `WithdrawChestGems`, `GemForgeOpenedEvent`: needs `gems.craft`, kiln.gemcraft's appended part (`service-older` before 0.2.0-dev.29.15) and kiln.net; pending until the server world runs |
| `ui.gemforge.sources` | sigdb feature (client, since 0.2.0-dev.29.15) | the offered rows in the GEMS grid ([../ui-gemforge.md](../ui-gemforge.md#chest-gems-in-the-gems-grid)) |

## Limits
- Server side only. On a game client that joined someone else's world, `gems.craft` is off.
- Offered chest gems are loose gems only: a weapon in a chest with a gem in it is not listed (the game's list shows only
  the player's own carriers). Insert, Upgrade and Recycle work on them; Remove (Unslot) names no gem slot.
- `BaseStorage` follows the game's rule for crafting from base storage: the base the player stands in, and the
  CanAccessInventories right of the player's user group.
- The forge screen of the player's game shows Kiln's actions only as the inventory updates (no animation for code
  actions on the player's screen; the forge itself plays its action sequence, as for a player's click).
- Gems on rings and armour: the game's action accepts any carrier with a socket ([gems-sockets.md](gems-sockets.md) adds
  those sockets). Lab: a ring in the backpack socketed and upgraded by code, and a socket into a worn ring.
- A game update that changes the forge's handler turns `gems.craft` off until Kiln is updated (`Capabilities.Why("gems.craft")`).

## For native code
`native/core/abi/services/kiln_gemcraft.h` (`kiln.gemcraft` v1, in kiln-items.dll): `submit`, `cancel`, `hold`, `held`,
`decide`, `watch`, `crafted`, `status`; appended in 0.2.0-dev.29.15 (check `header.size`): `extras` (which appended parts
run), `offer`, `withdraw`, `sources`, `payers`, `forge_material`. `KilnGemSource.lifetime_ms` / `expires_ms` are real
time (GetTickCount64); an offer keeps the player's session and `sources` lists only the offers of the session now.
`KilnGemRequest` gained `gem_holder`, `carrier_holder`, `pay_from`
(`KILN_GEM_PAY_*`), `pay_holder`, `return_to` (a request of the first version's size, `KILN_GEM_REQUEST_V1_SIZE`, reads
them as 0); `KilnGemCraft.flags` gained `KILN_GEMCRAFT_SOURCE` (an offered gem's action: `ticket` is the offer's token,
reported by `crafted` whatever its status) and `KILN_GEMCRAFT_ROUTED`. `KILN_GEM_RETURN` (an Unslot alone, forge 0, no holder) takes a gem out of any
carrier in the player's own inventories without a forge and free: what `Gems.ReturnSocketedGems` uses for sockets the
forge does not list. Evidence and the handler's layout: [docs/re/gems-forge.md](../../re/gems-forge.md).
