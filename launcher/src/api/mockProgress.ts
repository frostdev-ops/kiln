// The browser mock's start progress (`launch_begin`, `launch_progress`): a scripted start that plays the owner's start of
// 13 Nov 2026 (crates/kiln-manage/src/start_progress_tests.rs, the contract's timeline table), compressed by `scale`,
// through the launcher's steps (cook, profile, Steam) and the runtime's (progress.json), with a history of earlier
// starts kept in memory. The estimate is a port of kiln_manage::start_progress::estimate (RateRule::SameFirst), for the
// window's tests only: the Rust function is the one the launcher runs.
// Off until a test turns it on (`__kilnMock.scriptStart`): a plain mock start has no progress.json (a 0.2.0-dev.26
// runtime), as every older spec expects.
import type { StartEstimate, StartPhase, StartStep, StartTimeline } from "./commands";
/** start_progress STEAM_SLOW_MS (tests: `window.__kilnSteamSlowMs`). */
const STEAM_SLOW_MS: number = (globalThis as { __kilnSteamSlowMs?: number }).__kilnSteamSlowMs ?? 60_000;

interface Part { key: string; ms: number }
export interface KeptStep { id: string; ms: number; total?: number | null; parts?: Part[] }
export interface KeptStart { kiln: string; fingerprint: string; steps: KeptStep[] }
interface RStep { id: string; label: string; ms: number; items?: number[]; detail?: string }

export interface StartScript {
  /** Real time per scripted millisecond (0.1: the 147 s start in 14.7 s). */
  scale: number;
  cook: { key: string; name: string; ms: number }[];
  profileMs: number;
  steamMs: number;
  runtime: RStep[];
  /** The step after which the state is `loading` (boot.json is written then). */
  loadingAfter: string;
  /** A 0.2.0-dev.26 runtime: no progress.json at all; boot.json at the end of `loadingAfter`. */
  dev26?: boolean;
  /** Safe idle at this step, with this sentence. */
  idle?: { at: string; why: string };
  /** The services' detail in place of the owner's (a long one: the line's fit). */
  servicesDetail?: string;
  /** Kiln says nothing more once this step began (progress.json not written for 61 s: phase quiet). */
  quietAt?: string;
  /** Steam takes the start and no game process ever comes. */
  noProcess?: boolean;
  /** One step `factor` times as long as in the history. */
  slow?: { id: string; factor: number };
  fingerprint: string;
}

const KILN = "0.2.0-dev.27+20261113.2e75c1f";

const even = (n: number, ms: number) => Array.from({ length: n }, (_, i) => Math.round((ms * (i + 1)) / n));

/** The owner's start (the contract's table): 147 s from Play to the main menu. */
export function ownerScript(o: Partial<StartScript> = {}): StartScript {
  const services = [...Array.from({ length: 13 }, (_, i) => (i + 1) * 20), 18_250, ...Array.from({ length: 17 }, (_, i) => 18_250 + (i + 1) * 29)];
  return {
    scale: 0.1,
    cook: [
      { key: "kiln-equipment@kdh1:aa", name: "Kiln Equipment", ms: 26_000 },
      { key: "big-dummies@kdh1:bb", name: "BetterDummies", ms: 26_000 },
      { key: "watering-can@kdh1:cc", name: "Large Watering Can", ms: 27_000 },
    ],
    profileMs: 1_000,
    steamMs: 5_000,
    runtime: [
      { id: "start", label: "Reading Kiln's settings and the profile", ms: 95 },
      { id: "native-early", label: "Loading native mods before the hooks", ms: 605, items: even(3, 605) },
      { id: "early-plugins", label: "Starting ShroudForge", ms: 9_500, detail: "kiln-shroudforge.dll" },
      { id: "signatures", label: "Finding the game's functions", ms: 50 },
      { id: "services", label: "Starting Kiln's services", ms: 18_750, items: services, detail: "kiln-vfx: building 85 materials" },
      { id: "plugins", label: "Loading Kiln's plugins", ms: 800, items: even(10, 800) },
      { id: "native", label: "Loading native mods", ms: 500, items: even(3, 500) },
      { id: "dotnet", label: "Starting .NET", ms: 512 },
      { id: "mods", label: "Loading the mods", ms: 6_788, items: even(4, 6_788), detail: "Kiln Equipment" },
      { id: "content", label: "Applying the mods' content", ms: 12_400, items: even(40, 12_400), detail: "blocks and materials" },
      { id: "game", label: "The game is loading", ms: 11_600 },
    ],
    loadingAfter: "dotnet",
    fingerprint: "kms1:owner",
    ...o,
  };
}

const LABELS: Record<string, string> = {
  cook: "Cooking mods", profile: "Checking the profile", steam: "Starting the game through Steam",
};

// ---------------------------------------------------------------- the estimate (port)

function median(v: number[]): number | null {
  if (!v.length) return null;
  const s = [...v].sort((a, b) => a - b);
  const n = s.length;
  return n % 2 ? s[(n - 1) / 2] : Math.floor((s[n / 2 - 1] + s[n / 2]) / 2);
}

export interface StepNow { id: string; ms: number; tookMs: number | null; done?: number | null; total?: number | null; keys: string[]; parts: Part[]; partMs: number }
interface Usual { ms: number; starts: number; same: boolean }

function partHistory(h: KeptStart[], id: string, key: string): number[] {
  return [...h].reverse().map((r) => r.steps.find((s) => s.id === id)?.parts?.find((p) => p.key === key)?.ms).filter((x): x is number => x !== undefined).slice(0, 5);
}

function usual(h: KeptStart[], id: string, total: number | null | undefined, keys: string[], kiln: string, fp: string): Usual | null {
  if (keys.length) {
    const each = keys.map((k) => partHistory(h, id, k));
    if (each.every((v) => v.length)) return { ms: each.reduce((a, v) => a + median(v)!, 0), starts: Math.max(...each.map((v) => v.length)), same: true };
  }
  const rev = [...h].reverse();
  const same = rev.filter((r) => r.kiln === kiln && r.fingerprint === fp).map((r) => r.steps.find((s) => s.id === id)?.ms).filter((x): x is number => x !== undefined).slice(0, 5);
  if (same.length) return { ms: median(same)!, starts: same.length, same: true };
  const any = rev.map((r) => r.steps.find((s) => s.id === id)).filter((s): s is KeptStep => !!s).slice(0, 5)
    .map((s) => (total && s.total ? Math.floor((s.ms * total) / s.total) : s.ms));
  return any.length ? { ms: median(any)!, starts: any.length, same: false } : null;
}

export function estimate(plan: string[], steps: StepNow[], now: number, h: KeptStart[], kiln: string, fp: string, ready: boolean): StartEstimate {
  const lastBegun = Math.max(-1, ...steps.map((s) => plan.indexOf(s.id)));
  let left: number | null = 0, starts = 0, same = true, done = 0;
  let slow: StartEstimate["slow"] = null;
  const usuals: (number | null)[] = [];
  plan.forEach((id, i) => {
    const s = steps.find((x) => x.id === id);
    const u = usual(h, id, s?.total, s?.keys ?? [], kiln, fp);
    if (u) { starts = Math.max(starts, u.starts); same &&= u.same; }
    usuals.push(u?.ms ?? null);
    let rest: number | null;
    if (s && (s.tookMs !== null || ready)) { done++; rest = 0; }
    else if (s) {
      const elapsed = Math.max(0, now - s.ms);
      let byUsual: number | null = null;
      if (u) {
        const each = s.keys.length ? s.keys.map((k) => median(partHistory(h, id, k))) : null;
        if (each && each.every((x) => x !== null)) {
          const ended = s.parts.filter((p) => p.ms > 0).length;
          byUsual = Math.max(0, (each[ended] ?? 0) - (now - s.partMs)) + (each as number[]).slice(ended + 1).reduce((a, b) => a + b, 0);
        } else byUsual = Math.max(0, u.ms - elapsed);
      }
      const byRate = s.done && s.total != null && elapsed >= 2000 ? Math.floor((elapsed * Math.max(0, s.total - s.done)) / s.done) : null;
      const isSlow = !!u && u.ms > 0 && elapsed * 2 >= u.ms * 3;
      if (isSlow) slow = { id, usualMs: u!.ms };
      // the slow rule decides first; then the usual time alone on the same basis, else the larger of the two
      rest = isSlow ? byUsual : byUsual !== null && byRate !== null ? (u?.same ? byUsual : Math.max(byUsual, byRate)) : byUsual ?? byRate;
    } else if (lastBegun > i || ready) { done++; rest = 0; }
    else rest = u?.ms ?? null;
    left = left !== null && rest !== null ? left + rest : null;
  });
  const planned = plan.length;
  const fraction = ready ? 1 : left !== null ? (now + left > 0 ? Math.min(0.99, now / (now + left)) : 0) : Math.min(0.99, done / Math.max(1, planned));
  return { leftMs: ready ? 0 : left, starts, same: same && starts > 0, slow, fraction, byTime: left !== null, stepsDone: done, stepsPlanned: planned, usual: usuals };
}

// ---------------------------------------------------------------- the scripted start

interface Current { began: number; script: StartScript; mode: string; kept: boolean }

let script: StartScript | null = null;
const current: Record<string, Current | undefined> = {};
const history: Record<string, KeptStart[]> = {};
/** The bar's highest point per install and start, as the launcher keeps it. */
const steadyMax = new Map<string, number>();


/** Tests: plays `s` at the next Play (null: off, the plain mock start without progress.json). */
export function setStartScript(s: StartScript | null) { script = s; }
/** Tests: forgets the earlier starts (the next one is a first start). */
export function clearStartHistory(gameId = "client-steam") { delete history[gameId]; }

const total = (s: StartScript) => s.cook.reduce((a, c) => a + c.ms, 0);
export const processAt = (s: StartScript) => total(s) + s.profileMs + s.steamMs;
const bootAt = (s: StartScript) => processAt(s) + 100;
const durOf = (s: StartScript, r: RStep) => r.ms * (s.slow?.id === r.id ? s.slow.factor : 1);
const itemsOf = (s: StartScript, r: RStep) => (r.items ?? []).map((x) => x * (s.slow?.id === r.id ? s.slow.factor : 1));
export const endAt = (s: StartScript) => bootAt(s) + s.runtime.reduce((a, r) => a + durOf(s, r), 0);
/** When boot.json is there (ms after Play, unscaled). */
export function bootJsonAt(s: StartScript): number {
  let t = bootAt(s);
  for (const r of s.runtime) { t += durOf(s, r); if (r.id === s.loadingAfter) break; }
  return t;
}

/** `launch_begin`: Play was pressed (only with a script; a vanilla start has none). */
export function mockBegin(gameId: string, mode: string) {
  if (!script || mode === "vanilla") { delete current[gameId]; return; }
  current[gameId] = { began: Date.now(), script, mode, kept: false };
}
/** The scripted start of `gameId` (for the fake game's process and boot.json times), if one runs. */
export const scriptedStart = (gameId: string): { began: number; script: StartScript } | null => current[gameId] ?? null;
/** Tests: the game's Kiln starts writing progress.json now (a chained DLL held it back): a dev26 script speaks. */
export function mockSpeak(gameId: string) {
  const c = current[gameId];
  if (c) c.script = { ...c.script, dev26: false };
}
/** The fake game ended: the start is over. */
export function mockStartEnded(gameId: string) { delete current[gameId]; }

/** `launch_progress` for the scripted start at `nowMs`. */
export function mockProgress(gameId: string, nowMs = Date.now()): StartTimeline | null {
  const c = current[gameId];
  if (!c) return null;
  const s = c.script;
  const h = history[gameId] ?? [];
  let t = Math.max(0, (nowMs - c.began) / s.scale);
  if (s.quietAt) {
    // Kiln goes silent inside that step: the file stays as it was a moment after the step began
    let at0 = bootAt(s);
    for (const r of s.runtime) { if (r.id === s.quietAt) break; at0 += durOf(s, r); }
    t = Math.min(t, at0 + 1);
  }
  const steps: StepNow[] = [];
  const shown: StartStep[] = [];
  const plan = [...(c.mode === "profile" ? ["cook"] : []), "profile", "steam"];
  let state = "launcher";
  let steamSlow = false;
  // the launcher's steps
  let at = 0;
  if (c.mode === "profile") {
    const keys = s.cook.map((x) => x.key);
    const parts: Part[] = [];
    let i = 0, partMs = 0;
    for (; i < s.cook.length && at + s.cook[i].ms <= t; i++) { parts.push({ key: s.cook[i].key, ms: s.cook[i].ms }); at += s.cook[i].ms; }
    const cooking = i < s.cook.length;
    partMs = at;
    if (cooking) parts.push({ key: s.cook[i].key, ms: 0 });
    const took = cooking ? null : at;
    steps.push({ id: "cook", ms: 0, tookMs: took, done: Math.min(i, s.cook.length), total: s.cook.length, keys, parts, partMs });
    const n = s.cook.length;
    shown.push({ id: "cook", label: cooking ? (n === 1 ? `Cooking ${s.cook[i].name}` : `Cooking ${s.cook[i].name} (${i + 1} of ${n})`) : LABELS.cook,
      detail: cooking && t - partMs > 1000 ? "kiln: cooking assets/cook.json (modelcook)" : null, done: i, total: n, ms: 0, tookMs: took, state: "done", launcher: true });
    if (cooking) return finish();
  }
  for (const [id, ms] of [["profile", s.profileMs], ["steam", s.steamMs]] as const) {
    const took = at + ms <= t && !(id === "steam" && s.noProcess) ? ms : null;
    // Steam was asked a while ago and no game exists yet (start_progress STEAM_SLOW_MS): no number, the start goes on
    if (id === "steam" && took === null && (nowMs - c.began) - at * s.scale >= STEAM_SLOW_MS) steamSlow = true;
    steps.push({ id, ms: at, tookMs: took, keys: [], parts: [], partMs: at });
    shown.push({ id, label: LABELS[id], ms: at, tookMs: took, state: "done", launcher: true });
    if (took === null) return finish();
    at += ms;
  }
  // the runtime's
  if (s.dev26 || t < bootAt(s)) return finish();
  const runtimePlan = s.runtime.map((r) => r.id);
  plan.push(...runtimePlan);
  let r0 = bootAt(s);
  state = "booting";
  for (const r of s.runtime) {
    if (r0 > t) break;
    const dur = durOf(s, r);
    const ended = r0 + dur <= t;
    const items = itemsOf(s, r);
    const into = t - r0;
    const idle = s.idle?.at === r.id;
    const done = items.length ? (ended ? items.length : items.filter((x) => x <= into).length) : null;
    steps.push({ id: r.id, ms: r0, tookMs: ended && !idle ? dur : null, done, total: items.length || null, keys: [], parts: [], partMs: r0 });
    shown.push({ id: r.id, label: r.label, detail: ended ? null : (r.id === "services" ? s.servicesDetail : undefined) ?? r.detail ?? null, done, total: items.length || null, ms: r0, tookMs: ended && !idle ? dur : null, state: "done", launcher: false });
    if (idle) { state = "idle"; break; }
    if (ended && r.id === s.loadingAfter) state = "loading";
    r0 += dur;
  }
  if (state !== "idle" && t >= endAt(s) && !s.quietAt) state = "ready";
  return finish();

  function finish(): StartTimeline {
    const ready = state === "ready";
    const est = estimate(plan, steps, Math.round(t), h, KILN, s.fingerprint, ready);
    const slowSteam = () => { if (steamSlow) { est.leftMs = null; est.byTime = false; est.slow = null; est.fraction = Math.min(0.99, est.stepsDone / Math.max(1, est.stepsPlanned)); } };
    if (plan.every((p) => ["cook", "profile", "steam"].includes(p))) {
      // before the runtime speaks: its plan is the last start's, else unknown (the contract's 11 steps counted, no number)
      const last = h[h.length - 1];
      if (last) {
        plan.push(...last.steps.map((x) => x.id).filter((id) => !["cook", "profile", "steam"].includes(id)));
        Object.assign(est, estimate(plan, steps, Math.round(t), h, KILN, s.fingerprint, false));
      } else {
        plan.push(...s.runtime.map((r) => r.id));
        Object.assign(est, estimate(plan, steps, Math.round(t), h, KILN, s.fingerprint, false));
        est.leftMs = null; est.byTime = false; est.fraction = Math.min(0.99, est.stepsDone / est.stepsPlanned);
      }
    }
    slowSteam();
    // the launcher keeps the bar's highest point per install and start (kiln_manage::start_progress::Steady)
    const key = `${gameId}\n${c!.began}`;
    est.fraction = ready ? 1 : Math.max(steadyMax.get(key) ?? 0, est.fraction);
    steadyMax.set(key, est.fraction);
    const lastBegun = Math.max(-1, ...steps.map((x) => plan.indexOf(x.id)));
    const out: StartStep[] = plan.map((id, k) => {
      const have = shown.find((x) => x.id === id);
      const st: StartStep = have ? { ...have } : { id, label: LABELS[id] ?? s.runtime.find((r) => r.id === id)?.label ?? id, ms: null, tookMs: null, state: "next", launcher: id in LABELS };
      st.usualMs = est.usual[k] ?? null;
      st.state = st.ms != null ? (st.tookMs != null || ready ? "done" : "running") : ready || lastBegun > k ? "skipped" : "next";
      return st;
    });
    const running = out.findIndex((x) => x.state === "running");
    const running_ = running >= 0 ? running : null;
    const process = t >= processAt(s);
    let phase: StartPhase = state === "ready" ? "ready" : state === "idle" ? "idle" : state === "loading" ? "loading"
      : t >= bootAt(s) && !s.dev26 ? "booting" : process ? (s.dev26 && t >= bootJsonAt(s) ? "legacy" : "waiting") : "launcher";
    if (s.dev26 && t >= bootJsonAt(s)) phase = "legacy";
    const silent = !!s.quietAt && steps.some((x) => x.id === s.quietAt) && !ready && state !== "idle";
    if (silent) phase = "quiet";
    if (ready && !c!.kept) {
      c!.kept = true;
      (history[gameId] ??= []).push({ kiln: KILN, fingerprint: s.fingerprint, steps: steps.map((x) => ({ id: x.id, ms: x.tookMs ?? 0, total: x.total, parts: x.parts.filter((p) => p.ms > 0) })) });
    }
    return {
      phase, why: phase === "idle" ? s.idle?.why ?? null : null, kiln: KILN, started: new Date(c!.began).toISOString(), atMs: Math.round(t),
      quietMs: t >= bootAt(s) && !s.dev26 ? (silent ? 61_000 : 0) : null, steps: out, running: running_, estimate: est, launcher: true, steamSlow,
    };
  }
}
