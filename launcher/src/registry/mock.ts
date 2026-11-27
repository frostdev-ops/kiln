// A normal browser session has no native registry report or plans. Playwright supplies synthetic fixtures.
import type { RegistryReplayOptIns, RegistryRollback } from "./api";

export async function readMockRegistryReport(): Promise<string | null> { return null; }
export async function mockRegistryRollback(opts: { clear: boolean; dryRun: boolean }): Promise<RegistryRollback> {
  return { plan: null, previous: null, rollback: null, pending: null, target: null, changes: [], changesUnknown: null,
    blocked: "No registry plan yet: start the game once with Kiln.", dryRun: opts.dryRun, wrote: false, cleared: opts.clear ? false : null };
}
const optIns: RegistryReplayOptIns = {};
export async function mockRegistryReplayOptIns(): Promise<RegistryReplayOptIns> { return { ...optIns }; }
export async function mockRegistryReplayOpt(who: string, on: boolean): Promise<RegistryReplayOptIns> {
  optIns[who] = on;
  return { ...optIns };
}
