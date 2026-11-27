# Item costs: take an exact price, with a receipt

A mod that charges a player something (bait for a taming step, twigs for a revive, a tool's durability for an ability) needs more
than `Items.Take`: it must take **exactly** what it says, from **the slot it showed the player**, **once** even if the request is
sent again, and be able to give it back **once**. `ItemCosts` (Kiln.Items, since 0.2.0-dev.32) does that and answers with a
**receipt**. Taming steps and pet revives pay through it. The contract and the native table:
[services/item-costs.md](../services/item-costs.md).

## Make it: a blessing that costs 3 twigs

A server mod of one command. The player pays three twigs from one stack; if the blessing then fails, the twigs go back.

1. Make the project ([mdk.md](mdk.md)): `dotnet new kiln-mod -n Blessing`, with `<KilnServices>Items;Players;Chat</KilnServices>`.
2. The command:

   ```csharp
   public sealed class BlessingMod : Mod
   {
       [Command("bless", Description = "pay 3 twigs for a blessing")]
       private async Task Bless(CommandContext c)
       {
           if (!ItemCosts.StacksAvailable) throw new CommandException($"exact costs are off: {Capabilities.Why(ItemCosts.StacksCapability)}");
           var owner = EntityRef.Of(c.Source.Player);
           var twigs = Items.Lookup("Material_T1_Twigs")!.Id;

           var slot = ItemCosts.Choose(owner, twigs, 3)                              // one slot, as it is now: the first plain stack holding 3
                      ?? throw new CommandException("you need 3 twigs in one stack");
           var receipt = await ItemCosts.Take(slot, 3, Guid.NewGuid());              // exactly 3 from that slot, all or nothing
           if (!receipt.Committed) throw new CommandException($"not paid: {receipt.Refusal}: {receipt.Reason}");

           try { GiveTheBlessing(c.Source.Player); }                                  // your own effect
           catch
           {
               await ItemCosts.Refund(receipt, Guid.NewGuid());                       // one refund per paid receipt
               throw;
           }
           c.Reply("You are blessed.");
       }
   }
   ```

3. Build and deploy, join, hold three twigs in one stack and run `/bless`. The stack drops by three. With the twigs split over
   several stacks of one or two, `Choose` finds none and nothing is taken.

## The pieces

| You want | Call | Notes |
|---|---|---|
| a slot to pay from | `ItemCosts.Choose(owner, item, count)`, or `ItemSelection.Of(owner, slot)` from an `Items.Read` slot | a **snapshot** of one slot: owner, slot, item, count (and, for gear, the item's stamp) |
| to know before asking | `ItemCosts.Check(selection, amount, allowBreak)` | `null` when it could pay now; the cost is checked again when it runs |
| items off a plain stack | `ItemCosts.Take(selection, count, requestId)` | from that slot only; a changed slot refuses (`StaleSelection`, `NotAStack`) and **nothing** is taken |
| wear off a gear item | `ItemCosts.Wear(selection, units, requestId, allowBreak)` | through the game's own durability debit; refuses a broken item (`Broken`) or one that would break without `allowBreak` (`WouldBreak`) |
| to give it back | `ItemCosts.Refund(receipt, requestId)` | a take: the items to the same owner, the slot first (`NoRoom`, `Partial`); a wear: the durability to the same stamped item, never above its max |
| to know what happened | the `CostReceipt` | `State` (`Committed`, `Refused`, `Uncertain`), `Taken`, `Disposition`, `Refusal`, `Reason`; `ItemCostSettledEvent` for each |

- **A client never names a slot.** The server builds the selection from its own read: a picker shows the server's rows
  (`Taming.Choices` does that) and sends back which one.
- **One request id, one debit.** Every call carries a `Guid`: the same id for a network retry (it returns the same task or receipt
  and never pays twice), a new id for each new intended payment. The same id with another cost is `RequestConflict`.
- **An `Uncertain` receipt** (a fault inside the game's change) is never submitted again or refunded: whether it was paid is not known.
  Decide in your mod what the player is owed.
- **A refund reaches only an owner in the game, in the world the cost was paid in** (`StaleSession`, `WrongWorld` otherwise). Kiln
  keeps no refund for a later session.
- **Not promised:** atomicity with anything else (a pet record, your own save). A committed receipt says the inventory changed in the
  running world, nothing more.

## Errors

Bad arguments throw `ArgumentException` (no owner or slot, an empty request id, a count outside the selection's, wear outside 0 to
32,767); a capability that is off throws `KilnUnavailableException` naming `items.costs.exact` or `items.costs.wear`; everything the
game or the world refuses is a receipt with `Committed` false. Not on the tick thread: `InvalidOperationException`.

## In multiplayer

Costs are the server's: a dedicated server, or the server half of a hosted game. Players' games see the inventory change as they see
any other.

## Known limits

- The game's break notice is not sent for a wear that breaks a tool with `allowBreak`; the tool still breaks.
- A wear is the game's helper called from Kiln's own program, not the game's tool action: the game's per-action rules (the hit cap,
  the projectile spawn) do not apply, and `kiln.toolwear`'s rules never change it.
- A refund is a give: the "+N item" popup is not shown and no `PickupEvent` is sent.
- Lab-proven against the real game on a dedicated server (`mods/tests/item-costs-test`); nothing has run on a game client.
