# Kiln.Npcs: your lines in the game's NPC speech bubble and dialogue menu

A mod puts its own text in the game's own speech bubble over a friendly NPC: a villager (Ramone, Hannah, ...), a
craftsperson (the Blacksmith, the Alchemist, ...) or an assistant, and since 0.2.0-dev.32 in the NPC's dialogue menu
(E). Decided by the host and shown by every player whose game runs Kiln:

- **Say once**: `Npcs.Say(npc, "I'm starving", TimeSpan.FromSeconds(6))` shows the text over the NPC's head now, with
  no voice-over, also when the NPC is not the player's target.
- **Speak differently while a condition holds**: `Npcs.Speech(npc).Suppress()` silences its greeting bubbles,
  `.Replace("No food, no work.", ...)` makes it greet with your lines instead of its own, a new one each time it greets
  (`.Pick`: at random, in turn, seeded, or your own choice per player), `.Clear()` gives the game's lines back.
- **Its farewells** (since 0.2.0-dev.32): `Npcs.Farewells(npc).Replace("Come back with food.")`, the line in the menu's
  speech box when a player leaves it.
- **Its own line in the menu** (since 0.2.0-dev.32): `Npcs.MenuLine(npc).Replace("I can't work like this.")`, what the
  NPC says when a player opens its menu, for every player or (`SpeechPick.By`) per player; `Npcs.MenuSay(npc, player,
  text)` answers one player.
- **A talk page** (since 0.2.0-dev.32): `Npcs.Topics(npc).Set(new NpcTopic("feed", "Here, have some bread.") { Reply = ...,
  OnPick = ... })` adds your topics to the NPC's list after its own, so a villager without topics (Ramone) gets a page.

Requested by Marshall (Ryrin) for survival add-ons (NPCs that eat; craftspeople that stop working when hungry; Living
Larder).

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_npcspeech.h` (`kiln.npcspeech` v1, game client only) |
| Native | `native/services/npcspeech/` → `kiln/services/kiln-npcspeech.dll` (`npcspeech_core.h`: the rule inside the game's calls) |
| Managed | `managed/services/Kiln.Blocks/Npc/Speech/` (`Npcs`, `NpcSpeech`, `NpcLines`, `SpeechMode`, `SpeechResult`, `SayOptions`, `SpeechPick`, `NpcLineKind`, `NpcLineContext`, `NpcTopics`, `NpcTopic`, `NpcTopicContext`, `NpcTopicHint`; channels `kiln:npcspeech` v1 and `kiln:npcdialog` v1) |
| Symbols | `sigdb/symbols/client.d/npcspeech.json` (features `npcspeech.bubble`, `npcspeech.voice`), `client.d/npcdialog.json` (`npcspeech.farewell`, `npcspeech.menu_line`, `npcspeech.topics`) |
| Sample | `mods/samples/HungryCrafter` (`/hunger starve|feed [all]`) |
| Tests | `native/services/npcspeech/tests/run.sh` (host: the bubble rule, the picks and the voice-over mute on fake structs; `npcdialog_test.cpp`: farewells, the menu's line, replies and topics on a fake dialogue window), `managed/tests/Kiln.Blocks.Tests/NpcSpeechTests.cs`, `NpcDialogTests.cs`, `managed/tests/Kiln.TestKit.Tests/NpcSpeechFakeTests.cs`; lab: `mods/tests/npcspeech-test` (console driver) + `tools/nettest/npcspeechtest.py` and `npcdialogtest.py` (dev.32, 31.21-like and vanilla lab peers, a LABBOT bot) |
| Evidence | [docs/re/npc-speech.md](../../re/npc-speech.md) |

## Status

- **Static only** (client 26430625 / svn 1287369): the hooks' targets, the record layout and the bubble's parameters are
  read in the code, and sigdb finds all of them on that build.
- **Lab, dedicated server 26430253** (2026-11-25, fresh world, no player; `npcspeech-test` + `HungryCrafter` deployed,
  `tools/nettest/npcspeechtest.py`): **19 PASS, 0 FAIL**. The server lists `kiln:npcspeech` v1 (not required); a ready
  Kiln peer gets the snapshot's Reset; Say goes out once with both languages and 30 s; Replace goes out with its three
  lines and not again when repeated; a peer that joins later gets Reset, the Replace and the running Say (24.3 s left);
  Suppress, StopSaying (an empty Say) and Clear (mode 0) go out; the vanilla peer gets no Kiln frame. kiln.log:
  `npcspeech: npcspeech.bubble is off in this process (a dedicated server ...)` and the service skipped, no error.
  `Npcs.Available` is false there, as it must be.
- **Real game (Marshall/Ryrin, Living Larder, 31.19)**, a game client with his mod (#mod-dev, 2026-11-26): Replace lines
  show in the game's own bubble, with no voice-over; pressing E on a crafter kept her screen shut and showed the mod's
  Say line; the NPCs' own greetings came back when the condition cleared (Clear); farewells stayed the game's (as
  Limits says). One villager repeated the same Replace line while another varied: most likely the game keeps its last
  greeting pick for an NPC until the player leaves the area, and Replace chooses among your lines only when the game
  picks a new greeting (Rules). **Not seen yet**: Suppress hiding the greeting, a Say over an NPC that is not the
  player's target, how far a Say shows (Limits), the talk gesture on E [hyp].

- **0.2.0-dev.32 (picks, farewells, the menu's line, talk pages): static only** on client 26430625 (the dialogue
  window's functions and fields read in the code, `client.d/npcdialog.json` resolves all of them there, and the host
  tests run the rules on fake structs). Lab (dedicated server 26430253, dev.32, 31.21-like and vanilla lab peers and a LABBOT
  bot player, 2026-11-27): `npcdialogtest.py` 29 PASS, 0 FAIL and `npcspeechtest.py` 17 PASS, 0 FAIL. Nothing of the menu parts
  has been seen in a real game yet.

## Using it

```csharp
using Kiln;

// once: over the NPC's head now, for 6 seconds, no voice
Npcs.Say(npc, "I'm starving...", TimeSpan.FromSeconds(6));
Npcs.Say(npc, UiText.Of(("en", "I'm starving..."), ("de", "Ich verhungere...")), TimeSpan.FromSeconds(6));

// while hungry: its greetings are yours
var speech = Npcs.Speech(npc);       // this mod's override of that NPC
speech.Priority = 10;                // optional: outranks other mods' overrides of the same NPC
speech.Replace("No food, no work.", "My stomach is louder than my anvil.");
// or: speech.Suppress();            // no greeting bubble at all

// fed again
speech.Clear();
Npcs.Say(npc, "Thank you, Flameborn!", TimeSpan.FromSeconds(5));
```

Find the NPCs with `Villagers.List()` / `Villagers.Of(entity)` (Kiln.Creatures) and check `Npcs.CanSpeak(entity)`.

### Since 0.2.0-dev.32: picks, farewells, the menu's line, talk pages

```csharp
// greetings: a new line at each greeting (the default is SpeechPick.Random, never the same twice in a row)
var speech = Npcs.Speech(npc);
speech.Pick = SpeechPick.InOrder;                  // or SpeechPick.Seeded(42), or SpeechPick.By(ctx => ...)
speech.Replace("No food, no work.", "My stomach is louder than my anvil.");

// farewells: the line in the menu's speech box when a player leaves it
Npcs.Farewells(npc).Replace("Come back with something to eat.", "Don't forget me, Flameborn.");

// the menu's own line when a player opens it; per player: SpeechPick.By (-1 = no line for that player)
var plea = Npcs.MenuLine(npc);
plea.Pick = SpeechPick.By(ctx => fed.Contains(ctx.Player) ? -1 : 0);
plea.Replace("Please, Flameborn, I haven't eaten in days.");
plea.Refresh();                                    // after `fed` changed: ask again for every player now

// a talk page: your topics after the NPC's own (a villager without topics gets a page)
Npcs.Topics(npc).Set(new NpcTopic("feed", "Here, have some bread.")
{
    Reply = "Bread! Bless you.",                   // in the speech box when picked (null: none)
    Hint = NpcTopicHint.Attention,                 // the list's marker
    OnPick = ctx =>                                // on the host, for the player who picked it
    {
        Feed(ctx.Npc, ctx.Player);
        ctx.Reply("Now I can work again.");        // to that player only (Npcs.MenuSay)
    },
});
Npcs.Topics(npc).Clear();
```

| Member | Thread | What it does |
|---|---|---|
| `SpeechPick NpcSpeech.Pick` | any | how Replace picks per greeting (farewell, menu open): `SpeechPick.Random` (default), `InOrder`, `Seeded(int)`, `By(Func<NpcLineContext, int>)`; set it before Replace |
| `NpcLineKind NpcSpeech.Kind`, `void NpcSpeech.Refresh()` | any | which line (Greeting, Farewell, MenuLine); ask a `By` callback again for every player now |
| `NpcLines Npcs.Farewells(EntityId npc)` | any | this mod's override of the NPC's farewells: `Replace`, `Clear`, `Pick`, `Priority` (no `Suppress`: the menu's speech box shows a line or the game's) |
| `NpcLines Npcs.MenuLine(EntityId npc)` | any | this mod's override of the NPC's own line in its menu: `Replace`, `Clear`, `Pick`, `Priority` |
| `SpeechResult Npcs.MenuSay(EntityId npc, EntityId player, string\|UiText text)` | any | one line in that player's menu with the NPC: now if it is open, else when it opens within 5 s; once |
| `NpcTopics Npcs.Topics(EntityId npc)` | any | this mod's topics on the NPC's talk page: `Set(params NpcTopic[])`, `Add`, `Remove(id)`, `Clear()`, `List` |
| `NpcTopic(string id, string\|UiText label)` | any | `Reply` (UiText?), `Hint` (`NpcTopicHint`), `Close` (the dialogue ends after the Reply), `OnPick` (`Action<NpcTopicContext>`) |
| `NpcTopicContext` | tick | `Npc`, `Player`, `Topic`, `ModId`, `Reply(text)` |
| `NpcLineContext` | tick | what a `By` callback is asked: `Npc`, `Player`, `Kind`, `Opens` (this player's opens of it so far), `Last` (-1: none), `Lines` |

| Member | Thread | What it does |
|---|---|---|
| `SpeechResult Npcs.Say(EntityId npc, string text, TimeSpan duration, SayOptions? options = null)` | any | the text in the NPC's bubble for 0.5 s to 60 s, no voice-over; over any override; a new Say of the NPC replaces it (`SayOptions(Interrupt: false)`: refused while another mod's Say still shows) |
| `SpeechResult Npcs.Say(EntityId npc, UiText text, TimeSpan duration, SayOptions? options = null)` | any | the same with a text per language |
| `void Npcs.StopSaying(EntityId npc)` | any | ends the calling mod's Say before its time |
| `NpcSpeech Npcs.Speech(EntityId npc)` | any | the calling mod's override of the NPC |
| `SpeechResult NpcSpeech.Suppress()` | any | no greeting bubble, no greeting voice-over |
| `SpeechResult NpcSpeech.Replace(params string[] lines)`, `Replace(IEnumerable<UiText> lines)` | any | the NPC picks from these lines (1 to 32, up to 200 characters; a line's languages up to 2048 bytes of UTF-8 together) instead of its own, no voice-over |
| `void NpcSpeech.Clear()` | any | the next mod's override, else the game's own lines |
| `SpeechMode NpcSpeech.Mode`, `int NpcSpeech.Priority`, `EntityId Npc`, `string ModId` | any | this override |
| `SpeechMode Npcs.SpeechOf(EntityId npc)`, `bool Npcs.IsSaying(EntityId npc)` | any | what the NPC does now, from every mod |
| `bool Npcs.CanSpeak(EntityId npc)` | tick | a friendly NPC with dialogue lines (`DynamicNpcState` on a server, `DynamicNpcDialog` on a client) |
| `bool Npcs.Available` | any | this game shows Kiln speech itself (a client with `npcspeech.bubble`); false on a dedicated server, which still sends to its clients |

`SpeechResult(Ok, Reason)`: `Ok` false with a reason when the call was refused (another mod's Say with
`Interrupt: false`; on a joined client, the bubble is not available here). Wrong arguments (EntityId.None, a duration
out of range, an empty or too long text, no line or more than 32) throw `ArgumentException`.

### Rules
- **One override per mod and NPC.** Several mods can each have one. The highest `Priority` shows; on a tie the most
  recent `Suppress`/`Replace`. When it clears, the next one shows, else the game's own lines.
- **One Say per NPC.** It shows over any override and ends at its time, with `StopSaying`, or with a newer Say.
- **It all ends** when the mod unloads (its overrides and Says) and when the world ends (everything). The owner is the
  mod whose code makes the call where Kiln runs mod code (the tick thread, Kiln's tasks, event handlers); a call from a
  thread the mod started itself belongs to no mod (`"kiln"`) and ends with the world, not with the mod. A
  `NpcSpeech` handle keeps the mod it was made for on any thread.
- **Replace picks per greeting** (since 0.2.0-dev.32): a greeting is the bubble showing after at least 1 s hidden (the
  player looked away and back, walked off and back) or the game picking a new line (a dialogue closed); each one shows
  one of your lines as `Pick` says, and the same line stays while the bubble stays up. In 31.16-31.21 the NPC picked again
  only when the game's own pick changed, so an NPC with one greeting (Ramone) kept one line; a 31.21 client joined to a
  dev.32 host still does (it has no `kiln:npcdialog`).
- **Picks**: `Random` (each player's game picks; never the same line twice in a row), `InOrder` (the lines in turn),
  `Seeded(seed)` (a pure mix of the seed, the NPC and the open's number: every client shows the same line for the same
  open), `By(callback)` (the host decides per player, below). Farewells and the menu's line pick the same way, at each
  farewell and each menu open.
- **`SpeechPick.By`**: the callback runs on the host's tick thread, as your mod, and returns the index of the line the
  player sees next, or -1 for the game's own line (the menu's line: no line). It runs one greeting ahead: for every Kiln
  player when the override begins or at `Refresh()`, then after each of that player's opens (the client reports it);
  the player's game shows what it was given last (until the first answer: the game's own line, the menu none, so a
  condition never shows a line you did not choose for that player). A throw or an index out of
  range counts as -1. This is how a condition per player works (a player who fed the NPC gets another line, or none).
- **Farewells**: when a player leaves the NPC's menu, the game picks one of its farewells for the speech box; Kiln
  puts yours there instead. Farewells have no voice-over.
- **The menu's own line**: when a player opens the menu, the NPC says your line in the speech box first (with the game's
  reveal and sound); dismissing it shows the topics, or ends the dialogue when there are none. A craftsperson with a
  menu line opens its menu instead of going straight to crafting, so the line is seen. A reply waiting
  (`MenuSay`) wins over it at that open.
- **Talk pages**: the NPC's own topics stay first and are never removed; every mod's topics follow (this process's own
  mods first, then the host's), at most 16 in all: a mod whose topics do not all fit gets `Ok` with a note saying how
  many are left off (they show when there is room). A topic that a mod on both sides set on the host and on a joined
  client shows once there (the client's own). A topic's `Reply` shows in the speech box when picked; `OnPick` runs
  on the host for the player who picked it, as the mod that set it. The same page again sends nothing. Topic ids are
  per mod (`Remove`, the pick); a page holds them in the order the mods first set theirs.
- **Texts**: a `string` is the same in every language; a `UiText` per language (exact key, the primary language, `en`,
  `*`, as Kiln's item texts; a text with neither `en` nor `*` shows its `UiText` fallback in the other languages). No
  game text entry, no resource patch: Kiln registers the text with kiln.content's loca hooks under a tag of its own.
- **Distinct texts**: kiln.content keeps every text for the session, so a world holds up to 4096 distinct ones (Say
  and Replace lines together, per client); past that a new text is not shown (the call answers `Ok` with a note) and
  known ones still are. Keep numbers, names and timers that change often out of the texts.
- **Sizes**: up to 200 characters per language, and all languages of one line (or one Say) together up to 2048 bytes
  of UTF-8, the size a Kiln client accepts (about three languages of 200 characters). Bigger throws
  `ArgumentException` at the call, not later on the clients.

## Where it shows

| Peer | What it sees |
|---|---|
| The host's own screen (a hosted world) | everything at once |
| A Kiln client (joined to a Kiln dedicated server or a hosted world) | what the host decided: the full state when it joins, every change after |
| A vanilla client | the game's own lines; nothing is sent to it, nothing changes for it |
| A joined Kiln client's own mod | its calls change that player's screen only, over what the server sent (its topics run their `OnPick` on that client) |
| A 31.16-31.21 Kiln client (joined to a dev.32 host) | greetings, Suppress and Say as in 31.21 (a new line only when the game picks a new one); no farewells, menu line, topics or `By` picks |

- **Server mods are enough**: the mod can be `"side": "server"`, `"clients": "none"`. Players need Kiln, not the mod.
  A mod that runs on both sides should decide on the host (`Lifecycle.IsServerRunning`) so the clients' copies do not
  decide again.
- **Who picks**: `Random`, `InOrder` and `Seeded` lines are picked by each player's own game, with no message per
  greeting. `By` lines and topic picks go through the host: the player's game reports each open and pick, the host runs
  your callback or `OnPick` and answers that player only. Players need Kiln 0.2.0-dev.32 for the menu parts and the
  picks; the mod runs on the host only.
- **A player's game reports, the host checks**: `ctx.Player` is the player the network knows for that connection, never
  one a game names. The host takes at most 10 opens (bursts of 20) and 4 topic picks (bursts of 8) a second from each
  player's game and drops the rest (kiln.log says so), so a modified game cannot run your callback or `OnPick` once per
  message. The host does not check that the player stands at the NPC or has the menu open: guard what a topic gives
  (items, quest steps) as you would any player's request.
- Channel `kiln:npcdialog` v1, `required: false` (since 0.2.0-dev.32: Lines, Next, Topics, MenuSay, Reset to clients;
  Opened, Picked to the server). A dev.32 client whose server has it takes greetings from it (with their pick) and
  ignores v1's Speech; a 31.16-31.21 client never sees it and keeps v1 as it was.
- Channel `kiln:npcspeech` v1, `required: false` (messages: Speech, Say, Reset). A dedicated server has no bubbles of
  its own: `Npcs.Available` is false there and the calls still go out to the clients. So do not put the calls under
  `Npcs.Available` or `Capabilities.Is("npcspeech.bubble")`: on a server that skips them, and no player sees anything.
  They carry no `[RequiresCapability]` for that reason and never throw `KilnUnavailableException`.

## What the game does, and what Kiln changes
The bubble is the game client's own HUD: the NPC layer picks a line from the NPC's `NpcDialog` greetings when the NPC
becomes the player's target within 5 m, shows it, then the dialogue's lines while the menu is open (details:
[docs/re/npc-speech.md](../../re/npc-speech.md)). kiln.npcspeech hooks two functions of that layer on the client:

- the per-NPC bubble call: Kiln notes which NPC it is, and while Kiln speaks for that NPC it mutes the voice-overs of
  its greeting lines (and puts them back after);
- the bubble widget: inside that NPC's call only, Suppress hides the bubble, Replace swaps the line, Say shows its
  text.

Hooks go in with the first override or Say. Mods never call game functions.

Since 0.2.0-dev.32, the dialogue menu (details: [docs/re/npc-speech.md](../../re/npc-speech.md) section 6). The menu is
the NPC layer's dialogue window: each frame of the menu the game updates it; when it closes it picks a farewell for its
speech box; when it opens it clears the box (only the Flame altar's NPC says a line there), builds the topic list from
the NPC's options whose condition holds, and sends a craftsperson straight to crafting when it has nothing to say. The
list is not drawn at all when it is empty: that is why a villager such as Ramone has no talk page. kiln.npcspeech hooks
three of its functions on the client, with their own hooks going in at the first use:

- the window's update: after the game's, a farewell of the mod's replaces the game's in the box; at an open, the mod's
  menu line (or a host's reply) shows through the game's own line setter, and the craftsperson stays in the menu;
- the topic list's builder: after the game's topics, the mods' rows, through the list's own grow callback;
- the line call a topic's click makes: a pick of a Kiln topic (by its line id) is reported to the host.

### Limits
- **Kiln clients only** (the text is the client's own data and its own pick).
- **A Say shows where the game draws bubbles**: over NPCs the HUD's NPC layer covers (near the player). Not seen yet:
  how far that is.
- **Suppress** hides the greeting bubble and its voice-over. When a player opens the NPC's menu (E), the server still
  plays the NPC's talk gesture and mumble (its animation, [hyp], not seen); the menu itself is unchanged.
- **Farewells, the menu's line and topics** need Kiln 0.2.0-dev.32 on the player's game (capabilities below); the
  menu's speech box shows one line at a time (a topic's reply, a farewell, the menu's line, a `MenuSay`).
- **Topics**: the same for every player of an NPC (a per-player answer is `ctx.Reply`); 16 per NPC with every mod's; a
  topic without a `Reply` shows nothing when picked until the host answers (and nothing if it does not); `Close` needs a
  Reply. A topic's reply most likely plays the NPC's talk gesture on the server (the game's own select input, [hyp], not
  seen); its id is none of the NPC's lines, so the server sets no knowledge for it (docs/re/npc-speech.md section 6).
- **`By` picks** reach a player's game one greeting late after a change (the answer travels to the client); `Refresh()`
  shortens that to one round trip.
- **A greeting with a condition** (a quest hint) is the bubble's, not the menu's: Replace covers it, and while Kiln
  speaks for the NPC it plays no voice-over. The game's menu shows no greeting of its own (the Flame altar's NPC aside;
  31.16's page said otherwise, read again in 0.2.0-dev.32); `Npcs.MenuLine` adds one.
- **Voice-overs are muted for the next pick**: a greeting the game already picked when your override began (the
  player was looking at the NPC) keeps its voice-over for that one greeting. The transition line ("Anything else?")
  is not muted; no NPC of the game has a voice-over on it (client 26430625).
- **No "would greet now" event**: the game decides that on each client (its target and range), with no server event
  to hook.
- The game's setting "NPC Speech Bubbles" off: no bubbles, also no Kiln ones.

## Capabilities and when something is off
| Row | Meaning | Off |
|---|---|---|
| `kiln.npcspeech` | the service is here (game client) | dedicated server, or an unknown client build: the calls still go out from a host; a joined client's own calls answer `Ok: false` |
| `npcspeech.bubble` | this process shows Kiln's speech in its bubbles (`Npcs.Available`) | a dedicated server (wrong side: it sends, its clients show), or a client build without the bubble functions: kiln.log `npcspeech: npcspeech.bubble is off ...`, NPCs keep the game's lines on that client |
| `npcspeech.voice` | the dialogue record's layout checks out | kiln.log `npcspeech: the dialogue record's layout did not check out ...`: texts change, voice-overs stay the game's |
| `npcspeech.farewell` | the dialogue window's update and its fields check out (`client.d/npcdialog.json`) | farewells stay the game's on that client; kiln.log `npcspeech: npcspeech.farewell did not resolve ...` |
| `npcspeech.menu_line` | ... and the game's line setter | the menu's line and `MenuSay` stay the game's on that client (`MenuSay` to this game's own player answers `Ok: false`) |
| `npcspeech.topics` | the topic list's builder, the line call and the list's layout | no Kiln topics on that client's pages; kiln.log `npcspeech: npcspeech.topics did not resolve ...` |
| `content.loca` | kiln.content's loca hooks | kiln.log `npcspeech: kiln.content's loca hooks ... are not here`: only Suppress works; Replace and Say leave that client the game's lines (the call answers `Ok` with that note, a host still sends to its clients), and the next change of the NPC tries again |

`kiln.log` lines on a client: `npcspeech: ready: bubble 0x..., draw 0x..., voice on; hooks go in with the first
override or Say`, then `npcspeech: bubble hooks in (...)` at the first use.

## Testing your mod (Kiln.TestKit)
The kit has a recording fake of kiln.npcspeech: the kit is the host, and `KilnHarness.NpcSpeech` reads what a Kiln
client's bubble of each NPC would be told.

```csharp
using var h = new KilnHarness();
h.Step();
h.Run(() => Npcs.Say(npc, "I'm starving...", TimeSpan.FromSeconds(30)));
Assert.Equal("I'm starving...", h.NpcSpeech.Saying(npc));
h.Run(() => Npcs.Speech(npc).Replace("No food, no work."));
Assert.Equal(SpeechMode.Replace, h.NpcSpeech.ModeOf(npc));
Assert.Equal(["No food, no work."], h.NpcSpeech.LinesOf(npc));      // LinesOf(npc, "de-de"): German
```

Since 0.2.0-dev.32: `PickOf(npc, kind)` ("Random", "InOrder", "Seeded(n)", "By"), `FarewellsOf`, `MenuLinesOf`,
`TopicsOf` (labels), `RepliesTo(player)` (what `MenuSay` and `ctx.Reply` told that player) and `PickTopic(npc, topicId,
player)`, which runs the topic's `OnPick` as the host would for that player:

```csharp
h.Run(() => Npcs.Topics(npc).Set(new NpcTopic("feed", "Here, have some bread.") { OnPick = ctx => ctx.Reply("Thanks!") }));
Assert.Equal(["Here, have some bread."], h.NpcSpeech.TopicsOf(npc));
Assert.True(h.NpcSpeech.PickTopic(npc, "feed", player));
Assert.Equal([(npc, "Thanks!")], h.NpcSpeech.RepliesTo(player));
```

Not modelled: the game's bubble (target, range, random pick, voice-overs), its menu, a `By` callback's picks (the kit
has no players' games to ask for) and the network. A Say's duration runs on
real time, as on a server: the kit's steps do not move it (use `Npcs.StopSaying`, or a short Say and `StepUntil`).

## Manual test (a real client)
With `mods/samples/HungryCrafter` on the host (a hosted world, or a dedicated server and a joined Kiln client):

1. Stand near a villager, `/hunger starve`: "I'm starving... I can't work like this." over its head for 8 s, no voice.
2. Look away for a moment and back at it: one of the three
   complaints instead of "Hello, Flameborn!"; press E: the same line, no voice-over (a craftsperson's greeting voice
   stays silent).
3. `/hunger feed`: "Thank you, Flameborn! Back to work.", then its own greetings again.
4. German text language: the German lines.
5. A vanilla client on the same server: the game's lines throughout, no error, no disconnect.
