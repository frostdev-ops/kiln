# NPC speech bubbles (greetings, idle lines) in the game client

Request: Marshall (Discord Ryrin), #mod-dev 2026-11-25. Game build: client 26430625 (sha 5d9ef2f1), server 26430253
(sha f0d824be), svn 1287369. Static analysis only (capstone over `/mnt/Dongus/kiln-wave/sfi/out/binaries` copies, Ghidra
headless decompile of single functions, `tools/kfc` over the client's data). Tags: **[data]** read from game data,
**[dis]** read in the code, **[hyp]** inferred, not seen.

## 1. Where the bubble text comes from

- **[data]** The bubble lines are the NPC's own **`NpcDialog` lines**, nothing else: `NpcDialog.dialogResource`
  (`keen::NpcDialogResource`) `initLines` (greetings), `transitionLines` ("Anything else?"), `farewellLines`. Each line
  has `bodyTexts: BlobArray<LocaTagId>` (variants) and a parallel `voiceovers` array.
  Ramone Jones (`Townsfolk_Male_villager_02_RamoneJones`, `fe2ce3af-...`): init lines `723637513` (his wake-up line,
  `isWakeup`), `2262475715` "Hello, Flameborn!", `3935509066` "Our hero!", `934215426` "Good day to you!"; transition
  `2803405929` "Anything else?"; farewells `653977772` "See you soon!", `899690196` "Praise be!", `1310400781` "Farewell!".
  All his voiceovers are null.
- **[data]** Who has them: every villager (`Townsfolk_*`), every craftsperson and assistant (`NPC_Workshop_*`), the
  trader and the Bard: all 55 templates with `NpcState` also have `NpcDialog`. "No talk page" on a base villager means his
  `dialogOptions` are all gated by knowledge requirements, not that he has no lines.
- **[data]** `NpcState.defaultGreetingSequence` / `attentionGreetingSequence` and `ActiveNpcState.socializingSetup.sequences`
  are **animations** (Ramone's `a0df25b9` "NPC_Townfolk_Greet": one SetAnimationEvent). They hold no text. The mumble a
  villager makes is the `VoiceoverNotifierEvent`s of the talk sequence (`51f5b029` "NPC_Townfolk_Talk": 34 notifiers,
  chosen per voice by entity tags), which the server plays as the dialogue's `defaultSequence`.
- So "the game's existing lines" are loca ids; a Kiln text needs a loca id the game can look up. kiln.content's loca
  hooks already answer ids Kiln registers (`kc_nc_set_texts`, the same path as new items' names, per language), so a mod
  never needs a game text entry or a resource patch.

## 2. Who picks a line and who draws it (client) **[dis]**

Everything about the text is **client-side**. The server never sends a line's text.

1. `ui_dialog` (client program #684, `0x460b40`; template NpcDialog, read RenderTransform/UiOffsets, write
   DynamicNpcDialog and G38UiPlayData) keeps one **record per NPC** (0x14b80 B) in a hash map at
   `G38UiPlayData+0x463de0` (find `0xd28db0(map, &entityId)`; key hash `x^=x>>16; x*=0x45d9f3b` twice). It sorts the
   NPC's lines into lists of 0x294-byte entries `{u32 lineId; u32 bodyTexts[32] @+8; u8 nTexts @+0x88; Guid vo[32]
   @+0x8c; u8 nVo @+0x28c}`: init lines with a marker/requirement ("priority") at `+0x90/+0x98` (ptr/count), plain
   greetings at `+0x5338/+0x5340`, transitions `+0xa5e0/+0xa5e8`, farewell ids `+0xf888/+0xf890`, options
   `+0xf8d0/+0xf8d8`. The record is rebuilt only when it is new or the knowledge counter (`DynamicNpcDialog
   .syncedKnowledgeQueryCounter`) changes. Records are never removed while the map lives, and they are **inline**: when
   the map grows (find-or-insert `0x459230` → `0x45c1f0`, x4), `0x459590` copies every record to the new storage (its
   lists' entries as they are, the knowledge counter too, so no rebuild follows) and the NPC layer passes the new
   address from then on. A line entry's `+0` is its line id (PickLines reads it into the slot); review 2026-11-26.
2. The HUD's **NPC layer** (`doNpcUIWindow`, "[GameUi] NPC Layer", `0xc48d70`) calls the **per-NPC bubble** function
   `0xcc7760(entry, layer, slots, pos)` for each nearby NPC (`entry+0` entity id, `entry+8` the record). It runs a small
   state machine per NPC: idle → (the NPC is the player's interaction target, within **5 m**, `0x13d45e4`) pick lines
   (`0xd99e10(slot, record, storeGlobal)`: a random entry of the priority list, else of the plain greetings, and a random
   body text of it; also a transition line) → show the greeting → while the dialogue screen (HUD 26) is open show the plain
   greeting → after it, the transition line. The line ids go out as a local event `{entity, lineId, voiceover}`
   (`0xb0f2c0`), which is how `play_dialog_voiceover` plays a line's voice-over **[hyp: the event is
   SelectDialogOptionEvent; its layout matches]**.
3. The bubble itself is drawn by `0xd0dda0(BubbleParams*)`: `+0 u32 text (LocaTagId)`, `+4 u8 show` (fades to
   shown/hidden), `+5 u8 reset` (hide at once), `+6 u8 kind`, `+8 f32`, pointers at `+0x10/+0x18`. A text id 0 keeps
   the bubble hidden (`0xcc7760` clears `show` when the text is 0). The game's settings `hideNpcSpeechBubble` and the
   UI's own hide flags are checked by the game before or inside this; Kiln does not bypass them.
4. The dialogue window's own state (`0xd95ca0`) takes the plain greeting and a random farewell id from the same record
   when the dialogue opens and closes.
5. **Voice:** craftspeople's lines carry real voice-overs (the record's `vo[]`); villagers' are null and only mumble
   through the talk sequence, which the **server** plays when a player picks/opens a line (`npc_behavior`, creatures.md
   7.3 step 4).

Not followed: whether `0xb0f2c0`'s event reaches the server as `SelectDialogOptionAction` (so a Suppress may still
show the talk gesture when a player presses E); the NPC layer's own range (which NPCs get an entry). The farewell of
`0xd95ca0` is drawn in the dialogue window's speech box (section 6).

Review 2026-11-26 **[dis] [data]**: the voice-over events (`0xb0f2c0`, type 0x55 `{+4 entity, +8 line, +0xc Guid}`) carry
the GUID the slot copied at pick time: the priority line's at the next `PickLines`, the plain greeting's when the
dialogue opens (`0xd49c50`, and `0xd95ca0`'s own pick or the copy at `+0x30974`), the transition line's after it
closes (`0xd49d00`). A greeting picked before Kiln muted the record keeps its voice for that one greeting. The
transition list (`+0xa5e0`) is not muted; none of the 41 villager / workshop templates has a voice-over on a
transition line (`tools/kfc` decode of each). The menu shows a priority line's text itself (`0xd80ab0`: window
`+0x2b0`, the voice event from a copy of the record's entry), the farewell likewise (`0xd80a30`). The bubble's
params are a stack copy NpcBubble builds for each call (`[rbp-0x60]` → `[rbp-0x40]`), so Kiln's writes never outlive
the draw; BubbleDraw's other caller (`0xc442bb`) is outside NpcBubble.

## 3. Greeting range and cooldowns (server) **[refl] [data]**

`npc_behavior` (server #294) writes the player's `NpcDialogPartner {isInGreetingRange, lastGreetingTime}` and the NPC's
`DynamicNpcState {nextGreetingTime, greetedPlayers (PlayerMask), playersInSight}` and plays `defaultGreetingSequence`
(the wave). `NpcState.minDistanceToPlayer` 2 m, `idleSetup` cooldown 15-30 s (Ramone). This is animation only; it is
replicated as the actor's sequence. The text bubble does **not** depend on it: the client shows the bubble from its own
target/range test.

## 4. What Kiln hooks (kiln.npcspeech)

- The host cannot make a vanilla client show a different text: the text is the client's own template data and its own
  random pick. **Kiln clients only**; a vanilla client keeps the game's lines (nothing sent to it, nothing breaks).
- The decision is the host's; it travels as data (per NPC: mode + texts) on a Kiln channel; each Kiln client applies it
  where the game draws: the bubble's text id and visibility, and the record's voice-over GUIDs.
- Hooks (client, Kiln owns them; mods never call game functions):
  - `npcspeech/NpcBubble` `0xcc7760`: entry detour. Notes the NPC (entity, record) for the draw below; for an NPC
    under Kiln speech it mutes the record's voice-overs (priority + plain greetings) and puts them back when the
    override ends.
  - `npcspeech/BubbleDraw` `0xd0dda0`: entry detour. Inside an NPC's bubble call only: Suppress → `show` 0;
    Replace → the shown text becomes one of the mod's lines (a new pick each time the game picks a new line); Say →
    the mod's text, `show` 1, for its duration (also when the NPC is not the target: every NPC the layer draws).
  - `npcspeech/PickLines` `0xd99e10`: anchor only (never hooked/called): the record's list offsets are captured from
    its code, so a moved layout turns the voice part off instead of writing at a stale offset.
- Graceful degrade: a symbol or capture that does not resolve lowers `npcspeech.bubble` (no bubble change) or
  `npcspeech.voice` (texts change, voices stay the game's), with one kiln.log line. No text hook
  (`content.loca`/kiln.content missing): `npcspeech.bubble` lowered too, as Kiln's texts could not be shown.

## 5. Reproduce
- Lines and texts: `python3 tools/kfc/kfc.py decode fe2ce3af-e771-4179-9450-f2dee8d21209 keen::ecs::TemplateResource` (Ramone's
  NpcDialog), the En_Us table of the `LocaTagCollectionResource` (`kfc.mapmarkers.loca_table`) for the ids; sequences with
  `decode <guid> keen::actor::ActorSequenceResource`.
- Code: Ghidra headless decompile of single functions (no auto-analysis) of the client copy: `0x460b40` ui_dialog,
  `0xcc7760` NpcBubble, `0xd99e10` PickLines, `0xd0dda0` BubbleDraw, `0xd95ca0` the dialogue window's lines, `0xd28db0`
  the record map's find; callers by rel32 scan. sigdb: `python3 tools/sigport/sigdb_check.py sigdb/symbols/client.json
  <enshrouded.exe>` resolves `npcspeech/*` and both features on 26430625.
- Usage and status: [docs/kiln/services/npc-speech.md](../kiln/services/npc-speech.md).

## 6. The dialogue menu: farewells, its own line, the talk page (dev.32) **[dis] [data]**

Lane npc-dialogue (2026-11-27), same client build; capstone and read-only Ghidra decompiles of single functions.

**Why a Replace stayed on one line.** NpcBubble's state per NPC (a 0x50-byte slot in the NPC layer plus the UI
element's state): 1 idle -> 2 when the NPC becomes the target within 5 m (PickLines picks once) -> 3 while the dialogue
(HUD 26) is open -> 5 after it closes (PickLines again; the transition line from then on). Nothing returns to 1: looking
away only hides the bubble, so the pick stays until the NPC leaves the layer or a dialogue closes. Ramone has one plain
greeting and no priority one, so the game's text never changed and 31.16's Replace (a new pick when the game's text
changes) kept its line. Since dev.32 a greeting is also the bubble showing after 1 s hidden.

**The dialogue window** is the NPC layer's UI element (doNpcUIWindow `0xc48d70`, element hash `0xd9924a0cc74e608a`), W.
`DialogUpdate` `0xd95ca0`(W) runs every frame of HUD 26 (and for the pre-altar Flame speech window, `0xbc3f70`/`0xc69f50`):
- close request `W+0x16`: a random farewell id of the record (`+0xf888`, count `+0xf890`), else a default one, through
  `SetLine` `0xd80a30`(W, tag): `W+0x2b0` = the text, the reveal restarts (`W+0x7e0`), `W+0x3ce0` = 0 (the box shows),
  the UI's line sound. Its voice event has a null GUID: farewells have no voice-over. SetLine's four call sites are all
  farewells (two in `0xd8ce30`, which nothing calls).
- open request `W+0x15` (doNpcUIWindow sets it while not in HUD 26): `W+8` = the record of `play.target` (`ui/play.target`
  0x2c50, the dialogue partner); none: return 0 and the caller closes the dialogue. `record+4` is `NpcDialog.isFlame`:
  not Flame, the box is cleared (`W+0x2b0` = 0, `W+0x3ce0` = 1); Flame, a random priority greeting through `SayLine`
  `0xd80ab0`, none: return 0. Then `BuildTopics` and `W+0x14` (straight to crafting: a crafting entry, no topic, a happy
  NPC and the player's setting).
- The box is `flameSpeechBox` in the dialogue window `0xb8f690` (the NPC's name and `W+0x2b0`); dismissing it with no
  topic ends the dialogue. So the menu's own line exists only for the Flame altar's NPC; for everyone else the box shows
  a topic's reply and the farewell (31.16's note that "a greeting with a condition shows in the menu" was the bubble's
  state 2).
- `SayLine`(W, entry): a random body text of a line entry into the box, its voice event {play.target, line id, GUID};
  `W+0x1c` keeps the entry (its end action PlayOutro closes the dialogue after the line).

**The talk page.** `BuildTopics` `0xd762b0`(W) copies the record's options (`+0xf8d0`, those whose requirement holds)
into W's topic list (`W+0x12a8`: ptr, count `+8`, cap `+0x10`, grow callback `+0x18` = bool(list*, -needed, 0)); a topic
is 0x2a0 bytes: kind byte (0: say the entry; else a HUD screen), the 0x294-byte line entry at +4 (line id, option text
+4, body texts +8, count +0x88, voice-overs +0x8c, count +0x28c, hint +0x290, start/end action +0x291/+0x292), a pointer
at +0x298. It adds the crafting entry (`W+0x808`, `dialogResource.crafting`) and special topics (`W+0xaa0`, from the
player's NPC info flags). It runs at an open and every frame doVerticalTopicListDialogMenu (`0xb8ffc0`) draws, which
**draws no list at all when options, crafting entry and special topics are empty**: Ramone's two options are one-time
topics gated by named queries (Extern 2239375158 and 1760803964, Equals 0) and his `crafting` text is 0, so E shows his
plain greeting in the bubble and nothing else. A topic's click: kind 0, SayLine(W, a copy of its entry).

**The server's half.** A reply's voice event most likely becomes `NpcDialogClientSync` and the player's
`SelectDialogOptionAction {line id, npc}` (creatures.md 7.3, **[hyp]**: the event's layout matches): npc_behavior plays the
talk sequence;
`unlock_knowledge_from_dialog_option_input` (`0x1fb380` on 26430253) pushes knowledge only when `FindDialogLine` finds
the id among the NPC's own lines (an unknown id: nothing).

**What Kiln hooks** (client, `client.d/npcdialog.json`): DialogUpdate (after the game's update: a farewell of Kiln's into
W's box after a close request; a menu line of Kiln's through SetLine after an open request, W+0x14 cleared), BuildTopics
(Kiln's topics after the game's, through the list's own grow callback) and SayLine (a Kiln topic's pick by its line id).
SetLine is called, never hooked. The dialogue's NPC is `play.target` (UiApp -> ui.data -> state -> state.play, as kiln.ui
reads it). Scripts and decompiles: `/mnt/Dongus/kiln-wave/dev32/out/npc-dialogue/re/`.
