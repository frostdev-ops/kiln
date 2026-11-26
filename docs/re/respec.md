# The skill respec's payment and the screens that decide it (GitHub #150)

Builds: game client Steam 26430625 / svn 1287369, `enshrouded.exe` sha256 `5d9ef2f1…0c77` (the pinned client), and the
dedicated server Steam 26430253 / svn 1287369, `enshrouded_server.exe` sha256 `f0d824be…aeb2`. Image base `0x140000000`;
addresses are RVAs, client `C`, server `S`. Read from the exe files only (never run): capstone disassembly, the offline
resolver (`tools/sigport/sigdb_check.py`), the reflection dump in `sigdb/types/client/<sha>.json`, the build records. Tags
as in the other RE pages: **[dis]** disassembly, **[refl]** reflection, **[sig]** the sigdb rows
`sigdb/symbols/server.d/respec-sources.json` and `client.d/respec-sources.json`, qualified by
`native/services/items/tests/respec_sources_sigdb_test.py` (61 checks on both exes). The skill tree itself:
docs/re/progression.md section 3, docs/re/abilities.md "Expanding the skill tree".

## 1. Where the game charges the respec item **[dis]**
`player_skilltree` (S `0xd4780`, C `0x2e87e0`) handles the version-checked `UnlockSkillAction {skillId +0, unlearnSkill
+4, resetAllSkills +5, versionData +8}` **[refl]** from `PlayerInput.fromClient` (+0x180..+0x18b):

| Action | S | C | Payment |
|---|---|---|---|
| unlearn one node | `0xbbab0` (call S `0xd4962`) | `0x2cfb10` (call C `0x2e89c2`) | SkillNodeSet off, rebuild, connectivity, then the payer with the node's cost (`SkillNodeById(...)+0x84`, a u16); a refusal sets the node again (`NodeSet(..., unlock 1)`, S `0xbbc38`) |
| reset all (`skills/Reset`) | `0xb9a00` (call S `0xd4944`) | `0x2ce370` (call C `0x2e89a4`) | `PointsSpent` (S `0x9222f0`), then the payer with that sum **only when its pay flag (dl) is set**: `test sil,sil; je` (S `0xb9a96`); a refusal leaves Reset without resetting (`test al,al; je` S `0xb9aa6`) |
| the join check's reset | `0xb9a00` from S `0xd48a7` | from C `0x2e8907` | `xor edx,edx` before the call: pay 0, **free**, the payer is never called |

The action's reset passes `!free-skills` (S `0x9398f0`, `xor al,1; movzx edx,al`), so it is paid unless the world's
free-skills setting is on. The respec payer has exactly these two callers on each exe (the unlearn and Reset) **[sig]**.

## 2. The payer: `respec/Payer`, S `0xb9230`, C `0x2cdce0` **[dis] [sig]**
`bool Payer(iterator* it, u16 cost)`, byte-identical on both builds except the rel32s:
- `movzx ebx,dx; test bx,bx; jne; mov al,1; jmp ret`: **cost 0 is paid at once**, no transaction;
- an inventory-transaction setup on the stack (`rsp+0x60`, docs/re/items.md section 3): flags 0, **stock mode 2**
  (`mov word [rsp+0x60],0x200`), the iterator's accessors (`[rcx+0xc8]`, `+0xb0`, `+0xe8` ... `+0x110`), no Fuel accessor
  (setup `+0x70` = `rbp-0x30` stays zero, as in the blueprint program's), the bases word (setup `+0xd8` = `rbp+0x38` =
  `[it+0xc0]`) and the player's position (setup `+0xe0` = `rbp+0x40`, the transform scaled); then `InventoryTxCtor`
  (S `0x3ed10`) and `InventoryTxInit` (S `0x1b9300`) with the transaction at `rbp+0x50`;
- one 24-byte cost line at `rsp+0x40`: item `+0x10` = **`0x41a792dc`** (`respec/item`, Runes,
  `Material_T1_Token_WeaponUpgrade_Rune01`), count `+0x14` = the cost; `{ptr rsp+0x40, count 1}` at `rsp+0x30`;
- the player: `world/CurrentEntityId(*it)` (S `0x65b870`);
- **`rules/InventoryPay(tx = rbp+0x50, lines, player, 1, 0)`**; the return address is **`respec/RespecPaid`** = S
  `0xb93c3`, C `0x2cde73`. 0: `items/InventoryCommit` and answer 1 (paid); anything else: `items/InventoryRollback`,
  answer 0 (refused).

InventoryPay in stock mode 2 (docs/re/stations.md section 10.4) pays the line from the player's bag, then from the Magic
chests of the base the player stands in (`stations/BaseStockForPlayer`, behind `CanAccessInventories`), and answers 4 when
they are short. So today the respec already counts and pays the base's Magic chests; it never sees a crafting source
registered with `Crafting.AddSource`, which only the recipe payment (`rules/RecipePaid`) and Kiln's other adapters ask.
The Runes id appears once in each exe's `.text`, in this payer (client: once more in `.rdata`, a data table) **[dis]**.

## 3. The screens that decide whether a respec is sent (client only) **[dis] [sig]**
A respec reaches the server only as `UnlockSkillAction` in the client's input, written from two UI commands (the UI
dispatcher C `0x85c7f0`, jump table `0x85e6e0` / `0x85e578`): **`0x4f`** (case at C `0x85d4ea`) becomes
`UnlearnSkillEvent` (`0x466b67a5`) and **`0x4e`** (C `0x85d492`) `UnlockSkillEvent` (`0x20a1960c`); the input copy (C
`0x3b1258`..`0x3b13af`) writes `skillId`, and for `0x4e` sets `resetAllSkills` when the skill is 0 (`cmp [rdi],0; sete al;
mov [r14+0x17d],al`, C `0x3b12ce`), for `0x4f` `unlearnSkill` = 1 (C `0x3b1399`). Who writes them:

- **The node details on the respec skill tree** (`HudScreen.SkillTreeRespec`; C `0xcb7890`, called from the tree
  screen C `0xcb6e60`): `movzx r14d,word [rax+0x1c]` (the node's cost), the local player's UI inventory mirror (C
  `0xd40fe0`), then `stations/client.UiAvailableCount(item = [G38UiPlayData+0x44b238], mirror, published Magic-chest map)`
  (C `0xd397d0`, the bag plus, in a base, the base's published chest stock: docs/re/stations.md section 10.3) returning at
  **`respec/client.NodeCountRet`** = C `0xcb7e98`; `mov esi,eax; ... cmp esi,r14d; jae` sets `bl` = affordable, and only
  then the node offers action 2 (C `0xcb81cf`: `test bl,bl; je; ... mov cl,2; call 0xd91830`). The tree's action handler
  (C `0xd4fc80`, called from C `0xcbb2c5`) sends command `0x4f` with the node's id on that action (C `0xd4fe3f`..`0xd4fedf`).
- **The Flame Altar's reskill dialog** (`HudScreen.AltarReskill`; C `0xc7ece0`, called from the altar screen C
  `0xbc3f70`): the same count of `[G38UiPlayData+0x44b238]` returning at **`respec/client.AltarCountRet`** = C `0xc7eeb4`,
  then `cmp eax,[G38UiPlayData+0x44b23c]` (the points spent), `setae [rbp+0x60]` (met); the reset button is enabled only
  with met and a cost above 0 (C `0xc7f8f0`..`0xc7f903`); its confirmation sends command `0x4e` with skill 0 (C
  `0xc7fbaf` -> C `0xb16300`, `mov [rax+4],r12d` with r12d 0), i.e. `resetAllSkills`.
- **The respec tree's Rune row** (C `0xcb9730`, the tree's respec mode): the same count at
  **`respec/client.HeaderCountRet`** = C `0xcb9cb4`, `mov [rbp-0x48],eax`: the number the row shows. Display only.

`player_ui` writes `G38UiPlayData+0x44b238` (the respec item, from its config, C `0x2b9763`) and `+0x44b23c` (the cost,
C `0x2b98df`) every frame **[dis]**; the constants are `respec/client.ui.item` / `respec/client.ui.cost` **[sig]**. So
with Runes only in a linked chest, a vanilla client's unlearn is never offered and the altar's reset stays greyed: nothing
is sent. The server has no screen check of its own: a respec that reaches it is paid or refused by the payer alone.

## 4. What kiln-items does (since 0.2.0-dev.31.18, capability `crafting.respec_sources`)
- **The payment (server world):** no hook of its own. kiln.rules' `InventoryPay` hook (installed for crafting sources)
  hands every call to kiln-items' payment adapter; a call returning to `respec/RespecPaid` is the respec branch. With a
  link of that player registered with `Respec` (`KILN_CRAFT_SOURCE_RESPEC`), the transaction the server world's (its
  bases word is the server's PlayerBasesSystem, its InventorySetup and Inventory accessors check out:
  `access_from_tx_reads`, the payer's transaction has no Fuel accessor), the adapter counts the bag and the base list
  (`BaseStockForPlayer`, the game's own order), then the respec links (every container once, the filter, the player's
  login and `CanAccessInventories`, an initialized ordinary container; a link to one of the base's Magic chests is
  counted with the base, not twice). Covered by the bag and the chests: the game pays as always, nothing from the links.
  Short by more than the links hold: the game refuses (`respec:` line with the shortfall). Otherwise the adapter pays the
  line itself through `gems/PayMaterial` in the same transaction: the bag, the chests, then the links in link order, and
  answers 0 (the payer commits); a refused or faulting take answers 4 (the payer rolls back, the bag's take included).
- **The free reset** never reaches the payer (section 1), and cost 0 never reaches InventoryPay: untouched.
- **Before paying from links** the server's Pre checks the payer's entry (`0x3b` bytes) and its payment call (the
  `respec/RespecPaid` pattern's range) with kiln.hooks' `code_state`; another module's code there stands the feature down
  (`respec: ... stands down`, the module named, `crafting.respec_sources` faulted). Until that check ran, and without
  kiln.rules' payment hook, a respec is the game's.
- **A Kiln client** gets the server's respec snapshot (Kiln.Blocks `RespecStock`, message 11 on `kiln:craftstock`: the
  respec item's row, what the player's respec links hold, without the base's Magic chests) and, while it is on, mid hooks
  at `respec/client.NodeCountRet` and `respec/client.AltarCountRet` raise `eax` to the game's count plus the row: the
  node offers its unlearn and the altar enables its reset when the server would pay. `respec/client.HeaderCountRet` does
  the same for the shown number; it is not one of the feature's symbols and degrades alone. A hosting player gets the
  message in process. A hook fault stands the feature down (`crafting.respec_sources` faulted on the next Pre); the row's
  fault only the row.
- **A hosting client's prediction world** runs the same payer on its own transaction: its bases word is not the server
  world's, so it stays the game's (one `respec:` line); the server's state then reaches the prediction.

## 5. Lab (dev31-respec) **[lab]**
**2026-11-26, server 26430253, `mods/tests/respec-test`, the craft lab's save (one base): 20 PASS, 0 FAIL.** A lab has no
logged-in player, so the server ran with `KILN_STATIONS_CRAFT_LAB=1`: a spawned player character (`1_Player`, entity
2619, standing in the base) gets respec links of its own through the private `kiln_stations_lab_respec_link` (only with
that switch, only in a dedicated server's process, only for an id outside the player range, at most 16 links, each a
kiln.log warning). The vanilla node `0xc01ee111` (cost 2) went into its tree as a
save holds it (`GameTree.LabPutInSave`, `changeCount` bumped so the game rebuilt its masks: 2 points spent), and every
unlearn and reset went through the game's own input handler (`GameTree.GameRequest`). Each case is read against Kiln's
payment counters (`kiln_stations_craft_stats`, whose 14th value counts the respec payer's calls):
- `crafting.respec_sources` resolved on the running server;
- no link: the payer's `InventoryPay` call reached Kiln (`respec +1`, idle), the game refused: the node stayed;
- Runes only in the linked chest (5): the unlearn was paid from it (3 left), the node was gone; kiln.log `respec: a skill
  respec paid 2 from additional crafting sources (... the bag and Magic chests counted 0, paid 0 ...)`;
- one Rune in the bag: the bag 1, the link 1; two in the bag: the game's own payment (`covered`), the link untouched;
- one Rune in the link only: refused (`short by 1 of item 0x41a792dc`), nothing moved, the node stayed;
- one Rune in a Magic chest of the base: the chest before the link;
- the paid reset with 2 points spent: 2 from the link; a reset with nothing spent: the payer never reached `InventoryPay`
  (`respec +0`), the link untouched.

## 6. Not seen yet
No client run: the unlearn's and the altar's checks on a Kiln client (section 3, the mid hooks) are read from the code
and host-tested only. The join check's free reset is static evidence (a character without a client never passes that
check). Open questions only a game answers: that the node and the altar redraw as offered in the frame the hook raises the
count (the screens recount every frame, so they should); a real player's respec with a mod's link (the lab used the lab
stand-in for the player record); a hosting client's prediction world in the moment before the server's state arrives.
