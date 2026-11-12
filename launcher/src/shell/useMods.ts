// The main list's data and edits (shell/mods.ts builds the rows): the Library of every kind, the loaded profile and its
// client preview, the game folder's mods, the last start's failures, and the switches, which edit the loaded profile
// through its save queue (pages/profiles/useProfileEdit.ts). The backend writes the game's profile.json again after each
// save where the profile is loaded (profile_save), so the next Play, and Steam's own Play button, use it.
import { useQuery } from "@tanstack/react-query";
import { useMemo } from "react";
import * as api from "../api/commands";
import type { GameInstall, ProfileMod } from "../api/commands";
import { qk, useActiveProfile, useGameStatus, useLaunchState, useLibrary, useProfiles } from "../api/hooks";
import { engineStatusKey } from "../components/EngineStatus";
import { useFound } from "../pages/profiles/GameFolder";
import { useProfileEdit } from "../pages/profiles/useProfileEdit";
import { buildRows, countLine, type ModRow } from "./mods";
import { useRetained } from "./notices";

/** The profile a player gets when they never made one (kiln_manage::quick::DEFAULT_PROFILE). Loaded, the main screen
 *  does not name it. */
export const DEFAULT_PROFILE = "My mods";
export const isDefaultProfile = (p: { id: string; name: string } | null | undefined) => !!p && (p.id === "my-mods" || p.name === DEFAULT_PROFILE);

export function useMods(game: GameInstall | null) {
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
    if (r.entry) { edit.toggle(r.kind, r.id, on); return; }
    const v = r.lib?.versions[0];
    if (!on || !v) return;
    const mod: ProfileMod = { id: r.id, version: v.version, hash: v.hash, enabled: true };
    edit.add(r.kind, mod);
  };
  /** Another Library version: a mod in the profile keeps its switch; one the profile lacks goes in with it, on. */
  const setVersion = (r: ModRow, version: string, hash: string) => {
    if (!profile || !r.kind || r.found) return;
    const kind = r.kind;
    if (!r.entry) { edit.add(kind, { id: r.id, version, hash, enabled: true }); return; }
    edit.update((p) => { p.mods[kind] = (p.mods[kind] ?? []).map((m) => (m.id === r.id ? { ...m, version, hash } : m)); });
  };
  /** Out of the loaded profile (the Library keeps it). */
  const drop = (r: ModRow) => { if (r.kind && r.entry) edit.remove(r.kind, r.id); };

  const loading = lib.isLoading || active.isLoading || (!!pid && !profile && edit.query.isLoading);
  return {
    rows, count, profile, profileId: pid, summary, installed, loading, edit, toggle, setVersion, drop,
    error: lib.error ?? edit.query.error ?? null, found, preview,
  };
}
