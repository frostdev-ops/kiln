// Mod kinds for the UI: the lists, the names players see and the badge tones. Mirrors kiln_manage::manifest::ModKind
// (ALL, CORE, is_engine, label) and engine_mods (IMPORTED, plugin_of, runs_yet). docs/kiln/engines.md.
import type { ModKind, ProfileMod } from "./commands";

type Tone = "ok" | "warn" | "err" | "info" | "idle" | "ember";

/** Every kind, in ModKind::ALL order. */
export const MOD_KINDS: ModKind[] = ["kiln", "native", "plugin", "emf", "eml", "xhl", "shroudforge", "set", "greeneye", "flame", "shroudtopia", "enshwick", "shroudlink", "ember"];
/** Kiln's own four. */
export const CORE_KINDS: ModKind[] = ["kiln", "native", "plugin", "emf"];
/** Other engines' mods and external-host companions that the Library imports (engine_mods::IMPORTED): every engine kind. */
export const IMPORTED_KINDS: ModKind[] = ["eml", "xhl", "shroudforge", "set", "greeneye", "flame", "shroudtopia", "enshwick", "shroudlink", "ember"];
/** Every kind that is not one of Kiln's own four. */
export const ENGINE_KINDS: ModKind[] = IMPORTED_KINDS;
/** Kinds the Library holds and profiles and servers carry, but whose plugin can't start them yet (engine_mods::runs_yet:
 *  Ember has published no mod ABI, so kiln-ember only finds and reports its mods). The UI says so wherever they show. */
export const NOT_RUNNING_KINDS: ModKind[] = ["ember"];
/** What to tell a player about a kind that doesn't run yet. */
export const NOT_RUNNING_NOTE: Partial<Record<ModKind, string>> = {
  ember: "Ember mods don't run yet: Ember has not published its mod ABI, so kiln-ember only finds and reports them. They stay in the profile; running them will require a compatible loader update.",
};

export const isEngineKind = (k: ModKind) => !CORE_KINDS.includes(k);
export const runsYet = (k: ModKind) => !NOT_RUNNING_KINDS.includes(k);

/** ModKind::label: the engine's full name. */
export const KIND_NAME: Record<ModKind, string> = {
  kiln: "Kiln", native: "Native", plugin: "Plugin", emf: "EMF", eml: "EML (Enshrouded Mod Loader)", xhl: "XHL (requires XHL-Core)", shroudforge: "ShroudForge",
  set: "Shrouded Engine Tweaks", greeneye: "Greeneye Loader", flame: "Flame", shroudtopia: "Shroudtopia", enshwick: "Enshwick",
  shroudlink: "shroudlink", ember: "Ember (server SDK)",
};
/** The short word for a badge or a filter. */
export const KIND_SHORT: Record<ModKind, string> = {
  kiln: "Kiln", native: "Native", plugin: "Plugin", emf: "EMF", eml: "EML", xhl: "XHL", shroudforge: "ShroudForge", set: "SET", greeneye: "Greeneye",
  flame: "Flame", shroudtopia: "Shroudtopia", enshwick: "Enshwick", shroudlink: "shroudlink", ember: "Ember",
};
export const KIND_TONE: Record<ModKind, Tone> = {
  kiln: "ember", native: "info", emf: "ok", plugin: "idle", eml: "warn", xhl: "warn", shroudforge: "info", set: "ok", greeneye: "ok", flame: "ember",
  shroudtopia: "info", enshwick: "ok", shroudlink: "info", ember: "idle",
};
/** engine_mods::plugin_of: the Kiln plugin that runs a kind. */
export const KIND_PLUGIN: Partial<Record<ModKind, string>> = {
  emf: "kiln-emf", eml: "kiln-eml", shroudforge: "kiln-shroudforge", set: "kiln-set", greeneye: "kiln-greeneye", flame: "kiln-flame",
  shroudtopia: "kiln-shroudtopia", enshwick: "kiln-enshwick", shroudlink: "kiln-shroudlink", ember: "kiln-ember",
};
/** Kinds whose settings the launcher edits (engine_settings_get/set). EMF's ini is the Configs tab's. */
export const SETTINGS_KINDS: ModKind[] = ["shroudforge", "shroudtopia"];

/** A profile's `mods` with every kind's list empty (profile.rs serializes all kinds). */
export const emptyMods = (): Record<ModKind, ProfileMod[]> => Object.fromEntries(MOD_KINDS.map((k) => [k, []])) as unknown as Record<ModKind, ProfileMod[]>;
/** A profile's list of a kind; an older backend may leave the engine kinds out. */
export const modsOf = (mods: Partial<Record<ModKind, ProfileMod[]>>, k: ModKind): ProfileMod[] => mods[k] ?? [];
/** Every kind's count added up (ProfileSummary.counts; missing kinds count 0). */
export const totalCount = (counts: Partial<Record<ModKind, number>> | undefined): number =>
  MOD_KINDS.reduce((n, k) => n + (counts?.[k] ?? 0), 0);
