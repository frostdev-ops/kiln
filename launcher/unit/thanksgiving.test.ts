// Thanksgiving weekend (shell/thanksgiving.ts): the turkey shows from the fourth Thursday of November through the Sunday
// after, in the player's local time, every year; ?turkey=1 / ?turkey=0 force it for tests. And the fire of every other
// day is the same shader as before (shell/Stage.tsx FRAG: no turkey in it).
import { afterAll, describe, expect, test } from "bun:test";
import { isThanksgivingWeekend, thanksgivingDay, turkeyOverride, turkeyTime } from "../src/shell/thanksgiving";
import { FRAG, FRAG_TURKEY } from "../src/shell/Stage";

const TZ0 = process.env.TZ;
afterAll(() => {
  if (TZ0 === undefined) delete process.env.TZ;
  else process.env.TZ = TZ0;
});
/** Runs `fn` with the process in time zone `tz` (Bun follows process.env.TZ as it changes). */
function inZone<T>(tz: string, fn: () => T): T {
  const was = process.env.TZ;
  process.env.TZ = tz;
  try { return fn(); } finally { if (was === undefined) delete process.env.TZ; else process.env.TZ = was; }
}
const local = (y: number, mo: number, d: number, h = 0, mi = 0, s = 0, ms = 0) => new Date(y, mo - 1, d, h, mi, s, ms);

describe("thanksgivingDay", () => {
  // US Thanksgiving, as published
  const known: [number, number][] = [[2018, 22], [2019, 28], [2020, 26], [2021, 25], [2022, 24], [2023, 23], [2024, 28], [2025, 27], [2026, 26], [2027, 25], [2028, 23], [2029, 22], [2030, 28], [2035, 22], [2040, 22]];
  for (const [y, d] of known) test(`${y}: 22-28 November, ${d}`, () => expect(thanksgivingDay(y)).toBe(d));
  test("always a Thursday, the fourth of the month, for 200 years", () => {
    for (let y = 1950; y < 2150; y += 1) {
      const d = thanksgivingDay(y);
      expect(new Date(y, 10, d).getDay()).toBe(4);
      expect(d).toBeGreaterThanOrEqual(22);
      expect(d).toBeLessThanOrEqual(28);
    }
  });
});

describe("isThanksgivingWeekend (local time)", () => {
  for (const tz of ["America/New_York", "America/Los_Angeles", "UTC", "Europe/Berlin", "Asia/Tokyo"]) {
    test(`2026 in ${tz}: Thursday 26 through Sunday 29 November`, () => inZone(tz, () => {
      expect(isThanksgivingWeekend(local(2026, 11, 25, 23, 59, 59, 999))).toBe(false);
      expect(isThanksgivingWeekend(local(2026, 11, 26, 0, 0))).toBe(true);
      expect(isThanksgivingWeekend(local(2026, 11, 27, 12, 0))).toBe(true);
      expect(isThanksgivingWeekend(local(2026, 11, 29, 23, 59, 59, 999))).toBe(true);
      expect(isThanksgivingWeekend(local(2026, 11, 30, 0, 0))).toBe(false);
    }));
  }
  test("a weekend that ends in December (2019: 28 November to 1 December)", () => inZone("America/Chicago", () => {
    expect(isThanksgivingWeekend(local(2019, 11, 27, 23, 59))).toBe(false);
    expect(isThanksgivingWeekend(local(2019, 11, 28, 0, 0))).toBe(true);
    expect(isThanksgivingWeekend(local(2019, 11, 30, 18, 0))).toBe(true);
    expect(isThanksgivingWeekend(local(2019, 12, 1, 23, 59))).toBe(true);
    expect(isThanksgivingWeekend(local(2019, 12, 2, 0, 0))).toBe(false);
    expect(isThanksgivingWeekend(local(2030, 12, 1, 9, 0))).toBe(true);
    expect(isThanksgivingWeekend(local(2030, 12, 2, 9, 0))).toBe(false);
  }));
  test("other years, other days", () => inZone("America/Denver", () => {
    for (const [y, d] of [[2024, 28], [2025, 27], [2027, 25], [2028, 23], [2029, 22]] as const) {
      expect(isThanksgivingWeekend(local(y, 11, d - 1, 12))).toBe(false);
      for (let k = 0; k < 4; k += 1) expect(isThanksgivingWeekend(local(y, 11, d + k, 12))).toBe(true);
      expect(isThanksgivingWeekend(local(y, 11, d + 4, 12))).toBe(false);
    }
    // the rest of the year: never
    expect(isThanksgivingWeekend(local(2026, 7, 4, 12))).toBe(false);
    expect(isThanksgivingWeekend(local(2026, 12, 25, 12))).toBe(false);
    expect(isThanksgivingWeekend(local(2026, 11, 19, 12))).toBe(false); // the third Thursday
    expect(isThanksgivingWeekend(local(2026, 10, 12, 12))).toBe(false); // Canada's Thanksgiving: not this one
  }));
  test("one moment, decided by each player's own clock", () => {
    // 04:30 UTC on 26 November 2026: still Wednesday evening in the Americas, Thursday already in Europe and Asia
    const start = new Date(Date.UTC(2026, 10, 26, 4, 30));
    expect(inZone("America/New_York", () => isThanksgivingWeekend(start))).toBe(false);
    expect(inZone("America/Los_Angeles", () => isThanksgivingWeekend(start))).toBe(false);
    expect(inZone("UTC", () => isThanksgivingWeekend(start))).toBe(true);
    expect(inZone("Asia/Tokyo", () => isThanksgivingWeekend(start))).toBe(true);
    // 04:30 UTC on 30 November: still Sunday in New York and Honolulu, Monday in London and Kiritimati
    const end = new Date(Date.UTC(2026, 10, 30, 4, 30));
    expect(inZone("America/New_York", () => isThanksgivingWeekend(end))).toBe(true);
    expect(inZone("Pacific/Honolulu", () => isThanksgivingWeekend(end))).toBe(true);
    expect(inZone("Europe/London", () => isThanksgivingWeekend(end))).toBe(false);
    expect(inZone("Pacific/Kiritimati", () => isThanksgivingWeekend(end))).toBe(false);
  });
});

describe("the test override", () => {
  test("?turkey=1 / ?turkey=0 win over the date; anything else is the date (off under automation)", () => {
    expect(turkeyOverride("")).toBeNull();
    expect(turkeyOverride("?mock=1")).toBeNull();
    expect(turkeyOverride("?turkey=maybe")).toBeNull();
    for (const v of ["1", "on", "true", "yes", "ON"]) expect(turkeyOverride(`?turkey=${v}`)).toBe(true);
    for (const v of ["0", "off", "false", "no"]) expect(turkeyOverride(`?mock=1&turkey=${v}`)).toBe(false);
    const july = local(2026, 7, 4, 12), thursday = local(2026, 11, 26, 12);
    expect(turkeyTime(july, "", false)).toBe(false);
    expect(turkeyTime(thursday, "", false)).toBe(true);
    expect(turkeyTime(july, "?turkey=1", false)).toBe(true);
    expect(turkeyTime(thursday, "?turkey=0", false)).toBe(false);
    // a browser under automation (Playwright) sees every day's launcher unless a spec asks for the turkey
    expect(turkeyTime(thursday, "", true)).toBe(false);
    expect(turkeyTime(july, "?turkey=1", true)).toBe(true);
  });
});

describe("the shaders", () => {
  test("every other day's fire has no turkey in it; the Thanksgiving one adds it", () => {
    for (const word of ["uV", "roast", "rigShadow", "drum(", "drips(", "smoke(", "sizzle("]) {
      expect(FRAG.includes(word)).toBe(false);
      expect(FRAG_TURKEY.includes(word)).toBe(true);
    }
    // the turkey's shader is every-day's plus its own lines: each line of FRAG outside the chamber is still in it
    for (const line of FRAG.split("\n").filter((l) => !/diff=|fl\+=|ramp\(fl\)|col=chamber/.test(l))) expect(FRAG_TURKEY.split("\n")).toContain(line);
  });
});
