// The Play control shared by the mouth (the shell's Play button and pilot light) and the Play page: the game's state
// as one view, whether Play can start, and launch_play. Remembers how this launcher started each game this session
// (a vanilla start writes no boot.json). The elapsed time counts from the game process's real start (game status
// `started`), whoever started it; only without one from what this launcher saw.
import { useIsMutating, useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { create } from "zustand";
import * as api from "../api/commands";
import type { GameInstall, LaunchMode } from "../api/commands";
import { invalidateGame, useActiveProfile, useGame, useGameStatus, useLaunchState, useProfiles, useRuntimeChanges } from "../api/hooks";
import { toast } from "../ui";
import { playState, runStarted, type GameStateView } from "./gameState";

// A shared monotonic clock: changing the system clock while a start is pending cannot restart its grace period.
const epoch = Date.now(), tick = performance.now();
const playNow = () => epoch + performance.now() - tick;

interface LaunchMemo {
  at: Record<string, number>;
  mode: Record<string, LaunchMode>;
  /** When the game was first seen running, per game (the clock's fallback when the status has no start time). */
  since: Record<string, number>;
  process: Record<string, string>;
}
export const useLaunchMemo = create<LaunchMemo>(() => ({ at: {}, mode: {}, since: {}, process: {} }));

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
  const changingRuntime = useRuntimeChanges((m) => !!game && m.last[game.id]?.state === "running");
  const memo = useLaunchMemo();
  const id = game?.id ?? "";
  const s = status.data;
  const running = !!s?.running;
  const at = memo.at[id];
  const process = running ? String(s?.pid ?? s?.started ?? "running") : null;
  const sameRun = !memo.process[id] || memo.process[id] === process;
  const now = useNow(running || at !== undefined ? 1000 : 0);
  const state = playState(game ? s : null, launchState.data, sameRun ? { at, mode: memo.mode[id], seen: memo.since[id] } : {}, now, !!game && status.isLoading);
  const { view, kilnMissing } = state;
  const launch = useMutation({
    mutationKey: ["launch_play"],
    mutationFn: (req: api.LaunchRequest) => api.launchPlay(id, req),
    onSuccess: (r) => {
      useLaunchMemo.setState((m) => ({ at: { ...m.at, [id]: playNow() }, mode: { ...m.mode, [id]: r.mode } }));
      invalidateGame(qc, id);
      toast.ok(r.mode === "vanilla" ? "Starting vanilla" : r.mode === "no-mods" ? "Starting without mods" : "Starting", r.backup ? "Saves backed up first." : "Through Steam.");
    },
    onError: (e) => toast.error(e),
  });

  // Missing or skewed process timestamps fall back to when this session first saw it running. The launch request
  // can precede the actual process by minutes while Steam starts or prepares Proton.
  useEffect(() => {
    if (!id) return;
    const since = useLaunchMemo.getState().since[id];
    if (running && process && (!since || !sameRun)) {
      useLaunchMemo.setState((m) => ({
        since: { ...m.since, [id]: playNow() }, process: { ...m.process, [id]: process },
        ...(!sameRun ? { at: Object.fromEntries(Object.entries(m.at).filter(([g]) => g !== id)), mode: Object.fromEntries(Object.entries(m.mode).filter(([g]) => g !== id)) } : {}),
      }));
    } else if (!running && since) {
      // the run this launcher saw has ended: its launch (time, mode) is over too, so a later start from Steam is not
      // taken for it
      useLaunchMemo.setState((m) => {
        const drop = <T,>(r: Record<string, T>) => { const n = { ...r }; delete n[id]; return n; };
        return { since: drop(m.since), at: drop(m.at), mode: drop(m.mode), process: drop(m.process) };
      });
    }
  }, [id, running, process, sameRun]);
  const launching = useIsMutating({ mutationKey: ["launch_play"] });
  const since = runStarted(s, now, memo.since[id]);
  const starting = state.starting || (!running && !s?.steamUpdating && (launching > 0 || (at !== undefined && now - at < 120_000)));
  const activeProfile = profiles.data?.find((p) => p.id === active.data?.profileId) ?? null;
  return {
    game,
    view,
    installed: !!s?.runtime.installed,
    running,
    canPlay: !!s && !running && !s.steamUpdating && launching === 0 && !starting && !changingRuntime,
    busy: launching > 0 || starting,
    starting,
    elapsed: running && since ? Math.max(0, Math.round((now - since) / 1000)) : null,
    stuck: kilnMissing || (!!s?.playOncePending && !running && memo.mode[id] !== "vanilla" && at !== undefined && now - at > 120_000),
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
  const [now, setNow] = useState(playNow);
  useEffect(() => {
    if (!ms) return;
    setNow(playNow());
    const t = setInterval(() => setNow(playNow()), ms);
    return () => clearInterval(t);
  }, [ms]);
  return now;
}
