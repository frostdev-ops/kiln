// Drop a mod anywhere on the window: library_add imports it, adds it to the game's active profile (enabled; "My mods"
// for a first-time player) and writes the game's profile.json, so the next Play uses it. The toast is the mod's name
// and Undo (profile_revert: out of the profile again, the library copy stays); missing requirements are named in one
// line. Modpack zips go through the consent dialog (modpack_install). Settings "Add new mods to the active profile"
// off: drops are only imported into the library. Drops on a DropZone (Library's niche, Runtime's row) are its own.
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { ShieldAlert } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { create } from "zustand";
import * as api from "../api/commands";
import type { AddReport, GameInstall, GameStatus, ImportReport, LauncherSettings, ModpackInstallResult } from "../api/commands";
import { invalidateGame, qk, useActiveProfile, useGame, useGameStatus, useProfiles, useSettings } from "../api/hooks";
import { Banner, Button, KeyValue, Modal, Skeleton, Toggle, errorText, runTask, toast } from "../ui";
import { useInstallPrompt } from "./firstRun";
import { detailsAction, leftOutLine, names as nameList, summarize, usePickProfile } from "./importResult";
import { notifyActivation, notifyAdd } from "./notices";

/** "Night Owl", "Night Owl and Dawn Lamp", "3 mods". */
function names(m: { name: string }[]): string {
  return nameList(m.map((x) => x.name));
}

/** A modpack zip dropped on the window, waiting for the player's yes. */
export const useDropModpack = create<{ offers: { game: GameInstall; path: string }[] }>(() => ({ offers: [] }));
function offerModpacks(game: GameInstall, paths: string[]) {
  useDropModpack.setState((s) => ({ offers: [...s.offers, ...paths.filter((path) => !s.offers.some((o) => o.game.id === game.id && o.path === path)).map((path) => ({ game, path }))] }));
}

const addQueues = new WeakMap<QueryClient, Promise<unknown>>();
const pendingAdds = new WeakMap<QueryClient, Map<string, Promise<AddReport | null>>>();

/** The drop's work, shared by the window, Library's niche and a drop made before Kiln was installed. Resolves with the
 *  report (null when nothing ran). */
export function useAddMods(): (paths: string[], target?: GameInstall) => Promise<AddReport | null> {
  const qc = useQueryClient();
  const nav = useNavigate();
  const { game } = useGame();
  const status = useGameStatus(game?.id);
  const settings = useSettings();
  const ref = useRef({ game, status: status.data, settings: settings.data });
  ref.current = { game, status: status.data, settings: settings.data };

  const refresh = (profileId: string | null, gameId?: string) => {
    qc.invalidateQueries({ queryKey: ["library_list"] });
    qc.invalidateQueries({ queryKey: ["profile_resolve"] });
    if (profileId) qc.invalidateQueries({ queryKey: qk.profile(profileId) });
    if (gameId) invalidateGame(qc, gameId);
  };

  const undo = async (r: AddReport, gameId: string) => {
    try {
      const p = await api.profileRevert(r.profileId!, r.changes);
      refresh(r.profileId, gameId);
      const kept = r.changes.filter((c) => {
        const m = p.mods[c.kind].find((m) => m.id === c.after.id);
        return c.before ? !m || m.version !== c.before.version || m.hash !== c.before.hash || m.enabled !== c.before.enabled : !!m;
      });
      if (kept.length) { toast.info("Undo kept later changes", `${kept.length} mod${kept.length === 1 ? " was" : "s were"} changed since this add.`); return; }
      // new entries leave the profile; one that replaced another version (or was switched on) goes back to that
      toast.ok(r.changes.every((c) => !c.before) ? `Removed ${names(r.added)}` : "Undone");
    } catch (e) { toast.error(e, "Undo failed"); }
  };

  const importOnly = async (paths: string[], game: GameInstall | null) => {
    try {
      const detected = await Promise.all(paths.map((p) => api.libraryDetect(p)));
      const packs = detected.filter((d) => d.kind === "modpack" && /\.zip$/i.test(d.path)).map((d) => d.path);
      if (packs.length && game) offerModpacks(game, packs);
      else if (packs.length) toast.info("No game yet", "Find Enshrouded on Play before installing a modpack.");
      const files = paths.filter((p) => !packs.includes(p));
      if (!files.length) return null;
      const r = await runTask<ImportReport>(() => api.libraryImport(files), { quiet: true });
      refresh(null);
      const sum = summarize(r);
      const more = detailsAction({ heading: "Imported into the Library", summary: sum });
      if (sum.taken.length) toast.ok(`Imported ${nameList(sum.taken)}`, leftOutLine(sum), { actions: [{ label: "Library", onClick: () => nav("/library") }, ...more] });
      else toast.info("Nothing to import", leftOutLine(sum), { actions: more });
    } catch (e) { toast.error(e, "Import failed"); }
    return null;
  };

  /** After the player picked a profile for a mod that only reached the Library. */
  const pickedAdd = (r: AddReport, game: GameInstall) => {
    refresh(r.profileId, game.id);
    notifyAdd(r); // what writing the picked profile again kept in the game folder, and what else changed in it
    if (r.added.length) {
      toast.ok(`Added ${names(r.added)} to ${r.profileName ?? "the profile"}`, r.applied ? undefined : `${r.profileName ?? "It"} is not the active profile: turn it on on Play.`,
        { actions: [{ label: "Undo", onClick: () => void undo(r, game.id) }] });
    } else toast.info(`Already in ${r.profileName ?? "the profile"}`);
  };

  const add = async (paths: string[], game: GameInstall | null) => {
    // the cache as it is now, not as it was at the last render (an add right after Install Kiln)
    const status = (game && qc.getQueryData<GameStatus>(qk.gameStatus(game.id))) || (game?.id === ref.current.game?.id ? ref.current.status : undefined);
    const settings = qc.getQueryData<LauncherSettings>(qk.settings) ?? ref.current.settings;
    if (!paths.length) return null;
    if (settings?.addToActiveProfile === false) return importOnly(paths, game);
    if (!game) { toast.info("No game yet", "Find Enshrouded on Play first."); return null; }
    if (status && !status.runtime.installed) {
      if (status.runtime.proxy?.kind === "shroudforge") toast.info("Kiln is not in this game", undefined, { actions: [{ label: "Runtime", onClick: () => nav("/runtime") }] });
      else toast.info("Install Kiln first", undefined, { actions: [{ label: "Install Kiln", onClick: () => useInstallPrompt.getState().show({ game, paths }) }] });
      return null;
    }
    let r: AddReport;
    try {
      r = await runTask<AddReport>(() => api.libraryAdd(game.id, paths), { failed: "Not added" });
    } catch { return null; }
    refresh(r.profileId, game.id);
    notifyAdd(r); // what writing the profile again kept in the game folder, and what else changed in it
    if (r.modpacks.length) offerModpacks(game, r.modpacks);
    const found = [...r.import.imported, ...r.import.existing];
    const sum = summarize(r.import);
    const detail = (heading: string) => ({ heading, summary: sum, libraryOnly: r.libraryOnly });
    if (r.added.length) {
      const more = [
        r.missing.length ? `Needs ${r.missing.map((m) => m.id).join(", ")}.` : "",
        r.resumed ? `${r.profileName ?? "Your profile"} is on again.` : "",
        r.running ? "Applies at the next start." : "",
        leftOutLine(sum) ?? "",
      ].filter(Boolean).join(" ");
      toast.ok(`Added ${names(r.added)}`, more || undefined, { actions: [{ label: "Undo", onClick: () => void undo(r, game.id) }, ...detailsAction(detail(`Added to ${r.profileName ?? "the profile"}`))] });
    } else if (r.already.length) {
      toast.info(`Already in ${r.profileName ?? "the profile"}`, leftOutLine(sum), { actions: detailsAction(detail("Already there")) });
    } else if (r.libraryOnly && found.length) {
      // no profile is active in this game and none is remembered: the mod waits in the Library, and the player can pick a profile
      const label = nameList(sum.taken);
      toast.info(`Imported ${label}`, "Only in your Library: no profile is active in this game.", {
        actions: [{ label: "Add to a profile", onClick: () => usePickProfile.setState({ pending: { game, entries: found, label, onAdded: pickedAdd } }) }, ...detailsAction(detail("Imported into the Library"))],
      });
    } else if (!r.modpacks.length) {
      toast.info("Nothing to add", leftOutLine(sum), { actions: detailsAction(detail("Nothing was added")) });
    }
    return r;
  };
  return (paths, target) => {
    const game = target ?? ref.current.game;
    const key = JSON.stringify([game?.id, [...new Set(paths)].sort()]);
    const pending = pendingAdds.get(qc) ?? new Map<string, Promise<AddReport | null>>();
    pendingAdds.set(qc, pending);
    const existing = pending.get(key);
    if (existing) return existing;
    const job = (addQueues.get(qc) ?? Promise.resolve()).then(() => add([...new Set(paths)], game));
    pending.set(key, job);
    addQueues.set(qc, job.catch(() => {}));
    void job.finally(() => pending.delete(key)).catch(() => {});
    return job;
  };
}

/** Where a drop at (x, y) belongs: not the window's while a dialog is open or over a DropZone (it handles its own). */
function windowTakes(el: Element | null): boolean {
  if (document.querySelector('[role="dialog"][aria-modal="true"]')) return false;
  return !el?.closest("[data-drop], .k-drop");
}

/** The window-wide drop target: a veil with the niche while files are dragged over the window; the drop goes to
 *  useAddMods. Mounted once in the shell. */
export function DropAnywhere({ add }: { add: (paths: string[]) => void }) {
  const { game } = useGame();
  const active = useActiveProfile(game?.id);
  const profiles = useProfiles();
  const settings = useSettings();
  const [over, setOver] = useState(false);
  const cb = useRef(add);
  cb.current = add;

  useEffect(() => {
    if (api.isTauri()) {
      let off: (() => void) | undefined;
      let live = true;
      const at = (p?: { x: number; y: number }) => (p ? document.elementFromPoint(p.x / window.devicePixelRatio, p.y / window.devicePixelRatio) : null);
      import("@tauri-apps/api/webview").then(({ getCurrentWebview }) =>
        getCurrentWebview().onDragDropEvent((e) => {
          if (!live) return;
          const p = e.payload;
          if (p.type === "enter" || p.type === "over") setOver(windowTakes(at(p.position)));
          else if (p.type === "drop") { setOver(false); if (p.paths.length && windowTakes(at(p.position))) cb.current(p.paths); }
          else setOver(false);
        })).then((f) => (live ? (off = f) : f())).catch((e) => { if (live) toast.error(e, "File drops unavailable"); });
      return () => { live = false; off?.(); };
    }
    // a plain browser (mock mode): HTML5 drag events; the file names stand in for paths
    let idle: ReturnType<typeof setTimeout> | undefined;
    const files = (e: DragEvent) => !!e.dataTransfer && [...e.dataTransfer.types].includes("Files");
    const dragover = (e: DragEvent) => {
      if (!files(e)) return;
      e.preventDefault();
      setOver(windowTakes(e.target as Element));
      clearTimeout(idle);
      idle = setTimeout(() => setOver(false), 400);
    };
    const leave = (e: DragEvent) => { if (!e.relatedTarget) setOver(false); };
    const drop = (e: DragEvent) => {
      setOver(false);
      if (!e.dataTransfer?.files.length) return;
      e.preventDefault();
      if (!windowTakes(e.target as Element)) return;
      cb.current([...e.dataTransfer.files].map((f) => f.name));
    };
    window.addEventListener("dragover", dragover);
    window.addEventListener("dragleave", leave);
    window.addEventListener("drop", drop);
    return () => { clearTimeout(idle); window.removeEventListener("dragover", dragover); window.removeEventListener("dragleave", leave); window.removeEventListener("drop", drop); };
  }, []);

  if (!over) return null;
  const name = profiles.data?.find((p) => p.id === active.data?.profileId)?.name ?? (profiles.data?.length ? null : "My mods");
  const into = settings.data?.addToActiveProfile === false || !name ? "Into the library" : `Into ${name}`;
  return (
    <div className="k-dropveil" aria-hidden>
      <div className="k-niche hot">
        <div className="k-niche-fire"><i /></div>
        <svg viewBox="0 0 232 300"><path d="M1 299V128C1 74 62 31 116 1C170 31 231 74 231 128V299Z" /></svg>
        <div className="k-niche-in">
          <span className="k-niche-word">Drop to add</span>
          <span className="k-niche-hint">{into}</span>
        </div>
      </div>
    </div>
  );
}

/** The consent dialog for a dropped modpack zip: what it is and what it adds, "this runs code on your PC", a box to
 *  tick, then modpack_install (a profile bound to its server, activated), as for a server's offer on Play. */
export function ModpackDropOffer() {
  const offer = useDropModpack((s) => s.offers[0]);
  if (!offer) return null;
  return <DroppedModpack key={`${offer.game.id}:${offer.path}`} game={offer.game} path={offer.path} />;
}

function DroppedModpack({ game, path }: { game: GameInstall; path: string }) {
  const qc = useQueryClient();
  const status = useGameStatus(game.id);
  const [agree, setAgree] = useState(false);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<ModpackInstallResult | null>(null);
  const preview = useQuery({
    queryKey: ["modpack_inspect", game.id, path], queryFn: () => api.modpackInspect(game.id, { path }), retry: false,
  });
  const close = () => useDropModpack.setState((s) => ({ offers: s.offers.slice(1) }));
  const p = preview.data;
  const file = path.split(/[\\/]/).pop() ?? path;
  const server = p?.server.name ?? p?.manifest?.server.name ?? p?.server.id ?? null;
  const install = async () => {
    setBusy(true);
    try {
      const r = await runTask<ModpackInstallResult>(() => api.modpackInstall(game.id, { source: { path }, consent: true, activate: true }), { done: "Modpack installed" });
      setDone(r);
      notifyActivation(r.activation);
      invalidateGame(qc, game.id);
      qc.invalidateQueries({ queryKey: ["library_list"] });
    } catch { /* toasted */ } finally { setBusy(false); }
  };
  return (
    <Modal open onClose={close} locked={busy} width={560} title={done ? "Modpack installed" : "Install this modpack?"}
      footer={done ? <Button onClick={close}>Close</Button> : (
        <><Button variant="ghost" onClick={close} disabled={busy}>Cancel</Button>
          <Button variant="primary" disabled={!agree || !preview.data || !!preview.error || !status.data || status.data.running || status.data.steamUpdating} loading={busy} onClick={install}>Install</Button></>
      )}>
      {done ? (
        <span>{done.profile.name} is active.</span>
      ) : (
        <div className="k-stack">
          <KeyValue rows={[
            ["File", <span className="k-mono">{file}</span>],
            server && ["Server", server],
            p?.manifest && ["Modpack", `${p.manifest.name} ${p.manifest.version}`],
          ]} />
          {preview.isLoading ? <Skeleton lines={1} /> : preview.error ? <Banner tone="err" title="Can't read it">{errorText(preview.error)}</Banner> : p && (
            <span className="k-hint">
              {p.missing.length ? `New: ${p.missing.map((m) => m.name ?? m.id).join(", ")}.` : "Everything in it is in your library already."}
              {p.nativeCode && " It contains native code (DLLs)."}
            </span>
          )}
          <Banner tone="warn" title="This runs code on your PC">
            <span className="k-row" style={{ gap: 8 }}><ShieldAlert size={16} />Install only modpacks you trust.</span>
          </Banner>
          <Toggle checked={agree} onChange={setAgree} label="I trust it and want its mods" />
          {status.data?.running && <span className="k-hint">Close the game first.</span>}
        </div>
      )}
    </Modal>
  );
}

