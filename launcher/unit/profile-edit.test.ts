// The profile save queue (src/pages/profiles/useProfileEdit.ts) and a profile deleted and made again under the same id
// (Nexus 2026-11-13: "profile my-mods changed meanwhile (generation 3 on disk, 29 here); reload it").
import { expect, test } from "bun:test";
import type { Profile } from "../src/api/commands";
import { emptyMods } from "../src/api/kinds";
import { adopts } from "../src/pages/profiles/useProfileEdit";

const profile = (created: string, generation: number, mods: string[] = []): Profile => ({
  format: 1, id: "my-mods", name: "My mods", created, updated: created, generation, notes: "",
  mods: { ...emptyMods(), eml: mods.map((id) => ({ id, version: "1.0.0", hash: `kdh1:${id}`, enabled: true })) }, dropins: [], dataDir: "shared", server: null,
});

test("an older copy of the same profile is not taken over the one the queue holds", () => {
  const held = profile("2026-11-01T10:00:00Z", 29);
  expect(adopts(held, profile("2026-11-01T10:00:00Z", 28))).toBe(false);
  expect(adopts(held, profile("2026-11-01T10:00:00Z", 29))).toBe(true);
  expect(adopts(held, profile("2026-11-01T10:00:00Z", 30))).toBe(true);
  expect(adopts(null, profile("2026-11-01T10:00:00Z", 1))).toBe(true);
});

test("a profile deleted and made again under the same id is taken, whatever its generation", () => {
  // the player's steps: My mods edited 28 times (generation 29), deleted, made again, a mod added (generation 3)
  const old = profile("2026-11-01T10:00:00Z", 29, ["old-mod"]);
  const again = profile("2026-11-13T07:20:00Z", 3, ["renamed-mod"]);
  expect(adopts(old, again)).toBe(true);
});
