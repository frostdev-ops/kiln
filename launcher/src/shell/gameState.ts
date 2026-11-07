// One place that turns a GameStatus into a label + tone: the top-bar pill and Play use it.
import type { BootState, GameStatus } from "../api/commands";
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
 *  known process start time this says nothing. */
export function playOnceMissed(s: GameStatus | null | undefined, now = Date.now()): boolean {
  if (!s?.running || !s.playOncePending || !s.started) return false;
  const started = Date.parse(s.started);
  return !Number.isNaN(started) && now - started >= KILN_LOAD_GRACE_MS;
}

/** `launch` (launch_state) decides whether boot.json belongs to the running process (under Proton the pid in it is the
 *  game's Windows pid, so the backend compares write and start times); without it the pids must match. */
export function gameState(s: GameStatus | null | undefined, launch?: BootState | null, loading = false, now = Date.now()): GameStateView {
  if (!s && loading) return { phase: "none", label: "Checking", tone: "idle", pulse: true, detail: null };
  if (!s) return { phase: "none", label: "No game", tone: "idle", pulse: false, detail: null };
  if (s.steamUpdating) return { phase: "updating", label: "Updating", tone: "warn", pulse: true, detail: "Steam is updating the game" };
  if (!s.running) return { phase: "stopped", label: "Stopped", tone: "idle", pulse: false, detail: null };
  const boot = launch ? (launch.boot && !launch.stale ? launch.boot : null) : s.boot && s.pid && s.boot.pid === s.pid ? s.boot : null;
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
