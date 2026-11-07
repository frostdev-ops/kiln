// bun test unit/ (from launcher/): the game-state view a start without Kiln must not leave on "Starting" forever.
import { expect, test } from "bun:test";
import type { GameStatus } from "../src/api/commands";
import { gameState, KILN_LOAD_GRACE_MS, playOnceMissed } from "../src/shell/gameState";

const t0 = Date.parse("2026-11-07T07:40:00Z");
function status(over: Partial<GameStatus>): GameStatus {
  return {
    id: "g", running: true, pid: 4242, started: new Date(t0).toISOString(), steamUpdating: false,
    runtime: { installed: true } as GameStatus["runtime"], activeProfile: null, playOncePending: false, boot: null, launchOptions: null,
    ...over,
  };
}

test("a pending play-once flag is 'starting' only within the grace", () => {
  const s = status({ playOncePending: true });
  expect(gameState(s, null, false, t0 + 5_000).phase).toBe("starting");
  expect(playOnceMissed(s, t0 + 5_000)).toBe(false);
  const late = gameState(s, null, false, t0 + KILN_LOAD_GRACE_MS + 1);
  expect(late.phase).toBe("running");
  expect(late.label).toBe("No Kiln");
  expect(late.tone).toBe("warn");
  expect(playOnceMissed(s, t0 + 10 * 60_000)).toBe(true);
});

test("no verdict without a process start time, or when stopped, or without a flag", () => {
  const far = t0 + 10 * 60_000;
  expect(playOnceMissed(status({ playOncePending: true, started: null }), far)).toBe(false);
  expect(gameState(status({ playOncePending: true, started: null }), null, false, far).phase).toBe("starting");
  expect(playOnceMissed(status({ playOncePending: true, running: false }), far)).toBe(false);
  expect(playOnceMissed(status({ playOncePending: false }), far)).toBe(false);
  expect(gameState(status({}), null, false, far).label).toBe("Running");
});
