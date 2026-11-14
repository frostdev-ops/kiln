# sigport: carrying the symbol database across game updates

When Enshrouded updates, functions move, structs grow, and register allocation changes. A pattern in
`sigdb/symbols/*.json` that no longer matches turns a feature off, and an exe without a verified build record keeps
Kiln idle (policy `verified-only`). `sigport port` finds every pattern site of the old exe in the new exe, re-derives
the typed holes, regenerates patterns, and writes a candidate build record. `kilnctl update` runs it automatically,
boots a lab copy with the self tests, and marks the build verified only when everything is green.

Code: `tools/sigport/` (Python 3 + capstone, MIT). Tests: `python3 -m unittest discover -s tools/sigport/tests`.
Benchmarks: `tools/sigport/bench.py`. Results: `sigdb/evidence/sigport-bench-2026-11-04.json`.

## Quick use
```sh
# port the repo's server sigdb from an exe it resolves on to a new exe (never edits sigdb/)
python3 tools/sigport/port.py --old OLD.exe --new NEW.exe                 # -> .out/sigport/server-<old8>-<new8>/
python3 tools/sigport/port.py --old enshrouded_server.exe --new enshrouded.exe --side server   # server -> client

# the same through kilnctl, plus the lab gate; --mark writes the verified record when everything is green
kilnctl port <instance> [--old OLD.exe] [--new NEW.exe] [--defs TREE/server.json] [--mark]
kilnctl gate <instance> --baseline           # once, on a verified build: the test results the gate compares with
kilnctl update <instance>                    # steamcmd; on a new unverified build: port -> gate -> mark
kilnctl update <instance> --dry-run [--new-exe NEW.exe] [--old OLD.exe]   # rehearsal: no stop/steamcmd, no record
```

## Output of a port
`<out>/` holds:
| File | What |
|---|---|
| `report.txt`, `report.json` | per symbol: old/new RVA, method, confidence, each strategy's old and new pattern with notes; per constant: old/new value, delta, captures (votes), corroboration, confidence; summary (complete?, definitions unchanged?, features lost, moved-constant families) |
| `symbols.patch` | unified diff of the definition files (`a/sigdb/symbols/...`), only for strategies at or above `--min-confidence` (0.8). Empty when the definitions work as they are |
| `tree/` | the complete patched definition tree (`server.json`, `server.d/`, `client.json`, `client.d/`), input of a gate or of the next port |
| `builds/<side>/<sha256>.json` | candidate build record, `"verified": false`, with a `sigport` section (source sha, report, low-confidence and lost names) |

Exit status: 0 complete (every symbol and constant that resolved on the old exe is ported at >= `--min-confidence`),
1 incomplete, 2 bad input. `--record-to sigdb/builds` also writes the candidate record there when that sha has none.
The live sigdb is never changed: a human reviews and applies `symbols.patch`.

Edits keep the files' hand formatting: values are replaced in place (`defs.py` locates JSON value spans). For the client
(`client.json` + `client.d` overlays of `server.d`), a changed symbol that comes from a shared `server.d` file goes into
the `client.d/<x>.json` overlay (created with `"extends"` if needed), so a client port never edits server definitions.

## Which bytes the core matches: the exe file, not the live code

sigport and `sigdb_check.py` read the exe file. Since 2026-11-07 the core's resolver (`native/core/src/sigdb.cpp`)
does too, so the patterns sigport writes match the same bytes in the game:

- `init_image` maps the exe once, copy-on-write, for the life of the process. It hashes the build's sha256 over that
  view, so the file is still read only once.
- sigdb parses the view's `.text` (`native/core/src/pe_text.h`) and applies the base relocations that touch `.text` to
  its private copy, with the delta between the preferred `ImageBase` (0x140000000) and the live base. Under Wine the
  server loads at 0x6FFFFC670000. Neither the server nor the client has a fixup in `.text` today, but if one is
  added, the copy still equals the live code.
- Every pattern scans that copy. A hit at file offset `o` becomes the live address `base + text_rva + (o - text_raw)`.
  A rel32 hole's target is computed from that live address. d8/d16/d32/s32 holes are instruction bytes, which are the
  same in both copies once relocated. Scopes work the other way round: a scope symbol's live address is translated
  into the copy.
- The uniqueness rule (exactly one hit), constant votes, fixed values, features, build records and the
  verified-build policy are unchanged. They run on the same results.
- **Fallback.** sigdb scans the live `.text` as before, and logs a warning, when the file can't be mapped or parsed,
  its `.text` isn't the loaded one (build timestamp, image size, RVA or size differ), or more than 1% of its bytes
  differ from the live code. What the differing bytes are does not decide it: hooks (a jump out of the image, a
  redirected call) and bytes another mod changed in place both leave the file as the loaded build. From 2026-11-07 to
  0.2.0-dev.27 a difference that was no known hook form sent the whole scan to the live code, where that start's
  hooks hid functions. A client with The Worst Map Mod (four map marker patches made before Kiln's scan) and other
  loaders' hooks lost 20 symbols and 22 features that way (a player's report of 14 Nov 2026).
- **Speed.** Unscoped strategies (each one a scan of all of `.text`) run up front on up to 8 threads. The copy can't
  change under them, even while another engine is still writing its hooks into the live code.

What it buys: an engine that hooks first (Enshwick, EMF, ShroudForge, SET, a shroudlink core) writes a `jmp` over
the prologue of the functions it hooks. A pattern that covers such a prologue no longer matches the live code, but it
still matches the file. See [engines/README.md](engines/README.md#when-another-engine-hooks-first).

**Diagnostics.** After resolving, sigdb compares each resolved symbol's matched bytes, and the first 5 bytes at its
address, with the live code. Where they differ, someone hooked or patched that function before Kiln looked. sigdb
follows the live `jmp` (relays and trampolines too) to the module it ends in:

```
[kiln] sigdb: scanning the exe file's code (.text 0xbd3bcc bytes, 0 base relocations applied); live code differs in 147 bytes, 28 runs (hooks or patches made before this check) (0.8 ms)
[kiln] sigdb: symbol chat/ChatLogAdd matched on the file's code; its live bytes differ at rva 0x9151f0 (jmp to 0x5590240): hooked or patched by someone else
[kiln] sigdb: symbol gameplay/InventoryTransfer matched on the file's code; its live bytes differ at rva 0x19e860 (jmp to enshwick.dll+0x11ea80): hooked or patched by someone else
[kiln] sigdb: 432/432 symbols, 529/529 constants resolved in 90.3 ms
[kiln] sigdb: 11 symbols resolved on the file's code whose live bytes differ (hooked first by someone else; Kiln's hooks chain behind theirs)
```

(A lab server with Enshwick started first. A bare address means the jmp ends in a relay stub outside any module.
"bytes changed in place" means a patch, not a jmp. With no other engine, the 14 differing bytes are the proxy's entry
gate, which touches no symbol.)

Differences that are no hook get a line of their own, with the first eight places:

```
[kiln] sigdb: 4 places (4 bytes) of the live code are patched in place by another mod, not hooked: rva 0x31629d, 0x3168c9, 0xd33058, 0xd331ee. The scan reads the file's code, so they hide nothing from Kiln
```

**Boot time** (lab server, interleaved runs of the same lab with only kiln-core.dll swapped): sigdb 653–677 ms on
the live scan before, 90–93 ms after. Most of the gain comes from the parallel unscoped scans; the file check
costs 0.8 ms. Kiln's boot (`starting` to `boot complete`) went from 1241–1444 ms to 607–713 ms. Hashing is
unchanged (60–71 ms), now from the mapped view.

`sigdb-report.json` carries the same information:

- a top-level `"code"` object: `source` (`disk` or `live`), `note`, `base_relocations_applied`, `live_differs_bytes`,
  `live_differs_runs`, `live_patched_in_place` (places that are no hook) with `live_patched_in_place_at` (the first
  eight RVAs) and `live_differs_symbols`;
- per symbol, `"live": {"differs_at": "<rva>", "by": "jmp to <module>+0x..."}`.

Mod fragments (`kiln.hooks` `LoadSymbols`) resolve the same way at runtime. Kiln's own hooks are armed by then, so a
fragment report also lists the functions Kiln hooked (`by: jmp to kiln-core.dll+...`). The hooks API's `Scan` and
`ScanHole` (`h_scan`, `h_scan_hole`) also match on the file's code and return live addresses.

Tests: `native/core/tests/test-sigdisk.sh [--server <exe>] [--client <exe>]` covers parsing, relocations (a test PE
loaded at another base, including a fixup that straddles the end of `.text`), translation and rel32 captures. With
the exes, it also hooks every resolved function in a copy and shows that a live scan loses them while the file scan
keeps them all.

## How it works
### 1. Function features (`exeinfo.py`, cached per exe sha in `~/.cache/kiln/sigport/`)
Functions from `.pdata` with chained unwind entries folded into their primary function, plus leaf functions found as
direct call targets outside every `.pdata` range (code reached only by a tail jump or a pointer becomes a pseudo
function on demand). Per function: size, instruction count, a normalised body hash (rip-relative displacements and
external branch targets masked, everything else kept), the mnemonic sequence (3-grams), call targets in order,
imported calls (IAT), referenced strings, `.rdata` values, immediates, struct displacements, conditional branches,
returns, internal branch targets. The server exe (30k functions, 2.9M instructions) takes 5 s, the client 7 s.

### 2. Function matching (`matcher.py`)
Each tier feeds the next:
1. identical: reciprocal-unique normalised bodies of >= 32 bytes. The idea comes from ShroudForge's profile rebase
   (ShroudForge, `modules/game-profile-discovery/rebase_profile_contract.cpp`, MIT): a body that is unique in both images, with
   only relocations masked, is the same procedure. 16,456 of 30,394 functions between 1273043 and 1285601.
2. callsite: an identical body calls in the same order, so its callees correspond position by position. This binds
   small and ambiguous callees (ShroudForge's "matching parent callsites bind otherwise ambiguous small callees").
   Rip-relative data references bind globals the same way. Conflicting votes cancel. +7,309.
3. anchors: a string, `.rdata` value, import, large immediate or mapped global that exactly one function references
   on each side votes for that pair. Accepted when the pair is the reciprocal best and similar enough.
4. propagation: unmatched callees and callers of matched pairs, scored, reciprocal best, repeated until stable.
5. neighbourhood: MSVC keeps object files in link order, so an unmatched function sits between the images of its
   matched neighbours. Candidates in that window are scored.

Similarity is a weighted mix of mnemonic 3-gram Jaccard (0.35), size ratio (0.10), agreement of already-mapped callees
(0.20), strings (0.15), immediates (0.10), imports and `.rdata` values (0.05 each). Result: 28,722 of 30,394 functions
matched old→new, and every function behind a sigdb symbol is matched correctly in both benchmarks (113/113, 116/116).

### 3. Site location, per strategy (pattern)
Every strategy (`find[i]`) of every symbol is ported separately, scope dependencies first:
- exact: the old pattern matches once in the new search range (the scope from the ported scope symbol), inside the
  image of the old site's function. Confidence 0.99 (0.95 when the old function has no match).
- mapped: the old site's function is matched. The two functions' instruction streams are aligned (difflib on
  instructions with numbers masked; equal-length replaced runs map position by position, scored 0.9 when only
  registers differ; a mnemonic-only pass for the rest). The pattern start is mapped. Hand-written patterns often start
  after an instruction's REX prefix, so the prefix length is adjusted. When the whole-function alignment misses the
  site (instruction scheduling differs, as on the client), a shape search finds the pattern's own instruction run
  (registers and numbers masked) in the new function, nearest to where the aligned neighbours put it.
- relaxed: the old pattern with every displacement and immediate wildcarded, searched inside the matched function or
  the neighbourhood window.
Confidence of a mapped site = function-pair confidence (identical 1.0, callsite 0.98, else 0.55 + 0.45 × similarity)
× alignment quality of the pattern's instructions. A rel32 capture whose target equals the matcher's image of the old
target is an independent second witness and halves the remaining doubt.

### 4. Holes
For exact and relaxed sites the holes sit at the same offsets. For mapped sites each hole is located as an operand of
an old instruction (displacement, immediate, part of an immediate as in `movabs` split into two `d32`, or a branch
target), the counterpart instruction is taken from the alignment, and the same operand is read. Capstone 5 reports
`disp_size` 2 under a `0x66` prefix, so the displacement size is computed from the layout. A field that outgrew its
encoding (disp8 → disp32) changes the hole kind (`d8` → `d32`, noted). A rel32 target is cross-checked against the
function match.

### 5. Patterns
Kept unchanged when the old pattern still matches once in the right place with the same hole kinds. Otherwise
regenerated at the new site: instruction bytes, rip-relative displacements and branch displacements wildcarded,
typed holes at their new operand positions. The pattern grows by instructions until it is unique, then trailing bytes
are trimmed (binary search; uniqueness is monotone). It keeps at least 12 literal bytes for whole-image patterns
(prologues recur) and 6 for scoped ones. By default the pattern is generalised: old and new instructions are walked in
lockstep, differing bytes become `??` (a whole displacement/immediate field when any of its bytes differ), and the
pattern must be unique on both exes. So the patched sigdb still resolves on the old build, like the hand-ported
one. `--no-generalize` cuts patterns from the new exe only. A scope grows (to the next 64/256 bytes) only when the
pattern cannot fit.

### 6. Constants
Layout constants come out of a resolve of the patched definitions on the new exe (the same resolver as
`sigdb_check.py`). Each one is reported with its votes: captures by distinct strategies, and corroboration. For
corroboration, other matched functions that use the old value as the same kind of operand are read through the
instruction alignment. Corroboration can only raise confidence for values below 0x10000, which recur in unrelated
structs. For distinctive values, a majority against lowers it. A fixed constant (`"value"`) that captures contradict is
updated in the patch when every capture agrees. A fixed constant nothing captures is carried over unverified and listed
(`unverified_fixed`); the gate's tests are what check it. The report groups moved constants by delta. For
1273043 → 1285601 this reproduces the documented shift without being told: +0x1c00 for 15 world-struct fields (the
program table grew), +0x8 for 6 program-context slice fields. It also found a third family, -0x700000 for the pending
event buffer (`gameplay/storage.pending` 0xeb9ef0 → 0x7b9ef0, 12 corroborating sites).

## Descriptors of typed hooks (`sig` blocks)
A symbol can carry a descriptor of its function for kiln.hooks' typed links ([hooks.md](services/hooks.md)): either
`"sig"` inside the symbol's entry, or under a file's top-level `"sigs"` section (for a symbol another file defines,
or a name that only a `rel32` hole captures). Structs they name are declared once under `"structs"`.

```json
"structs": { "OptS32": { "size": 8, "align": 4, "fields": [ { "name": "value", "type": "s32", "offset": 0 },
                                                             { "name": "has", "type": "u8", "offset": 4 } ] } },
"sigs": {
  "time/SetTimeScale": {
    "conv": "win64", "ret": "void",
    "args": [ { "name": "daytime", "type": "keen::Daytime*" }, { "name": "scale", "type": "f32" },
              { "name": "ecsNow", "type": "s64" } ],
    "abi": { "reads": ["rcx", "xmm1", "r8"], "stack": [], "ret": "callee" },
    "invoke": "tick"
  },
  "items/GetItemVolume": { "sret": "OptS32", "args": [ { "name": "item", "type": "keen::ItemInfo*" } ],
                           "abi": { "reads": ["rcx", "rdx"], "stack": [], "ret": "rax" }, "invoke": "any" }
}
```

- `args` lists the declared parameters only. Types: `s8` to `u64`, `f32`, `f64`, `bool`, `ptr`, `T*` (any pointer),
  or a struct declared under `structs` (by value: 1, 2, 4 or 8 bytes travel in the register, any other size by
  reference). A struct returned through a hidden buffer is `"sret": "<Struct>"`, never an argument. At most 20
  positions (the hidden buffer included). Varargs functions cannot be described.
- `abi` is the function's fingerprint when the descriptor was written: the argument registers it reads before it
  writes them (in position order), `passes` (registers it hands on untouched; only needed for a function that just
  jumps on, like `world/ReadOther`), the stack argument slots it reads (offsets from the entry `rsp`, `"0x28"` is the
  fifth position) and what was written last before its returns. At load the core computes the fingerprint again from
  the exe file's bytes and compares: other `reads` or `stack`, a declared position the code neither reads nor passes,
  or a read the descriptor does not declare turn the feature `sig/<symbol>` off (the symbol itself still resolves)
  and refuse every typed hook of it; kiln.log names the argument. `ret` is recorded for review and not compared.
- `invoke`: `none` (hook only), `tick` (may be called on the tick thread) or `any`.
- `"abi": "trust"` (a mod's own fragment only): accept a function whose code cannot be followed (it jumps through a
  register before it uses an argument). A mismatch is never trusted.

`sigdb_check.py --fptool <kiln-fptool>` checks every descriptor against an exe with the core's own pass
(`tools/sigport/fptool/build.sh` builds the tool for the host and prints its path; the exe is read as a file):

```
  ok  sig time/SetTimeScale: match: 3 arguments (rcx, xmm1, r8), the function reads [rcx, xmm1, r8]  [passes ...; ret callee; 18 insns]
```

Without `--fptool` it only says whether each described symbol resolves. To write a new descriptor, leave `abi` out,
run the check, and copy `reads`, `stack` and `ret` from the line it prints. `port.py` does not carry `sig` blocks to
a new build yet.

## Benchmarks (`bench.py`)
| Run | Symbols P / R | Constants P / R |
|---|---|---|
| blind replay 1273043 → 1285601 (derived 1273043-only sigdb; truth: today's verified sigdb on 1285601) | 1.0 / 1.0 (125) | 1.0 / 1.0 (314) |
| same, exact pattern hits disabled (matcher only) | 1.0 / 1.0 | 1.0 / 1.0 |
| server 1285601 → client 26404966 (truth: client.json + hand overlays) | 1.0 / 1.0 (124) | 0.997 / 0.997 (312) |
| server 1273043 → client (build and side at once) | 1.0 / 0.976 | 0.997 / 0.997 |
| EMF 0.3.3's 41 server signatures (8 break on 1285601) | 1.0 / 1.0, all 8 broken ones found | - |

- The derived 1273043 sigdb (`sigdb/evidence/sigport-1273043-derived.json`) was made by porting today's sigdb
  backwards with `--regenerate-all --no-generalize`, so every pattern was cut from 1273043 bytes. It was then checked by
  hand: 122/122 agree with today's (generalised) sigdb resolved on 1273043, and 44/44 agree with EMF's documented
  `server_1273043` RVAs. It also finds the 3 Dismantle targets that today's sigdb misses on 1273043; those were
  checked by hand.
- The client misses: `net/after_sync.gs_register` is a fixed value that differs on the client (13 vs 15) and that no
  pattern captures (FP; making it a typed hole would make it portable). `time/weather.mode` is in a function the
  client re-implemented; the tool found the right value but at confidence 0.69, so it is left for a human.
- The 4 EMF signatures that needed hand ports (SpawnWrapper, IterNext, CurrentEntityId, AllocPendingHit) are ported
  automatically to the hand-ported RVAs, with patterns that resolve on both builds.
- A port takes 10-15 s for the server and ~25 s server → client.

## kilnctl integration
### Exe cache
A port needs the old exe after SteamCMD has replaced it. kilnctl keeps a copy of every exe with a verified record in
`~/.local/state/kiln/exe-cache/<sha256>.exe` (reflinked when the filesystem can, never executed). The copy is made
by `update` (before SteamCMD), `port` and `gate`. The port's old exe defaults to the newest cached exe with a verified
record (deployed `kiln/sigdb/builds/server/` or the source tree's `sigdb/builds/server/`).

### `kilnctl update <name>`
stop → backup → cache the exe (if verified) → steamcmd → `sigdb_check` → if Kiln is deployed and the new sha has no
verified record: **port → gate → mark** → summary → start. Marking requires all three:
1. the port is complete (nothing lost, nothing below confidence),
2. the deployed definitions work as they are (`symbols.patch` is empty). Otherwise the instance would run definitions
   the gate did not test,
3. the gate is green.

Then kilnctl writes `kiln/sigdb/builds/server/<sha>.json` with `"verified": true` into the instance (never replacing a
verified record), a copy for the repository into the port's folder, and the gate result as the new baseline. Without
all three, nothing is marked and `NEXT.txt` in the port's folder says what a human has to do (apply the patch and
redeploy, review the low-confidence names, look at the gate's regressions). The server then starts in safe idle as
before. `--no-port` keeps the old report-only behaviour, `--no-gate` ports without booting (nothing is marked), and
`--no-mark` leaves marking to a human. `--dry-run` does no stop, backup, SteamCMD or start, ports and gates as if
`--new-exe` (default: the instance's exe) had just been installed, and never writes a record.

### The lab gate (`kilnctl gate`)
A sibling instance `<name>-gate`: `<dir>.gate` gets the instance's game files (reflinked), its deployed `kiln/` folder
and its save (copied), policy `any`, the candidate record, the candidate definitions when the port changed patterns,
and the test mods (`mods/tests/<id>`, built with `dotnet` into `~/.local/state/kiln/gate-tests/`). `<dir>.gate.pfx` is
reflinked from the instance's prefix once. Its query port is the instance's + 1 (`--gate-port`). The user-group
passwords are replaced with random ones. The instance's own mods and native plugins are left out unless
`--gate-instance-mods` is given: Kiln.Admin, for example, may serve a bridge folder outside the server folder. The
copy boots through kilnctl's normal start (preflight included). kilnctl waits for each test's summary line in
`kiln/logs/kiln.log`, then stops the copy with a save.

Default tests: world, players, chat, items, settings, gameplay, hooks, combat, spawn, content (`--tests a,b`). The
verdict is green when Kiln hooked SimStep, every test printed a summary, and no test fails more, or other checks, than
in the baseline. FAIL lines are compared with digits masked. Some tests fail on a verified build too (the lab world:
combat 1, items 2, spawn 1), so record a baseline once on a verified build: `kilnctl gate <name> --baseline`. Without a
baseline every FAIL counts. Results: `~/.local/state/kiln/gate/<name>/`.

Verified in a lab: baseline on 1285601;
`update --dry-run --old <1273043 exe>` (port 122/122 + 314/314, no definition change, gate GREEN in 1:42, "ready to
mark, dry run"); `port --mark` wrote the record, and a plain start under `verified-only` then logged
`build c5c9fe50…: verified` and `tick: SimStep hooked`; a port from the derived 1273043-only sigdb changed 9
strategies, the gate booted the candidate definitions (197/197 symbols, 319/319 constants, GREEN), and kilnctl left it
for a human (NEXT.txt + symbols.patch).

## Limits
- Static evidence only. The gate's tests are the runtime check, and they cover what they cover.
- A fixed constant nothing captures cannot be ported. Prefer typed holes over `"value"`.
- A function the game rewrote (the client's WeatherUpdate) comes out at low confidence: by design it goes to a
  human.
- The gate tests data from the new game install. With `--dry-run --new-exe`, the copy has the instance's game data
  next to another exe.
- Probes whose scope is a symbol only a rel32 capture defines (`@content/LootFind.fields`) do not resolve in the core
  either; sigport reports them as "neither" instead of porting them.
