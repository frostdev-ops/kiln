// Drop a mod anywhere on the window: library_add imports it, switches it on in the loaded profile ("My mods"
// for a first-time player) and writes the game's profile.json, so the next Play uses it. The toast is the mod's name
// and Undo (profile_revert: out of the profile again, the library copy stays); missing requirements are named in one
// line. Modpack zips go through the consent dialog (modpack_install). Settings "Switch new mods on"
// off: drops are only imported into the library. Drops on a DropZone (Library's niche, Runtime's row) are its own.
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { ShieldAlert } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { create } from "zustand";
import * as api from "../api/commands";
import type { Activation, AddReport, GameInstall, GameStatus, ImportReport, LauncherSettings, ModpackInstallResult } from "../api/commands";
import { invalidateGame, qk, useActiveProfile, useGame, useGameStatus, useProfiles, useSettings } from "../api/hooks";
import { Banner, Button, KeyValue, Modal, Skeleton, Toggle, errorText, runTask, toast } from "../ui";
import { useInstallPrompt } from "./firstRun";
import { detailsAction, leftOutLine, names as nameList, staysIfDetails, summarize, usePickProfile } from "./importResult";
import { notifyActivation, notifyAdd } from "./notices";
import { t, useT } from "../i18n";

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
      if (kept.length) { toast.info(t("shell.drop.undoKept"), t("shell.drop.undoKeptDetail", { count: kept.length })); return; }
      // new entries leave the profile; one that replaced another version (or was switched on) goes back to that
      toast.ok(r.changes.every((c) => !c.before) ? t("shell.catalogInstall.removed", { name: names(r.added) }) : t("shell.drop.undone"));
    } catch (e) { toast.error(e, t("shell.catalogInstall.undoFailed")); }
  };

  const importOnly = async (paths: string[], game: GameInstall | null) => {
    try {
      const detected = await Promise.all(paths.map((p) => api.libraryDetect(p)));
      const packs = detected.filter((d) => d.kind === "modpack" && /\.zip$/i.test(d.path)).map((d) => d.path);
      if (packs.length && game) offerModpacks(game, packs);
      else if (packs.length) toast.info(t("shell.drop.noGame"), t("shell.drop.noGameModpack"));
      const files = paths.filter((p) => !packs.includes(p));
      if (!files.length) return null;
      const r = await runTask<ImportReport>(() => api.libraryImport(files), { quiet: true });
      refresh(null);
      const sum = summarize(r);
      const d = { heading: t("shell.drop.headingAddedOff"), summary: sum };
      const more = detailsAction(d);
      if (sum.taken.length) toast.ok(t("shell.drop.addedOff", { names: nameList(sum.taken) }), leftOutLine(sum) ?? t("shell.drop.switchOnHint"), { actions: [{ label: t("common.show"), onClick: () => nav("/") }, ...more], ...staysIfDetails(d) });
      else toast.info(t("shell.drop.nothingToImport"), leftOutLine(sum), { actions: more, ...staysIfDetails(d) });
    } catch (e) { toast.error(e, t("shell.drop.importFailed")); }
    return null;
  };

  /** The picked profile is not the loaded one: "Load it" loads it in this game, so the mods it took are on. */
  const loadPicked = async (r: AddReport, game: GameInstall) => {
    if (!r.profileId) return;
    try {
      const a = await api.profileActivate(r.profileId, game.id);
      qc.setQueryData(qk.active(game.id), a);
      refresh(r.profileId, game.id);
      toast.ok(r.profileName ? t("shell.drop.loaded", { name: r.profileName }) : t("shell.drop.loadedProfile"), t("shell.drop.loadedDetail"));
      notifyActivation(a);
    } catch (e) { toast.error(e, r.profileName ? t("shell.drop.loadFailed", { name: r.profileName }) : t("shell.drop.loadFailedProfile")); }
  };

  /** After the player picked a profile for a mod that only reached the Library. */
  const pickedAdd = (r: AddReport, game: GameInstall) => {
    refresh(r.profileId, game.id);
    notifyAdd(r); // what writing the picked profile again kept in the game folder, and what else changed in it
    const loaded = r.applied || qc.getQueryData<Activation | null>(qk.active(game.id))?.profileId === r.profileId;
    const load = !loaded && !r.running && r.profileId ? [{ label: t("shell.drop.loadIt"), onClick: () => void loadPicked(r, game) }] : [];
    const notLoaded = loaded ? undefined
      : r.profileName ? (load.length ? t("shell.drop.notLoadedLoad", { name: r.profileName }) : t("shell.drop.notLoaded", { name: r.profileName }))
      : (load.length ? t("shell.drop.itNotLoadedLoad") : t("shell.drop.itNotLoaded"));
    if (r.added.length) {
      toast.ok(r.profileName ? t("shell.drop.addedTo", { names: names(r.added), profile: r.profileName }) : t("shell.drop.addedToProfile", { names: names(r.added) }),
        notLoaded, { actions: [...load, { label: t("common.undo"), onClick: () => void undo(r, game.id) }] });
    } else toast.info(r.profileName ? t("shell.drop.alreadyIn", { profile: r.profileName }) : t("shell.drop.alreadyInProfile"), notLoaded, { actions: load });
  };

  const add = async (paths: string[], game: GameInstall | null) => {
    // the cache as it is now, not as it was at the last render (an add right after Install Kiln)
    const status = (game && qc.getQueryData<GameStatus>(qk.gameStatus(game.id))) || (game?.id === ref.current.game?.id ? ref.current.status : undefined);
    const settings = qc.getQueryData<LauncherSettings>(qk.settings) ?? ref.current.settings;
    if (!paths.length) return null;
    if (settings?.addToActiveProfile === false) return importOnly(paths, game);
    if (!game) { toast.info(t("shell.drop.noGame"), t("shell.catalogInstall.findGameFirst")); return null; }
    if (status && !status.runtime.installed) {
      if (status.runtime.proxy?.kind === "shroudforge") toast.info(t("shell.drop.notInGame"), undefined, { actions: [{ label: t("nav.runtime"), onClick: () => nav("/runtime") }] });
      else toast.info(t("shell.drop.installFirst"), undefined, { actions: [{ label: t("shell.appShell.installKiln"), onClick: () => useInstallPrompt.getState().show({ game, paths }) }] });
      return null;
    }
    let r: AddReport;
    try {
      r = await runTask<AddReport>(() => api.libraryAdd(game.id, paths), { failed: t("shell.catalogInstall.notAdded") });
    } catch { return null; }
    refresh(r.profileId, game.id);
    notifyAdd(r); // what writing the profile again kept in the game folder, and what else changed in it
    if (r.modpacks.length) offerModpacks(game, r.modpacks);
    const found = [...r.import.imported, ...r.import.existing];
    const sum = summarize(r.import);
    const detail = (heading: string) => ({ heading, summary: sum, libraryOnly: r.libraryOnly });
    if (r.added.length) {
      const more = [
        r.missing.length ? t("shell.catalogInstall.needs", { list: r.missing.map((m) => m.id).join(", ") }) : "",
        r.resumed ? t("shell.drop.resumed") : "",
        r.running ? t("shell.catalogInstall.nextStart") : "",
        leftOutLine(sum) ?? "",
      ].filter(Boolean).join(" ");
      const d = detail(t("shell.drop.headingAddedOn"));
      toast.ok(t("shell.catalogInstall.added", { name: names(r.added) }), more || t("shell.drop.onNextPlay"), { actions: [{ label: t("common.undo"), onClick: () => void undo(r, game.id) }, ...detailsAction(d)], ...staysIfDetails(d) });
    } else if (r.already.length) {
      const d = detail(t("shell.drop.headingAlreadyOn"));
      toast.info(t("shell.drop.alreadyOn", { names: names(r.already), count: r.already.length }), leftOutLine(sum), { actions: detailsAction(d), ...staysIfDetails(d) });
    } else if (r.libraryOnly && found.length) {
      // no profile is active in this game and none is remembered: the mod waits in the Library, and the player can pick a profile
      const label = nameList(sum.taken);
      toast.info(t("shell.drop.addedOff", { names: label }), t("shell.drop.noProfileLoaded"), {
        actions: [{ label: t("shell.drop.addToProfile"), onClick: () => usePickProfile.setState({ pending: { game, entries: found, label, onAdded: pickedAdd } }) }, ...detailsAction(detail(t("shell.drop.headingAddedOff")))],
        ...staysIfDetails(detail(t("shell.drop.headingAddedOff"))),
      });
    } else if (!r.modpacks.length) {
      const d = detail(t("shell.drop.headingNothing"));
      toast.info(t("shell.drop.nothingToAdd"), leftOutLine(sum), { actions: detailsAction(d), ...staysIfDetails(d) });
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
  const t = useT();
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
        })).then((f) => (live ? (off = f) : f())).catch((e) => { if (live) toast.error(e, t("shell.drop.dropsUnavailable")); });
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
  // (only whether there is one matters here: "My mods" is the profile a first add makes)
  const name = profiles.data?.find((p) => p.id === active.data?.profileId)?.name ?? (profiles.data?.length ? null : "My mods");
  const into = settings.data?.addToActiveProfile === false || !name ? t("shell.drop.veilOff") : t("shell.drop.headingAddedOn");
  return (
    <div className="k-dropveil" aria-hidden>
      <div className="k-niche hot">
        <div className="k-niche-fire"><i /></div>
        <svg viewBox="0 0 232 300"><path d="M1 299V128C1 74 62 31 116 1C170 31 231 74 231 128V299Z" /></svg>
        <div className="k-niche-in">
          <span className="k-niche-word">{t("shell.drop.dropToAdd")}</span>
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
  const t = useT();
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
      const r = await runTask<ModpackInstallResult>(() => api.modpackInstall(game.id, { source: { path }, consent: true, activate: true }), { done: t("shell.drop.modpackInstalled") });
      setDone(r);
      notifyActivation(r.activation);
      invalidateGame(qc, game.id);
      qc.invalidateQueries({ queryKey: ["library_list"] });
    } catch { /* toasted */ } finally { setBusy(false); }
  };
  return (
    <Modal open onClose={close} locked={busy} width={560} title={done ? t("shell.drop.modpackInstalled") : t("shell.drop.installModpack")}
      footer={done ? <Button onClick={close}>{t("common.close")}</Button> : (
        <><Button variant="ghost" onClick={close} disabled={busy}>{t("common.cancel")}</Button>
          <Button variant="primary" disabled={!agree || !preview.data || !!preview.error || !status.data || status.data.running || status.data.steamUpdating} loading={busy} onClick={install}>{t("common.install")}</Button></>
      )}>
      {done ? (
        <span>{t("shell.drop.isActive", { name: done.profile.name })}</span>
      ) : (
        <div className="k-stack">
          <KeyValue rows={[
            [t("shell.drop.file"), <span className="k-mono">{file}</span>],
            server && [t("shell.drop.server"), server],
            p?.manifest && [t("shell.drop.modpack"), `${p.manifest.name} ${p.manifest.version}`],
          ]} />
          {preview.isLoading ? <Skeleton lines={1} /> : preview.error ? <Banner tone="err" title={t("shell.drop.cantRead")}>{errorText(preview.error)}</Banner> : p && (
            <span className="k-hint">
              {p.missing.length ? t("shell.drop.newMods", { list: p.missing.map((m) => m.name ?? m.id).join(", ") }) : t("shell.drop.allInLibrary")}
              {p.nativeCode && ` ${t("shell.drop.nativeCode")}`}
            </span>
          )}
          <Banner tone="warn" title={t("shell.drop.runsCode")}>
            <span className="k-row" style={{ gap: 8 }}><ShieldAlert size={16} />{t("shell.drop.trustOnly")}</span>
          </Banner>
          <Toggle checked={agree} onChange={setAgree} label={t("shell.drop.trustToggle")} />
          {status.data?.running && <span className="k-hint">{t("shell.drop.closeGame")}</span>}
        </div>
      )}
    </Modal>
  );
}

