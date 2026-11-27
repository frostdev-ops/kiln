# The mod API: NPC speech and dialogue (Kiln.Npcs in Kiln.Blocks, dev.31.16+ and dev.32+)

Part of the mod API guides ([api.md](api.md) is the index). Here: your own text in the game's speech bubble over a friendly
NPC (dev.31.16+), and, new in dev.32, its greeting picks, farewells, the line it speaks when its menu opens, and topics on its
talk page. For NPCs of your own (`NpcBlock`, quests, conversations) see
[quests-talents-sounds.md](quests-talents-sounds.md). Grep [api-index/Blocks-npc.md](api-index/Blocks-npc.md) for signatures.

- Assembly: `Blocks` (the `Npcs` class lives in Kiln.Blocks). Pages: `docs/kiln/modding/npc-dialogue.md` (a walkthrough), `docs/kiln/services/npc-speech.md` (the contract and
  every limit), wiki https://frostdev.io/kiln/wiki/npc-dialogue. Sample: `HungryCrafter` (`/hunger starve|feed [all]`).
- A mod can be `"side": "server"`, `"clients": "none"`: the **host decides, Kiln clients show it**. Players need Kiln, not
  your mod. Vanilla players keep the game's own lines and nothing is sent to them. For the menu parts (farewell, the menu's
  line, topics, `SpeechPick.By`) the player needs Kiln 0.2.0-dev.32; a 31.16 to 31.21 client shows greetings, `Suppress` and
  `Say` as before and ignores the rest.
- Status: dev.31.16 bubbles were seen in a real game (Marshall/Ryrin's Living Larder: Replace lines show with no voice-over).
  The dev.32 menu parts ran on a dedicated lab server with lab peers and bots, and in host tests on fake dialogue windows:
  **not seen in a real game** (first menu before the host answers, per-player budget, farewell NPC). Say so.

## Find an NPC, then speak

```csharp
foreach (var v in Villagers.List().Where(v => Npcs.CanSpeak(v.Entity))) { /* an NPC with dialogue lines */ }
Npcs.Say(npc, "I'm starving...", TimeSpan.FromSeconds(6));         // once, now, no voice-over; 0.5 s to 60 s
var speech = Npcs.Speech(npc);                                      // this mod's override of that NPC's greetings
speech.Priority = 10;                                               // outranks other mods' overrides of the same NPC
speech.Pick = SpeechPick.InOrder;                                   // set before Replace
speech.Replace("No food, no work.", "My stomach is louder than my anvil.");
// speech.Suppress();                                               // or: no greeting bubble at all
speech.Clear();                                                     // the game's lines (or the next mod's) again
```

Every text is a `string` (the same in every language) or a `UiText` per language:
`UiText.Of(("en", "I'm starving"), ("de", "Ich verhungere"))`. A text with neither `en` nor `*` shows its fallback in other
languages.

| Call | What it does |
|---|---|
| `Npcs.Say(npc, text, duration, SayOptions?)` | one bubble over the NPC now, also when it is not the player's target; a newer Say replaces it; `new SayOptions(Interrupt: false)` is refused while another mod's Say shows; `Npcs.StopSaying(npc)` ends yours |
| `Npcs.Speech(npc)` -> `NpcSpeech` | your override of the **greeting** bubble: `Replace(lines)` (1 to 32 lines), `Suppress()`, `Clear()`, `Pick`, `Priority`, `Mode` |
| `Npcs.Farewells(npc)` | your override of the line in the menu's speech box when a player **leaves** it: `Replace`, `Clear`, `Pick`, `Priority` (`Suppress` answers `Ok: false`) |
| `Npcs.MenuLine(npc)` | the NPC's own line when its menu **opens**, then the topics (or the dialogue ends when there are none): `Replace`, `Clear`, `Pick`, `Priority`, `Refresh()`. A craftsperson with a menu line opens its menu first, so the line is seen |
| `Npcs.MenuSay(npc, player, text)` | one line in **that player's** menu with the NPC: now if it is open, else when it opens within 5 s; once. Wins over the menu line at that open |
| `Npcs.Topics(npc)` -> `NpcTopics` | your topics on the NPC's talk page: `Set(params NpcTopic[])`, `Add`, `Remove(id)`, `Clear()`, `List` |
| `Npcs.SpeechOf(npc)`, `IsSaying(npc)`, `CanSpeak(npc)` | what the NPC does now, from every mod; `CanSpeak` is tick-thread |

`SpeechResult(Ok, Reason)`. Wrong arguments (no NPC, a duration out of range, an empty or too long text, no line or more than
32) throw `ArgumentException`. **Do not guard these calls with `Npcs.Available` or `Capabilities.Is("npcspeech.bubble")`:** both
are false on a dedicated server (it draws no bubbles itself) and it still sends to its Kiln players, so a guarded mod shows
nothing to anyone.

## Picks: a new line each time

`NpcSpeech.Pick` says how `Replace` chooses among your lines at each greeting, farewell or menu open:

- `SpeechPick.Random` (the default): each player's game picks, never the same line twice in a row;
- `SpeechPick.InOrder`: your lines in turn;
- `SpeechPick.Seeded(42)`: a pure mix of the seed, the NPC and the open's number, so every client shows the same line for the
  same open;
- `SpeechPick.By(ctx => index)`: **the host decides per player.** The callback runs on the host's tick thread, as your mod,
  and returns the index of the line that player sees next, or `-1` for the game's own line (the menu's line: no line). It
  runs **one open ahead**: for every Kiln player when the override begins or at `Refresh()`, then after each of that player's
  opens. Until the first answer the player sees the game's own line (the menu: none). A throw or an index out of range counts
  as `-1`. `NpcLineContext` has `Npc`, `Player`, `Kind`, `Opens` (this player's opens so far), `Last` (-1: none), `Lines`.

A greeting is the bubble showing after at least 1 s hidden, or the game picking a new line. Call `Refresh()` after the state your
`By` callback reads changed; it asks again for every player now.

```csharp
var plea = Npcs.MenuLine(npc);
plea.Pick = SpeechPick.By(ctx => fed.Contains(ctx.Player) ? -1 : 0);   // no plea for a player who fed it
plea.Replace("Please, Flameborn, I haven't eaten in days.");
// later, when `fed` changes:
plea.Refresh();
Npcs.Farewells(npc).Replace("Come back with something to eat.", "Don't forget me, Flameborn.");
```

## A talk page: topics

```csharp
Npcs.Topics(npc).Set(new NpcTopic("feed", "Here, have some bread.")
{
    Reply = "Bread! Bless you.",                 // in the speech box when picked (null: none)
    Hint = NpcTopicHint.Attention,               // the list's marker
    OnPick = ctx =>                              // on the host, as your mod, for the player who picked it
    {
        FeedNpc(ctx.Npc);
        ctx.Reply("Now I can work again.");      // to that player only (Npcs.MenuSay)
    },
});
```

The NPC's own topics stay first and are never removed; every mod's topics follow (this process's own mods first, then the
host's), **at most 16 in all**: a mod whose topics do not all fit gets `Ok` with a note (they show when there is room). A villager
without topics (Ramone) gets a page. `Close` ends the dialogue after the `Reply` (needs one). Topic ids are per mod, up to 64
characters. `NpcTopicContext`: `Npc`, `Player`, `Topic`, `ModId`, `Reply(text)`; it is valid in the callback only.

## Limits and rules

- **One override per mod and NPC** (per kind: greeting, farewell, menu line). The highest `Priority` shows; on a tie the most
  recent. When it clears, the next one shows, else the game's own lines. **One Say per NPC.**
- **It all ends** when your mod unloads (overrides, Says, topics) and when the world ends. A call made on a thread your mod
  started itself belongs to no mod (`"kiln"`) and ends only with the world.
- **Sizes:** a line is up to **200 characters** per language and all languages of one line together up to **2048 bytes of
  UTF-8**; 1 to 32 lines per `Replace`. Bigger throws `ArgumentException` at the call, not later on the clients. A world holds up
  to **4,096 distinct texts**; past that a new text is not shown (the call answers `Ok` with a note). Keep names, numbers and timers
  that change often out of the texts.
- **Farewells have no voice-over;** `Replace` greetings have none either.
- **A mod on both sides decides on the host** (`Lifecycle.IsServerRunning`) so the client's copy does not decide again; a joined
  client's own calls change only its screen.
- **Texts** go through kiln.content's loca hooks under a tag of Kiln's own: no resource patch, no game text entry.
- Tests: `KilnHarness.NpcSpeech` in Kiln.TestKit: `ModeOf`, `LinesOf`, `Saying`, `PickOf`, `FarewellsOf`, `MenuLinesOf`,
  `TopicsOf`, `RepliesTo`, `PickTopic(npc, topicId, player)` ([testing.md](testing.md)).
