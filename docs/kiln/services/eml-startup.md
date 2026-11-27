<!-- SPDX-License-Identifier: MIT -->
# kiln.eml.startup v1

**Kiln-internal:** the startup handshake between kiln-eml and Kiln's interpreter for ShroudForge mods (kiln-sfi). The
header is not in the SDK and is no mod API.

The header is declarations only: `native/core/abi/services/kiln_eml_startup.h`. GPL kiln-eml provides the table;
MIT kiln-sfi is its one consumer. It is an **early export only**: there is no service name, no
`register_service`, no capability row. kiln-sfi resolves `kiln_eml_startup_service` (`KILN_EML_STARTUP_SERVICE_EXPORT`)
from the already-loaded kiln-eml module in its early phase. The getter returns one process-lifetime immutable table for
`min_version <= 1` and NULL above; a missing export means an older kiln-eml without the seam. The getter grants
nothing. kiln.eml.archive stays 152 bytes: the grant hand-off lives here, not in an archive member. kiln-core's kiln.boot
(`native/core/src/services/boot_archive_eml.cpp`) and kiln-content's kiln.content
(`native/services/content/gamedata_bootview_eml.cpp`) are the table's other callers, of one member only
(`published_identity`), from the same export.

## Table

`KilnEmlStartupApiV1`, version 1: an 8-byte header plus **6 pointers = 56 bytes** on x64. The table is append-only:
`published_identity` was appended to the first five (48 -> 56 bytes, still version 1), so a kiln-eml from before it
serves a 48-byte table without it.

| Offset | Member | Called by |
|---|---|---|
| 8 | attach | kiln-sfi: early phase, once, before the loader is armed |
| 16 | witness_wait | kiln-sfi: the loader thread, before the first package DLL loads |
| 24 | witness_recheck | kiln-sfi: the thread that concludes the native run (the loader thread, or its deadline waiter), once every planned DLL is terminal, before native_terminal |
| 32 | native_terminal | kiln-sfi: once, after every planned DLL reached its outcome, at ShroudForge's 15 s deadline at the latest (or at once when the loader was never armed) |
| 40 | outcome | kiln-sfi: plugin step, once kiln-eml's startup barrier is done |
| 48 | published_identity | kiln-core's kiln.boot: its bound(), after every on_ready; kiln.content: its first resource-patch prepare, after that |

`KILN_EML_STARTUP_HAS(api, member)` checks the extent, the version and non-null before a member is read.

## Records

| Record | Bytes | Fields (offset) |
|---|---|---|
| `KilnEmlStartupKeyV1` | 56 | size 0, flags 4 (zero), launch_id 8 (kiln.launch's u64), plan_sha256 16 (kiln.launch's plan hash), session 48 |
| `KilnEmlStartupWitnessV1` | 40 | size 0, state 4, plan_id 8 (RFC 4122 order), revision 24, flags 32 (zero), reserved 36 (zero) |
| `KilnEmlArchiveIdentityV1` | 160 | from `kiln_eml_archive.h`, never redeclared |

Witness states: PENDING 0, READY 1, REFUSED 2. The JSON format number is `KILN_EML_STARTUP_FORMAT` = 1.

## Rules

- **Records** follow kiln.eml.archive: the caller sets size to at least the v1 size; larger sizes are accepted and the
  bytes past v1 are untouched; nonzero v1 flags or reserved, a short size, a needed null pointer or a key that does
  not match kiln-eml's own kiln.launch read is INVALID_ARGUMENT. A call refused for its key or its records changes no
  state (it is not the one attach and not the one terminal).
- **Buffers:** `required` is mandatory. Text is counted UTF-8 without a NUL. On BUFFER_TOO_SMALL `required` is the
  complete byte count and nothing else is written (not the witness, not the identity). On OK `required` is the
  complete count. Other failures leave every output unchanged. No partial writes.
- **Getter:** any-thread, side-effect free; the table's header is size 56, version 1.
- **Key and session:** `launch_id` (never 0) and `plan_sha256` (raw bytes) are kiln.launch's. `attach` and
  `published_identity` take `key.session = 0`; `attach` issues a nonzero session, and every other member takes that
  session. It prevents accidental
  misuse; it is not access control (in-process code is trusted).
- **attach:** once per process, from kiln-sfi's early phase, before its loader is armed. `planned_dlls` is the size of
  kiln-sfi's native plan (0 is valid); the terminal lists exactly that many DLLs. Only an OK attach counts: a later
  attach is UNAVAILABLE, and so is an attach in a start whose backend is not kiln, after kiln-eml's early phase
  failed, or once kiln-eml's startup barrier has begun (kiln-eml then runs without native facts).
- **witness_wait:** blocks at most `timeout_ms` (0 polls). OK with READY (`plan_id` is the launch plan's,
  `revision` kiln-eml's opaque value), REFUSED (the reason is the text, `required` > 0) or PENDING (the timeout
  passed). `plan_id` and `revision` are zero unless READY. The state only moves from PENDING to READY or REFUSED and
  then never changes; calls may repeat, so after BUFFER_TOO_SMALL a call with timeout 0 gets the same answer. Only
  READY for kiln-sfi's own plan id lets a package DLL load.
- **witness_recheck:** once every planned DLL is terminal and before `native_terminal`. The witness must equal the
  READY one this session received (state, plan id, revision); anything else, or no READY witness, is INVALID_ARGUMENT.
  OK with `required = 0`: the witnessed input is unchanged. OK with `required > 0` (or BUFFER_TOO_SMALL): it changed,
  and the text says how. The answer becomes the terminal's `coherent`. UNAVAILABLE once kiln-eml's wait for the
  terminal has ended; kiln-eml's own check before its pass stays the authority for its pass. kiln-sfi reads that
  UNAVAILABLE as not confirmed (a DLL may have loaded after kiln-eml's check): a run that loaded a DLL then does not
  start the interpreter (`startup-input-changed`).
- **native_terminal:** the first call with a valid key counts, whatever its JSON. A record that fails the schema, whose
  `planId` is not the plan's, whose `dlls` are not `planned_dlls` in number or that names a package the plan does not
  list is INVALID_ARGUMENT, and kiln-eml then has no terminal (its DLL packages and their dependents are excluded,
  `native-terminal-missing`). A later call, or one after kiln-eml's wait expired, is UNAVAILABLE and only logged. OK
  means kiln-eml's pass uses the terminal; any other result means it does not.
- **outcome:** UNAVAILABLE until kiln-eml's startup barrier has finished; a barrier that faults before its outcome
  exists leaves it UNAVAILABLE for the process (kiln-sfi: `startup-outcome-missing`). Afterwards every call gives the
  same answer: OK (or BUFFER_TOO_SMALL with the complete size) with the same bytes and the same identity.
  `identity->phase` is PUBLISHED exactly when the outcome's `grant` is true, with the published generation's pins;
  otherwise every v1 field after `size` is zero. kiln-sfi checks phase, launch_id, plan_id and plan_sha256, and the
  JSON against the identity, before it acquires a kiln.eml.archive view; a mismatch is `startup-grant-mismatch`.
- **published_identity**: the identity alone of the generation kiln-eml granted this launch, for a reader that is
  not kiln-sfi: kiln.boot's OnBoot game data in a kiln-backend start and kiln.content's resource-patch check
  (`boot_archive_eml.h` `published_identity_of`, one call for both). Session-free and read-only: no state changes,
  nothing is attached or granted, and it works whether or not kiln-sfi attached. OK, with exactly the identity `outcome`
  hands (size kept, bytes past v1 untouched), only once the barrier finished with a grant. UNAVAILABLE before the barrier
  finished, for a start without a grant (`grant` false, or a barrier that faulted) and in a process with no launch
  installed (not a kiln-backend start of this kiln-eml, or its early phase failed). A key that does not name the
  installed launch and plan, a nonzero session, a short record or a null pointer is INVALID_ARGUMENT. Nothing is written
  on a failure. The caller checks every pin itself and acquires its own kiln.eml.archive view with exactly them; without
  an identity a kiln-backend start's OnBoot has no game data (never the boot snapshot).
- **Threads:** every member is any-thread, never enters Lua, never calls back and is panic-contained (INTERNAL).
  `witness_wait` blocks at most `timeout_ms`; no other member blocks on kiln-eml's pass. **Loader lock:** kiln-sfi may
  call `witness_recheck` and `native_terminal` on its deadline thread while a package's DllMain holds the loader lock,
  so these members and anything they log (the core's log included) never load a library, never resolve a symbol lazily
  (GetProcAddress, a delay-load import's first call) and never start and join a thread; kiln-eml's wait for the terminal
  is bound by the same rule, and so is `published_identity` (a timed-out package's initializer may still run at
  kiln.boot's bound()). kiln-eml's provider logs nothing from a member: a terminal it refuses is noted and logged
  at Kiln's plugin step, on the boot thread.

How kiln-eml fills the identity pins: `launch_id` is 8 zero bytes and then kiln.launch's u64
big-endian; `plan_id` the plan UUID bytes; `plan_sha256` kiln.launch's plan hash; `generation` kiln-eml's
process-unique nonzero value; `reload_epoch` 1; `archive_sha256` the kiln-eml generation digest (below), the
same value as the JSON's `archiveSha256`; `registry_id` / `registry_sha256` from the registry snapshot. The JSON's
`launchId`, `planId` and `planSha256` name the same launch.

## kiln-eml's provider

`gpl/kiln-eml/kiln-eml/src/startup_seam.rs` provides the table; plugin.rs wires it into kiln-eml's plugin steps.

- **Early phase** (`kiln_plugin_early`, a kiln-backend start only): the frozen plan is read again as kiln-sfi reads it
  (sfi-policy's reader with `build_info`'s executable digest and side, no two packages in one folder under two names
  by `check_folders`, the plan id of its file name, backend `kiln`);
  the install lock is taken for the process (another game process of the install refuses it too); the startup witness runs
  inside `catch_unwind` (a panic is a REFUSED witness, `startup-internal-error`: fail closed, no grant); then the launch
  is installed, so kiln-sfi's early phase, which runs next, can attach. A plan that cannot be read, or a panic before
  the witness, installs no launch: `attach` is UNAVAILABLE and no startup pass runs. In kiln mode another EML loader
  found in the process refuses the witness (`eml-loader-loaded`); ShroudForge's folder and config are never read.
- **Witness answers**: decided before kiln-sfi attaches (READY or REFUSED; PENDING only if a caller asks before the early
  phase decided). A busy install lock refuses the witness (`lock-busy`) and, at on_ready, stops the start before the
  game's entry point (the existing stop).
- **Recheck**: the stamped witness checked again (metadata and the small parsed files only).
- **Coherence**: a terminal's `coherent` false counts for a run that loaded a DLL (no publication, no grant,
  `startup-input-changed`); a run that loaded none changed nothing, so a coherence it could not confirm (kiln-sfi's
  recheck unanswered within 5 s, or answered UNAVAILABLE) is not a change, as kiln-sfi reads it. kiln-eml's own check
  before its pass stays the authority either way; the record keeps kiln-sfi's `native.coherent` and text.
- **Terminal**: read strictly (native-terminal.schema.json v1, its bounds and rules, the plan's id, `planned_dlls` DLL
  records, only packages the plan lists, every package with a DLL that did not load withdrawn). A withdrawal of a package
  with no DLL record is taken (a Lua-only dependent; a package that only registers DLLs from its Lua; a not-armed run of a
  kiln-sfi that could not read its native plan and attached with 0 DLLs).
- **Barrier** (kiln-eml's early on_ready, the boot main thread): `attach` closes; after an attach it waits at most 20 s
  from the attach for the terminal (no attach: no wait). The end of that wait and the decision on a terminal are one step
  under the provider's lock: a terminal or recheck after it is UNAVAILABLE (a late terminal is noted and logged in
  kiln.log at Kiln's plugin step). Then the startup pass (or the refused witness's outcome), the grant, and the record. A
  barrier that panics outside the pass's own containment leaves `outcome` UNAVAILABLE.
- **Grant**: `kiln.eml.archive`'s `authorize_with_archive` once per kiln start, phase PUBLISHED (never PREPARED), with the
  pins above; never retired in-process. An authorization that fails turns the outcome into `failed` without a grant
  (`startup-grant-missing`).
- **Kiln's bundled ShroudForge's output**: a start that settles on the verified BASE finds that
  output live when the player used Kiln's bundled ShroudForge before choosing Kiln's interpreter. Its record beside the
  backup (`<stem>.kfc.prepared.json`) proves it when it names the live header (whole SHA-256), the resource file (its
  ends, length and time) and the backups present (length and time), and Steam's build where both name one: then the
  BASE is restored over it (`restored`, granted). A record that names the live header but not the rest, and output
  nothing proves, are refused with nothing changed (`base-not-restored`), as is a witness whose live set would become
  a new working baseline while it is that output (`baseline-shroudforge-output`). Each such text ends with what the
  player does: verify the game files in Steam, then start again from the Kiln launcher. kiln-sfi's
  `startup-grant-missing` reason quotes it.
- **BASE origin**: the record's `baseOrigin` is a fact about the live generation, so an outcome without a grant
  (`refused`, `failed`) writes `null` whatever the witness found; kiln-sfi reads a non-null origin only with a grant.
- **State file**: kiln/state/eml.json lists one row per package of the pass (`source: "shroudforge"`), the
  unlisted EML contributors (skipped in the ShroudForge and EML dialects; the EML pass's own in a start without a
  dialect), and every other package with the plan's reason.
- **Progress** (dev.30.5): while the pass runs, the state file says `phase: "running"` with `progress.stage`
  `startup` and its step: `checking` (the wait for kiln-sfi's terminal, the witness checked again, admission, the
  startup cache), `mods` (the packages' startup code), `verify` (the stage closed, flushed and checked), `write` (the
  publication), `save` (the change journal and the startup cache), with `stepNumber` and `steps` (5) and the bytes of
  the game's files read or written in the step. The launcher's start bar shows it ("Preparing ShroudForge mods for
  Kiln's interpreter: checking the prepared game data (3 of 5)"); kiln.log gets a line every 10 s while a step moves
  bytes, a warning naming any flush to the drive that took two seconds or more, and one line with what the pass and each
  step took. A start the startup cache serves ends after `checking`. No thread is started for it (see below).
- **What a publication reads**: the check of the prepared stage reads back and hashes every content of the stage's own
  containers and decodes every resource; the game's own containers are not read in full (the baseline index lists those
  contents at the same place, and the input witness holds the files): four contents of each container, spread from its
  first to its last, are read back and checked, which a working baseline that no longer describes the game's files
  (a backup left from an earlier run) fails. On a client the pass of a small package reads
  about ten times the resource file (around 2 GiB: the staging copy, the stage's digests, the compare with the resource
  backup), not the 48 GiB of containers it read before dev.30.5.
- **Threads and the loader lock**: kiln-sfi may call `witness_recheck` and `native_terminal` from its deadline thread
  while a package's DllMain holds the loader lock. No member loads a library, resolves a symbol, starts or joins a thread,
  logs or initializes a lazy static that could; the barrier's wait is a `Condvar` wait. Wine's loader takes the loader
  lock for every LoadLibrary and GetProcAddress, also of a module already loaded, and for GetModuleFileNameW and
  RtlPcToFileHeader, so kiln-eml's early phase of a kiln start asks the system once what on_ready and the pass would ask
  it: `prime_the_pass` (the first Lua state of the process, whose seed makes the C runtime resolve
  GetSystemTimePreciseAsFileTime; std's first thread-local with a destructor, for which std sets up its destructor
  callback with FlsAlloc and GetModuleHandleExW from the callback's address), the executable's name (plugin.rs
  `exe_stem`), Wine's path conversion export for a placement receipt's Unix path (discover.rs `prime_dos_name`), the
  export the package containment checks use (`wine_get_unix_file_name`: sfi-package's `same_folder`, which sfi-core's
  package source and rules reader use, and mod-loader-base's `wine_within`, through `prime_the_pass`) and whether this
  is Wine at all (mod-loader-lua's `prime_witness_checks`). The two-DLL integration (`native/core/tests/sfi_startup`)
  runs kiln-eml's real on_ready, discovery included, while a package DLL's initializer holds the lock: the pass starts
  15.0 s after the attach (the deadline thread's terminal) and on_ready, with the barrier, pass, publication and grant,
  ends while the initializer still runs, natively and under Wine, also for an initializer that never returns.
  kiln-sfi's plugin step (main and on_ready) ends then too: its look for ShroudForge's own loader is the early phase's
  while its native run may still be inside a package DLL's load. A kiln-mode start opens no `kiln.progress` window (the
  integration serves a stand-in that makes the core's user32 calls; with an older kiln-eml its first begin under the
  held lock waited for the initializer under Wine), and sfi-core's export-root Wine lookup (`secure.rs`) is primed in
  the early phase. The real core's plugin step loads its service DLLs first (boot.cpp `load_plugins`), which waits for
  a slow initializer anyway, and kiln.boot's bound() comes after it. Under Proton only, a first call on a path the
  integration does not take can still wait for that initializer: a package's Lua reaching another lazily bound C runtime
  function, or a contained panic. Natively none of them waits.

**Generation digest** (`archive_sha256` of the identity and `archiveSha256` of the record,
`startup::generation_digest`): SHA-256 of the bytes `"kiln-eml generation v1\0"`, the stem's length (u64 LE) and
bytes, the ownership receipt's SHA-256 (32 zero bytes for the BASE), then the working baseline's backups in the fixed
order `<stem>.kfc.bak`, `<stem>.kfc_resources.bak`, `<stem>.kfc.bak.integrity`, each a presence byte (0 absent, 1
present) and, when present, its length (u64 LE) and raw SHA-256. It names the generation the receipt lists over that
baseline; it is not a hash of the archive's bytes.

**Rules this side follows**, each in one place: an unlisted EML contributor (`kiln_mode::unlisted_rule`); the handoff a
kiln-mode start owes ShroudForge, settled by a hosted start's `stand_aside` (plugin.rs
`settle_owed_handoff_under_hosted`); a DLL that changed the witnessed input (the startup pass's `run` step 1; the loud
kiln.log line, `startup_seam::changed_input_by_dll`); the rebaseline after lost backups (the inspector's note
`receipt-stale`, then the witness's `startup_host::set_aside_stale_receipt` keeps the old receipt as
`<stem>.stale-<uuid>.json` and the new baseline's origin is `rebaselined-unclassified`; Kiln's own output still live
stays `baseline-missing`).

## JSON records

Schemas in `schema/sfi/eml-startup/`, fixtures in `schema/sfi/eml-startup/fixtures/` (valid records and one-rule-broken
invalid ones), test `schema/tests/sfi_eml_startup.mjs`. Every object is closed; a typed serializer writes the fields
in schema order. Chains are nearest first (the dependency a package needed first, the failed package last), as
sfi-state's skip chain, never name their own package and keep their first 64 entries. Writers clamp free text to its
`maxLength` at a character boundary and `warnings` to its first 64; rows, DLL records and ids are never clamped, and
their bounds cover the plan reader's (4096 packages, 16384 records, 32768-byte paths).

**`native-terminal.schema.json`** (kiln-sfi to kiln-eml): `format`, `planId`, `notArmed`, `deadlinePassed`,
`coherent {ok, text}` (text exactly when not ok), `withdrawn[] {id, code, text, chain}`, `dlls[] {statusId, modId,
outcome}` with outcome `loaded`, `foreign-owned`, `refused`, `faulted`, `timed-out` or `withdrawn` (the native
manager's `Outcome` kinds), one per planned DLL in plan order.

- A DLL that did not load (`refused`, `faulted`, `timed-out`, `withdrawn`) withdraws its package; the converse does
  not hold (a package whose first DLL loaded and whose second faulted is withdrawn, the first stays `loaded`).
  `withdrawn` also lists Lua-only dependents.
- Not armed (the native manager's `conclude_without_loader`: the native policy refuses, the plan has no DLL, the plan's native part
  cannot be read, or the loader thread could not start) means every DLL is `refused`, `deadlinePassed` false and
  `coherent.ok` true, and `withdrawn` names every candidate that ships native code, a package that only registers
  DLLs from its Lua (`runtime-register-dll`) included, with their dependents: kiln-sfi's admission refuses them all.
  A run made at Kiln's plugin step (kiln-sfi's early phase did not run) has no attach, so it pushes no terminal. A
  refused or missing witness is an armed run: every DLL `refused`, `coherent` false (`no startup witness was
  issued`).
- `timed-out` needs `deadlinePassed`.
- An armed run's terminal comes at ShroudForge's 15 s startup deadline at the latest, counted from the moment
  kiln-sfi arms its loader (ShroudForge counts from handing its whole plan to its ordered worker), so it reaches
  kiln-eml inside its wait (attach + 20 s) even when a DLL's initializer hangs. A separate thread that kiln-sfi starts
  before the loader thread loads anything pushes it then: the DLL still initializing and every DLL not started are
  `timed-out` (`deadlinePassed` true), their packages `dll-timed-out` (a dependent whose own DLL never started
  too), the packages that need them `dependency-withdrawn`. The
  loader thread is left inside the initializer, never stopped; no load starts once the deadline has closed the run (a
  load committed just before can enter the loader just after: it is `timed-out` too), and a load that returns later
  is only logged (kiln.log). Exactly one terminal is pushed: a run every DLL of which is terminal in time is pushed by
  the loader thread, never by the deadline thread. kiln-sfi waits for a recheck that another of its threads is asking
  at most 5 s after the conclusion began; then the terminal is pushed with `coherent` false (the recheck did not
  answer), and an answer that comes later changes nothing.
- `code` is kiln-sfi's word (sfi-state's code grammar): `native-policy-refused`, `native-not-armed`, `witness-refused`,
  `dll-faulted`, `dll-timed-out`, `dll-refused` (the package's first DLL that did not load, by its outcome; also an
  armed run whose loader stopped before it asked for the witness) or `dependency-withdrawn`. `native-not-armed` covers
  a plan with no DLL whose package registers DLLs from its Lua. kiln-eml writes a withdrawn package's outcome row as `native-refused` with the entry's `text`
  and `chain`.

**`startup-outcome.schema.json`** (kiln-eml to kiln-sfi): `format`, `launchId` (16 lower-case hex digits),
`planId`, `planSha256`, `dialect` (`shroudforge` | `eml` | `none`), `state` (`committed` | `cached` | `unchanged` |
`restored` | `refused` | `failed`), `grant`, `receiptSha256`, `archiveSha256`, `baseOrigin` (`{token, label}` from
inspection.rs ORIGINS, null for unrecorded), `native {received, coherent, text}`, `attempt` (`first` | `retry` |
`none`), `error`, `retryError`, `packages[]`, `warnings[] {code, text}`.

- `grant` is true exactly in the first four states; then `archiveSha256` is set. A refused start never grants.
  Without a grant both digests are null and every row's `archivePresent` is null (nothing is proven live); with a
  grant, `failed` and `dependency-skipped` rows are `false` (proven absent).
- `restored` keeps no receipt and no base origin (a verified restore removes them).
- A retry is shroudforge-only and carries the first error; `first` has no `retryError`; `none` has neither error. A
  cached start may replay a retried success (`attempt` and errors are part of the cache value).
- `native.coherent` false carries text; no terminal (`received` false) is never coherent.
- Rows are the plan-listed packages only, one per package: none in dialect `none`; their `pass` is the dialect.
- After a failed retry every row of the pass is `excluded` with `startup-retry-failed`, the first attempt's failing
  package too (kiln-sfi's sfi.json reducer, `state/reducer.rs`, reads it so); `failed` and `dependency-skipped` rows come from a retry that succeeded.

Row mapping (data values are sfi-state's):

| Case | data | code | chain | archivePresent |
|---|---|---|---|---|
| ran and published, or vouched by the cache | `committed` / `cached-verified` | none | [] | true |
| export-only package that ran | `committed` | none | [] | true (vacuous, as sfi-state's committed rule needs) |
| failed in its own entrypoint, retry succeeded | `failed` | `startup-failed` | [] | false with a grant, else null |
| dropped by the retry | `excluded` | `dependency-skipped` | nearest first, ending at the failed package | false with a grant, else null |
| retry failed too (every package of the pass) | `excluded` | `startup-retry-failed` | [] | false after the verified restore, else null |
| native refusal, changed folder, stale plan, changed input, no attach, no terminal, witness refusal | `excluded` | `native-refused`, `package-changed`, `plan-stale`, `startup-input-changed`, `backend-unavailable`, `native-terminal-missing`, the refusal's code | from the cause | false only if the live generation is proven without it, else null |

Codes are the sfi-state code grammar, not a closed list (the witness refusal reasons come from kiln-eml's inspector).

## The startup change journal

A kiln-mode start in the ShroudForge dialect that publishes a generation also writes its change journal beside the
ownership receipt: `<game>/kiln/eml/ownership/<stem>.change-journal.json` (its own record: the outcome above and its strict
reader do not change). Per package of the committed attempt, in run order, with its plan identity: the resources its
entrypoint changed, each with the member paths it changed there, the ones it created (by ordinal) and the contents it
created, never a value; at most 4096 entries per package and 32768 in all (`truncated` past that). Since 0.2.0-dev.32
this is format 2 of dialect `shroudforge` (`schema/sfi/eml-journal/eml-journal-2.schema.json`): after each entrypoint the
asset session compares the resources it wrote (format 1's rule) with what they held before it ran, as the commit encodes
them, and names the package on the members that differ (`sf_startup/journal_paths.rs`; kiln-eml README change 45); a
resource it wrote back as it was is left out. The record lists the plan's packages the committed attempt did not run
(`notApplied`: the failed package and those left out with it, with the row's text; never a package left out as
`package-changed`, which is not as the plan lists it rather than failed on this build), which the registry's effect replay
reads. Before, format 1 (`startup-journal.schema.json`): the resources each package wrote, per resource. The startup
cache's record is format 3 since (a record of format 2 names a format-1 journal: it misses once, and the pass journals its
publication anew).

- **Binding:** it names one generation (`generation` is the outcome's `archiveSha256`, `receiptSha256` the receipt
  beside it). kiln-eml writes it after its one publication, under the install guard, only while the receipt is still the
  one it names (`StartupJournal::write_for`), and before the startup cache, which records its SHA-256: a cache hit needs
  that journal byte for byte (a missing or other journal is a miss, the pass runs again). A journal that cannot be written
  leaves the publication standing with the warning `startup-journal-not-saved` and no cache record.
- **Removal:** every publication (`prepare_from`, before it writes anything), the verified BASE (`restore_base`,
  `forget_receipt`) and EML's own no-mod restore (`before_restore`) remove it before the receipt or a live file changes;
  one that cannot be removed refuses that publication or restore, with nothing changed. A kiln-mode start in the EML
  dialect writes its own after its publication (since 0.2.0-dev.32; before, none): format 2 of dialect `eml` from EML's record
  of who wrote what (change 44), bound like the ShroudForge dialect's to the outcome's `archiveSha256`, with the plan
  digests and `notApplied`; an unchanged EML-dialect start keeps it.
- **Readers** use it only when `generation` is the start's live generation: a writer that ignores the receipt (hosted
  ShroudForge, another loader) can leave it naming a generation that is no longer live. Read it and close it at once (a
  reader that keeps it open on Windows can make the next kiln-mode start fail its removal).
- **Kiln's registry stage** is one reader (since 0.2.0-dev.31.17, `native/services/content/registry/archive_journal.cpp`,
  MIT code over this file format): once per start it checks `generation` against the identity kiln-eml granted for the
  launch (`published_identity`'s `archive_sha256`) and names each package on the registry fields its member paths name
  (format 2; recipe and loot entries by their element), or (format 1) on the entries whose resource GUID it lists
  (`changed` and `created`; only registries whose entries are whole resources: items, drops, balance), as `archiveWriter`
  rows of certainty `named` in `kiln/state/registry.json` ([registries guide](../modding/registries/README.md#who-else-wrote-a-field)).
- **Hosted ShroudForge** (Kiln's bundled ShroudForge, since 0.2.0-dev.32) writes the same format 2 of its own pass beside its
  record of the pass, `<game>/<stem>.kfc.change-journal.json` (`writer` `shroudforge`), bound to the SHA-256 of the
  published `<stem>.kfc`; Kiln reads it in a start whose backend is hosted or installed ShroudForge (kiln-eml stood
  aside), never kiln-eml's place.

### EML's own pass (format 2)

An EML start outside kiln mode (`runner::run`, kiln-eml's plugin with EML mods) that publishes a generation writes the
same file in format 2 (`schema/sfi/eml-journal/eml-journal-2.schema.json`, dialect `eml`, 0.2.0-dev.31.18): per
EML mod of the committed run, in the order their entrypoints ran, the resources it wrote with the **member paths** it
wrote in each, at their places in the published resource (`maxStackSize`, `recipes[37].craftingDuration`, `input[]` for a
list's members, `""` for the whole resource), the ones it created and the contents it created; no plan digests (`null`).

- **Recording:** EML's runtime (mod-loader-lua `env/writes.rs`, kiln-eml README change 44) names the running entrypoint
  and keeps a small log on each value wrapper of the writes made through it; once the mods ran it walks the values as
  they will be written, once, into paths. A write into a value detached from its resource is not listed; a copy assigned
  into a resource is the assigning mod's; a value put in place over another (a field, an element, a variant's value, the
  resource's data) takes over the writes logged in the one it replaced, member by member where it has that member (a copy
  has them all), so they move with their members afterwards (the new value can be a copy of the old one); a
  mod's entry module writes as that mod, whoever requires it first. Off outside a run; in a run one thread-local read
  and a scan of a few entries per write (5,000 recipes, 10,546 writes: about +3 to 5 % on the mods' Lua time; the walk
  1.4 ms with nothing logged, 3.6 ms with those writes, beside 3.5 ms for EML's own conversion of the same tree).
- **Binding:** `generation` and `receiptSha256` are the SHA-256 of the receipt the publication wrote. kiln-eml writes the
  journal after that publication, under its install lock, only while the receipt is still that one
  (`StartupJournal::write_for`), and reports the live generation in `kiln/state/eml.json` (`generation`, phase `done`): the
  receipt it published, or the one an unchanged start found live (that start runs no Lua and keeps the journal byte for
  byte). The same removals as format 1 apply (every publication and restore removes it first). A journal that cannot be
  written leaves the publication standing with a warning in kiln.log; Kiln then names none of its mods.
- **Retry:** a mod whose Lua fails is dropped and the run repeats; only the committed run is recorded (`attempt` `retry`).
- **Bounds:** format 1's, plus at most 16,384 member paths per resource (more: `[""]`, the whole resource; room for a path
  per recipe of the game's one recipe registry several times over) of at most 256 bytes (longer: the member that holds
  it), and at most 8 MiB of paths in all (a resource past that is listed whole), so a journal stays below the 16 MiB a
  reader takes. A mod whose id is not a ShroudForge identifier is listed in `unattributed`.
- **Kiln's registry stage** reads it in an EML start against kiln-eml's `generation` (kiln.eml `state_json`, this process's
  eml.json) and names each mod on the Kiln fields its paths hold or lie in, recipes and loot entries by their element. A
  Kiln reader from before format 2 refuses it whole (it names nobody).

## Tests

| Test | What it pins |
|---|---|
| `native/core/tests/abi_headers/eml_startup.c` via `native/core/tests/test-abi-headers.sh` | every size, offset and width, the enum values, the constants, every member signature (placeholders), the extent macro on truncated tables; run as C11 and C++20 (archive header first), layout part compile-only for Windows x64 |
| `crates/sfi/kiln-abi/tests/eml_startup.rs` | the generated bindings: sizes, offsets, constants, callback signatures |
| `tools/sfi/abigen/generate.py --check` | the generated bindings match the header |
| `native/core/tests/sfi_startup/run.py` (host.cpp, package_dll.c, run-native.ps1; fixtures from kiln-eml's `tests/m6_two_dll_fixture.rs`) | the real kiln-eml.dll and kiln-sfi.dll, built as the release builds them, in one game-free test host that runs them in the core's order with fake core services, under Wine and natively on Windows: in every kiln-backend step kiln.boot's bound() over the core's `boot_archive_eml.h` (the published identity, the view acquired with its pins, an OnBoot read that answers the value a package's startup Lua committed; unavailable without a grant) and no `kiln.progress` use; kiln-sfi's plugin step ending while a slow or hung initializer holds the loader lock; the witness before the first package DLL, the outcome's grant acquired by the runtime (`game.types`), admission of exactly the committed packages, a changed input (no publication), a busy install lock, no kiln-sfi, a policy or plan that arms no loader with a data package that registers DLLs from its Lua, a slow and a hung initializer holding the loader lock (deadline terminal, barrier, Proton timings), a cached generation's added container rewritten by a DLL (and the next start refused as `plan-stale`), ShroudForge's folder never opened (Wine inotify, a deny-all ACL on Windows), kills after the early step and after the startup record, no failpoint string in kiln-eml.dll |
| `schema/tests/sfi_eml_startup.mjs` | the two schemas against their fixtures, field order, closed objects, each invalid fixture one change from a valid one, the grammar shared with sfi-state, eml-archive and common, the bounds against the plan reader and sfi-state, the base-origin labels against inspection.rs, the format number against the header, the chain and DLL rules a schema cannot say |
