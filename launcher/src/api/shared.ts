// The basics every layer shares, without Tauri: the error every command and admin op rejects with, JSON values, and the
// few shapes the design system (src/ui) and the admin pages (@kiln/admin-ui, src/admin) take from the launcher's
// backend. ./commands.ts re-exports all of them, so `import { ... } from "../api/commands"` keeps working; modules the
// admin pages' package reaches import them from here, so the package never reaches the command layer (and Tauri).
// Types mirror crates/kiln-manage (serde camelCase).

export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };
export type ServerId = string;

export type ErrorCode =
  | "not_implemented" | "invalid_argument" | "not_found" | "already_exists" | "io" | "parse" | "schema" | "conflict"
  | "game_running" | "steam_updating" | "permission" | "unsupported" | "network" | "hash_mismatch"
  | "consent_required" | "refused" | "timeout" | "canceled" | "busy" | "auth_failed" | "admin_unavailable"
  | "op_failed" | "internal"
  /** The data folder cannot be used: its pointer names a folder that is missing, unreadable or not a Kiln data folder. */
  | "data_folder";

/** What every command rejects with. */
export interface KilnError {
  code: ErrorCode;
  message: string;
  detail?: unknown;
}

export function isKilnError(e: unknown): e is KilnError {
  return typeof e === "object" && e !== null && "code" in e && "message" in e;
}

/** Any rejection as a KilnError (Tauri passes the serialized error through; anything else becomes "internal"). */
export function toKilnError(e: unknown): KilnError {
  if (isKilnError(e)) return e;
  return { code: "internal", message: e instanceof Error ? e.message : String(e) };
}

// configs (config_read's schema snapshot; ui/SchemaForm.tsx)
export interface SnapshotEntry {
  path: string;
  type: string;
  comment: string | null;
  default: Json;
  requiresRestart: boolean;
  synced: boolean;
  min?: number;
  max?: number;
  /** [Range(min, max, step)]: the slider moves from min in steps of this size (editor metadata only). */
  step?: number;
  /** [Range(..., SoftMin = true)] (mod API 0.2.26): min is where the slider starts, not a limit. */
  softMin?: boolean;
  /** [Range(..., SoftMax = true)]: max is where the slider ends, not a limit; higher values are kept (kiln#155). */
  softMax?: boolean;
  options?: string[];
}
export interface SchemaSnapshot {
  format: number;
  source: "kiln" | "emf";
  file: string;
  valueFormat: "jsonc" | "ini";
  id: string;
  mod: string;
  modVersion: string | null;
  kind: string | null;
  written: string | null;
  kiln: string | null;
  sections: { path: string; comment: string | null; synced: boolean }[];
  entries: SnapshotEntry[];
}
export interface ValidationError {
  path: string;
  message: string;
}

// placements (ui/Toast.tsx notifyPlacementCleanup)
/** A placed mod folder Kiln left where it is (engine_mods.rs RetainedPlacement): `mayRun`, its own loader may still run
 * it whatever the profile says. */
export interface RetainedPlacement {
  path: string;
  reason: string;
  mayRun: boolean;
  /** The profile has the mod off, or no longer has it (absent: on, or an older backend). */
  off?: boolean;
}
export interface PlacementCleanup {
  removed: string[];
  retained: RetainedPlacement[];
  /** Files the mods wrote into removed copies, now in the game's kiln/kept-mod-files: one sentence per mod. Absent: none. */
  keptFiles?: string[];
}
