// Thanksgiving weekend: the Play page's fire roasts a turkey (shell/Stage.tsx FRAG_TURKEY), the pilot light shows a
// drumstick and a "Happy Thanksgiving" line sits over the mouth. Only over US Thanksgiving weekend in the player's own
// local time: from 00:00 on the fourth Thursday of November to the end of the Sunday after it, every year. Every other
// day the launcher draws exactly the fire it always does.
// Tests (the browser mock, Playwright) force it with ?turkey=1 (on) or ?turkey=0 (off). Without that the date decides,
// except in a browser driven by automation (navigator.webdriver: Playwright), where it stays off: the suites see the same
// launcher whatever day they run, and only the specs that ask for the turkey pay for it.

/** The day of November that is US Thanksgiving in `year`: the fourth Thursday (22 to 28). */
export function thanksgivingDay(year: number): number {
  const first = new Date(year, 10, 1).getDay(); // 0 Sunday .. 4 Thursday
  return 1 + ((4 - first + 7) % 7) + 21;
}

/** Whether `now` (local time) falls in Thanksgiving weekend: Thursday 00:00 up to (not including) Monday 00:00. The
 *  Sunday can be 1 December (when Thanksgiving is the 28th). */
export function isThanksgivingWeekend(now: Date = new Date()): boolean {
  const y = now.getFullYear();
  const day = thanksgivingDay(y);
  const from = new Date(y, 10, day);
  const to = new Date(y, 10, day + 4); // the Monday after, 00:00 local (Date rolls 29+ over into December)
  return now >= from && now < to;
}

/** The test override in the address (?turkey=1|on|true forces it on, ?turkey=0|off|false off), else null. */
export function turkeyOverride(search: string): boolean | null {
  const v = new URLSearchParams(search).get("turkey");
  if (v === null) return null;
  if (/^(1|on|true|yes)$/i.test(v)) return true;
  if (/^(0|off|false|no)$/i.test(v)) return false;
  return null;
}

/** Whether the turkey shows now: the override, else off under automation, else the date. */
export function turkeyTime(
  now: Date = new Date(),
  search: string = typeof location !== "undefined" ? location.search : "",
  automated: boolean = typeof navigator !== "undefined" && navigator.webdriver === true,
): boolean {
  return turkeyOverride(search) ?? (!automated && isThanksgivingWeekend(now));
}
