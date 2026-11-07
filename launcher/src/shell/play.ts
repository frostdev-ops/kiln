// The Play control shared by the mouth (the shell's Play button and pilot light) and the Play page: the game's state
// as one view, whether Play can start, and launch_play. Remembers how this launcher started each game this session
// (a vanilla start writes no boot.json). The elapsed time counts from the game process's real start (game status
// `started`), whoever started it; only without one from what this launcher saw.
import { useIsMutating, useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { create } from "zustand";
import * as api from "../api/commands";
import type { GameInstall, LaunchMode } from "../api/commands";
import { invalidateGame, useActiveProfile, useGame, useGameStatus, useLaunchState, useProfiles } from "../api/hooks";
import { toast } from "../ui";
import { gameState, KILN_LOAD_GRACE_MS, playOnceMissed, type GameStateView } from "./gameState";

interface LaunchMemo {
  at: Record<string, number>;
  mode: Record<string, LaunchMode>;
  /** When the game was first seen running, per game (the clock's fallback when the status has no start time). */
  since: Record<string, number>;
}
export const useLaunchMemo = create<LaunchMemo>(() => ({ at: {}, mode: {}, since: {} }));

export interface PlayControl {
  game: GameInstall | null;
  view: GameStateView;
  installed: boolean;
  running: boolean;
  /** Play can start now (a game, stopped, not updating). */
  canPlay: boolean;
  busy: boolean;
  /** Waiting for Kiln to boot: a start this launcher made (or a play-once start) with no fresh boot.json yet. */
  starting: boolean;
  /** Seconds since the game started (while running). */
  elapsed: number | null;
  /** Kiln did not load: play-once.ini still there 120 s after our launch, or `kilnMissing`. */
  stuck: boolean;
  /** The game runs, and Kiln did not load into it: the play-once flag was not consumed (KILN_LOAD_GRACE_MS after the
   *  process started), or a non-vanilla start of this launcher has no boot.json of this run a minute in. Never for a
   *  vanilla start. The play surfaces show "No Kiln" and the Steam-setting banner instead of "Starting" forever. */
  kilnMissing: boolean;
  profileName: string | null;
  play: (mode: LaunchMode, profileId?: string) => void;
}

export function usePlayControl(): PlayControl {
  const qc = useQueryClient();
  const { game } = useGame();
  const status = useGameStatus(game?.id);
  const launchState = useLaunchState(game?.id);
  const active = useActiveProfile(game?.id);
  const profiles = useProfiles();
  const memo = useLaunchMemo();
  const id = game?.id ?? "";
  const s = status.data;
  const running = !!s?.running;
  const now = useNow(running ? 1000 : 0);
  const raw = gameState(game ? s : null, launchState.data, !!game && status.isLoading, now);
  const at = memo.at[id];
  const bootFresh = !!launchState.data?.boot && !launchState.data.stale;
  const vanillaStart = memo.mode[id] === "vanilla";
  const startedAt = running && s?.started ? Date.parse(s.started) : NaN;
  const kilnMissing = !!game && !vanillaStart && (playOnceMissed(s, now)
    || (running && !!s?.runtime.installed && !!launchState.data && !bootFresh && !!at && now - (Number.isNaN(startedAt) ? at : startedAt) >= 2 * KILN_LOAD_GRACE_MS));
  // a vanilla start writes no boot.json: say so when this launcher started it that way
  const view: GameStateView = raw.phase === "running" && !raw.detail?.startsWith("Kiln") && vanillaStart
    ? { ...raw, phase: "vanilla", label: "Vanilla", tone: "info", detail: "Started without Kiln" }
    : kilnMissing ? { phase: "running", label: "No Kiln", tone: "warn", pulse: true, detail: "The game runs without Kiln: Steam did not load it" } : raw;
  const launch = useMutation({
    mutationKey: ["launch_play"],
    mutationFn: (req: api.LaunchRequest) => api.launchPlay(id, req),
    onSuccess: (r) => {
      useLaunchMemo.setState((m) => ({ at: { ...m.at, [id]: Date.now() }, mode: { ...m.mode, [id]: r.mode } }));
      invalidateGame(qc, id);
      toast.ok(r.mode === "vanilla" ? "Starting vanilla" : r.mode === "no-mods" ? "Starting without mods" : "Starting", r.backup ? "Saves backed up first." : "Through Steam.");
    },
    onError: (e) => toast.error(e),
  });

  // the elapsed clock's fallback (no process start time): our launch when it was just now, else boot.json's time, else
  // the first time the game was seen running. An older launch of ours is another run (the game was started again
  // from Steam since).
  useEffect(() => {
    if (!id) return;
    const since = useLaunchMemo.getState().since[id];
    if (running && !since) {
      const boot = launchState.data?.boot && !launchState.data.stale ? Date.parse(launchState.data.boot.written) : NaN;
      const ours = useLaunchMemo.getState().at[id];
      const at = ours && Date.now() - ours < 120_000 ? ours : Number.isNaN(boot) ? Date.now() : boot;
      useLaunchMemo.setState((m) => ({ since: { ...m.since, [id]: at } }));
    } else if (!running && since) {
      // the run this launcher saw has ended: its launch (time, mode) is over too, so a later start from Steam is not
      // taken for it
      useLaunchMemo.setState((m) => {
        const drop = <T,>(r: Record<string, T>) => { const n = { ...r }; delete n[id]; return n; };
        return { since: drop(m.since), at: drop(m.at), mode: drop(m.mode) };
      });
    }
  }, [id, running, launchState.data]);
  const launching = useIsMutating({ mutationKey: ["launch_play"] });
  const real = running && s?.started ? Date.parse(s.started) : NaN;
  const since = Number.isNaN(real) ? memo.since[id] : real;
  const starting = !kilnMissing && (view.phase === "starting" || (launching > 0 && !running)
    || (running && !bootFresh && !!at && !vanillaStart && now - at < 120_000));
  const activeProfile = profiles.data?.find((p) => p.id === active.data?.profileId) ?? null;
  return {
    game,
    view,
    installed: !!s?.runtime.installed,
    running,
    canPlay: !!s && !running && !s.steamUpdating,
    busy: launching > 0 || starting,
    starting,
    elapsed: running && since ? Math.max(0, Math.round((now - since) / 1000)) : null,
    stuck: kilnMissing || (!!s?.playOncePending && !running && !!at && Date.now() - at > 120_000),
    kilnMissing,
    profileName: activeProfile?.name ?? null,
    play: (mode, profileId) => launch.mutate({ mode, profileId: profileId ?? null }),
  };
}

/** "03:12", "1:04:09". */
export function clock(sec: number): string {
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
  const mm = String(m).padStart(2, "0"), ss = String(s).padStart(2, "0");
  return h ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}

/** Re-renders every `ms` (0: never). */
export function useNow(ms: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!ms) return;
    const t = setInterval(() => setNow(Date.now()), ms);
    return () => clearInterval(t);
  }, [ms]);
  return now;
}
