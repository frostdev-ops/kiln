// The launcher's seasons, one at a time: Thanksgiving weekend's turkey (thanksgiving.ts), then the festive season. The
// festive look (shell/Festive.tsx, Stage.tsx FRAG_FESTIVE: a mantel of lamps, candles and lanterns from many winter
// traditions over the fire, snow outside the arch, garlands on the pages) starts as the turkey's weekend ends, 00:00 on
// the Monday after the fourth Thursday of November, and lasts until 00:00 on 8 January (Hanukkah, Christmas, Kwanzaa,
// the New Year, Orthodox Christmas on 7 January), in the player's own local time. The turkey wins on its own dates, so
// the two never show together. Every other day the launcher draws exactly the fire it always does.
// The player can switch the festive look off (Settings > General > Look): kept in this machine's local storage for the
// season it was switched off in (the next season starts on again).
// Tests force it with ?theme=festive (on) or ?theme=normal (off); under automation (navigator.webdriver: Playwright) it
// stays off otherwise. ?theme=auto lets the date decide even under automation (the turkey too), as for a player.
import { useEffect, useMemo, useState, useSyncExternalStore } from "react";
import { thanksgivingDay, turkeyOverride, turkeyTime } from "./thanksgiving";

export type Season = "turkey" | "festive" | null;

/** The festive season that `now` falls in, named by the year it started (1-7 January belong to the year before), or
 *  null outside it: from the Monday after Thanksgiving weekend, 00:00, up to (not including) 8 January, 00:00. */
export function festiveSeasonOf(now: Date): number | null {
  const y = now.getFullYear();
  if (now.getMonth() === 0) return now < new Date(y, 0, 8) ? y - 1 : null;
  const from = new Date(y, 10, thanksgivingDay(y) + 4); // the turkey's end (Date rolls 29+ over into December)
  return now >= from ? y : null;
}

export function isFestiveSeason(now: Date = new Date()): boolean {
  return festiveSeasonOf(now) !== null;
}

/** The address's ?theme: "festive" forces the look on, "normal" (or "default") off, "auto" lets the date decide even
 *  under automation; anything else (or none) is null. */
export function themeOverride(search: string): "festive" | "normal" | "auto" | null {
  const v = new URLSearchParams(search).get("theme")?.toLowerCase();
  return v === "festive" ? "festive" : v === "normal" || v === "default" ? "normal" : v === "auto" ? "auto" : null;
}

/** What shows now. `optedOut`: the season the player switched the festive look off in (null: never). `choice`: the
 *  Settings switch moved in this run (beats the address). The turkey first, on its own dates or forced. */
export function seasonNow(
  now: Date = new Date(),
  search: string = typeof location !== "undefined" ? location.search : "",
  automated: boolean = typeof navigator !== "undefined" && navigator.webdriver === true,
  optedOut: number | null = readOptOut(),
  choice: boolean | null = null,
): Season {
  const force = themeOverride(search);
  const auto = force === "auto";
  if (turkeyTime(now, search, automated && !auto)) return "turkey";
  // a forced turkey-off still leaves its weekend to the turkey (no festive look before Monday)
  const season = festiveSeasonOf(now);
  if (choice !== null) return choice && (season !== null || force === "festive") ? "festive" : null;
  if (force === "festive") return "festive";
  if (force === "normal") return null;
  if (automated && !auto) return null;
  return season !== null && optedOut !== season ? "festive" : null;
}

/** Whether the Settings switch is offered: inside the season (or with the look forced on for a test), never while the
 *  turkey shows. */
export function festiveOffered(
  now: Date = new Date(),
  search: string = typeof location !== "undefined" ? location.search : "",
  automated: boolean = typeof navigator !== "undefined" && navigator.webdriver === true,
): boolean {
  const force = themeOverride(search);
  if (force === "festive") return turkeyOverride(search) !== true;
  if (automated && force !== "auto") return false;
  return isFestiveSeason(now) && !turkeyTime(now, search, false);
}

// ------------------------------------------------------------------ the player's switch (local storage)
const OPT_OUT = "kiln.festive.off";

function readOptOut(): number | null {
  try {
    const v = typeof localStorage !== "undefined" ? localStorage.getItem(OPT_OUT) : null;
    return v && /^\d{4}$/.test(v) ? Number(v) : null;
  } catch {
    return null;
  }
}

let choice: boolean | null = null;
const listeners = new Set<() => void>();
let version = 0;

/** The Settings switch: off keeps the opt-out for this season (on again forgets it); it applies at once. */
export function setFestive(on: boolean, now: Date = new Date()) {
  const season = festiveSeasonOf(now);
  try {
    if (on) localStorage.removeItem(OPT_OUT);
    else if (season !== null) localStorage.setItem(OPT_OUT, String(season));
  } catch {
    /* no storage: this run only */
  }
  choice = on;
  version += 1;
  for (const l of listeners) l();
}

const subscribe = (changed: () => void) => {
  listeners.add(changed);
  return () => { listeners.delete(changed); };
};

/** What shows, looked at again every 10 minutes (a launcher left open turns the turkey on as its weekend starts, the
 *  festive look on as it ends and off on 8 January) and at once when the Settings switch moves. */
export function useSeason(): Season {
  const v = useSyncExternalStore(subscribe, () => version, () => version);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    const timer = setInterval(() => setTick((n) => n + 1), 600_000);
    return () => clearInterval(timer);
  }, []);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  return useMemo(() => seasonNow(undefined, undefined, undefined, undefined, choice), [v, tick]);
}

/** The Settings row: whether it is offered (re-read each render) and whether the look is on. */
export function useFestiveSwitch(): { offered: boolean; on: boolean } {
  useSyncExternalStore(subscribe, () => version, () => version);
  return { offered: festiveOffered(), on: seasonNow(undefined, undefined, undefined, undefined, choice) === "festive" };
}
