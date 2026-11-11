// A profile's drop-in folders: mods placed in the game folder by hand. Mirrors kiln_manage::profile (dropin_kinds,
// DROPIN_KINDS, dropins_file). Every plugin reads profile.json `dropins` the same way since dev.22: a folder is read
// when its kind's switch is on. A profile without `dropinsEngines` (made before dev.22) keeps what it loaded then;
// saving from here always writes the full list with `dropinsEngines`, so nothing changes for it.
import type { ModKind, Profile } from "../../api/commands";
import { CORE_KINDS, MOD_KINDS } from "../../api/kinds";

/** The kinds with a drop-in switch (their plugin reads the profile's `dropins`), in ModKind::ALL order. */
export const DROPIN_KINDS: ModKind[] = ["kiln", "native", "plugin", "emf", "eml", "shroudforge", "greeneye", "flame", "shroudtopia", "enshwick", "shroudlink"];

/** Engine kinds whose plugin, before dev.22, read its folder whenever the profile listed no mod of that engine. */
const READ_WHEN_UNLISTED: ModKind[] = ["greeneye", "flame", "shroudtopia", "shroudlink", "ember"];
// XHL-Core is external: Kiln's drop-in switches cannot control its manual companions.
const PROFILE_DROPIN_KINDS = MOD_KINDS.filter((k) => k !== "xhl");

/** What the switches show: the kinds whose folder this profile switches on. A profile from before dev.22 shows what
 *  it loaded then: every kind with all four Kiln kinds on; else its list, Enshwick's folder, and the folders of
 *  READ_WHEN_UNLISTED while the profile has no mod of that engine. */
export function dropinKinds(p: Pick<Profile, "dropins" | "dropinsEngines" | "mods">): ModKind[] {
  if (p.dropinsEngines) return PROFILE_DROPIN_KINDS.filter((k) => p.dropins.includes(k));
  if (CORE_KINDS.every((k) => p.dropins.includes(k))) return [...PROFILE_DROPIN_KINDS];
  return PROFILE_DROPIN_KINDS.filter((k) => p.dropins.includes(k) || k === "enshwick"
    || (READ_WHEN_UNLISTED.includes(k) && (p.mods[k] ?? []).length === 0));
}

/** Writes `kinds` into the profile (as the dialog and "Load the game-folder mods" save it). */
export function setDropins(p: Profile, kinds: ModKind[]): void {
  p.dropins = PROFILE_DROPIN_KINDS.filter((k) => kinds.includes(k));
  p.dropinsEngines = true;
}

/** Switches one kind's folder on or off, keeping what the others show. */
export function withDropin(p: Profile, kind: ModKind, on: boolean): void {
  const cur = dropinKinds(p);
  setDropins(p, on ? [...cur, kind] : cur.filter((k) => k !== kind));
}

/** profile.json `dropins` true: every switch on. */
export const allOn = (kinds: ModKind[]) => DROPIN_KINDS.every((k) => kinds.includes(k));
