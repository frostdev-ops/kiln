# The game's chat field (kiln.ui chat history)

Client build 26430625 (`enshrouded.exe` sha256 `5d9ef2f140b8ed49…`). Static analysis of the exe (capstone, read as a
file), and in-game traces by bisdat (GitHub issue 44, fork `bisdat/kiln` pull request 2, Proton, 17 Nov 2026) for
what only a running game shows. Definitions: `sigdb/symbols/client.d/chatui.json` (feature `chat.ui.input`). Code:
`native/services/ui/chat_input_probe.cpp`. Player page: [docs/kiln/chat-history.md](../kiln/chat-history.md). The
server's side of chat: [chat.md](chat.md).

## Where the field is processed
```
GameUiFrame (UI thread, once per UI frame)
  HudChatWindow 0xcdc320       the HUD chat (HUD screen 43); its input only while the chat is open
    0xcdd674 call TextInputProcess(widget = rbp+0x100, &result = rbp+0x25b0, &"Send to chat", 0)   ret 0xcdd679
    result.event 1 (Enter) and length > 0: QueueChatMessage 0xd72d90(UiApp, &caller string, 0, 0, 0), then the clear
    either event: toggleHudScreen 0xd74850(UiApp, 1)       Enter sends and closes; Escape closes
  SocialChat 0xcbe6d0          the in-game menu's Social tab
    0xcc0777 call TextInputProcess(widget = rbp+0x890, &result = rbp+0x83f8, &"Send to chat", 0)   ret 0xcc077c
```
`TextInputProcess` 0xd2a170 serves every text field of the game: 14 direct calls in the exe. None of the callers reads
the returned `rax` (it is `&result`); each reads `result` again. Kiln acts only on the two chat calls, told apart by
the return address.

## TextInputProcess(widget, &result, const GameText* label, u8 mode) -> &result
- `result` is 8 bytes: `u32 event` (0 nothing, 1 Enter, 2 Escape) and a `u8` at +4. The editing callee
  `TextInputEdit` 0xd4ae60 zeroes both on entry and, on Enter or Escape, writes the event (`neg; sbb; add 2`) and
  clears the field's editing byte in the same place. The callers compare the whole dword (HUD: `test eax, eax`,
  `cmp eax, 1`; Social: `cmp dword [rbp+0x83f8], 1`).
- The widget: `TextInputCtor` 0xb232c0(widget, &buffer, style, mode) stores `widget+0x150 = &buffer` and
  `widget+0x158 = buffer.capacity`; `widget+8` is its layout node. The chat's buffer is a `StringHead`
  `{char* data, u64 length, u64 capacity}` of UTF-8 on the caller's stack (capacity 0x400), and it is exactly what the
  caller sends on Enter (`QueueChatMessage` gets `{data, length}` of it).
- `TextInputEdit` passes the caller's string, its capacity and the byte at widget+0x160 to the field each frame
  (`0xd28050(state, *(widget+0x150), *(widget+0x158), *(u8*)(widget+0x160))`). In the fork's traces the caller's
  string read before `TextInputProcess` could still be the previous frame's (an empty draft), and was current after
  it: Kiln reads it after the call.

## The field's state block
`TextInputState` 0xb09760(widget, 0x72615e88968a8b89, 1) returns it: the UI's hash-keyed state store (0x11a06e0,
keyed by the node `widget+8` and the hash, 0x1990 bytes, created and initialised on first use, touched every frame,
freed when the field is not drawn for a frame; closing the chat loses the typed text, as in vanilla).
`TextInputProcess` and `TextInputEdit` ask for it with the same arguments every frame.

| Offset | What |
|---|---|
| state+0x10 | the editor (below) |
| state+0x1980 | f32 horizontal scroll; the HUD chat sets it to 0 after its clear |
| state+0x1988 | u8 editing: set while the field has typing focus; cleared with an Enter/Escape event |

The editor (state+0x10): `+0` data, `+8` length **in characters**, `+0x10` capacity, inline storage from `+0x28`;
`+0x128` the undo history (0x1810 bytes); `+0x1938` cursor; `+0x193c` length limit and `+0x1940` its flag (set from the
widget's capacity); `+0x1944`, `+0x1948` the selection; `+0x1962` "changed". **The text is UTF-32**: the UTF-8
decoder 0x88c450 appends one u32 per character (`mov [rax+rcx*4], ebx`). Read as bytes, "/help" shows as `/`, three
zero bytes and `h`: the "3 control bytes" of the fork's traces. Kiln never reads the editor's text.

## EditSetText(&result, editor, const {char* data, u64 bytes}* utf8) -> &result
0x11ce250, the game's own replace-the-text. It sets the word at `+0x1960` to 1, sets "changed" when the cursor or the
length moves, clears the undo history (a 0x1800-byte zeroed block copied into `+0x128`), sets the length to 0, decodes the UTF-8 into the
editor (0x88c450), cuts the length to the limit when the limit flag is set, and puts the cursor and the selection at
the end. 0x1830 bytes of stack (`__chkstk`).
- **Result: 8 bytes**, `{u8 error, u32 at +4}`. On success EditSetText writes `dword [result+4] = 0` and
  `byte [result] = 0`; on a decoder error it copies the decoder's 8-byte result (`qword`, error byte non-zero, for
  example 0x27). The decoder itself writes only `byte [r]` and `dword [r+4]`. The callers' result slots are 8-byte
  class too (HUD reuses the event slot at rbp+0x25b0, next variable at +0x25c8). Kiln passes a 64-byte buffer and
  treats a non-zero error byte as a failed replace.
- The chat's clear after Enter (HUD 0xcdd6f9, Social 0xcc07f9): `length` of the caller string = 0;
  `state = TextInputState(widget, hash, 1)`; `EditSetText(&slot, state+0x10, &{"", 0})`; the HUD also writes
  `state+0x1980 = 0`. Kiln's replace is the same call with a history line, before `TextInputProcess` of that frame,
  so the frame's own processing copies the editor into the caller's string.

## Keys and characters
From the dossier ([dis], the field's key table 0x11db250; keen UI key codes): Enter submits, Escape cancels,
Left/Right move (Ctrl: by word), **Up/Down in a single-line field do what Left/Right do**, Home/End, Backspace,
Delete, Insert combinations, Ctrl+A/C/X/V/Z/Y. Tab is not bound, but the character path 0x11b6cb0 inserts `'\t'`
(it drops the other control characters). Keys reach the game as raw input in its window procedure, characters as
`WM_CHAR` (docs/re/client-render.md); kiln.ui's window procedure sees both first. The exe imports no IMM32: an IME's
composition is Windows' default IME window, and raw input still reports the arrows the IME uses for its candidates.

## What Kiln's sigdb captures (chatui.json)
| Name | 26430625 | Captured in |
|---|---|---|
| `chatui/TextInputProcess`, `TextInputState`, `EditSetText`, `HudChatWindow`, `SocialChat` | 0xd2a170, 0xb09760, 0x11ce250, 0xcdc320, 0xcbe6d0 | prologues |
| `chatui/HudInputReturn`, `SocialInputReturn` | 0xcdd679, 0xcc077c | the call in each scope, followed by the event test |
| `chatui/TextInputEdit` | 0xd4ae60 | rel32 of TextInputProcess's call |
| `chatui/state.hash.lo`, `.hi` | 0x968a8b89, 0x72615e88 | TextInputProcess, TextInputEdit, the HUD clear (3 captures; their rel32 also checks TextInputState) |
| `chatui/state.edit` | 0x10 | the HUD and Social clears (their rel32 also checks EditSetText) |
| `chatui/state.editing` | 0x1988 | TextInputEdit, where the event is written |
| `chatui/widget.buffer` | 0x150 | TextInputEdit's two reads of the widget |

`native/services/ui/tests/chat_sigdb_test.py` checks these against an installed client exe. After a game update,
sigport moves the patterns; if a capture no longer matches, `chat.ui.input` is off and the chat field stays vanilla.
Still to recheck by hand on a new build: the `StringHead` and `TextSlice` shapes, the result layouts above, and that
the editor stays UTF-32.

## What the fork's game tests showed (26430625, Proton)
Both call sites are reached from the HUD chat and the Social tab; the caller's string holds the submitted line
(widget bytes = the command, no control bytes); `EditSetText` followed by `TextInputProcess` shows the new text and
the caller's string reads it back; Up/Down recall and the draft come back in both fields; Enter, Escape, Alt+Tab and
gameplay arrows are unaffected; commands survive a restart.
