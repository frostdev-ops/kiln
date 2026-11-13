// What a start is doing, in words (launch_progress, kiln_manage::start_progress; docs/kiln/launcher/app.md "What a start
// is doing"): the running step's line, the time left as a person reads it, where the number comes from, and what the
// bar says to a screen reader. Shared by the Play page, the top bar's pill, the mouth and the Deck's Home.
import type { PreStart, StartStep, StartTimeline } from "../api/commands";

/** Phases in which the start is still going: the bar shows. `quiet` (no word from Kiln for 60 s) is not one: it is
 *  said as what it is, without a bar or a countdown (quietText). */
export const ACTIVE = new Set(["launcher", "waiting", "booting", "loading"]);
/** Steam was asked this long ago and no game came: the start is over (kiln_manage::start_progress::STEAM_WAIT_MS; the
 *  browser tests set a shorter one before the page loads, `window.__kilnSteamWaitMs`). */
export const STEAM_WAIT_MS: number = (globalThis as { __kilnSteamWaitMs?: number }).__kilnSteamWaitMs ?? 5 * 60_000;

/** The sentence of a start Kiln has said nothing about for 60 s (120 s in the early plugins). */
export function quietText(t: StartTimeline): string {
  const s = t.running != null ? t.steps[t.running] : null;
  const secs = Math.round((t.quietMs ?? 0) / 1000);
  // a client that loads its mods only at the first world waits at `mods` with nothing done: the game is at its menu
  if (s?.id === "mods" && !s.done) return "The game is waiting at its menu: Kiln loads the mods when the game asks for them (on some PCs only once a world loads).";
  return `Kiln has said nothing for ${secs} s${s ? ` (last: ${s.label})` : ""}. A step can be slow on a busy PC; if the game does not move, close it and save a support report on the Logs page.`;
}
/** The timeline is shown in place of the boot stages (a dev.26 runtime's `legacy` and the end states are not). */
export const showsProgress = (t: StartTimeline | null | undefined): t is StartTimeline => !!t && ACTIVE.has(t.phase);

/** "about 40 s", "about 1 min 25 s", "a few seconds": rounded to 5 s above 20 s, whole seconds from 5 to 20 s. */
export function aboutText(ms: number): string {
  const s = ms / 1000;
  if (s < 5) return "a few seconds";
  const r = s > 20 ? Math.round(s / 5) * 5 : Math.round(s);
  return r >= 60 ? `about ${Math.floor(r / 60)} min${r % 60 ? ` ${r % 60} s` : ""}` : `about ${r} s`;
}

/** A step's duration: "0.6 s", "19 s", "1 min 19 s". */
export function tookText(ms: number): string {
  const s = ms / 1000;
  if (s < 10) return `${s.toFixed(1)} s`;
  const r = Math.round(s);
  return r >= 60 ? `${Math.floor(r / 60)} min${r % 60 ? ` ${r % 60} s` : ""}` : `${r} s`;
}

/** "from your last 4 starts", "from your last start" (with other mods: "of this game"). */
export function basisText(t: StartTimeline): string {
  const n = t.estimate.starts;
  const what = n === 1 ? "start" : `${n} starts`;
  return t.estimate.same ? `From your last ${what} with these mods` : `From your last ${what} of this game, with other mods or another Kiln`;
}

/** "Starting Kiln's services: kiln-vfx, building 85 materials (14 of 31)". */
export function stepLine(s: StartStep, prep?: PreStart | null): string {
  // kiln-eml's own progress is the detail of the early plugins (it is the finer source there)
  if (s.id === "early-plugins" && prep) return prep.message.replace(/\.$/, "");
  const detail = s.detail ? `: ${s.detail.replace(/: /g, ", ")}` : "";
  const count = s.id !== "cook" && s.total ? ` (${Math.min(s.done ?? 0, s.total)} of ${s.total})` : "";
  return `${s.label}${detail}${count}`;
}

export interface StartWords {
  /** The running step's line, or the sentence of a state that is not progress. */
  line: string;
  /** "About 40 s left", the first start's sentence, "taking longer than usual (usually 19 s)". */
  left: string;
  /** Where the number comes from (on hover). */
  basis: string | null;
  /** 0..100 for the bar. */
  pct: number;
  /** The bar's aria-valuetext. */
  valueText: string;
  /** Short: the pill and the mouth ("about 40 s left", "step 5 of 14"). */
  short: string;
  /** Step N of M. */
  stepOf: string;
  first: boolean;
}

/** The words for a timeline. Its fraction is the launcher's, kept from moving back per install and start
 *  (kiln_manage::start_progress::Steady): the window draws it as it comes. */
export function startWords(t: StartTimeline, prep?: PreStart | null): StartWords {
  const fraction = t.phase === "ready" ? 1 : t.estimate.fraction;
  const e = t.estimate;
  const running = t.running != null ? t.steps[t.running] : null;
  const pct = Math.round(Math.max(0, Math.min(1, fraction)) * 100);
  const n = Math.min(e.stepsPlanned, e.stepsDone + (running ? 1 : 0));
  const stepOf = `Step ${Math.max(1, n)} of ${e.stepsPlanned}`;
  let line = running ? stepLine(running, prep) : "Starting";
  // the game's process exists, Kiln has not said anything yet (a 0.2.0-dev.26 runtime never does: its boot.json then
  // ends the bar, as it ended "Waiting for Kiln to boot" before)
  if (t.phase === "waiting") line = "Waiting for Kiln to boot";
  const first = e.leftMs === null;
  let left: string;
  let short: string;
  if (t.steamSlow) {
    // Steam took the start a minute ago and the game's process does not exist yet: no number, and why
    left = "Steam is still preparing the game: this can take minutes the first time.";
    short = "Steam is preparing";
  } else if (first) {
    left = `First start with these mods on this PC: Kiln is timing it. ${stepOf}.`;
    short = stepOf.toLowerCase();
  } else if (e.slow) {
    const slow = t.steps.find((s) => s.id === e.slow!.id);
    const after = e.leftMs! > 0 ? ` Then ${aboutText(e.leftMs!)}.` : "";
    left = `${slow?.label ?? "This step"}: taking longer than usual (usually ${tookText(e.slow.usualMs)}).${after}`;
    short = "longer than usual";
  } else {
    left = `${cap(aboutText(e.leftMs!))} left.`;
    short = `${aboutText(e.leftMs!)} left`;
  }
  const valueText = first ? `${stepOf}: ${line}` : `${pct} percent, ${short}: ${line}`;
  return { line, left, basis: first ? null : basisText(t), pct, valueText, short, stepOf, first };
}

const cap = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);

/** What a step row says on the right: what it took, that it runs (and what it usually takes), what it usually takes. */
export function stepTime(s: StartStep): string {
  if (s.state === "done") return s.tookMs != null ? tookText(s.tookMs) : "done";
  if (s.state === "running") return s.usualMs != null ? `usually ${tookText(s.usualMs)}` : "now";
  if (s.state === "skipped") return "not needed";
  return s.usualMs != null ? `usually ${tookText(s.usualMs)}` : "";
}
