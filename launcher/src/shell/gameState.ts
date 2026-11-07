// One place that turns a GameStatus into a label + tone: the top-bar pill and Play use it.
import type { BootState, GameStatus, LaunchMode } from "../api/commands";
import type { Tone } from "../ui";

export type GamePhase = "none" | "updating" | "stopped" | "starting" | "running" | "vanilla";

export interface GameStateView {
  phase: GamePhase;
  label: string;
  tone: Tone;
  pulse: boolean;
  detail: string | null;
}

/** How long the game may run before a play-once.ini still in the folder means Kiln's proxy did not run. The proxy reads
 *  and deletes the flag in its DllMain, within the first second of the process; the grace only covers slow disks and
 *  the process-list poll. */
export const KILN_LOAD_GRACE_MS = 30_000;

/** The game has run for a while and the play-once flag is still there: Kiln's dinput8.dll was not loaded this start
 *  (Steam did not load it, e.g. a Proton build that prefers its own dinput8), so the game runs without Kiln. Without a
 *  known process start time, `seen` is when this session first observed the running process. */
export function runStarted(s: GameStatus | null | undefined, now: number, fallback?: number): number | undefined {
  const parsed = s?.started ? Date.parse(s.started) : NaN;
  return Number.isFinite(parsed) && parsed <= now ? parsed : fallback;
}

export function playOnceMissed(s: GameStatus | null | undefined, now = Date.now(), seen?: number): boolean {
  if (!s?.running || s.steamUpdating || !s.runtime.installed || !s.playOncePending) return false;
  const started = runStarted(s, now, seen);
  return started !== undefined && now - started >= KILN_LOAD_GRACE_MS;
}

/** Decisions shared by both Play surfaces. A fresh boot wins over a leftover flag. Missing/invalid/future process
 *  timestamps use when this session first saw the process; time spent waiting for Steam is not process runtime. */
export function playState(s: GameStatus | null | undefined, launch: BootState | null | undefined,
  memo: { at?: number; mode?: LaunchMode; seen?: number }, now: number, loading = false) {
  const boot = currentBoot(s, launch);
  const started = runStarted(s, now, memo.seen);
  const ours = memo.at !== undefined && memo.mode !== undefined;
  const vanilla = boot ? boot.playOnce?.mode === "vanilla" : ours && memo.mode === "vanilla";
  const noBootLate = ours && started !== undefined && now - Math.max(started, memo.at!) >= 2 * KILN_LOAD_GRACE_MS;
  const kilnMissing = !!s?.running && !s.steamUpdating && !!s.runtime.installed && !boot && !vanilla
    && (playOnceMissed(s, now, memo.seen) || (!!launch && noBootLate));
  const raw = gameState(s, launch, loading, now);
  const view: GameStateView = s?.running && !s.steamUpdating && vanilla
    ? { phase: "vanilla", label: "Vanilla", tone: "info", pulse: true, detail: "Started without Kiln" }
    : kilnMissing ? { phase: "running", label: "No Kiln", tone: "warn", pulse: true, detail: "The game runs without Kiln: Steam did not load it" } : raw;
  const starting = !!s?.running && !s.steamUpdating && !vanilla && !boot && !kilnMissing
    && (s.playOncePending || (ours && !noBootLate));
  return { view, kilnMissing, starting };
}

function currentBoot(s: GameStatus | null | undefined, launch?: BootState | null) {
  return launch ? (launch.boot && !launch.stale ? launch.boot : null) : s?.boot && s.pid && s.boot.pid === s.pid ? s.boot : null;
}

/** `launch` (launch_state) decides whether boot.json belongs to the running process (under Proton the pid in it is the
 *  game's Windows pid, so the backend compares write and start times); without it the pids must match. */
export function gameState(s: GameStatus | null | undefined, launch?: BootState | null, loading = false, now = Date.now()): GameStateView {
  if (!s && loading) return { phase: "none", label: "Checking", tone: "idle", pulse: true, detail: null };
  if (!s) return { phase: "none", label: "No game", tone: "idle", pulse: false, detail: null };
  if (s.steamUpdating) return { phase: "updating", label: "Updating", tone: "warn", pulse: true, detail: "Steam is updating the game" };
  if (!s.running) return { phase: "stopped", label: "Stopped", tone: "idle", pulse: false, detail: null };
  const boot = currentBoot(s, launch);
  if (!boot) {
    if (playOnceMissed(s, now)) return { phase: "running", label: "No Kiln", tone: "warn", pulse: true, detail: "The game runs without Kiln: Steam did not load it" };
    return s.playOncePending
      ? { phase: "starting", label: "Starting", tone: "ember", pulse: true, detail: "Waiting for Kiln to boot" }
      : { phase: "running", label: "Running", tone: "ok", pulse: true, detail: s.pid ? `pid ${s.pid}` : null };
  }
  if (boot.playOnce?.mode === "vanilla") return { phase: "vanilla", label: "Vanilla", tone: "info", pulse: true, detail: "Started without Kiln" };
  const what = boot.safeIdle ? "safe idle" : boot.profile?.name ?? (boot.profile?.mode === "legacy" ? "drop-ins" : boot.profile?.mode ?? "");
  return { phase: "running", label: boot.safeIdle ? "Safe idle" : "Running", tone: boot.safeIdle ? "warn" : "ok", pulse: true, detail: `Kiln ${boot.kiln}${what ? ` · ${what}` : ""}` };
}
