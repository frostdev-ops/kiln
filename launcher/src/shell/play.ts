// The Play control shared by the mouth (the shell's Play button and pilot light) and the Play page: the game's state
// as one view, whether Play can start, and launch_play. Remembers how this launcher started each game this session
// (a vanilla start writes no boot.json). The elapsed time counts from the game process's real start (game status
// `started`), whoever started it; only without one from what this launcher saw.
import { useIsMutating, useMutation, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { create } from "zustand";
import { persist } from "zustand/middleware";
import { stopForProcess, stopKey } from "./stoppedStart";

const useStops = create(persist<{ notices: Record<string, api.StoppedStart>; dismissed: Record<string, string> }>(() => ({ notices: {}, dismissed: {} }), { name: "kiln-stopped-starts" }));
function clearStop(id: string) {
  const s = useStops.getState().notices[id];
  useStops.setState((m) => ({ notices: Object.fromEntries(Object.entries(m.notices).filter(([g]) => g !== id)), dismissed: s ? { ...m.dismissed, [id]: stopKey(s) } : m.dismissed }));
}
import * as api from "../api/commands";
import type { GameInstall, LaunchMode, StartTimeline } from "../api/commands";
import { invalidateGame, useActiveProfile, useGame, useGameStatus, useLaunchState, useProfiles, useRuntimeChanges, useStartTimeline } from "../api/hooks";
import { runTask, toast } from "../ui";
import { bootSnapshotBeforeOnce, cookBeforePlay, cookingNotice } from "./cook";
import { notifyLibraryChanged } from "./libraryChanged";
import { gameState, playState, runStarted, type GameStateView } from "./gameState";
import { notifyActivation, notifyActivationOf, notifyInstall } from "./notices";
import { quietIsKiln, quietText, showsProgress, STEAM_WAIT_MS } from "./startProgress";
import { t } from "../i18n";

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

/** A start this launcher asked Steam for (Play, a modpack offer's Launch): the window follows it until the game runs. */
export function rememberLaunch(gameId: string, mode: LaunchMode) {
  useLaunchMemo.setState((m) => ({ at: { ...m.at, [gameId]: playNow() }, mode: { ...m.mode, [gameId]: mode } }));
}

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
  /** What the start is doing (launch_progress) while it runs, from Play to the main menu; null when there is nothing to
   *  show (a start from Steam with a runtime that writes no progress.json, or the start is over). */
  timeline: StartTimeline | null;
  /** The last timeline whatever its phase (idle, failed, ended, legacy: said as what they are). */
  lastTimeline: StartTimeline | null;
  stoppedStart: api.StoppedStart | null;
  dismissStop: () => void;
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
  const stops = useStops();
  const evidence = launchState.data?.stoppedStart;
  useEffect(() => {
    if (evidence && stops.dismissed[id] !== stopKey(evidence)
      && (!stops.notices[id] || stopKey(stops.notices[id]) !== stopKey(evidence)))
      useStops.setState((m) => ({ notices: { ...m.notices, [id]: evidence } }));
  }, [id, evidence, stops.dismissed, stops.notices]);
  const savedStop = stops.notices[id] ?? null;
  const stoppedStart = stopForProcess(savedStop, status.data);
  useEffect(() => {
    if (savedStop && !stoppedStart) clearStop(id);
  }, [id, savedStop, stoppedStart]);
  const s = status.data;
  const running = !!s?.running;
  const at = memo.at[id];
  const process = running ? String(s?.pid ?? s?.started ?? "running") : null;
  const sameRun = !memo.process[id] || memo.process[id] === process;
  const now = useNow(running || at !== undefined ? 1000 : 0);
  const launching = useIsMutating({ mutationKey: ["launch_play"] });
  // a start Steam was asked for and no game came: over when the backend gives it up too (start_progress STEAM_WAIT_MS)
  const pending = at !== undefined && !running && now - at < STEAM_WAIT_MS;
  const state = playState(game ? s : null, launchState.data, sameRun ? { at, mode: memo.mode[id], seen: memo.since[id] } : {}, now, !!game && status.isLoading);
  // the window's own final decisions end the following too: a vanilla game, No Kiln (with no word from Kiln so far)
  const known = qc.getQueryData<StartTimeline | null>(["launch_progress", id]) ?? null;
  const heard = !!known && ["booting", "loading", "quiet", "ready", "idle", "failed"].includes(known.phase);
  const decided = running && (state.view.phase === "vanilla" || (state.kilnMissing && !heard));
  const following = !!game && !decided && (running || launching > 0 || pending);
  const tlq = useStartTimeline(id, following, running);
  // an answer of a start that is not followed any more (refused, given up, decided, the game gone) is not shown
  const tl = following ? tlq.data ?? null : null;
  // the runtime says what it does (progress.json): Kiln is loaded, and "Running" waits for the main menu (ready)
  const spoke = !!tl && ["booting", "loading", "quiet", "ready", "idle", "failed"].includes(tl.phase);
  const kilnMissing = state.kilnMissing && !spoke;
  // the states that are not a start going well win over the timeline, as before it: Steam updating, vanilla, No Kiln
  // (the proxy did not load: play-once still there, or no boot and no word from Kiln a minute in)
  const exception = !!s?.steamUpdating || (running && (state.view.phase === "vanilla" || kilnMissing));
  const progress = !stoppedStart && !exception && showsProgress(tl) ? tl : null;
  const running_ = !!progress && running;
  const quiet = !exception && running && tl?.phase === "quiet" ? tl : null;
  const view: GameStateView = stoppedStart ? { phase: running ? "running" : "stopped", label: t("shell.play.stoppedStart"), word: "stoppedStart", tone: "warn", pulse: false, detail: stoppedStart.reason } : exception ? state.view
    : running_ ? { phase: "starting", label: t("shell.gameState.starting"), word: "starting", tone: "ember", pulse: true, detail: progress!.running != null ? progress!.steps[progress!.running].label : t("shell.play.waitingForKiln") }
    : quiet ? { phase: "running", label: t("shell.play.noWord"), word: "noWord", tone: "warn", pulse: true, detail: quietText(quiet), detailKiln: quietIsKiln(quiet) }
    : tl?.phase === "idle" && running && state.view.word !== "safeIdle" ? { phase: "running", label: t("shell.gameState.safeIdle"), word: "safeIdle", tone: "warn", pulse: true, detail: tl.why ?? t("shell.play.hookedNothing"), detailKiln: tl.why == null ? true : undefined }
    : tl?.phase === "failed" && running ? { phase: "running", label: t("shell.play.kilnStopped"), word: "kilnStopped", tone: "warn", pulse: true, detail: tl.why ?? t("shell.play.couldNotGoOn"), detailKiln: tl.why == null ? true : undefined }
    : state.kilnMissing && spoke ? gameState(game ? s : null, launchState.data, false, now) : state.view;
  const launch = useMutation({
    mutationKey: ["launch_play"],
    // Mods with a cook recipe are cooked first when they need it (a start with the profile only), then the boot
    // game-data step runs (also for a single start with another profile). Neither stops the start: what could not be
    // done is said in a notice, and the game starts all the same.
    // the game travels with the request: TanStack gives a pending mutation the options of the latest render, so a
    // closure's game id may be another game's by the time the request ends (another one selected meanwhile)
    mutationFn: async ({ gameId, req }: { gameId: string; req: api.LaunchRequest }) => {
      // the start is timed from here: its own steps (cook, profile, Steam), then what the game's Kiln says
      clearStop(gameId);
      qc.setQueryData(["launch_state", gameId], (old: api.BootState | undefined) => old ? { ...old, stoppedStart: null } : old);
      await api.launchBegin(gameId, req.mode).catch(() => {});
      void qc.invalidateQueries({ queryKey: ["launch_progress", gameId] });
      if (await switchBackFirst(gameId, req)) invalidateGame(qc, gameId);
      await beforeStart(gameId, req);
      return api.launchPlay(gameId, req);
    },
    onSuccess: (r, { gameId }) => {
      rememberLaunch(gameId, r.mode);
      invalidateGame(qc, gameId);
      toast.ok(r.mode === "vanilla" ? t("shell.play.startingVanilla") : r.mode === "no-mods" ? t("shell.play.startingNoMods") : t("shell.gameState.starting"),
        r.backup ? t("shell.play.savesBackedUp") : t("shell.play.throughSteam"));
      notifyActivation(r.activation);
    },
    // the re-apply before the start may have kept copies even when the start then failed; a mod changed in its Library
    // folder is named, with "Use the changed files" and Play again
    onError: (e, vars) => {
      // Play ended before the game existed: the start the launcher was timing is over (the window as before Play)
      void api.launchEnd(vars.gameId).catch(() => {}).then(() => qc.invalidateQueries({ queryKey: ["launch_progress", vars.gameId] }));
      notifyActivationOf(e); if (!notifyLibraryChanged(qc, e, () => launch.mutate(vars))) toast.error(e);
    },
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
    } else if (!running && !since && memo.at[id] !== undefined && !pending && launching === 0) {
      // Steam was asked and no game came within the wait (the backend gave the start up too): Play again
      useLaunchMemo.setState((m) => {
        const drop = <T,>(r: Record<string, T>) => { const n = { ...r }; delete n[id]; return n; };
        return { at: drop(m.at), mode: drop(m.mode) };
      });
    } else if (!running && since) {
      // the run this launcher saw has ended: its launch (time, mode) is over too, so a later start from Steam is not
      // taken for it
      useLaunchMemo.setState((m) => {
        const drop = <T,>(r: Record<string, T>) => { const n = { ...r }; delete n[id]; return n; };
        return { since: drop(m.since), at: drop(m.at), mode: drop(m.mode), process: drop(m.process) };
      });
    }
  }, [id, running, process, sameRun, pending, launching, memo.at]);
  const since = runStarted(s, now, memo.since[id]);
  const starting = stoppedStart ? false : exception ? state.starting && !s?.steamUpdating
    : (state.starting && !(spoke && !progress)) || running_ || (!running && !s?.steamUpdating && (launching > 0 || (!!progress && pending) || (at !== undefined && now - at < 120_000)));
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
    timeline: progress,
    lastTimeline: tl,
    stoppedStart,
    dismissStop: () => clearStop(id),
    profileName: activeProfile?.name ?? null,
    play: (mode, profileId) => launch.mutate({ gameId: id, req: { mode, profileId: profileId ?? null } }),
  };
}

/** (dev.29) After a switch back from a test build, the game's Kiln is the beta's and this launcher ships an older one
 *  (InstallState.switchBack): a start with Kiln (not vanilla) installs the launcher's own first, through the ordinary
 *  install (its receipt, its backup, its refusals: a running game, Steam updating, a folder that is not Kiln's). When it
 *  cannot, the start goes on with the Kiln the game has and says why. Resolves true when it installed. */
export async function switchBackFirst(gameId: string, req: api.LaunchRequest): Promise<boolean> {
  if (req.mode === "vanilla") return false;
  const back = await api.gameStatus(gameId).then((s) => s.runtime.switchBack ?? null).catch(() => null);
  if (!back) return false;
  try {
    const r = await runTask<api.InstallResult>(() => api.runtimeInstall(gameId, back.to, {}), {
      done: t("shell.play.kilnInstalled", { version: back.to }), detail: () => t("shell.play.switchBackDetail", { from: back.from }), quiet: true,
    });
    notifyInstall(r);
    return true;
  } catch (e) {
    toast.error(e, t("shell.play.switchBackFailed", { to: back.to, from: back.from }));
    return false;
  }
}

/** What a start does before launch_play, by its mode: with the profile, its cook and then the boot game-data step
 *  (cook_run); a single start with another profile (`once`), only the boot game-data step for that profile's mods
 *  (boot_snapshot_run: it cooks nothing); vanilla and without mods, nothing. Resolves whatever happened: neither step
 *  stops a start. */
export async function beforeStart(gameId: string, req: api.LaunchRequest): Promise<void> {
  if (req.mode === "profile") await cookBeforePlay(gameId, cookingNotice);
  else if (req.mode === "once" && req.profileId) await bootSnapshotBeforeOnce(gameId, req.profileId);
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
    const timer = setInterval(() => setNow(playNow()), ms);
    return () => clearInterval(timer);
  }, [ms]);
  return now;
}
