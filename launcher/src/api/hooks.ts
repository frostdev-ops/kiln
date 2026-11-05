// TanStack Query hooks over commands.ts. Pages use these; the shell mounts useBackendEvents()
// once so backend events refresh the right queries.
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { create } from "zustand";
import * as api from "./commands";
import type { GameId, GameInstall, ModKind, ProfileId } from "./commands";

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
  resolve: (p: ProfileId, side: api.RunSide) => ["profile_resolve", p, side] as const,
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
};

// ---------------------------------------------------------------- selected game (persisted)

interface GameSel { gameId: GameId | null; setGameId: (id: GameId | null) => void }
export const useGameSel = create<GameSel>((set) => ({
  gameId: typeof localStorage !== "undefined" ? localStorage.getItem("kiln.gameId") : null,
  setGameId: (id) => { if (id) localStorage.setItem("kiln.gameId", id); else localStorage.removeItem("kiln.gameId"); set({ gameId: id }); },
}));

export const useAppInfo = () => useQuery({ queryKey: qk.appInfo, queryFn: api.appInfo, staleTime: Infinity, retry: false });
export const useSettings = () => useQuery({ queryKey: qk.settings, queryFn: api.appSettingsGet });
export const useGames = () => useQuery({ queryKey: qk.games, queryFn: api.gameList });

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

export const useGameStatus = (g: GameId | null | undefined) =>
  useQuery({ queryKey: qk.gameStatus(g ?? ""), queryFn: () => api.gameStatus(g!), enabled: !!g, refetchInterval: 4000 });
export const useLaunchState = (g: GameId | null | undefined) =>
  useQuery({ queryKey: qk.launchState(g ?? ""), queryFn: () => api.launchState(g!), enabled: !!g, refetchInterval: 5000 });
export const useProfiles = () => useQuery({ queryKey: qk.profiles, queryFn: api.profileList });
export const useProfile = (p: ProfileId | null | undefined) => useQuery({ queryKey: qk.profile(p ?? ""), queryFn: () => api.profileGet(p!), enabled: !!p });
export const useActiveProfile = (g: GameId | null | undefined) =>
  useQuery({ queryKey: qk.active(g ?? ""), queryFn: () => api.profileActive(g!), enabled: !!g });
export const useLibrary = (kind: ModKind | null = null) => useQuery({ queryKey: qk.library(kind), queryFn: () => api.libraryList(kind) });

/** Refetches everything about a game (after install, activation, launch). */
export function invalidateGame(qc: QueryClient, g: GameId) {
  // the game list too: an install's id changes once Kiln is installed (path hash -> receipt id)
  for (const k of [qk.games, qk.gameStatus(g), qk.launchState(g), qk.runtimeStatus(g), qk.active(g), qk.pending(g), qk.profiles, qk.backups(g)])
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
    return () => { live = false; offs.forEach((f) => f()); };
  }, [qc]);
}
