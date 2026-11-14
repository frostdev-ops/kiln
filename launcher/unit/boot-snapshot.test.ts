// bun test unit/ (from launcher/): the one line Play shows after the boot game-data step (shell/bootSnapshot.ts).
import { expect, test } from "bun:test";
import { bootSnapshotLine } from "../src/shell/bootSnapshot";

test("a snapshot in place says nothing", () => {
  expect(bootSnapshotLine(undefined)).toBeNull();
  expect(bootSnapshotLine({ status: "reused", emptyKinds: [] })).toBeNull();
  expect(bootSnapshotLine({ status: "made", emptyKinds: [], bytes: 208_000_000 })).toBeNull();
});

test("no snapshot: the game starts without it, and why", () => {
  for (const status of ["refused", "failed", "unavailable"] as const) {
    const line = bootSnapshotLine({ status, emptyKinds: [], reason: "no python3 was found" });
    expect(line).toContain("was not made (no python3 was found)");
    expect(line).toContain("The game starts without it");
  }
  expect(bootSnapshotLine({ status: "failed", emptyKinds: [] })).toContain("(no reason given)");
});

test("a snapshot that lacks a kind names it", () => {
  expect(bootSnapshotLine({ status: "reused", emptyKinds: ["skills", "materials"] })).toBe(
    "The game data for OnBoot mods has no skills, materials (the game's archive could not give them): an OnBoot read of them finds nothing.");
});
