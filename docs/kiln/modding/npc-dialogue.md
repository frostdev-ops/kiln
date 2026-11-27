# NPC dialogue: your lines in the game's speech bubble and menu

A mod puts its own text in the game's own speech bubble over a friendly NPC (a villager, a craftsperson, an assistant) and, since
0.2.0-dev.32, in its dialogue menu (the E key): the greetings, the farewells, the line the menu opens with, and a talk page of
your own topics. The host decides; every player whose game runs Kiln sees it. A server mod is enough. The contract, every rule and
the limits: [services/npc-speech.md](../services/npc-speech.md).

## Make it: a craftsperson who asks to be fed

A server mod of one class. While the NPC is hungry it complains instead of greeting, opens its menu with a plea and offers a
topic that feeds it; fed, it thanks the player and its own lines come back. It is
[`mods/samples/HungryCrafter`](../../../mods/samples/HungryCrafter) in small.

1. Make the project ([mdk.md](mdk.md)): `dotnet new kiln-mod -n HungryNpc`, with `<KilnServices>Blocks;Creatures;World;Ui;Chat</KilnServices>`.
2. The mod:

   ```csharp
   public sealed class HungryNpcMod : Mod
   {
       private readonly HashSet<EntityId> _hungry = [];

       [Command("hunger", Description = "make the nearest NPC hungry", Permission = CommandPermission.Admin)]
       private void Hunger(CommandContext c)
       {
           var at = World.Position(c.Source.Player) ?? throw new CommandException("your position is not known");
           var npc = Villagers.List().Where(v => v.Position is not null && Npcs.CanSpeak(v.Entity))
                              .OrderBy(v => v.Position!.Value.DistanceTo(at)).FirstOrDefault()?.Entity
                     ?? throw new CommandException("no NPC with speech near you");
           Starve(npc);
           c.Reply("it is hungry now");
       }

       private void Starve(EntityId npc)
       {
           if (!_hungry.Add(npc)) return;
           Npcs.Say(npc, "I'm starving...", TimeSpan.FromSeconds(8));          // once, at once, over its head, no voice

           var speech = Npcs.Speech(npc);                                      // its greetings while hungry
           speech.Pick = SpeechPick.InOrder;                                   // a new complaint at each greeting, in turn
           speech.Replace("No food, no work.", "My stomach is louder than my anvil.");

           Npcs.Farewells(npc).Replace("Come back with something to eat.");   // the line in the menu's box when a player leaves
           Npcs.MenuLine(npc).Replace("Please, Flameborn, I can't craft on an empty stomach.");   // what it says when the menu opens

           Npcs.Topics(npc).Set(new NpcTopic("feed", "Here, have some bread.")  // a page of your own, after the NPC's topics
           {
               Reply = "Bread! Bless you.",
               OnPick = ctx => Feed(ctx.Npc),                                   // runs on the host, for the player who picked it
           });
       }

       private void Feed(EntityId npc)
       {
           if (!_hungry.Remove(npc)) return;
           Npcs.Speech(npc).Clear();                                            // its own lines and page come back
           Npcs.Farewells(npc).Clear();
           Npcs.MenuLine(npc).Clear();
           Npcs.Topics(npc).Clear();
           Npcs.Say(npc, "Thank you, Flameborn! Back to work.", TimeSpan.FromSeconds(6));
       }
   }
   ```

3. Build and deploy to a server with Kiln, join with a Kiln client, stand near a villager and run `/hunger`. The bubble shows the
   `Say` for 8 seconds; look away and back and you get a complaint; press E and the menu opens with the plea and the "Here, have
   some bread." topic; pick it and the NPC thanks you and its own lines return. A vanilla client on the same server sees the game's
   own lines throughout.

Texts can be per language: `UiText.Of(("en", "..."), ("de", "..."))` wherever a text is taken. A condition per player (a player who
fed the NPC gets another line, or none) is `SpeechPick.By(ctx => ...)`.

## The calls

| You want | Call |
|---|---|
| a line over its head now | `Npcs.Say(npc, text, duration)`; `Npcs.StopSaying(npc)` |
| its greetings to be yours | `Npcs.Speech(npc).Replace(lines)`, `.Suppress()` (no bubble), `.Clear()` |
| a new line each greeting | `speech.Pick = SpeechPick.Random` (default) `| InOrder | Seeded(seed) | By(ctx => index)` |
| its farewells to be yours | `Npcs.Farewells(npc).Replace(lines)` |
| its menu to open with a line | `Npcs.MenuLine(npc).Replace(lines)`; one line to one player: `Npcs.MenuSay(npc, player, text)` |
| topics on its page | `Npcs.Topics(npc).Set(new NpcTopic(id, label) { Reply, Hint, Close, OnPick })`, `.Remove(id)`, `.Clear()` |
| the NPCs that can speak | `Villagers.List()`, `Villagers.Of(entity)`, `Npcs.CanSpeak(entity)` |

Rules in a line each:

- **One override per mod and NPC.** Several mods each have one; the highest `Priority` shows, then the most recent. When it clears,
  the next one shows, else the game's own lines. A `Say` shows over every override.
- **It all ends** when your mod unloads and when the world ends.
- **The host decides**; `OnPick` and a `By` callback run on the host's tick thread, as your mod. Guard what a topic gives (items,
  quest steps) as you would any player's request: the host does not check that the player stands at the NPC.
- **Do not put the calls under `Npcs.Available` or a capability check:** on a dedicated server they are off and the calls still go
  out to the clients.
- **Texts:** up to 200 characters per language, all languages of one line together up to 2,048 bytes, 1 to 32 lines per override; a
  world holds up to 4,096 distinct texts per client.

## In multiplayer

| Peer | Sees |
|---|---|
| A Kiln client (0.2.0-dev.32) | everything the host decided: the bubbles, farewells, the menu line, topics and `By` picks |
| A 31.16 to 31.21 Kiln client | greetings, `Suppress` and `Say` as before; no farewells, menu line, topics or `By` picks |
| A vanilla client | the game's own lines; nothing is sent to it |

Players need Kiln, not your mod (`"side": "server"`, `"clients": "none"`).

## Known limits

- Kiln clients only: the text is the client's own data. The game's setting "NPC Speech Bubbles" off shows no bubbles at all.
- The menu parts (farewells, the menu line, topics, `By` picks) are static on the client and lab-proven on a server with bot
  players; **nothing of them has been seen in a real game yet**. Seen in a real game (31.19): `Replace` lines in the bubble with no
  voice-over, `Say` on a crafter, and the NPC's own greetings back after `Clear`. Not seen: `Suppress` hiding the greeting, a `Say`
  over an NPC that is not the player's target, and how far a `Say` shows.
- A topic is the same for every player of an NPC (a per-player answer is `ctx.Reply`); 16 per NPC with every mod's.
- A greeting the game already picked when your override began keeps its voice-over for that one greeting.

Test without a game: Kiln.TestKit's `KilnHarness.NpcSpeech` records what a client's bubble would be told, and
`PickTopic(npc, id, player)` runs a topic's `OnPick` ([services/npc-speech.md](../services/npc-speech.md#testing-your-mod-kilntestkit)).
