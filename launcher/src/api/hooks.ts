// TanStack Query hooks over commands.ts. Pages use these; the shell mounts useBackendEvents()
// once so backend events refresh the right queries.
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { create } from "zustand";
import * as api from "./commands";
import type { Distribution, GameId, GameInstall, ModKind, ProfileId } from "./commands";
import { distributionOf } from "./distribution";
import { notifyActivation } from "../shell/notices";

/** Query keys, one place. */
export const qk = {
  appInfo: ["app_info"] as const,
  settings: ["app_settings"] as const,
  games: ["game_list"] as const,
  gameStatus: (g: GameId) => ["game_status", g] as const,
  launchState: (g: GameId) => ["launch_state", g] as const,
  runtimeList: ["runtime_list"] as const,
  runtimeStatus: (g: GameId) => ["runtime_status", g] as const,
  profiles: ["profile_list"] as const,
  profile: (p: ProfileId) => ["profile_get", p] as const,
  resolve: (p: ProfileId, side: api.RunSide, g: GameId | null = null) => ["profile_resolve", p, side, g] as const,
  active: (g: GameId) => ["profile_active", g] as const,
  library: (k: ModKind | null) => ["library_list", k] as const,
  configs: (t: api.ConfigTarget) => ["config_list", t] as const,
  config: (t: api.ConfigTarget, f: string) => ["config_read", t, f] as const,
  nativeState: (t: api.ConfigTarget) => ["config_native_state", t] as const,
  kilnJson: (g: GameId) => ["config_kiln_read", g] as const,
  logs: (t: api.LogTarget) => ["logs_list", t] as const,
  crashes: (t: api.LogTarget) => ["logs_crash_reports", t] as const,
  saves: (g: GameId) => ["saves_list", g] as const,
  backups: (g: GameId) => ["saves_backups", g] as const,
  pending: (g: GameId) => ["modpack_pending", g] as const,
  dataFolder: ["data_folder_get"] as const,
  dataFolderSize: ["data_folder_size"] as const,
};

// ---------------------------------------------------------------- selected game (persisted)

interface GameSel { gameId: GameId | null; setGameId: (id: GameId | null) => void }
export const useGameSel = create<GameSel>((set) => ({
  gameId: typeof localStorage !== "undefined" ? localStorage.getItem("kiln.gameId") : null,
  setGameId: (id) => { if (id) localStorage.setItem("kiln.gameId", id); else localStorage.removeItem("kiln.gameId"); set({ gameId: id }); },
}));

export const useAppInfo = () => useQuery({ queryKey: qk.appInfo, queryFn: api.appInfo, staleTime: Infinity, retry: false });
/** This build's distribution (api/distribution.ts): "direct" updates itself, "nexus" never does (no update UI at all).
 *  null until app_info answers: show neither the update UI nor the Nexus line then. */
export function useDistribution(): Distribution | null {
  const info = useAppInfo();
  return info.data ? distributionOf(info.data) : null;
}
/** Nexus stays hidden and idle until the backend explicitly enables this build. */
export function useNexusEnabled() {
  const info = useAppInfo();
  return info.isSuccess && info.data?.nexusEnabled === true;
}
export const useSettings = () => useQuery({ queryKey: qk.settings, queryFn: api.appSettingsGet });
export const useGames = () => useQuery({ queryKey: qk.games, queryFn: api.gameList });
/** The data folder in use (fixed until the launcher restarts; the start-up gate, shell/DataFolderGate.tsx, fills it). */
export const useDataFolder = () => useQuery({ queryKey: qk.dataFolder, queryFn: api.dataFolderGet, staleTime: Infinity, retry: false });
/** Its size, measured on its own (the path shows at once). */
export const useDataFolderSize = (enabled = true) =>
  useQuery({ queryKey: qk.dataFolderSize, queryFn: api.dataFolderSize, staleTime: 60_000, retry: false, enabled });

/** The selected client install (falls back to the default game, then the first client). */
export function useGame(): { game: GameInstall | null; games: GameInstall[]; loading: boolean; error: unknown } {
  const games = useGames();
  const settings = useSettings();
  const sel = useGameSel((s) => s.gameId);
  const list = games.data ?? [];
  const clients = list.filter((g) => g.kind === "client");
  const game = clients.find((g) => g.id === sel) ?? clients.find((g) => g.id === settings.data?.defaultGameId) ?? clients[0] ?? null;
  return { game, games: list, loading: games.isLoading, error: games.error };
}

// ---------------------------------------------------------------- polling

/** The launcher window has the focus and is shown. False while the game (or anything else) is in front: the launcher's
 *  own polls slow down then (usePoll); backend events (kiln://game, kiln://last-server) still refresh at once. */
const windowActive = () => typeof document === "undefined" || (!document.hidden && document.hasFocus());
export const useWindowActive = create<{ active: boolean }>(() => ({ active: windowActive() }));
if (typeof window !== "undefined") {
  const update = () => useWindowActive.setState({ active: windowActive() });
  window.addEventListener("focus", update);
  window.addEventListener("blur", update);
  document.addEventListener("visibilitychange", update);
}

/** How often an unfocused launcher polls at most. */
export const UNFOCUSED_POLL_MS = 30_000;

/** A poll interval: `ms` while the window is active, at most every UNFOCUSED_POLL_MS while it is not (focusing the
 *  window again restarts the timer at `ms`). Hidden windows do not poll at all (TanStack Query's default). */
export function usePoll(ms: number): number {
  const active = useWindowActive((s) => s.active);
  return active ? ms : Math.max(ms, UNFOCUSED_POLL_MS);
}

export const useGameStatus = (g: GameId | null | undefined) =>
  useQuery({ queryKey: qk.gameStatus(g ?? ""), queryFn: () => api.gameStatus(g!), enabled: !!g, refetchInterval: usePoll(4000) });

/** launch_state polls only while the game runs (or is not known to be stopped): kiln.log's error count grows then.
 *  A game that is not running writes none of its files, and its start, stop and state files arrive as kiln://game,
 *  which refetches this query (invalidateGame). */
export function useLaunchState(g: GameId | null | undefined) {
  // the same game_status query, read without a timer of its own
  const running = useQuery({ queryKey: qk.gameStatus(g ?? ""), queryFn: () => api.gameStatus(g!), enabled: !!g, select: (s) => s.running,
    refetchOnMount: false, refetchOnWindowFocus: false }).data;
  const poll = usePoll(5000);
  return useQuery({ queryKey: qk.launchState(g ?? ""), queryFn: () => api.launchState(g!), enabled: !!g, refetchInterval: running === false ? false : poll });
}
/** What kiln-eml does before the game's window exists (`launch_prestart`), polled every 1.5 s while `on` (the game is
 *  starting, or runs and Kiln has not booted yet): null when nothing, or the sentence for the launch status. */
export const usePreStart = (g: GameId | null | undefined, on: boolean) =>
  useQuery({ queryKey: ["launch_prestart", g ?? ""] as const, queryFn: () => api.launchPrestart(g!), enabled: !!g && on, refetchInterval: 1500 });
export const useProfiles = () => useQuery({ queryKey: qk.profiles, queryFn: api.profileList });
export const useProfile = (p: ProfileId | null | undefined) => useQuery({ queryKey: qk.profile(p ?? ""), queryFn: () => api.profileGet(p!), enabled: !!p });
export const useActiveProfile = (g: GameId | null | undefined) =>
  useQuery({ queryKey: qk.active(g ?? ""), queryFn: () => api.profileActive(g!), enabled: !!g });
export const useLibrary = (kind: ModKind | null = null) => useQuery({ queryKey: qk.library(kind), queryFn: () => api.libraryList(kind) });

/** Shows `profileId` (null: no profile) as the game's active profile at once, before the activation answers: Play's
 *  name, the mouth and the Profiles list read these two queries. Await it before the command; the returned function
 *  puts the old values back when the activation fails. */
export async function showActive(qc: QueryClient, g: GameId, profileId: ProfileId | null): Promise<() => void> {
  // a refetch that started before the activation must not land on top of it
  await qc.cancelQueries({ queryKey: qk.active(g) });
  await qc.cancelQueries({ queryKey: qk.profiles });
  const before = qc.getQueryData<api.Activation | null>(qk.active(g));
  const list = qc.getQueryData<api.ProfileSummary[]>(qk.profiles);
  qc.setQueryData<api.Activation | null>(qk.active(g), profileId
    ? { generation: 0, pathMode: "z", profileJson: "", copied: 0, fingerprint: "", ...before, gameId: g, profileId, activated: new Date().toISOString() }
    : null);
  if (list) {
    qc.setQueryData<api.ProfileSummary[]>(qk.profiles, list.map((p) => ({
      ...p, active: p.id === profileId ? [...p.active.filter((x) => x !== g), g] : p.active.filter((x) => x !== g),
    })));
  }
  return () => {
    qc.setQueryData(qk.active(g), before ?? null);
    if (list) qc.setQueryData(qk.profiles, list);
  };
}

// ---------------------------------------------------------------- the last runtime change per game

/** An install, update, rollback or uninstall started on Runtime, kept for this session so its outcome stays in view
 *  after the page is left: Runtime shows it until dismissed, Play shows a failure. */
export interface RuntimeChange {
  /** "Update to 0.2.0-dev+20261106.f1ad110", "Uninstall" */
  what: string;
  /** The bundle version it puts in (null: uninstall). */
  version: string | null;
  taskId: api.TaskId | null;
  state: "running" | "done" | "failed";
  error: api.KilnError | null;
  at: number;
}
interface RuntimeChanges { last: Record<GameId, RuntimeChange>; put: (g: GameId, c: RuntimeChange) => void; clear: (g: GameId) => void }
export const useRuntimeChanges = create<RuntimeChanges>((set, get) => ({
  last: {},
  put: (g, c) => set({ last: { ...get().last, [g]: c } }),
  clear: (g) => { const { [g]: _, ...rest } = get().last; set({ last: rest }); },
}));

/** Refetches everything about a game (after install, activation, launch). */
export function invalidateGame(qc: QueryClient, g: GameId) {
  // the game list too: an install's id changes once Kiln is installed (path hash -> receipt id)
  // (cook_plan: which mods need a cook follows the profile in the game and the game's build)
  for (const k of [qk.games, qk.gameStatus(g), qk.launchState(g), qk.runtimeStatus(g), qk.active(g), qk.pending(g), qk.profiles, qk.backups(g), ["cook_plan", g]])
    qc.invalidateQueries({ queryKey: k });
}

/** Mount once (the shell): backend events refresh queries. */
export function useBackendEvents() {
  const qc = useQueryClient();
  useEffect(() => {
    const offs: (() => void)[] = [];
    let live = true;
    const add = (p: Promise<() => void>) => p.then((f) => (live ? offs.push(f) : f()));
    add(api.onKilnEvent("kiln://game", (e) => {
      if (e.status) qc.setQueryData(qk.gameStatus(e.gameId), e.status);
      invalidateGame(qc, e.gameId);
    }));
    add(api.onKilnEvent("kiln://last-server", (e) => qc.invalidateQueries({ queryKey: qk.pending(e.gameId) })));
    // a profile written again after an edit, a change outside the launcher or at start-up: what it kept in the game
    // folder waits in the backend until taken here, so a re-apply before this listener existed is shown too, once
    const take = () => api.activationNotices().then((list) => list.forEach((a) => { notifyActivation(a); invalidateGame(qc, a.gameId); })).catch(() => {});
    add(api.onKilnEvent("kiln://activation", () => void take()).then((off) => { void take(); return off; }));
    return () => { live = false; offs.forEach((f) => f()); };
  }, [qc]);
}
