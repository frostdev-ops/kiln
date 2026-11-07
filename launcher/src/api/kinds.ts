// Mod kinds for the UI: the lists, the names players see and the badge tones. Mirrors kiln_manage::manifest::ModKind
// (ALL, CORE, is_engine, is_reserved, label) and engine_mods (IMPORTED, plugin_of). docs/kiln/engines.md.
import type { ModKind, ProfileMod } from "./commands";

type Tone = "ok" | "warn" | "err" | "info" | "idle" | "ember";

/** Every kind, in ModKind::ALL order. */
export const MOD_KINDS: ModKind[] = ["kiln", "native", "plugin", "emf", "eml", "shroudforge", "set", "greeneye", "flame", "shroudtopia", "enshwick", "shroudlink", "ember"];
/** Kiln's own four. */
export const CORE_KINDS: ModKind[] = ["kiln", "native", "plugin", "emf"];
/** Other engines' mods that the Library imports and Kiln's engine plugins run (engine_mods::IMPORTED). */
export const IMPORTED_KINDS: ModKind[] = ["eml", "shroudforge", "set", "greeneye", "flame", "shroudtopia"];
/** Listed in the formats, no importer yet: shown when present, no special UI. */
export const RESERVED_KINDS: ModKind[] = ["enshwick", "shroudlink", "ember"];
/** Every kind that is not one of Kiln's own four. */
export const ENGINE_KINDS: ModKind[] = [...IMPORTED_KINDS, ...RESERVED_KINDS];

export const isEngineKind = (k: ModKind) => !CORE_KINDS.includes(k);
export const isReservedKind = (k: ModKind) => RESERVED_KINDS.includes(k);

/** ModKind::label: the engine's full name. */
export const KIND_NAME: Record<ModKind, string> = {
  kiln: "Kiln", native: "Native", plugin: "Plugin", emf: "EMF", eml: "EML (Enshrouded Mod Loader)", shroudforge: "ShroudForge",
  set: "Shrouded Engine Tweaks", greeneye: "Greeneye Loader", flame: "Flame", shroudtopia: "Shroudtopia", enshwick: "Enshwick",
  shroudlink: "shroudlink", ember: "Ember (server SDK)",
};
/** The short word for a badge or a filter. */
export const KIND_SHORT: Record<ModKind, string> = {
  kiln: "Kiln", native: "Native", plugin: "Plugin", emf: "EMF", eml: "EML", shroudforge: "ShroudForge", set: "SET", greeneye: "Greeneye",
  flame: "Flame", shroudtopia: "Shroudtopia", enshwick: "Enshwick", shroudlink: "shroudlink", ember: "Ember",
};
export const KIND_TONE: Record<ModKind, Tone> = {
  kiln: "ember", native: "info", emf: "ok", plugin: "idle", eml: "warn", shroudforge: "info", set: "ok", greeneye: "ok", flame: "ember",
  shroudtopia: "info", enshwick: "idle", shroudlink: "idle", ember: "idle",
};
/** engine_mods::plugin_of: the Kiln plugin that runs a kind. */
export const KIND_PLUGIN: Partial<Record<ModKind, string>> = {
  emf: "kiln-emf", eml: "kiln-eml", shroudforge: "kiln-shroudforge", set: "kiln-set", greeneye: "kiln-greeneye", flame: "kiln-flame",
  shroudtopia: "kiln-shroudtopia",
};
/** Kinds whose settings the launcher edits (engine_settings_get/set). EMF's ini is the Configs tab's. */
export const SETTINGS_KINDS: ModKind[] = ["shroudforge", "shroudtopia"];

/** A profile's `mods` with every kind's list empty (profile.rs serializes all 13). */
export const emptyMods = (): Record<ModKind, ProfileMod[]> => Object.fromEntries(MOD_KINDS.map((k) => [k, []])) as unknown as Record<ModKind, ProfileMod[]>;
/** A profile's list of a kind; an older backend may leave the engine kinds out. */
export const modsOf = (mods: Partial<Record<ModKind, ProfileMod[]>>, k: ModKind): ProfileMod[] => mods[k] ?? [];
/** Every kind's count added up (ProfileSummary.counts; missing kinds count 0). */
export const totalCount = (counts: Partial<Record<ModKind, number>> | undefined): number =>
  MOD_KINDS.reduce((n, k) => n + (counts?.[k] ?? 0), 0);
