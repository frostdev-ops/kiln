import { expect, test } from "bun:test";
import type { BootJson, BootState, GameStatus, LaunchMode } from "../src/api/commands";
import { playState } from "../src/shell/gameState";

const t0 = Date.parse("2026-11-07T12:00:00Z");
const absent: BootState = { boot: null, mods: null, native: null, stale: true, log: null };
const status = (over: Partial<GameStatus> = {}): GameStatus => ({
  id: "g", running: true, pid: 42, started: new Date(t0).toISOString(), steamUpdating: false,
  runtime: { installed: true } as GameStatus["runtime"], activeProfile: null, playOncePending: false, boot: null, launchOptions: null, ...over,
});
const memo = (mode: LaunchMode = "profile") => ({ at: t0, mode, seen: t0 });

test("the flag grace is 30 seconds and the no-boot grace is 60 seconds", () => {
  const flagged = status({ playOncePending: true });
  expect(playState(flagged, absent, memo("no-mods"), t0 + 29_999).starting).toBe(true);
  expect(playState(flagged, absent, memo("no-mods"), t0 + 30_000).kilnMissing).toBe(true);
  expect(playState(status(), absent, memo(), t0 + 59_999).starting).toBe(true);
  const late = playState(status(), absent, memo(), t0 + 60_000);
  expect(late.kilnMissing).toBe(true);
  expect(late.view.label).toBe("No Kiln");
  expect(late.starting).toBe(false);
});

test("missing, invalid and future process timestamps use observation time for both graces", () => {
  for (const started of [null, undefined, "broken", new Date(t0 + 86_400_000).toISOString()]) {
    const flagged = status({ started, playOncePending: true });
    const m = { ...memo("no-mods"), seen: t0 + 10_000 };
    expect(playState(flagged, absent, m, t0 + 39_999).kilnMissing).toBe(false);
    expect(playState(flagged, absent, m, t0 + 40_000).kilnMissing).toBe(true);
    expect(playState(status({ started }), absent, m, t0 + 69_999).kilnMissing).toBe(false);
    expect(playState(status({ started }), absent, m, t0 + 70_000).kilnMissing).toBe(true);
  }
});

test("a long wait for Steam does not count as time the process has run", () => {
  const m = { ...memo(), seen: t0 + 180_000 };
  const s = status({ started: null });
  expect(playState(s, absent, m, t0 + 190_000).starting).toBe(true);
  expect(playState(s, absent, m, t0 + 190_000).kilnMissing).toBe(false);
  expect(playState(s, absent, { ...m, mode: "vanilla" }, t0 + 190_000).view.phase).toBe("vanilla");
});

test("vanilla is never missing or waiting for Kiln, even with an unconsumed flag", () => {
  for (const seconds of [0, 29, 30, 60, 600]) {
    const v = playState(status({ playOncePending: true }), absent, memo("vanilla"), t0 + seconds * 1000);
    expect(v.kilnMissing).toBe(false);
    expect(v.starting).toBe(false);
    expect(v.view.phase).toBe("vanilla");
  }
});

test("a fresh boot overrides a leftover flag and old launcher mode", () => {
  const boot = { pid: 42, kiln: "0.2.0", profile: { name: "My mods" }, playOnce: { mode: "none", nonce: null } } as BootJson;
  const fresh = { ...absent, boot, stale: false };
  const s = status({ playOncePending: true });
  expect(playState(s, fresh, memo(), t0 + 600_000).kilnMissing).toBe(false);
  expect(playState(s, fresh, memo("vanilla"), t0 + 600_000).view.phase).toBe("running");
  expect(playState(s, { ...fresh, stale: true }, memo(), t0 + 600_000).kilnMissing).toBe(true);
});

test("stopped, updating, uninstalled and unobserved external starts are not missing", () => {
  for (const over of [{ running: false }, { steamUpdating: true }, { runtime: { installed: false } as GameStatus["runtime"] }]) {
    expect(playState(status({ playOncePending: true, ...over }), absent, memo(), t0 + 600_000).kilnMissing).toBe(false);
  }
  expect(playState(status(), absent, {}, t0 + 600_000).kilnMissing).toBe(false);
  expect(playState(undefined, absent, memo(), t0 + 600_000).kilnMissing).toBe(false);
});
