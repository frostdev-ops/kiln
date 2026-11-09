# Pacing: where the game waits, and its task pools (kiln.pacing)

Dedicated server svn 1287369 (Steam 26430253, exe sha256 `f0d824be7fcd48eb…`), previous server svn 1285601 (Steam 26404334,
`c5c9fe50c6398d2a…`), client builds 26404966 (`ce6fdba63e27744…`) and the current one (`5d9ef2f140b8ed49…`). Definitions:
`sigdb/symbols/server.d/pacing.json`, `client.d/pacing.json`. Service: `native/services/pacing/pacing.cpp`. Usage:
[../kiln/modding/lifecycle.md](../kiln/modding/lifecycle.md) (Pacing).

The places and their meaning are Enshwick's (MIT; `crates/internal/loader/src/pacing.rs` and `anchors/routes.toml`
`pacing.*`, v1.0.0-alpha.13). Enshwick finds each place from the strings of the function around it (`~fn:pacing.*` loads) and a
byte shape in it. Kiln finds the same instructions with sigdb byte patterns; the strings below are how the functions were located
and are what to look for on a build where a pattern stops matching.

## The three functions

| Strings the function loads | Function |
|---|---|
| `Unhandled result code %d in MsgWaitForMultipleObjectsEx` | the event loop's wait (one function, chained in `.pdata`) |
| `Main_Server` | the server's game-loop thread (the pass that calls the wait) |
| `[task] Worker count: %u/%u/%u` | the task system's constructor (`[task] TaskSystemSetup:` in the log) |

## Places (server 26430253; 26404334 has the same addresses; every symbol is a mid-function address)

| Symbol | RVA | Instruction hooked | Registers at the hook |
|---|---|---|---|
| `pacing/LoopWait` | `0x5c954b` | `mov rdx,[rbp-0x28]`, right after `cmovl r9d,edx` | `r9d` = ms the loop will wait, `rbx` = ns left before its deadline |
| `pacing/ServerWait` | `0x7ae4b3` | `mov r8,r12`, right after the three-way pick of the wait | `r9` = the wait in ns (arg 4 of the call to `0x5c9040`) |
| `pacing/DefaultWorkers` | `0x5d5573` | `mov ecx,edi` (then `cmp al,dil`) | `rax` = the default group's limit, the byte `[cfg+9]` |
| `pacing/BackgroundWorkers` | `0x5d5590` | `movzx r13d,cl` (then `cmp al,r13b`) | `rax` = the background group's limit, the byte `[cfg+0xa]` |

**Loop wait.** Before the wait the function takes `rbx` (the time left to the deadline, ns) through the multiplier
`0x431bde82d7b634db` (divide by 1e6, `imul rbx; sar rdx,0x12`), clamps the result into `0..0xffffffff` (negative: 0) into `r9d`, and calls
`WaitForMultipleObjectsEx(count, handles, FALSE, r9d, TRUE)`. With less than a millisecond left the truncated value is 0 and
the loop polls until the deadline. `rbx` is not written between the multiplication and the call, which is why the sigdb pattern
holds the whole division: the hook can rely on `rbx`. The same function serves the server's main loop, its game loop and its
session loop; the hook sees every thread that waits. On the client the function has two waits after one division: the
client's second wait (`cmovl r8d,edx`, `r9d = 0x1cff` queue mask, `call rax` = `MsgWaitForMultipleObjectsEx`) is taken when
`[r13+0x48] != 0`, and the hook there reads `r8d`.

**Server wait.** `Main_Server`'s pass picks the wait with `cmp [rbx+0x53c],r15d` (players online?): 40,000,000 ns (`0x2625a00`) with nobody online;
with players 1,000,000 ns (`0xf4240`), or 100,000 ns (`rdi`) when the last pass took more than 14 ms (`cmp qword [rbp+0x67], 0xd59f80`).
It then calls `0x5c9040(ctx, handles, loop, wait)`: the callee turns the wait into a deadline (`now + wait`, saturating), cuts it to the next
scheduled timer, and runs the loop wait above until the deadline. So a longer wait never waits past the next timer, and an
event still ends the wait early. The sim steps once per pass, so with nobody online the step rate is about `1 / (wait + step time)`: 24.5 Hz with
the 40 ms wait.

**Task workers.** The constructor (the log's `[task] TaskSystemSetup:` block) takes, per group, `min(count, limit)` where `count` comes from the CPU
topology the game logs (`CPU Types: LPerformance 16, PPerformance 8` on the lab host) and the limit is a byte of the setup struct (`[r15+9]` default,
`[r15+0xa]` background, `[r15+0xb]` the third group, Shader): `movzx eax,byte [r15+9]; mov ecx,edi; cmp al,dil; cmovb ecx,eax`. Hooking before the
`mov`/`cmp` and lowering `rax` lowers the group's size. The log shows the result: `Group 'Default'  Worker:10  Main:2`, `Group 'Background'  Worker:8`
(`Worker` is the limit plus `Main`), and the ECS `TaskQueue: workerCount:8` follows the default group. With a cap of 4 and 3 on the 16-CPU lab host
the lines read `Worker:6  Main:2`, `Worker:3` and `workerCount:6`.
The pools start at process start (game log time 0.3 s), long before the first server step (a lab's first step is some 6 s after
boot), which is when Kiln mods load: a mod cannot cap them; an operator file or native code before the game's entry can (`kiln/config/kiln-pacing.json`).
On the client the same function runs before Kiln has a window to hook; the service does not offer the caps there.

## Builds

| Build | LoopWait | MessageWait | ServerWait | DefaultWorkers | BackgroundWorkers |
|---|---|---|---|---|---|
| server 26430253 (svn 1287369) | `0x5c954b` | - | `0x7ae4b3` | `0x5d5573` | `0x5d5590` |
| server 26404334 (svn 1285601) | `0x5c954b` | - | `0x7ae4b3` | `0x5d5573` | `0x5d5590` |
| client current (`5d9ef2f1…`) | `0xf5b3ee` | `0xf5b3bc` | `0xe52883` | `0x8ea833` | `0x8ea850` |
| client 26404966 (`ce6fdba6…`) | `0xf5b30e` | `0xf5b2dc` | `0xe527a3` | `0x8ea753` | `0x8ea770` |

Each pattern matches exactly once on each exe (`tools/sigport/sigdb_check.py`). The client rows are static only: nothing was run in
a client. The server rows were hooked and run in a lab (docs/kiln/modding/lifecycle.md, Pacing).

## Checked in a lab server (Steam build 26430253, nobody online, Wine 11, 16 CPUs)
- Loop wait: every call has `ms = trunc(left_ns / 1e6)` (0 inconsistent in 1.3 to 1.8 million calls); about 160,000 to 200,000 calls a second over 6 threads,
  99.8 % of them with 0 ms (the polling); loops with no deadline give `ms = 0xffffffff`, `left = INT64_MAX`; the server loop's own thread never waits
  more than the 40 ms idle wait, and 80 ms or 10 ms when a server-wait handler sets those (the game's own deadline arithmetic follows the register).
- Server wait: 40,000,000 ns on every pass with nobody online (0 online, 0 slow in a lab); a handler making it 80 ms gave 12.0 Hz against 22.2 Hz,
  10 ms gave 54.8 Hz, and with the handler gone the rate returned (22.0 Hz). On a quiet host the baseline is 24.2 Hz (1 / (40 ms + a step of about 1 ms)).
- Worker caps: `Worker:6 Main:2` and `Worker:3` with `{"default": 4, "background": 3}` (above).
- Next to Enshwick's optimizer mod (alpha.13, exact waits on): its loop-wait hook was chained under Kiln's: its sleeps cut the loop's waits from about 160,000
  to about 500 a second (its own log: `exact waits 140/s`), while Kiln's handlers still saw every wait. Started the other way round (Kiln's hooks first, Enshwick at `"start": "ready"`), Enshwick
  does not find the places Kiln already patched: `loader feature pacing partly off: missing pacing.background_workers, pacing.default_workers,
  pacing.loop_wait, pacing.server_wait` and the optimizer's `hooked: exact waits false ... server loop wait false`. Kiln's side kept working.
