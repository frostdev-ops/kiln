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

/** `launch` (launch_state) decides whether boot.json belongs to the running process (under Proton the pid in it is the
 *  game's Windows pid, so the backend compares write and start times); without it the pids must match. */
export function gameState(s: GameStatus | null | undefined, launch?: BootState | null, loading = false): GameStateView {
  if (!s && loading) return { phase: "none", label: "Checking", tone: "idle", pulse: true, detail: null };
  if (!s) return { phase: "none", label: "No game", tone: "idle", pulse: false, detail: null };
  if (s.steamUpdating) return { phase: "updating", label: "Updating", tone: "warn", pulse: true, detail: "Steam is updating the game" };
  if (!s.running) return { phase: "stopped", label: "Stopped", tone: "idle", pulse: false, detail: null };
  const boot = launch ? (launch.boot && !launch.stale ? launch.boot : null) : s.boot && s.pid && s.boot.pid === s.pid ? s.boot : null;
  if (!boot) {
    return s.playOncePending
      ? { phase: "starting", label: "Starting", tone: "ember", pulse: true, detail: "Waiting for Kiln to boot" }
      : { phase: "running", label: "Running", tone: "ok", pulse: true, detail: s.pid ? `pid ${s.pid}` : null };
  }
  if (boot.playOnce?.mode === "vanilla") return { phase: "vanilla", label: "Vanilla", tone: "info", pulse: true, detail: "Started without Kiln" };
  const what = boot.safeIdle ? "safe idle" : boot.profile?.name ?? (boot.profile?.mode === "legacy" ? "drop-ins" : boot.profile?.mode ?? "");
  return { phase: "running", label: boot.safeIdle ? "Safe idle" : "Running", tone: boot.safeIdle ? "warn" : "ok", pulse: true, detail: `Kiln ${boot.kiln}${what ? ` · ${what}` : ""}` };
}
