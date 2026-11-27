// Fixed game-relative paths and byte caps live on the native side too.
import { invoke } from "@tauri-apps/api/core";
import { isTauri, toKilnError, type GameId } from "../api/commands";

export async function readRegistryReport(gameId: GameId): Promise<string | null> {
  try {
    return isTauri() ? await invoke<string | null>("registry_report_read", { gameId })
      : await (await import("./mock")).readMockRegistryReport();
  } catch (e) { throw toKilnError(e); }
}

/** One value a rollback changes back (kiln_manage::registry_plan::Change): null = no registry edit of it. */
export interface RegistryPlanChange { field: string; from: unknown; to: unknown }
/** registry_rollback's answer (launcher/src-tauri/src/commands/registry.rs): the plans registry.json names, the request
 * waiting for the next start, the plan a rollback goes to and what it changes back, or why it cannot. */
export interface RegistryRollback {
  plan: string | null; previous: string | null;
  /** registry.json's rollback object of the last start: plan, from, state (applied | refused), reason, changedBack, leftOut. */
  rollback: Record<string, unknown> | null;
  pending: { plan: string; from?: string | null; requested: string; by: string } | null;
  target: { hash: string; mods: { id: string; version: string }[] } | null;
  changes: RegistryPlanChange[];
  /** The changes back could not be computed (the plan rolled away from is gone): the start lists them. */
  changesUnknown: string | null;
  /** Why a rollback cannot be asked for: no plan yet, no earlier good plan, the previous plan cannot be started on. */
  blocked: string | null;
  dryRun: boolean; wrote: boolean; cleared: boolean | null;
}

/** dryRun: what a rollback would change back, nothing written. clear: the request goes (the next start runs the mods'
 * current edits again). Otherwise the request for the next start is written. Saves are never touched. */
export async function registryRollback(gameId: GameId, opts: { clear: boolean; dryRun: boolean }): Promise<RegistryRollback> {
  try {
    return isTauri() ? await invoke<RegistryRollback>("registry_rollback", { gameId, clear: opts.clear, dryRun: opts.dryRun })
      : await (await import("./mock")).mockRegistryRollback(opts);
  } catch (e) { throw toKilnError(e); }
}

/** Effect replay's opt-in file (kiln_manage::registry_plan::REPLAY_OPT_IN_REL, the one `kilnctl registry replay` writes):
 * package ("shroudforge:<id>" / "eml:<id>") -> opted in. */
export type RegistryReplayOptIns = Record<string, boolean>;
export async function registryReplayOptIns(gameId: GameId): Promise<RegistryReplayOptIns> {
  try {
    return isTauri() ? await invoke<RegistryReplayOptIns>("registry_replay_opt_ins", { gameId })
      : await (await import("./mock")).mockRegistryReplayOptIns();
  } catch (e) { throw toKilnError(e); }
}
/** Opts one package in to effect replay (on) or out of it; the next start reads the file. Returns the file's packages. */
export async function registryReplayOpt(gameId: GameId, who: string, on: boolean): Promise<RegistryReplayOptIns> {
  try {
    return isTauri() ? await invoke<RegistryReplayOptIns>("registry_replay_opt", { gameId, who, on })
      : await (await import("./mock")).mockRegistryReplayOpt(who, on);
  } catch (e) { throw toKilnError(e); }
}
