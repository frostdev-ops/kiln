// The main list's data and edits (shell/mods.ts builds the rows): the Library of every kind, the loaded profile and its
// client preview, the game folder's mods, the last start's failures, and the switches, which edit the loaded profile
// through its save queue (pages/profiles/useProfileEdit.ts). The backend writes the game's profile.json again after each
// save where the profile is loaded (profile_save), so the next Play, and Steam's own Play button, use it.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useMemo } from "react";
import * as api from "../api/commands";
import type { GameInstall, Profile, ProfileMod } from "../api/commands";
import { qk, useActiveProfile, useGameStatus, useLaunchState, useLibrary, useProfiles } from "../api/hooks";
import { engineStatusKey } from "../components/EngineStatus";
import { useFound } from "../pages/profiles/GameFolder";
import { useProfileEdit } from "../pages/profiles/useProfileEdit";
import { applyBulk, buildRows, bulkPlan, changeKey, countLine, revertBulk, type BulkChange, type BulkPlan, type ModRow } from "./mods";
import { useRetained } from "./notices";

/** The profile a player gets when they never made one (kiln_manage::quick::DEFAULT_PROFILE). Loaded, the main screen
 *  does not name it. */
export const DEFAULT_PROFILE = "My mods";
export const isDefaultProfile = (p: { id: string; name: string } | null | undefined) => !!p && (p.id === "my-mods" || p.name === DEFAULT_PROFILE);

/** What "All on" / "All off" did, for its Undo. The Undo is good only while its profile is the loaded one and nothing
 *  outside this list changed it: `gen` is the generation the profile has after the action and after every switch made
 *  in the list since (null while a save is in flight); a change from elsewhere (another page, another window, a
 *  profile deleted and made again) leaves the shown generation or `created` different, and the Undo says it cannot.
 *  The mods the player switched in the list since are `touched`: the Undo leaves them as they are. */
export interface BulkUndo {
  profileId: string;
  profileName: string;
  created: string;
  changes: BulkChange[];
  gen: number | null;
  touched: Set<string>;
  /** Why the Undo cannot run any more (a failed save, a change from elsewhere, done already). */
  over: string | null;
}
const undos = new Set<BulkUndo>();

export type UndoResult = { ok: true; kept: number } | { ok: false; why: string };

export function useMods(game: GameInstall | null) {
  const qc = useQueryClient();
  const gameId = game?.id ?? null;
  const status = useGameStatus(gameId);
  const installed = !!status.data?.runtime.installed;
  const active = useActiveProfile(gameId);
  const profiles = useProfiles();
  const pid = active.data?.profileId ?? null;
  const edit = useProfileEdit(pid ?? "");
  const lib = useLibrary(null);
  const profile = pid && edit.profile?.id === pid ? edit.profile : null;
  const preview = useQuery({ queryKey: qk.resolve(pid ?? "", "client", gameId), queryFn: () => api.profileResolve(pid!, "client", gameId), enabled: !!pid && !!gameId });
  const found = useFound(installed ? gameId : null, pid);
  const launch = useLaunchState(gameId);
  const hasXhl = (lib.data ?? []).some((e) => e.kind === "xhl");
  const engines = useQuery({ queryKey: engineStatusKey(gameId ?? ""), queryFn: () => api.engineStatus(gameId!), enabled: !!gameId && installed && hasXhl });
  const xhl = engines.data?.find((s) => s.kind === "xhl")?.prerequisite;
  const retained = useRetained((s) => (gameId ? s.byGame[gameId] : undefined));
  // an unreadable Library is said as such: never rows that call every mod gone (the profile's entries without it)
  const rows = useMemo(() => lib.error ? [] : buildRows({
    library: lib.data ?? [], profile, preview: preview.data ?? null, found: found.data?.mods ?? [], launch: launch.data ?? null,
    retained: retained ?? [], xhlHost: xhl ? xhl.available : null,
  }), [lib.error, lib.data, profile, preview.data, found.data, launch.data, retained, xhl]);
  const count = useMemo(() => countLine(rows), [rows]);
  const summary = profiles.data?.find((p) => p.id === pid) ?? null;

  /** On: in the loaded profile and enabled. Switching on a mod the profile lacks adds the Library's newest version;
   *  switching off keeps the entry, disabled, so its version, order and settings stay. */
  const toggle = (r: ModRow, on: boolean) => {
    if (!profile || !r.kind || r.found) return;
    if (r.entry) { edit.toggle(r.kind, r.id, on); touch(r); return; }
    const v = r.lib?.versions[0];
    if (!on || !v) return;
    const mod: ProfileMod = { id: r.id, version: v.version, hash: v.hash, enabled: true };
    edit.add(r.kind, mod);
    touch(r);
  };
  /** Another Library version: a mod in the profile keeps its switch; one the profile lacks goes in with it, on. */
  const setVersion = (r: ModRow, version: string, hash: string) => {
    if (!profile || !r.kind || r.found) return;
    const kind = r.kind;
    if (!r.entry) { edit.add(kind, { id: r.id, version, hash, enabled: true }); touch(r); return; }
    edit.update((p) => { p.mods[kind] = (p.mods[kind] ?? []).map((m) => (m.id === r.id ? { ...m, version, hash } : m)); });
    touch(r);
  };
  /** Out of the loaded profile (the Library keeps it). */
  const drop = (r: ModRow) => { if (r.kind && r.entry) { edit.remove(r.kind, r.id); touch(r); } };
  /** The generation the profile has once its queue is saved: an Undo's `gen` follows the saves made from the list. */
  const settle = (rec: BulkUndo) => {
    rec.gen = null;
    void edit.flush().then((ok) => {
      if (rec.over) return;
      if (!ok) { rec.over = "a save of the profile failed since"; return; }
      rec.gen = qc.getQueryData<Profile>(qk.profile(rec.profileId))?.generation ?? null;
    });
  };
  /** A switch, version or removal made in the list: every live Undo of this profile leaves the mod alone. A profile
   *  that changed elsewhere first (its shown generation is not the Undo's) ends that Undo. */
  function touch(r: ModRow) {
    if (!profile) return;
    for (const rec of undos) {
      if (rec.over || rec.profileId !== profile.id) continue;
      if (rec.gen !== null && (profile.generation !== rec.gen || profile.created !== rec.created)) { rec.over = `${rec.profileName} changed elsewhere since`; continue; }
      rec.touched.add(r.key);
      settle(rec);
    }
  }
  /** "All on" / "All off" over `shown`: every switch that would change, as one edit (one save of the profile). */
  const setAll = (shown: ModRow[], on: boolean): { plan: BulkPlan; undo: BulkUndo | null } | null => {
    if (!profile) return null;
    const plan = bulkPlan(shown, on);
    if (!plan.change.length) return { plan, undo: null };
    edit.update((p) => applyBulk(p, plan.change));
    const rec: BulkUndo = { profileId: profile.id, profileName: profile.name, created: profile.created, changes: plan.change, gen: null, touched: new Set(), over: null };
    undos.add(rec);
    settle(rec);
    return { plan, undo: rec };
  };
  /** Undoes setAll's changes in one save, from the list as it is now (call it on the mounted list's useMods). Leaves the
   *  mods touched since; refuses when another profile is loaded, a save still runs, or the profile changed elsewhere:
   *  the backend's copy is read first, since the list may not have seen another window's save yet. */
  const undoAll = async (rec: BulkUndo): Promise<UndoResult> => {
    const no = (why: string): UndoResult => ({ ok: false, why });
    if (rec.over) return no(rec.over);
    if (pid !== rec.profileId || !profile) return no(`${rec.profileName} is not the loaded profile now`);
    if (rec.gen === null || edit.saving) return no("the profile is still being saved");
    const elsewhere = (p: Profile) => p.created !== rec.created || p.generation !== rec.gen;
    let disk: Profile;
    try { disk = await api.profileGet(rec.profileId); } catch (e) { return no(api.toKilnError(e).message); }
    if (elsewhere(profile) || elsewhere(disk)) {
      rec.over = `${rec.profileName} changed elsewhere since`;
      void qc.invalidateQueries({ queryKey: qk.profile(rec.profileId) });
      return no(rec.over);
    }
    if (rec.over) return no(rec.over);
    rec.over = "it was undone already";
    undos.delete(rec);
    const kept = rec.changes.filter((c) => rec.touched.has(changeKey(c))).length;
    edit.update((p) => { revertBulk(p, rec.changes, rec.touched); });
    return { ok: true, kept };
  };

  const loading = lib.isLoading || active.isLoading || (!!pid && !profile && edit.query.isLoading);
  return {
    rows, count, profile, profileId: pid, summary, installed, loading, edit, toggle, setVersion, drop, setAll, undoAll,
    error: lib.error ?? edit.query.error ?? null, found, preview,
  };
}
