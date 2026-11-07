// A runtime change (install, update, rollback, uninstall) as one task, shared by Runtime and Play's Install Kiln: the
// change is kept per game (useRuntimeChanges), so its outcome outlives the page and Play shows a failure; a finished
// install puts its receipt into the cached status at once (re-hashing for a fresh status takes a while).
import type { QueryClient } from "@tanstack/react-query";
import * as api from "../api/commands";
import type { GameInstall, InstallState, RuntimeBundle } from "../api/commands";
import { invalidateGame, qk, useRuntimeChanges, type RuntimeChange } from "../api/hooks";
import { installNote, runTask } from "../ui";

/** `a` is older than `b`. runtime_list sorts newest first (the version, its build date, then when the bundle was
 *  built), so the list decides; a version not in it compares by version and build date. Never by the git id: two
 *  builds of one day differ only in when they were built. */
export function isOlder(a: string, b: string, list: RuntimeBundle[]): boolean {
  const i = list.findIndex((x) => x.version === a), j = list.findIndex((x) => x.version === b);
  if (i >= 0 && j >= 0) return i > j;
  const key = (v: string) => { const [base, build = ""] = v.split("+"); return { n: base.split("-")[0].split(".").map(Number), date: build.split(".")[0] }; };
  const [x, y] = [key(a), key(b)];
  for (let k = 0; k < 3; k++) if ((x.n[k] ?? 0) !== (y.n[k] ?? 0)) return (x.n[k] ?? 0) < (y.n[k] ?? 0);
  return x.date < y.date;
}

/** Runs `start` (a TaskId command) as the game's runtime change. True when it finished; false when it failed (the
 *  error is toasted and kept in useRuntimeChanges). */
export async function runRuntimeChange(qc: QueryClient, game: GameInstall, bundles: RuntimeBundle[], what: string, version: string | null, label: string,
  start: () => Promise<string>): Promise<boolean> {
  const { put } = useRuntimeChanges.getState();
  const gid = game.id;
  const base: RuntimeChange = { what, version, taskId: null, state: "running", error: null, at: Date.now() };
  put(gid, base);
  let ok = false;
  try {
    const r = await runTask<api.InstallResult | null>(async () => { const id = await start(); put(gid, { ...base, taskId: id }); return id; }, { done: label, detail: installNote });
    put(gid, { ...(useRuntimeChanges.getState().last[gid] ?? base), state: "done" });
    if (r?.runtime) {
      const newer = bundles.find((b) => b.sides.includes(game.kind) && isOlder(r.runtime.version, b.version, bundles))?.version ?? null;
      const fix = (s: InstallState | undefined) => s && { ...s, installed: true, receipt: r, modified: [], upgradeAvailable: newer };
      qc.setQueryData<InstallState>(qk.runtimeStatus(gid), fix);
      qc.setQueryData<api.GameStatus>(qk.gameStatus(gid), (s) => s && { ...s, runtime: fix(s.runtime)! });
    }
    ok = true;
  } catch (e) {
    put(gid, { ...(useRuntimeChanges.getState().last[gid] ?? base), state: "failed", error: api.toKilnError(e) });
  }
  invalidateGame(qc, gid);
  return ok;
}

/** Install (or update to, or roll back to) `b` in the game, as Runtime's buttons do. */
export function installBundle(qc: QueryClient, game: GameInstall, b: RuntimeBundle, bundles: RuntimeBundle[], current: string | null, opts: api.InstallOptions = {}) {
  const back = !!current && b.version !== current && isOlder(b.version, current, bundles);
  const what = !current ? `Install of ${b.version}` : back ? `Rollback to ${b.version}` : `Update to ${b.version}`;
  return runRuntimeChange(qc, game, bundles, what, b.version, `Kiln ${b.version} installed`,
    () => (back ? api.runtimeRollback(game.id, b.version) : api.runtimeInstall(game.id, b.version, opts)));
}
