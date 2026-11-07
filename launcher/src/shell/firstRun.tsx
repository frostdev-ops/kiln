// The first run, kept short for an ordinary player: the launcher looks for the game by itself (once per start, when it
// knows no client), and Install Kiln happens right on Play (one confirm, the same install task as Runtime). After the
// install, "My mods" is made and activated when there is no profile at all (profile_ensure_default), and a mod
// dropped before the install (its toast's Install Kiln) is added then.
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { useEffect } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { create } from "zustand";
import * as api from "../api/commands";
import type { GameInstall, RuntimeBundle } from "../api/commands";
import { qk, useGame, useGames, useGameStatus, useRuntimeChanges } from "../api/hooks";
import { Button, Modal, toast } from "../ui";
import { installBundle } from "./runtimeChange";

// ---------------------------------------------------------------- finding the game

interface DetectState { busy: boolean; tried: boolean }
/** game_detect in flight (automatic or a Detect press), and whether this start already looked once by itself. */
export const useDetect = create<DetectState>(() => ({ busy: false, tried: false }));

/** Runs game_detect; `quiet` (the automatic run) toasts nothing when it finds nothing: Play says so. */
export async function detectGames(qc: QueryClient, quiet = false): Promise<GameInstall[] | null> {
  useDetect.setState({ busy: true });
  try {
    const g = await api.gameDetect();
    qc.setQueryData(qk.games, g);
    if (!quiet && !g.some((x) => x.kind === "client")) toast.info("No Enshrouded found", "Add its folder by hand.");
    return g;
  } catch (e) {
    if (!quiet) toast.error(e);
    return null;
  } finally {
    useDetect.setState({ busy: false, tried: true });
  }
}

/** Mount once (the shell): when no client is known, look for one by itself, once per start. */
export function useAutoDetect() {
  const qc = useQueryClient();
  const games = useGames();
  const known = games.data?.some((g) => g.kind === "client") ?? false;
  useEffect(() => {
    if ((!games.isSuccess && !games.isError) || known || useDetect.getState().tried || useDetect.getState().busy) return;
    void detectGames(qc, true);
  }, [games.isSuccess, games.isError, known, qc]);
}

// ---------------------------------------------------------------- Install Kiln on Play

interface InstallPrompt {
  open: boolean;
  /** Paths dropped before Kiln was in the game: added once the install is done. */
  pending: { game: GameInstall; paths: string[] } | null;
  show: (pending?: { game: GameInstall; paths: string[] } | null) => void;
  close: () => void;
}
export const useInstallPrompt = create<InstallPrompt>((set) => ({
  open: false,
  pending: null,
  show: (pending = null) => set({ open: true, pending }),
  close: () => set({ open: false }),
}));

/** What Play's Install Kiln needs: the bundle it puts in (the newest for a client: the one the launcher ships), whether
 *  an install runs, and whether the game has ShroudForge (that switch stays on Runtime: it is typed). */
export function useKilnInstall(game: GameInstall | null) {
  const status = useGameStatus(game?.id);
  const bundles = useQuery({ queryKey: qk.runtimeList, queryFn: api.runtimeList, enabled: !!game });
  const change = useRuntimeChanges((s) => (game ? s.last[game.id] : undefined));
  const list = bundles.data ?? [];
  const bundle: RuntimeBundle | null = list.find((b) => b.sides.includes("client")) ?? null;
  return {
    bundle,
    bundles: list,
    loading: bundles.isLoading,
    installing: change?.state === "running",
    change,
    shroudforge: status.data?.runtime.proxy?.kind === "shroudforge",
  };
}

/** The confirm (mounted once in the shell; the mouth, Play and a drop's toast open it through useInstallPrompt). */
export function InstallKilnModal({ onAdd }: { onAdd: (paths: string[], game: GameInstall) => void }) {
  const qc = useQueryClient();
  const nav = useNavigate();
  const loc = useLocation();
  const selected = useGame();
  const { open, close, pending } = useInstallPrompt();
  const game = pending?.game ?? selected.game;
  const k = useKilnInstall(game);
  const status = useGameStatus(game?.id);
  if (!open || !game) return null;
  const running = !!status.data?.running;
  const go = async () => {
    const b = k.bundle;
    if (!b) return;
    close();
    if (loc.pathname !== "/") nav("/");
    const ok = await installBundle(qc, game, b, k.bundles, null);
    if (!ok) {
      if (pending) toast.info("Dropped mods are still waiting", undefined, { actions: [{ label: "Retry", onClick: () => useInstallPrompt.getState().show(pending) }] });
      return;
    }
    const installed = await qc.fetchQuery({ queryKey: qk.games, queryFn: api.gameList }).then((gs) => gs.find((g) => g.path === game.path) ?? game).catch(() => game);
    // a first-time player gets "My mods" now, so Play names it and a dropped mod has somewhere to go
    try {
      const a = await api.profileEnsureDefault(installed.id);
      if (a) qc.setQueryData(qk.active(installed.id), a);
      qc.invalidateQueries({ queryKey: qk.profiles });
    } catch (e) { toast.error(e, "Kiln installed, but the default profile could not be prepared"); }
    if (useInstallPrompt.getState().pending === pending) useInstallPrompt.setState({ pending: null });
    if (pending?.paths.length) onAdd(pending.paths, installed);
  };
  const cancel = () => { useInstallPrompt.setState({ open: false, pending: null }); };
  return (
    <Modal open onClose={cancel} title="Install Kiln" width={480}
      footer={k.bundle ? (
        <><Button variant="ghost" onClick={cancel}>Cancel</Button>
          <Button variant="primary" disabled={!status.data || running || k.installing || !!status.data?.steamUpdating} onClick={go}>Install</Button></>
      ) : (
        <><Button variant="ghost" onClick={cancel}>Cancel</Button><Button onClick={() => { cancel(); nav("/runtime"); }}>Runtime</Button></>
      )}>
      {k.loading ? null : k.bundle ? (
        <span className="k-mono k-muted">{k.bundle.version}</span>
      ) : <span>No Kiln runtime in this launcher.</span>}
      {running && <span className="k-hint">Close the game first.</span>}
    </Modal>
  );
}
