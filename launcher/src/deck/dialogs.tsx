// Deck dialogs shared by the screens: switch profile, Install Kiln, play once, find the game folder, a modpack's
// consent. Each is a layer: B closes it, focus returns to what opened it.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState, type RefObject } from "react";
import * as api from "../api/commands";
import type { DeckFolder, GameInstall } from "../api/commands";
import { invalidateGame, qk, showActive, useActiveProfile, useGameSel, useGameStatus, useProfiles, useRuntimeChanges } from "../api/hooks";
import { toast, runTask } from "../ui";
import { totalCount } from "../api/kinds";
import { useKilnInstall } from "../shell/firstRun";
import { installBundle } from "../shell/runtimeChange";
import { focusEl, focusFirst } from "./focus";
import { useLayer } from "./layers";
import { DeckButton, Dialog, Overlay, Row, Word, dialogFirst, useFirstFocus, useReturnFocus } from "./ui";
import { kilnLabel } from "./status";

/** The profiles: A makes one active (Steam's Play button uses it too). */
export function ProfileDialog({ game, onClose }: { game: GameInstall; onClose: () => void }) {
  const qc = useQueryClient();
  const profiles = useProfiles();
  const active = useActiveProfile(game.id);
  const status = useGameStatus(game.id);
  const running = !!status.data?.running;
  const changing = useRuntimeChanges((s) => s.last[game.id]?.state === "running");
  const blocked = !status.data || running || !!status.data.steamUpdating || changing;
  const cur = active.data?.profileId ?? "";
  const list = profiles.data ?? [];
  const activate = useMutation({
    mutationFn: (pid: string) => (pid ? api.profileActivate(pid, game.id) : api.profileDeactivate(game.id).then(() => null)),
    onMutate: (pid) => showActive(qc, game.id, pid || null),
    onSuccess: (r, pid) => {
      qc.setQueryData(qk.active(game.id), r);
      invalidateGame(qc, game.id);
      toast.ok(pid ? list.find((p) => p.id === pid)?.name ?? pid : "No profile");
    },
    onError: (e, _pid, undo) => { undo?.(); toast.error(e); },
  });
  const pick = (pid: string) => { onClose(); if (pid !== cur) activate.mutate(pid); };
  return (
    <Dialog name="profiles" title="Profile" onClose={onClose} wide>
      <div className="deck-list">
        {list.map((p) => {
          const n = totalCount(p.counts);
          return (
            <Row key={p.id} id={`profile-${p.id}`} title={p.name} end={p.id === cur ? <Word tone="ember">Active</Word> : `${n} mod${n === 1 ? "" : "s"}`}
              disabled={blocked || activate.isPending} a="Use" onClick={() => pick(p.id)} />
          );
        })}
        <Row id="profile-none" title="No profile" sub="The mods in the game folder" end={!cur ? <Word tone="ember">Active</Word> : undefined} disabled={blocked || activate.isPending} a="Use" onClick={() => pick("")} />
      </div>
      {running && <p className="deck-note">Close the game to switch.</p>}
    </Dialog>
  );
}

/** Install Kiln (the bundle the launcher ships), then "My mods" for a first-time player. */
export function InstallDialog({ game, onClose, update }: { game: GameInstall; onClose: () => void; update?: boolean }) {
  const qc = useQueryClient();
  const k = useKilnInstall(game);
  const status = useGameStatus(game.id);
  const running = !!status.data?.running;
  const current = status.data?.runtime.receipt?.runtime.version ?? null;
  const target = update ? (k.bundles.find((b) => b.version === status.data?.runtime.upgradeAvailable) ?? k.bundle) : k.bundle;
  const go = async () => {
    if (!target) return;
    onClose();
    const ok = await installBundle(qc, game, target, k.bundles, current);
    if (!ok || update) return;
    const installed = await qc.fetchQuery({ queryKey: qk.games, queryFn: api.gameList }).then((gs) => gs.find((g) => g.path === game.path) ?? game).catch(() => game);
    try {
      const a = await api.profileEnsureDefault(installed.id);
      if (a) qc.setQueryData(qk.active(installed.id), a);
      qc.invalidateQueries({ queryKey: qk.profiles });
    } catch (e) { toast.error(e, "Kiln installed, but the default profile could not be prepared"); }
  };
  return (
    <Dialog name="install" title={update ? "Update Kiln?" : "Install Kiln?"} onClose={onClose}
      buttons={<>
        <DeckButton onClick={onClose}>Cancel</DeckButton>
        <DeckButton primary disabled={!target || !status.data || running || k.installing || !!status.data?.steamUpdating} onClick={() => void go()}>{update ? "Update" : "Install"}</DeckButton>
      </>}>
      {target ? <span className="deck-mono">{kilnLabel(target.version)}</span> : !k.loading && <span>No Kiln runtime in this launcher.</span>}
      {running && <p className="deck-note">Close the game first.</p>}
    </Dialog>
  );
}

/** Play once without the profile: vanilla (no Kiln) or Kiln without mods. */
export function PlayOnceDialog({ onClose, onPlay }: { onClose: () => void; onPlay: (mode: "vanilla" | "no-mods") => void }) {
  return (
    <Dialog name="play-once" title="Play once" onClose={onClose}>
      <div className="deck-list">
        <Row id="once-nomods" title="Without mods" sub="Kiln runs, no mods load" a="Play" onClick={() => { onClose(); onPlay("no-mods"); }} />
        <Row id="once-vanilla" title="Vanilla" sub="No Kiln. Use another character." a="Play" onClick={() => { onClose(); onPlay("vanilla"); }} />
      </div>
    </Dialog>
  );
}

// ---------------------------------------------------------------- folders

/** Browsing history over deck_browse: `open` goes in, `up` goes back the way it came (else to the parent). Once the
 *  new folder shows, focus goes to its first stop, or (going up) to the folder it came from. */
export function useBrowse(start: string | null, scope: RefObject<HTMLElement | null>) {
  const [stack, setStack] = useState<(string | null)[]>([start]);
  const from = useRef<string | null>(null);
  const dir = stack[stack.length - 1];
  const q = useQuery({ queryKey: ["deck_browse", dir], queryFn: () => api.deckBrowse(dir), retry: false });
  const shown = q.data?.dir ?? null;
  const last = useRef<string | null>(null);
  useEffect(() => {
    if (!shown || shown === last.current) return;
    const first = last.current === null;
    last.current = shown;
    if (first) return; // the screen's own first focus
    const back = from.current;
    from.current = null;
    requestAnimationFrame(() => {
      const el = scope.current;
      if (!el) return;
      const row = back ? el.querySelector<HTMLElement>(`[data-deck-path="${CSS.escape(back)}"]`) : null;
      if (row) focusEl(row); else focusFirst(el);
    });
  }, [shown, scope]);
  return {
    dir,
    q,
    depth: stack.length - 1,
    open: (d: string) => { from.current = null; setStack((s) => [...s, d]); },
    jump: (d: string) => { from.current = null; setStack([start, d]); },
    up: (): boolean => {
      from.current = q.data?.dir ?? null;
      if (stack.length > 1) { setStack((s) => s.slice(0, -1)); return true; }
      const parent = q.data?.parent;
      if (parent && q.data?.dir !== parent) { setStack([parent]); return true; }
      from.current = null;
      return false;
    },
    reset: () => setStack([start]),
  };
}

/** Find the game by hand: browse to the folder with enshrouded.exe, then "Use this folder". */
export function FindGameDialog({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient();
  const setGameId = useGameSel((s) => s.setGameId);
  const ref = useRef<HTMLDivElement>(null);
  const b = useBrowse(null, ref);
  const add = useMutation({
    mutationFn: (path: string) => api.gameAdd(path),
    onSuccess: (g) => { qc.invalidateQueries({ queryKey: qk.games }); setGameId(g.id); toast.ok("Game found"); onClose(); },
    onError: (e) => toast.error(e),
  });
  const opener = useRef<HTMLElement | null>(document.activeElement as HTMLElement | null);
  useFirstFocus(ref, null, dialogFirst);
  useReturnFocus(opener);
  useLayer(ref, { modal: true, backLabel: "Up", back: () => { if (!b.up()) onClose(); },
    actions: [{ button: "x", label: "Close", run: onClose }] });
  const f = b.q.data;
  return (
    <Overlay name="find-game">
      <div ref={ref} className="deck-dialog wide tall" role="dialog" aria-modal="true" aria-label="Find the game folder">
        <h2 className="deck-dialog-title">Find Enshrouded</h2>
        <FolderView f={f} loading={b.q.isLoading} error={b.q.error} mode="game"
          onOpen={(d) => b.open(d)} onPlace={(d) => b.jump(d)}
          onUse={() => f && add.mutate(f.dir)} busy={add.isPending} />
      </div>
    </Overlay>
  );
}

/** A folder: in `game` mode its sub-folders and "Use this folder" when enshrouded.exe is there; in `mods` mode the
 *  mods first (the screen draws them), then the sub-folders. */
export function FolderView({ f, loading, error, mode, onOpen, onPlace, onUse, busy }: {
  f: DeckFolder | undefined; loading: boolean; error: unknown; mode: "game" | "mods";
  onOpen: (dir: string) => void; onPlace: (dir: string) => void; onUse?: () => void; busy?: boolean;
}) {
  if (loading) return <p className="deck-note">Reading…</p>;
  if (error || !f) return <p className="deck-note">{api.toKilnError(error).message}</p>;
  return (
    <div className="deck-folder">
      <div className="deck-places" role="group" aria-label="Places">
        {f.places.filter((p) => mode === "game" || p.kind !== "games").map((p) => (
          <DeckButton key={p.path} id={`place-${p.kind}-${p.name}`} className={p.path === f.dir ? "on" : ""} a="Open" onClick={() => onPlace(p.path)}>{p.name}</DeckButton>
        ))}
      </div>
      <p className="deck-path deck-mono" title={f.dir}>{f.dir}</p>
      {mode === "game" && f.hasGame && (
        <DeckButton primary autoFocus id="use-folder" disabled={busy} onClick={onUse}>Use this folder</DeckButton>
      )}
      <div className="deck-list">
        {f.folders.map((d) => <Row key={d.path} id={`dir-${d.name}`} path={d.path} icon={<FolderIcon />} title={d.name} a="Open" onClick={() => onOpen(d.path)} />)}
        {mode === "game" && !f.folders.length && !f.hasGame && <p className="deck-note">No folders here.</p>}
      </div>
    </div>
  );
}

export function FolderIcon() {
  return <svg viewBox="0 0 24 24" width="1em" height="1em" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round"><path d="M3 6.5h6l2 2h10v10.5H3z" /></svg>;
}

/** A modpack zip: what it is, "runs code on your PC", Install. */
export function ModpackDialog({ game, path, onClose }: { game: GameInstall; path: string; onClose: () => void }) {
  const qc = useQueryClient();
  const status = useGameStatus(game.id);
  const preview = useQuery({ queryKey: ["modpack_inspect", game.id, path], queryFn: () => api.modpackInspect(game.id, { path }), retry: false });
  const [busy, setBusy] = useState(false);
  const p = preview.data;
  const install = async () => {
    setBusy(true);
    try {
      const r = await runTask<api.ModpackInstallResult>(() => api.modpackInstall(game.id, { source: { path }, consent: true, activate: true }), { done: "Modpack installed" });
      invalidateGame(qc, game.id);
      qc.invalidateQueries({ queryKey: ["library_list"] });
      toast.ok(`${r.profile.name} is active`);
      onClose();
    } catch { /* toasted */ } finally { setBusy(false); }
  };
  const name = p?.manifest ? `${p.manifest.name} ${p.manifest.version}` : path.split(/[\\/]/).pop();
  return (
    <Dialog name="modpack" title="Install this modpack?" onClose={onClose} locked={busy}
      buttons={<>
        <DeckButton onClick={onClose} disabled={busy}>Cancel</DeckButton>
        <DeckButton primary disabled={busy || !p || !!preview.error || !status.data || status.data.running || status.data.steamUpdating} onClick={() => void install()}>Install</DeckButton>
      </>}>
      <span>{name}</span>
      <Word tone="warn">It runs code on your PC</Word>
      {status.data?.running && <p className="deck-note">Close the game first.</p>}
      {preview.error ? <span className="deck-note">{api.toKilnError(preview.error).message}</span> : null}
    </Dialog>
  );
}

/** Quit the launcher. */
export function QuitDialog({ onClose }: { onClose: () => void }) {
  return (
    <Dialog name="quit" title="Quit Kiln?" onClose={onClose}
      buttons={<>
        <DeckButton onClick={onClose}>Cancel</DeckButton>
        <DeckButton primary onClick={() => { onClose(); if (api.isTauri()) void api.deckQuit(); else toast.info("Quit"); }}>Quit</DeckButton>
      </>} />
  );
}
