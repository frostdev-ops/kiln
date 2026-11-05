# Faults and crash reports

## Fault policy
An exception that escapes a mod's code on the tick thread is a **fault** of that mod: tick handlers (Pre/Post),
game-log handlers, scheduled work (`Scheduler.Post`, `await` continuations, `async void`), IMC handlers, event-bus
handlers (`Events.HandlerFaulted`), `OnLoad`, and unobserved `Task` exceptions whose stack is in the mod's assembly.
Each fault is logged with its stack; the other handlers and mods carry on.

After `fault_limit` faults within `fault_window_s` seconds the mod is **disabled**: its registrations are revoked
(ticks, events, IMC, native owner), its record says why, a crash report is written and `ModDisabledEvent` fires.
Mods that depend on it keep running (logged). `reload <id>` gives it a fresh start.

```
[E] [host] mod sample-faulty threw in Pre (4/4): System.InvalidOperationException: … at step 2900
[E] [host] mod sample-faulty disabled after 4 faults within 120 s; `reload sample-faulty` to try again; crash report …\kiln\crash-reports\crash-2026-11-04_00.36.13.445-mod-disabled.txt
```

## kiln.json `mods` section (all optional)
```json
{ "policy": "any", "clr": true,
  "mods": { "fault_limit": 5, "fault_window_s": 60, "stop_timeout_ms": 5000, "test_timeout_s": 30, "crash_reports": true } }
```
| Key | Default | Meaning |
|---|---|---|
| `fault_limit` | 5 | faults within the window that disable a mod |
| `fault_window_s` | 60 | the window; 0 = count since load |
| `stop_timeout_ms` | 5000 | how long the stop signal waits for ServerStopping + unloads |
| `test_timeout_s` | 30 | default per-test timeout |
| `crash_reports` | true | write managed crash reports |

## Crash reports: `kiln/crash-reports/crash-<yyyy-MM-dd_HH.mm.ss.fff>-<kind>.txt`
| Kind | Written by | When |
|---|---|---|
| `mod-disabled` | Kiln.Host | the fault policy disabled a mod |
| `managed-unhandled` | Kiln.Host | an exception nobody caught on another thread (the process is going down); names the mod whose code threw |
| `host-fault` | Kiln.Host | Kiln.Host itself threw on the tick thread (first 3) |
| `native-fault` | kiln-core (`native/core/src/crash.cpp`) | an SEH exception in a native consumer the core's guards catch: tick and log subscribers, posted tasks, plugin entry points (the consumer is removed, the server runs on) |

Layout (both writers): description; `-- System --` (Kiln core/host/API versions, .NET, OS and Wine version, game exe
sha256 and verification, side, pid, uptime, step, phase); `-- Mods (load order) --` (id, version, state, faults,
ms/step, error, folder: `kiln/logs/mods.txt`, which Kiln.Host rewrites on every change); `-- Details --` (exception
and stack; native: code, faulting module+offset, access type and address, callback module, stack unwound from the
faulting context); `-- Last kiln.log lines --` (60). At most 20 managed and 10 native reports per run; the folder
keeps the newest 100.

Native example (lab, a test plugin writing through a null pointer):
```
Description: access violation (0xc0000005) in a tick subscriber (kiln-crash-test.dll+0x10e0); it was removed, the server runs on
Exception: 0xc0000005 (access violation) at kiln-crash-test.dll+0x1130
Access: write of 0x0
Stack (unwound from the fault, rsp 0x7c54e518):
  #0  kiln-crash-test.dll+0x1130
  #1  kiln-core.dll+0x22f91
  …
  #4  enshrouded_server.exe+0x6c8e83
```
