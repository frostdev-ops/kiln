// One place that turns a GameStatus into a label + tone: the top-bar pill and Play use it.
import type { BootState, GameStatus, LaunchMode } from "../api/commands";
import type { Tone } from "../ui";
import { t } from "../i18n";

export type GamePhase = "none" | "updating" | "stopped" | "starting" | "running" | "vanilla";

/** Which word `label` is, for logic: compare this, never the (translated) label. */
export type GameWord = "checking" | "noGame" | "updating" | "stopped" | "noKiln" | "starting" | "running" | "vanilla" | "safeIdle"
  | "stoppedStart" | "noWord" | "kilnStopped";

export interface GameStateView {
  phase: GamePhase;
  /** The word shown, in the current language. */
  label: string;
  /** What `label` says (stable across languages). */
  word: GameWord;
  tone: Tone;
  pulse: boolean;
  detail: string | null;
  /** True when `detail` is Kiln's own line about itself ("Kiln 0.2.0 · My mods", "Kiln hooked nothing"): Play's
   *  "Started … ago" line leaves it out. Absent: decided from the text (backend text, English: it starts with "Kiln"). */
  detailKiln?: boolean;
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
    ? { phase: "vanilla", label: t("shell.gameState.vanilla"), word: "vanilla", tone: "info", pulse: true, detail: t("shell.gameState.startedWithoutKiln"), detailKiln: false }
    : kilnMissing ? { phase: "running", label: t("shell.gameState.noKiln"), word: "noKiln", tone: "warn", pulse: true, detail: t("shell.gameState.noKilnDetail"), detailKiln: false } : raw;
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
  if (!s && loading) return { phase: "none", label: t("shell.gameState.checking"), word: "checking", tone: "idle", pulse: true, detail: null };
  if (!s) return { phase: "none", label: t("shell.gameState.noGame"), word: "noGame", tone: "idle", pulse: false, detail: null };
  if (s.steamUpdating) return { phase: "updating", label: t("shell.gameState.updating"), word: "updating", tone: "warn", pulse: true, detail: t("shell.gameState.steamUpdating"), detailKiln: false };
  if (!s.running) return { phase: "stopped", label: t("shell.gameState.stopped"), word: "stopped", tone: "idle", pulse: false, detail: null };
  const boot = currentBoot(s, launch);
  if (!boot) {
    if (playOnceMissed(s, now)) return { phase: "running", label: t("shell.gameState.noKiln"), word: "noKiln", tone: "warn", pulse: true, detail: t("shell.gameState.noKilnDetail"), detailKiln: false };
    return s.playOncePending
      ? { phase: "starting", label: t("shell.gameState.starting"), word: "starting", tone: "ember", pulse: true, detail: t("shell.gameState.waitingBoot"), detailKiln: false }
      : { phase: "running", label: t("shell.gameState.running"), word: "running", tone: "ok", pulse: true, detail: s.pid ? t("shell.gameState.pid", { pid: s.pid }) : null, detailKiln: false };
  }
  if (boot.playOnce?.mode === "vanilla") return { phase: "vanilla", label: t("shell.gameState.vanilla"), word: "vanilla", tone: "info", pulse: true, detail: t("shell.gameState.startedWithoutKiln"), detailKiln: false };
  const what = boot.safeIdle ? t("shell.gameState.whatSafeIdle") : boot.profile?.name ?? (boot.profile?.mode === "legacy" ? t("shell.gameState.whatDropIns") : boot.profile?.mode ?? "");
  return {
    phase: "running", label: boot.safeIdle ? t("shell.gameState.safeIdle") : t("shell.gameState.running"), word: boot.safeIdle ? "safeIdle" : "running",
    tone: boot.safeIdle ? "warn" : "ok", pulse: true,
    detail: what ? t("shell.gameState.kilnWhat", { version: boot.kiln, what }) : t("shell.gameState.kiln", { version: boot.kiln }), detailKiln: true,
  };
}
