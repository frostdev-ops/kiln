// The festive season (shell/season.ts): from the end of Thanksgiving weekend (the Monday after the fourth Thursday of
// November, 00:00 local) to 8 January 00:00 local, never together with the turkey, on by default and off for the rest of
// a season once the player switches it off; ?theme=festive|normal|auto for tests. And the fire of every other day is
// still the very same shader (FRAG), the festive one only adds the snow.
import { afterAll, describe, expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { festiveOffered, festiveSeasonOf, isFestiveSeason, seasonNow, themeOverride } from "../src/shell/season";
import { isThanksgivingWeekend, turkeyTime } from "../src/shell/thanksgiving";
import { FRAG, FRAG_FESTIVE, FRAG_TURKEY } from "../src/shell/Stage";
import de from "../src/i18n/de.json";
import ko from "../src/i18n/ko.json";

const TZ0 = process.env.TZ;
afterAll(() => {
  if (TZ0 === undefined) delete process.env.TZ;
  else process.env.TZ = TZ0;
});
function inZone<T>(tz: string, fn: () => T): T {
  const was = process.env.TZ;
  process.env.TZ = tz;
  try { return fn(); } finally { if (was === undefined) delete process.env.TZ; else process.env.TZ = was; }
}
const local = (y: number, mo: number, d: number, h = 0, mi = 0, s = 0, ms = 0) => new Date(y, mo - 1, d, h, mi, s, ms);
/** What a player's launcher shows (no address override, not automated, never switched off). */
const player = (now: Date, optedOut: number | null = null) => seasonNow(now, "", false, optedOut, null);

describe("the festive season's dates (local time)", () => {
  for (const tz of ["America/New_York", "America/Los_Angeles", "UTC", "Europe/Berlin", "Asia/Tokyo"]) {
    test(`2026 in ${tz}: Monday 30 November 00:00 to 7 January 23:59`, () => inZone(tz, () => {
      expect(player(local(2026, 11, 29, 23, 59, 59, 999))).toBe("turkey");
      expect(isFestiveSeason(local(2026, 11, 29, 23, 59, 59, 999))).toBe(false);
      expect(player(local(2026, 11, 30, 0, 0))).toBe("festive");
      expect(player(local(2026, 12, 25, 12))).toBe("festive");
      expect(player(local(2026, 12, 31, 23, 59))).toBe("festive");
      expect(player(local(2027, 1, 1, 0, 0))).toBe("festive");
      expect(player(local(2027, 1, 7, 23, 59, 59, 999))).toBe("festive");
      expect(player(local(2027, 1, 8, 0, 0))).toBeNull();
      expect(festiveSeasonOf(local(2027, 1, 7, 12))).toBe(2026);
      expect(festiveSeasonOf(local(2026, 12, 7, 12))).toBe(2026);
    }));
  }
  test("2027: Thanksgiving on the 25th, festive from Monday 29 November", () => inZone("America/Chicago", () => {
    expect(player(local(2027, 11, 25, 12))).toBe("turkey");
    expect(player(local(2027, 11, 28, 23, 59))).toBe("turkey");
    expect(player(local(2027, 11, 29, 0, 0))).toBe("festive");
    expect(player(local(2028, 1, 7, 23, 59))).toBe("festive");
    expect(player(local(2028, 1, 8, 0, 0))).toBeNull();
  }));
  test("a turkey weekend that ends in December (2030: 28 November to 1 December): festive from 2 December", () => inZone("Europe/Paris", () => {
    expect(player(local(2030, 12, 1, 23, 59))).toBe("turkey");
    expect(player(local(2030, 12, 2, 0, 0))).toBe("festive");
  }));
  test("the rest of the year: neither", () => inZone("America/Denver", () => {
    for (const [mo, d] of [[1, 8], [1, 20], [3, 1], [7, 4], [10, 31], [11, 1], [11, 25]] as const) expect(player(local(2026, mo, d, 12))).toBeNull();
  }));
  test("one moment, decided by each player's own clock", () => {
    // 04:30 UTC on 30 November 2026: still Sunday (turkey) in New York, Monday (festive) in London
    const t = new Date(Date.UTC(2026, 10, 30, 4, 30));
    expect(inZone("America/New_York", () => player(t))).toBe("turkey");
    expect(inZone("Europe/London", () => player(t))).toBe("festive");
    // 8 January 03:00 UTC: still the 7th in Los Angeles, over in Tokyo
    const e = new Date(Date.UTC(2027, 0, 8, 3, 0));
    expect(inZone("America/Los_Angeles", () => player(e))).toBe("festive");
    expect(inZone("Asia/Tokyo", () => player(e))).toBeNull();
  });
});

describe("the turkey and the festive look never together", () => {
  test("every hour from 1 November to 15 January, ten years, three zones", () => {
    for (const tz of ["America/New_York", "Pacific/Honolulu", "Asia/Tokyo"]) inZone(tz, () => {
      for (let y = 2026; y < 2036; y += 1) {
        let festive = 0;
        for (let t = local(y, 11, 1).getTime(); t < local(y + 1, 1, 15).getTime(); t += 3_600_000) {
          const now = new Date(t);
          const s = player(now);
          // exactly one or none, and the turkey on exactly its weekend
          expect(s === "turkey").toBe(isThanksgivingWeekend(now));
          if (s === "festive") { festive += 1; expect(isThanksgivingWeekend(now)).toBe(false); }
        }
        expect(festive).toBeGreaterThan(24 * 35);
      }
    });
  });
  test("forced: the turkey wins over ?theme=festive; a forced festive look waits for no date", () => {
    const july = local(2026, 7, 4, 12), thursday = local(2026, 11, 26, 12);
    expect(seasonNow(july, "?turkey=1&theme=festive", true, null, null)).toBe("turkey");
    expect(seasonNow(thursday, "?theme=festive", false, null, null)).toBe("turkey");
    expect(seasonNow(july, "?theme=festive", true, null, null)).toBe("festive");
    expect(seasonNow(thursday, "?turkey=0&theme=festive", false, null, null)).toBe("festive");
  });
});

describe("the player's switch", () => {
  const dec = local(2026, 12, 10, 20), jan = local(2027, 1, 5, 9), nextDec = local(2027, 12, 10, 20);
  test("on by default inside the season", () => expect(player(dec)).toBe("festive"));
  test("switched off: off for the rest of that season (into January too), on again the next", () => {
    expect(player(dec, 2026)).toBeNull();
    expect(player(jan, 2026)).toBeNull();
    expect(player(nextDec, 2026)).toBe("festive");
  });
  // 1-7 January belong to the season that began the November before: switched off then, the look comes back at the end
  // of November of that same calendar year, so the Settings text says the next season, never next year
  test("switched off in January, it comes back that same year: the Settings text says the next season, not next year", () => {
    expect(festiveSeasonOf(jan)).toBe(2026);
    expect(player(jan, festiveSeasonOf(jan))).toBeNull();
    expect(player(local(2027, 11, 29, 0, 0), festiveSeasonOf(jan))).toBe("festive");
    const nextYear: Record<string, string> = {
      en: "next year", de: "nächstes Jahr", es: "año que viene", fr: "an prochain", it: "anno prossimo", ja: "来年", ko: "내년",
      pl: "przyszłym roku", "pt-BR": "ano que vem", ru: "следующем году", "zh-CN": "明年",
    };
    for (const [lang, words] of Object.entries(nextYear)) {
      const more = JSON.parse(readFileSync(new URL(`../src/i18n/${lang}.json`, import.meta.url), "utf8")).settings.appearance.festiveMore as string;
      expect(`${lang}: ${more}`).not.toContain(words);
    }
    const en = JSON.parse(readFileSync(new URL("../src/i18n/en.json", import.meta.url), "utf8"));
    expect(en.settings.appearance.festiveMore).toEndWith("it comes back next season.");
  });
  test("the switch moved in this run wins at once; outside the season on does nothing", () => {
    expect(seasonNow(dec, "", false, 2026, true)).toBe("festive");
    expect(seasonNow(dec, "", false, null, false)).toBeNull();
    expect(seasonNow(local(2026, 7, 4), "", false, null, true)).toBeNull();
  });
  test("offered in the season only (or forced), never on the turkey's weekend, not under automation unless asked", () => {
    expect(festiveOffered(dec, "", false)).toBe(true);
    expect(festiveOffered(local(2026, 11, 27, 12), "", false)).toBe(false);
    expect(festiveOffered(local(2026, 7, 4), "", false)).toBe(false);
    expect(festiveOffered(local(2026, 7, 4), "?theme=festive", true)).toBe(true);
    expect(festiveOffered(dec, "", true)).toBe(false);
    expect(festiveOffered(dec, "?theme=auto", true)).toBe(true);
  });
});

describe("the address and automation", () => {
  test("?theme", () => {
    expect(themeOverride("")).toBeNull();
    expect(themeOverride("?mock=1&theme=festive")).toBe("festive");
    expect(themeOverride("?theme=NORMAL")).toBe("normal");
    expect(themeOverride("?theme=default")).toBe("normal");
    expect(themeOverride("?theme=auto")).toBe("auto");
    expect(themeOverride("?theme=disco")).toBeNull();
  });
  test("under automation off unless forced; ?theme=auto lets the date decide, the turkey's too", () => {
    const dec = local(2026, 12, 10), thursday = local(2026, 11, 26, 12);
    expect(seasonNow(dec, "", true, null, null)).toBeNull();
    expect(seasonNow(dec, "?theme=normal", false, null, null)).toBeNull();
    expect(seasonNow(dec, "?theme=auto", true, null, null)).toBe("festive");
    expect(seasonNow(thursday, "?theme=auto", true, null, null)).toBe("turkey");
    expect(turkeyTime(thursday, "", true)).toBe(false);
  });
});

describe("the season's words", () => {
  // the season runs into January: Korean says "the year's end and the New Year" (연말연시), not just the year's end (연말);
  // the snow outside the arch falls (내리고), it does not hang like the garlands (걸립니다); German calls the holidays
  // Festtage (Festzeit is a fair's or a festival's opening hours)
  test("Korean and German name the whole season, naturally", () => {
    expect(ko.shell.appShell.festive).toBe("즐거운 연말연시 보내세요");
    expect(ko.settings.appearance.festive).toBe("연말연시");
    expect(ko.settings.appearance.festiveMore).toContain("아치 밖에는 눈이 내리고, 페이지에는 화환이 걸립니다");
    expect(de.settings.appearance.festive).toBe("Festtage");
    expect(de.shell.appShell.festive).toBe("Frohe Festtage");
  });
});

describe("the shaders", () => {
  test("every other day's fire is the very same text as before the festive season, the festive one the reviewed text (sha-256)", () => {
    const sha = (s: string) => new Bun.CryptoHasher("sha256").update(s).digest("hex");
    expect(sha(FRAG)).toBe("feea587170a00be17aede5a03812b18c14b38bf0f7be09d43dd93cffc6a512c3");
    expect(sha(FRAG_TURKEY)).toBe("2fb021eeac902550b4ee46fb5302605ef37d2ebfddc6d3736717207e2e84ce7e");
    // and the festive fire is the text its review saw (0.2.0-dev.31.20): a change to it is a change to the season's look
    expect(sha(FRAG_FESTIVE)).toBe("25ad8c48e9af2f45435da92383c78f7dad05857388760f344107555b29db294c");
  });
  test("the festive fire is every day's plus the snow: no turkey in it, and no snow anywhere else", () => {
    for (const word of ["snow(", "melt"]) {
      expect(FRAG_FESTIVE.includes(word)).toBe(true);
      expect(FRAG.includes(word)).toBe(false);
      expect(FRAG_TURKEY.includes(word)).toBe(false);
    }
    for (const word of ["uV", "roast", "drum(", "smoke("]) expect(FRAG_FESTIVE.includes(word)).toBe(false);
    const festive = FRAG_FESTIVE.split("\n");
    for (const line of FRAG.split("\n")) expect(festive).toContain(line);
  });
});
