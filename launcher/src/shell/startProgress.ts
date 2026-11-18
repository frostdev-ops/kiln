// What a start is doing, in words (launch_progress, kiln_manage::start_progress; docs/kiln/launcher/app.md "What a start
// is doing"): the running step's line, the time left as a person reads it, where the number comes from, and what the
// bar says to a screen reader. Shared by the Play page, the top bar's pill, the mouth and the Deck's Home.
import type { PreStart, StartStep, StartTimeline } from "../api/commands";
import { t } from "../i18n";

/** Phases in which the start is still going: the bar shows. `quiet` (no word from Kiln for 60 s) is not one: it is
 *  said as what it is, without a bar or a countdown (quietText). */
export const ACTIVE = new Set(["launcher", "waiting", "booting", "loading"]);
/** Steam was asked this long ago and no game came: the start is over (kiln_manage::start_progress::STEAM_WAIT_MS; the
 *  browser tests set a shorter one before the page loads, `window.__kilnSteamWaitMs`). */
export const STEAM_WAIT_MS: number = (globalThis as { __kilnSteamWaitMs?: number }).__kilnSteamWaitMs ?? 5 * 60_000;

/** The sentence of a start Kiln has said nothing about for 60 s (120 s in the early plugins). A client in its world is
 *  not given one: the launcher takes that start for over (kiln_manage::start_progress, phase ready). */
export function quietText(tl: StartTimeline): string {
  const s = tl.running != null ? tl.steps[tl.running] : null;
  const secs = Math.round((tl.quietMs ?? 0) / 1000);
  // a client that loads its mods only at the first world waits at `mods` with nothing done: the game is at its menu
  if (s?.id === "mods" && !s.done) return t("shell.startProgress.quietMods");
  // Kiln's part is done (the last step): the game still loads, or its menu drew and Kiln did not see it (another mod's
  // hook over the menu); the start ends at the first world then
  if (s?.id === "game") return t("shell.startProgress.quietGame");
  return s ? t("shell.startProgress.quietLast", { secs, last: s.label }) : t("shell.startProgress.quiet", { secs });
}
/** True when quietText's sentence is about Kiln itself (every one but the menu wait at `mods`): Play's elapsed line
 *  leaves it out (GameStateView.detailKiln). */
export function quietIsKiln(tl: StartTimeline): boolean {
  const s = tl.running != null ? tl.steps[tl.running] : null;
  return !(s?.id === "mods" && !s.done);
}
/** The timeline is shown in place of the boot stages (a dev.26 runtime's `legacy` and the end states are not). */
export const showsProgress = (tl: StartTimeline | null | undefined): tl is StartTimeline => !!tl && ACTIVE.has(tl.phase);

/** "about 40 s", "about 1 min 25 s", "a few seconds": rounded to 5 s above 20 s, whole seconds from 5 to 20 s. */
export function aboutText(ms: number): string {
  const s = ms / 1000;
  if (s < 5) return t("shell.startProgress.fewSeconds");
  const r = s > 20 ? Math.round(s / 5) * 5 : Math.round(s);
  return r >= 60
    ? (r % 60 ? t("shell.startProgress.aboutMinSec", { min: Math.floor(r / 60), sec: r % 60 }) : t("shell.startProgress.aboutMin", { min: Math.floor(r / 60) }))
    : t("shell.startProgress.aboutSec", { sec: r });
}

/** A step's duration: "0.6 s", "19 s", "1 min 19 s". */
export function tookText(ms: number): string {
  const s = ms / 1000;
  if (s < 10) return t("shell.startProgress.sec", { sec: s.toFixed(1) });
  const r = Math.round(s);
  return r >= 60
    ? (r % 60 ? t("shell.startProgress.minSec", { min: Math.floor(r / 60), sec: r % 60 }) : t("shell.startProgress.min", { min: Math.floor(r / 60) }))
    : t("shell.startProgress.sec", { sec: r });
}

/** "from your last 4 starts", "from your last start" (with other mods: "of this game"). */
export function basisText(tl: StartTimeline): string {
  const n = tl.estimate.starts;
  return tl.estimate.same ? t("shell.startProgress.basisSame", { count: n }) : t("shell.startProgress.basisOther", { count: n });
}

/** "Starting Kiln's services: kiln-vfx, building 85 materials (14 of 31)". */
export function stepLine(s: StartStep, prep?: PreStart | null): string {
  // kiln-eml's own progress is the detail of the early plugins (it is the finer source there)
  if (s.id === "early-plugins" && prep) return prep.message.replace(/\.$/, "");
  const detail = s.detail ? s.detail.replace(/: /g, ", ") : "";
  const counted = s.id !== "cook" && !!s.total;
  const done = Math.min(s.done ?? 0, s.total ?? 0), total = s.total;
  if (detail && counted) return t("shell.startProgress.stepDetailCount", { label: s.label, detail, done, total });
  if (detail) return t("shell.startProgress.stepDetail", { label: s.label, detail });
  if (counted) return t("shell.startProgress.stepCount", { label: s.label, done, total });
  return s.label;
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
export function startWords(tl: StartTimeline, prep?: PreStart | null): StartWords {
  const fraction = tl.phase === "ready" ? 1 : tl.estimate.fraction;
  const e = tl.estimate;
  const running = tl.running != null ? tl.steps[tl.running] : null;
  const pct = Math.round(Math.max(0, Math.min(1, fraction)) * 100);
  const n = Math.min(e.stepsPlanned, e.stepsDone + (running ? 1 : 0));
  const stepOf = t("shell.startProgress.stepOf", { n: Math.max(1, n), total: e.stepsPlanned });
  let line = running ? stepLine(running, prep) : t("shell.gameState.starting");
  // the game's process exists, Kiln has not said anything yet (a 0.2.0-dev.26 runtime never does: its boot.json then
  // ends the bar, as it ended "Waiting for Kiln to boot" before)
  if (tl.phase === "waiting") line = t("shell.gameState.waitingBoot");
  const first = e.leftMs === null;
  let left: string;
  let short: string;
  if (tl.steamSlow) {
    // Steam took the start a minute ago and the game's process does not exist yet: no number, and why
    left = t("shell.startProgress.steamSlow");
    short = t("shell.startProgress.steamSlowShort");
  } else if (first) {
    left = t("shell.startProgress.firstStart", { stepOf });
    short = t("shell.startProgress.stepOfShort", { n: Math.max(1, n), total: e.stepsPlanned });
  } else if (e.slow) {
    const slow = tl.steps.find((s) => s.id === e.slow!.id);
    const step = slow?.label ?? t("shell.startProgress.thisStep");
    const usual = tookText(e.slow.usualMs);
    left = e.leftMs! > 0 ? t("shell.startProgress.slowThen", { step, usual, about: aboutText(e.leftMs!) }) : t("shell.startProgress.slow", { step, usual });
    short = t("shell.startProgress.slowShort");
  } else {
    left = t("shell.startProgress.left", { about: cap(aboutText(e.leftMs!)) });
    short = t("shell.startProgress.leftShort", { about: aboutText(e.leftMs!) });
  }
  const valueText = first ? t("shell.startProgress.valueFirst", { stepOf, line }) : t("shell.startProgress.value", { pct, short, line });
  return { line, left, basis: first ? null : basisText(tl), pct, valueText, short, stepOf, first };
}

const cap = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);

/** What a step row says on the right: what it took, that it runs (and what it usually takes), what it usually takes. */
export function stepTime(s: StartStep): string {
  if (s.state === "done") return s.tookMs != null ? tookText(s.tookMs) : t("shell.startProgress.done");
  if (s.state === "running") return s.usualMs != null ? t("shell.startProgress.usually", { took: tookText(s.usualMs) }) : t("shell.startProgress.now");
  if (s.state === "skipped") return t("shell.startProgress.notNeeded");
  return s.usualMs != null ? t("shell.startProgress.usually", { took: tookText(s.usualMs) }) : "";
}
