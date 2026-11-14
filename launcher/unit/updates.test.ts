// api/updates.ts: the words of a status (a stale or unknown one never reads as up to date), which pages may be opened,
// and the daily check's timing.
import { describe, expect, test } from "bun:test";
import { dailyDelay, hasUpdate, pageOf, reasonWords, updateWords, type ModUpdateStatus, type UnknownReason, type UpdateState } from "../src/api/updates";

const status = (patch: Partial<ModUpdateStatus>): ModUpdateStatus => ({
  libraryRef: { kind: "kiln", id: "m", version: "1.0.0", hash: "kdh1:0" }, name: "M",
  source: { provider: "github", destination: "example-org/m", origin: "manifest", authenticated: false },
  installedVersion: "1.0.0", candidateVersion: null, candidateIdentity: null, state: "unknown", compatibility: "unknown", coverage: "complete",
  checkedAt: null, stale: false, reason: "not_checked", pageUrl: null, ...patch,
});

describe("update words", () => {
  test("only a current status says up to date", () => {
    const states: UpdateState[] = ["available", "new-upload", "current", "unknown", "unsupported", "disabled"];
    for (const state of states) for (const stale of [false, true]) {
      if (state === "current" && stale) continue; // the backend never sends it
      const w = updateWords(status({ state, stale, reason: state === "unknown" || stale ? "network" : null, candidateVersion: "2.0.0" }));
      expect(/up to date/i.test(w.word + w.detail), `${state} ${stale}`).toBe(state === "current");
      if (stale && !["unsupported", "disabled"].includes(state)) expect(w.word + w.detail).toMatch(/stale|Not confirmed lately/);
    }
  });
  test("every reason has plain words", () => {
    const reasons: UnknownReason[] = ["incomplete_list", "invalid_metadata", "missing_asset", "ambiguous_asset", "ambiguous_release", "non_semver", "same_version_unbound",
      "network", "source_conflict", "rate_limited", "not_found", "not_checked", "release_removed"];
    for (const r of reasons) {
      expect(reasonWords(r).length).toBeGreaterThan(8);
      expect(reasonWords(r)).not.toContain("_");
      expect(updateWords(status({ reason: r })).detail).toContain(reasonWords(r));
    }
    expect(reasonWords("from_a_newer_kiln" as UnknownReason)).toBe("unknown reason");
  });
  test("an update names both versions; an incompatible one says so", () => {
    const w = updateWords(status({ state: "available", reason: null, candidateVersion: "1.1.0", compatibility: "incompatible" }));
    expect(w.word).toBe("Update 1.1.0");
    expect(w.detail).toContain("Version 1.1.0 is published; you have 1.0.0.");
    expect(w.detail).toContain("does not load on this Kiln");
    expect(hasUpdate(status({ state: "new-upload" })) && hasUpdate(status({ state: "available" })) && !hasUpdate(status({ state: "current" }))).toBe(true);
  });
});

test("only pages on the two provider sites open", () => {
  const ok = ["https://github.com/example-org/m/releases/tag/v1.1.0", "https://github.com/example-org/m/releases/tag/v1.3.0%2Bbuild.2", "https://www.nexusmods.com/enshrouded/mods/310?tab=files"];
  for (const u of ok) expect(pageOf(status({ pageUrl: u }))).toBe(u);
  const bad = [null, "", "javascript:alert(1)", "http://github.com/a/b", "https://github.com.evil.example/a", "https://evil.example/?https://github.com/", "https://nexusmods.com/x",
    "https://github.com/a/b <script>", "https://github.com/a/b\"", "file:///etc/passwd", " https://github.com/a/b"];
  for (const u of bad) expect(pageOf(status({ pageUrl: u })), String(u)).toBeNull();
});

test("the daily check waits 2 to 15 minutes at first, then 24 to 25 hours, and 5 minutes while busy", () => {
  const min = 60_000;
  expect([dailyDelay("first", 0), dailyDelay("first", 1)]).toEqual([2 * min, 15 * min]);
  expect([dailyDelay("next", 0), dailyDelay("next", 1)]).toEqual([24 * 60 * min, 25 * 60 * min]);
  expect(dailyDelay("next", 0.5)).toBe(24.5 * 60 * min);
  expect(dailyDelay("busy", 0.9)).toBe(5 * min);
  expect(dailyDelay("first", -3)).toBe(2 * min);
  expect(dailyDelay("first", 7)).toBe(15 * min);
});
