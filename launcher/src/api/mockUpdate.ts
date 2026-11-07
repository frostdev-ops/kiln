// Mock answers for the self-update commands (update.rs), merged into ./mock.ts's handlers. Up to date by default;
// tests and the dev console change it through window.__kilnUpdateMock:
//   offer("0.2.0-dev.43", "notes")        the next check finds this version (and the notice opens)
//   justUpdated("0.2.0-dev.42")           as after a restart into an update this launcher installed
//   failNextInstall([{source, step, message}])  the next download fails at these sources
//   set({...})                            any part of the status, then kiln://update
//   calls()                               the update_* commands called so far (the Nexus build's UI calls none)
// `?dist=nexus` (the Nexus Mods build): like the backend without the self-update feature (commands/update.rs): the
// status is "disabled", update_check changes nothing, update_install and update_restart reject with `unsupported`.
import type { Distribution, KilnError, Task, UpdateFailure, UpdateStatus } from "./commands";
import { NEXUS_PAGE } from "./distribution";

type Emit = (name: string, payload: unknown) => void;
type Args = Record<string, unknown>;

export function updateMockHandlers(emit: Emit, distribution: Distribution = "direct"): Record<string, (a: Args) => unknown> {
  const now = () => new Date().toISOString();
  const nexus = distribution === "nexus";
  const noSelfUpdate = "This build comes from Nexus Mods and does not update itself: new versions are on the Kiln page there.";
  let status: UpdateStatus = nexus ? {
    current: "0.2.0-dev.42", distribution: "nexus", channel: "dev", auto: false, install: { kind: "appimage", inPlace: false, reason: noSelfUpdate },
    page: NEXUS_PAGE, phase: "disabled", checked: null, available: null, failures: [], progress: null,
    ready: null, skipped: null, justUpdated: null, taskId: null, error: null,
  } : {
    current: "0.2.0-dev.42", distribution: "direct", channel: "dev", auto: true, install: { kind: "appimage", inPlace: true, reason: null },
    page: "https://frostdev.io/kiln", phase: "upToDate", checked: now(), available: null, failures: [], progress: null,
    ready: null, skipped: null, justUpdated: null, taskId: null, error: null,
  };
  const calls: string[] = [];
  const refuse = (): never => { throw { code: "unsupported", message: noSelfUpdate, detail: NEXUS_PAGE } satisfies KilnError; };
  let offer: UpdateStatus["available"] = null;
  let failNext: UpdateFailure[] | null = null;
  let seq = 0;
  const send = () => emit("kiln://update", structuredClone(status));
  const put = (p: Partial<UpdateStatus>) => { status = { ...status, ...p }; send(); return structuredClone(status); };
  const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

  const check = async () => {
    if (status.phase === "downloading" || status.phase === "ready") return status;
    put({ phase: "checking" });
    await sleep(500);
    if (!offer) return put({ phase: "upToDate", available: null, checked: now(), failures: [] });
    return put({ phase: status.skipped === offer.version ? "skipped" : "available", available: offer, checked: now(), failures: [] });
  };

  const install = () => {
    if (!status.install.inPlace) throw { code: "unsupported", message: status.install.reason ?? "This install cannot update itself." } satisfies KilnError;
    const id = `t-update-${++seq}`;
    const total = 96_000_000;
    const version = status.available?.version ?? "0.2.0-dev.43";
    const task: Task = { id, kind: "update_install", title: "Update the launcher", state: "running", progress: 0, message: "Looking for the update", started: now(), finished: null };
    const failures = failNext;
    failNext = null;
    put({ phase: "downloading", taskId: id, progress: null, error: null, failures: [] });
    emit("kiln://task", task);
    let received = 0;
    const tick = () => {
      received = Math.min(total, received + 9_600_000);
      const source = failures?.length ? "GitHub" : "frostdev.io";
      put({ progress: { received, total, source } });
      emit("kiln://task", { ...task, progress: received / total, message: `Downloading from ${source}: ${(received / 1048576).toFixed(1)} of ${(total / 1048576).toFixed(1)} MB` });
      if (received < total) { setTimeout(tick, 160); return; }
      if (failures?.length && failures.length > 1) {
        const error: KilnError = { code: "refused", message: "The update could not be downloaded and verified from any source.", detail: failures.map((f) => `${f.source}: ${f.message}`).join("; ") };
        put({ phase: "available", progress: null, failures, error });
        emit("kiln://task", { ...task, state: "failed", message: error.message, finished: now(), error });
        return;
      }
      put({ phase: "ready", progress: null, failures: failures ?? [], ready: { version, source, installed: true } });
      emit("kiln://task", { ...task, state: "done", progress: 1, message: `Verified ${version} from ${source}`, finished: now(), result: status });
    };
    setTimeout(tick, 200);
    return id;
  };

  const api = {
    set: (p: Partial<UpdateStatus>) => put(p),
    offer: (version: string, notes: string | null = null, runtime: string | null = null) => {
      offer = { version, notes, date: now(), source: "frostdev.io", page: "https://frostdev.io/kiln", runtime };
      return check();
    },
    justUpdated: (from: string, notes: string | null = null) => put({
      justUpdated: { from, to: status.current, at: now(), source: "frostdev.io", notes }, phase: "upToDate", available: null, ready: null,
    }),
    failNextInstall: (f: UpdateFailure[]) => { failNext = f; },
    calls: () => [...calls],
  };
  if (typeof window !== "undefined") (window as unknown as { __kilnUpdateMock?: typeof api }).__kilnUpdateMock = api;

  const handlers: Record<string, (a: Args) => unknown> = nexus ? {
    update_status: () => structuredClone(status),
    update_check: () => structuredClone(status),
    update_install: () => refuse(),
    update_restart: () => refuse(),
    update_skip: () => structuredClone(status),
    update_ack: () => structuredClone(status),
  } : {
    update_status: () => status,
    update_check: () => check(),
    update_install: () => install(),
    update_restart: () => {
      if (status.phase !== "ready" || !status.ready) throw { code: "conflict", message: "No update is ready." } satisfies KilnError;
      const to = status.ready.version;
      offer = null;
      put({ justUpdated: { from: status.current, to, at: now(), source: status.ready.source, notes: status.available?.notes ?? null }, current: to, phase: "upToDate", available: null, ready: null, taskId: null });
      return null;
    },
    update_skip: (a) => {
      const v = (a.version as string | null) ?? null;
      return put({ skipped: v, phase: status.available ? (status.available.version === v ? "skipped" : "available") : status.phase });
    },
    update_ack: () => put({ justUpdated: null }),
  };
  // every update_* call is recorded (tests check the Nexus build's UI makes none)
  return Object.fromEntries(Object.entries(handlers).map(([name, h]) => [name, (a: Args) => { calls.push(name); return h(a); }]));
}
