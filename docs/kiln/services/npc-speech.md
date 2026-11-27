# Kiln.Npcs: your lines in the game's NPC speech bubble

A mod puts its own text in the game's own speech bubble over a friendly NPC: a villager (Ramone, Hannah, ...), a
craftsperson (the Blacksmith, the Alchemist, ...) or an assistant. Two things, both decided by the host and shown by
every player whose game runs Kiln:

- **Say once**: `Npcs.Say(npc, "I'm starving", TimeSpan.FromSeconds(6))` shows the text over the NPC's head now, with
  no voice-over, also when the NPC is not the player's target.
- **Speak differently while a condition holds**: `Npcs.Speech(npc).Suppress()` silences its greeting bubbles,
  `.Replace("No food, no work.", ...)` makes it greet with your lines instead of its own, `.Clear()` gives the game's
  lines back.

Requested by Marshall (Ryrin) for survival add-ons (NPCs that eat; craftspeople that stop working when hungry).

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_npcspeech.h` (`kiln.npcspeech` v1, game client only) |
| Native | `native/services/npcspeech/` → `kiln/services/kiln-npcspeech.dll` (`npcspeech_core.h`: the rule inside the game's calls) |
| Managed | `managed/services/Kiln.Blocks/Npc/Speech/` (`Npcs`, `NpcSpeech`, `SpeechMode`, `SpeechResult`, `SayOptions`; channel `kiln:npcspeech`) |
| Symbols | `sigdb/symbols/client.d/npcspeech.json` (features `npcspeech.bubble`, `npcspeech.voice`) |
| Sample | `mods/samples/HungryCrafter` (`/hunger starve|feed [all]`) |
| Tests | `native/services/npcspeech/tests/run.sh` (host: the bubble rule and the voice-over mute on fake structs), `managed/tests/Kiln.Blocks.Tests/NpcSpeechTests.cs`, `managed/tests/Kiln.TestKit.Tests/NpcSpeechFakeTests.cs`; lab: `mods/tests/npcspeech-test` (console driver) + `tools/nettest/npcspeechtest.py` (Kiln and vanilla lab peers) |
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
- **Replace picks per greeting**: each time the game would pick a new line, the NPC shows one of yours (at random among
  them); the same pick stays while the game shows the same line.
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
| A joined Kiln client's own mod | its calls change that player's screen only, over what the server sent |

- **Server mods are enough**: the mod can be `"side": "server"`, `"clients": "none"`. Players need Kiln, not the mod.
  A mod that runs on both sides should decide on the host (`Lifecycle.IsServerRunning`) so the clients' copies do not
  decide again.
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

### Limits
- **Kiln clients only** (the text is the client's own data and its own pick).
- **A Say shows where the game draws bubbles**: over NPCs the HUD's NPC layer covers (near the player). Not seen yet:
  how far that is.
- **Suppress** hides the greeting bubble and its voice-over. When a player opens the NPC's menu (E), the server still
  plays the NPC's talk gesture and mumble (its animation, [hyp], not seen); the menu itself is unchanged.
- **Farewells** ("See you soon!") are drawn by the dialogue menu's own path, which Kiln does not change yet.
- **The menu's own line**: when the NPC has a greeting with a condition (a quest hint), the menu (E) shows that line
  itself, as the game wrote it; the bubble above shows yours. While Kiln speaks for the NPC that line plays no
  voice-over either.
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

Not modelled: the game's bubble (target, range, random pick, voice-overs) and the network. A Say's duration runs on
real time, as on a server: the kit's steps do not move it (use `Npcs.StopSaying`, or a short Say and `StepUntil`).

## Manual test (a real client)
With `mods/samples/HungryCrafter` on the host (a hosted world, or a dedicated server and a joined Kiln client):

1. Stand near a villager, `/hunger starve`: "I'm starving... I can't work like this." over its head for 8 s, no voice.
2. Walk well away (out of the settlement, so the game forgets its last pick) and back, target it: one of the three
   complaints instead of "Hello, Flameborn!"; press E: the same line, no voice-over (a craftsperson's greeting voice
   stays silent).
3. `/hunger feed`: "Thank you, Flameborn! Back to work.", then its own greetings again.
4. German text language: the German lines.
5. A vanilla client on the same server: the game's lines throughout, no error, no disconnect.
