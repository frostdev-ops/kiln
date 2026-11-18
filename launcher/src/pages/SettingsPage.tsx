// /settings: profiles (a background feature: components/ProfilesSettings.tsx), the library's storage, launcher settings,
// games, and Kiln's kiln.json for the selected game. `#profiles` (Play's profile line, the old /profiles) scrolls there.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { FolderPlus, Plus, RefreshCw, Trash2, X } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { useLocation } from "react-router-dom";
import * as api from "../api/commands";
import { KEEP_MAX, type ConfigDocument, type DesktopInfo, type Json, type LauncherSettings, type RenderInfo } from "../api/commands";
import { qk, useAppInfo, useGame, useGameStatus, useNexusEnabled, useSettings } from "../api/hooks";
import { fallbackEnabled } from "../api/fallback";
import { useAssetsStatus, useExtractTask } from "../components/GameArt";
import { UpdatesCard } from "../components/UpdateSettings";
import { DeckModeCard, saveSettings } from "../deck/mode";
import { NexusCard } from "../components/NexusSettings";
import { ProfilesCard } from "../components/ProfilesSettings";
import { LibraryCard } from "../components/LibrarySettings";
import { LanguageCard } from "../components/LanguageSettings";
import { rich, t, useT } from "../i18n";
import {
  Badge, Button, Card, ConfirmDanger, EmptyState, Field, IconButton, NumberInput, Page, PageHeader, PathPicker, SchemaForm, Select, Skeleton,
  Toggle, errorText, fmtAgo, fmtBytes, setTypeToConfirm, toast, typeToConfirm,
} from "../ui";

/** Windows (WebView2): path examples in Windows form. */
const WIN = /Windows/i.test(navigator.userAgent);

export default function SettingsPage() {
  const t = useT();
  const nexusEnabled = useNexusEnabled();
  const { hash } = useLocation();
  useEffect(() => {
    if (!hash) return;
    const timer = setTimeout(() => document.getElementById(hash.slice(1))?.scrollIntoView({ block: "start" }), 60);
    return () => clearTimeout(timer);
  }, [hash]);
  return (
    <Page>
      <PageHeader title={t("settings.title")} sub={t("settings.sub")} />
      <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
        <ProfilesCard />
        <LibraryCard />
        <LauncherCard />
        <LanguageCard />
        <GamesCard />
        <AppearanceCard />
        <UpdatesCard />
        <DeckModeCard />
        {nexusEnabled && <NexusCard />}
      </div>
      <KilnJsonCard />
      <AssetsCard />
    </Page>
  );
}

function LauncherCard() {
  const t = useT();
  const qc = useQueryClient();
  const s = useSettings();
  const { games } = useGame();
  const [root, setRoot] = useState("");
  if (s.isLoading) return <Card title={t("settings.launcher.title")}><Skeleton /></Card>;
  if (!s.data) return <Card title={t("settings.launcher.title")}><EmptyState title={t("settings.launcher.unavailable")} hint={errorText(s.error)} /></Card>;
  const v = s.data;
  const set = (patch: Partial<LauncherSettings>) => void saveSettings(qc, () => patch);
  return (
    <Card title={t("settings.launcher.title")}>
      <Field label={t("settings.launcher.defaultGame")} htmlFor="st-game">
        <Select id="st-game" value={v.defaultGameId ?? ""} onChange={(id) => set({ defaultGameId: id || null })}
          options={[{ value: "", label: t("settings.launcher.firstFound") }, ...games.filter((g) => g.kind === "client").map((g) => ({ value: g.id, label: g.path.split(/[\\/]/).pop() ?? g.id }))]} />
      </Field>
      <Field label={t("settings.launcher.savesKeep")} hint={t("settings.launcher.savesKeepHint")} htmlFor="st-keep">
        <NumberInput id="st-keep" integer min={1} max={100} value={v.savesKeep} onCommit={(n) => set({ savesKeep: n })} style={{ width: 120 }} />
      </Field>
      <Field label={t("settings.launcher.keepVersions")} hint={t("settings.launcher.keepVersionsHint")} htmlFor="st-keep-versions">
        <NumberInput id="st-keep-versions" integer min={0} max={KEEP_MAX} value={v.keepVersions ?? 1} onCommit={(n) => set({ keepVersions: n })} style={{ width: 120 }} />
      </Field>
      <Toggle checked={v.addToActiveProfile !== false} onChange={(on) => set({ addToActiveProfile: on })} label={t("settings.launcher.addToActive")}
        hint={t("settings.launcher.addToActiveHint")} />
      <Toggle checked={v.modFolderNotes !== false} onChange={(on) => set({ modFolderNotes: on })} label={t("settings.launcher.folderNotes")}
        hint={t("settings.launcher.folderNotesHint")} />
      <Toggle checked={v.allowHttpModpacks} onChange={(on) => set({ allowHttpModpacks: on })} label={t("settings.launcher.httpModpacks")} hint={t("settings.launcher.httpModpacksHint")} />
      <Toggle checked={v.modUpdatesDaily === true} onChange={(on) => set({ modUpdatesDaily: on })} label={t("settings.launcher.dailyUpdates")}
        hint={t("settings.launcher.dailyUpdatesHint")} />
      <TypeToConfirmToggle />
      <Field label={t("settings.launcher.steamRoots")} hint={t("settings.launcher.steamRootsHint")}>
        <div className="k-stack">
          {v.steamRoots.map((r) => (
            <div key={r} className="k-input-group">
              <span className="k-mono k-ellipsis" style={{ flex: 1 }}>{r}</span>
              <IconButton label={t("common.remove")} size="s" icon={<X />} onClick={() => set({ steamRoots: v.steamRoots.filter((x) => x !== r) })} />
            </div>
          ))}
          <div className="k-input-group">
            <div style={{ flex: 1 }}><PathPicker value={root} onChange={setRoot} directory placeholder={WIN ? "D:\\SteamLibrary" : "/mnt/games/SteamLibrary"} /></div>
            <IconButton label={t("common.add")} variant="secondary" icon={<Plus />} disabled={!root.trim()} onClick={() => { set({ steamRoots: [...v.steamRoots, root.trim()] }); setRoot(""); }} />
          </div>
        </div>
      </Field>
      <DevCard />
    </Card>
  );
}

/** Appearance: the fire on Play. Off (or KILN_LAUNCHER_NO_WEBGL=1 for one start) draws the plain Play button, the same
 *  one a machine without WebGL gets. On Linux also "Draw the launcher without the GPU" (the render marker, render.rs) and,
 *  in the AppImage, "Kiln Launcher in the app menu" (desktop.rs). */
function AppearanceCard() {
  const t = useT();
  const qc = useQueryClient();
  const s = useSettings();
  const info = useAppInfo();
  if (!s.data) return null;
  const forced = !!info.data?.noWebgl;
  const render = info.data?.render;
  const desktop = info.data?.desktop;
  const setDesktop = (on: boolean) => api.appDesktopSet(on).then((next) => {
    qc.setQueryData(qk.appInfo, (cur: api.AppInfo | undefined) => (cur ? { ...cur, desktop: next } : cur));
    toast.ok(on ? t("settings.appearance.desktopOn") : t("settings.appearance.desktopOff"));
  }).catch((e) => toast.error(e));
  const setRender = (on: boolean) => api.appRenderSet(on).then((next) => {
    qc.setQueryData(qk.appInfo, (cur: api.AppInfo | undefined) => (cur ? { ...cur, render: next } : cur));
    toast.ok(on ? t("settings.appearance.nextNoGpu") : t("settings.appearance.nextGpu"));
  }).catch((e) => toast.error(e));
  return (
    <Card title={t("settings.appearance.title")}>
      <Toggle checked={!forced && s.data.animatedFire !== false} disabled={forced} onChange={(on) => void saveSettings(qc, () => ({ animatedFire: on }))}
        label={t("settings.appearance.fire")} hint={forced ? fireOff(info.data) : t("settings.appearance.fireHint")} />
      {render && <Toggle checked={render.marker} onChange={(on) => void setRender(on)} label={t("settings.appearance.noGpu")} hint={renderHint(render)} />}
      {desktop && <Toggle checked={desktop.on && desktop.installed} onChange={(on) => void setDesktop(on)} label={t("settings.appearance.desktop")} hint={desktopHint(desktop)} />}
    </Card>
  );
}

/** The AppImage's app menu entry: what it does, where it is, what is in the way. */
function desktopHint(d: DesktopInfo): string {
  if (d.foreign) return t("settings.appearance.desktopHintForeign", { file: d.file });
  return d.on && d.installed ? t("settings.appearance.desktopHintFile", { file: d.file }) : t("settings.appearance.desktopHint");
}

/** Why the fire is off for this start (app_info.noWebgl). */
const fireOff = (info: api.AppInfo | undefined) =>
  info?.render?.mode === "software" ? t("settings.appearance.fireOffSoftware") : t("settings.appearance.fireOffEnv");

/** What the render switch says: when it applies, how this start draws and why, who turned it on. */
function renderHint(r: RenderInfo): string {
  const sw = r.mode === "software";
  const now = r.why === "restart" ? (sw ? t("settings.render.noGpuRestart") : t("settings.render.gpuRestart"))
    : r.why === "environment" ? (sw ? t("settings.render.noGpuEnv") : t("settings.render.gpuEnv"))
    : r.why === "argument" ? (sw ? t("settings.render.noGpuArg", { flag: "--software-render" }) : t("settings.render.gpuArg", { flag: "--gpu-render" }))
    : sw ? t("settings.render.noGpu") : t("settings.render.gpu");
  // markerWhy: the backend's sentence, as it is
  return `${t("settings.render.intro")} ${now}${r.marker && r.markerWhy ? ` ${r.markerWhy}.` : ""}`;
}

function DevCard() {
  const t = useT();
  const [on, setOn] = useState(fallbackEnabled());
  if (!api.isTauri()) return null;
  return (
    <Toggle checked={on} onChange={(v) => { localStorage.setItem("kiln.mockFallback", v ? "1" : "0"); setOn(v); }}
      label={t("settings.dev.mock")} hint={t("settings.dev.mockHint")} />
  );
}

function GamesCard() {
  const t = useT();
  const qc = useQueryClient();
  const { games } = useGame();
  const [path, setPath] = useState("");
  const [rm, setRm] = useState<api.GameInstall | null>(null);
  const detect = useMutation({ mutationFn: api.gameDetect, onSuccess: (g) => { qc.setQueryData(qk.games, g); toast.ok(t("settings.games.found", { count: g.length })); }, onError: (e) => toast.error(e) });
  const add = useMutation({ mutationFn: () => api.gameAdd(path.trim()), onSuccess: () => { setPath(""); qc.invalidateQueries({ queryKey: qk.games }); toast.ok(t("settings.games.added")); }, onError: (e) => toast.error(e) });
  return (
    <Card title={t("settings.games.title")} actions={<Button size="s" icon={<RefreshCw />} loading={detect.isPending} onClick={() => detect.mutate()}>{t("settings.games.detect")}</Button>}>
      {games.length === 0 ? <span className="k-hint">{t("settings.games.none")}</span> : (
        <div className="k-list" style={{ margin: "0 -8px" }}>
          {games.map((g) => (
            <div key={g.id} className="k-list-item" style={{ padding: "8px" }}>
              <span className="k-stack" style={{ gap: 1, flex: 1, minWidth: 0 }}>
                <span className="k-row" style={{ gap: 6 }}><span className="k-item-title">{g.path.split(/[\\/]/).pop()}</span><Badge>{g.kind}</Badge><Badge>{g.source}</Badge></span>
                <span className="k-item-sub k-mono k-ellipsis" title={g.path}>{g.path}</span>
              </span>
              <IconButton label={t("settings.games.forget")} size="s" icon={<Trash2 />} onClick={() => setRm(g)} />
            </div>
          ))}
        </div>
      )}
      <Field label={t("settings.games.addFolder")}>
        <div className="k-input-group">
          <div style={{ flex: 1 }}><PathPicker value={path} onChange={setPath} directory placeholder={WIN ? "D:\\SteamLibrary\\steamapps\\common\\Enshrouded" : "…/common/Enshrouded"} /></div>
          <IconButton label={t("common.add")} variant="secondary" icon={<FolderPlus />} disabled={!path.trim()} loading={add.isPending} onClick={() => add.mutate()} />
        </div>
      </Field>
      <ConfirmDanger open={!!rm} onClose={() => setRm(null)} title={t("settings.games.forgetAsk")} word={t("common.confirmWord.forget")} action={t("settings.games.forget")} message={t("settings.games.forgetMessage")}
        onConfirm={async () => { try { await api.gameRemove(rm!.id); qc.invalidateQueries({ queryKey: qk.games }); } catch (e) { toast.error(e); throw e; } }} />
    </Card>
  );
}

function KilnJsonCard() {
  const t = useT();
  const qc = useQueryClient();
  const { game } = useGame();
  const status = useGameStatus(game?.id);
  const key = qk.kilnJson(game?.id ?? "");
  const doc = useQuery({ queryKey: key, queryFn: () => api.configKilnRead(game!.id), enabled: !!game });
  const onDoc = (d: ConfigDocument) => qc.setQueryData(key, d);
  const set = useMutation({ mutationFn: (v: { path: string; value: Json }) => api.configKilnSet(game!.id, v.path, v.value), onSuccess: onDoc, onError: (e) => toast.error(e) });
  const reset = useMutation({ mutationFn: (path: string) => api.configKilnReset(game!.id, path), onSuccess: onDoc, onError: (e) => toast.error(e) });
  if (!game) return null;
  const installed = status.data?.runtime.installed;
  return (
    <Card title={t("settings.kilnJson.title")} sub={rich("settings.kilnJson.sub", { file: <span className="k-mono">kiln/kiln.json</span> })}>
      {status.data && !installed ? <EmptyState title={t("settings.kilnJson.notInstalled")} hint={t("settings.kilnJson.notInstalledHint")} /> :
        doc.isLoading ? <Skeleton lines={5} /> : doc.error ? <EmptyState title={t("settings.kilnJson.unavailable")} hint={errorText(doc.error)} /> : doc.data?.schema && (
          // ui.language has its own control (Language: components/LanguageSettings.tsx)
          <SchemaForm schema={{ ...doc.data.schema, entries: doc.data.schema.entries.filter((e) => e.path !== "ui.language") }} values={doc.data.values} errors={doc.data.errors} disabled={set.isPending}
            onChange={(path, value) => set.mutate({ path, value })} onReset={(path) => reset.mutate(path)} />
        )}
      {doc.data && <span className="k-hint">{t("settings.kilnJson.overrides")}</span>}
    </Card>
  );
}

function AssetsCard() {
  // status only: the launcher makes the art on its own (components/GameArt.tsx, docs/kiln/launcher/gameart.md)
  const t = useT();
  const st = useAssetsStatus();
  const editorTask = useExtractTask(st.data, "editor");
  const adminTask = useExtractTask(st.data, "admin");
  const d = st.data;
  const e = d?.editor;
  const a = d?.admin;
  const when = (at?: string | null) => (at ? fmtAgo(at) : null);
  // a count from the art's counts, in words ("1,234 icons"); null when there is none
  const n = (c: Record<string, number> | undefined, k: "icons" | "ui" | "tiles") => {
    const count = c?.[k];
    if (!count) return null;
    return k === "icons" ? t("settings.art.icons", { count }) : k === "ui" ? t("settings.art.ui", { count }) : t("settings.art.tiles", { count });
  };
  const join = (...xs: (string | null | false | undefined)[]) => xs.filter(Boolean).join(" · ");
  const badge = (present: boolean, current: boolean | null | undefined, task: unknown) =>
    task ? <Badge tone="ember">{t("settings.art.preparing")}</Badge> : !present ? <Badge tone="idle">{t("settings.art.missing")}</Badge>
      : current === false ? <Badge tone="warn">{t("settings.art.updating")}</Badge> : <Badge tone="ok">{t("settings.art.ready")}</Badge>;
  const row = (label: string, b: ReactNode, lines: string[]) => (
    <div className="k-list-item" style={{ padding: "10px 0" }}>
      <span className="k-stack" style={{ gap: 1, flex: 1 }}>
        <span className="k-row" style={{ gap: 8 }}><span className="k-item-title">{label}</span>{b}</span>
        {lines.filter(Boolean).map((l) => <span key={l} className="k-item-sub">{l}</span>)}
      </span>
    </div>
  );
  return (
    <Card title={t("settings.art.title")} sub={t("settings.art.sub")}>
      {st.error ? <EmptyState title={t("settings.art.unavailable")} hint={errorText(st.error)} /> : !d || !e || !a ? <Skeleton /> : (
        <div className="k-list">
          {row(t("settings.art.editor"), badge(e.present, e.current, editorTask), [
            !e.canExtract && !e.present ? t("settings.art.noClient") : join(
              e.source === "game" ? t("settings.art.fromGame", { build: e.build ?? "?" }) : e.source === "copy" ? t("settings.art.fromFolder") : null,
              n(e.counts, "icons"), n(e.counts, "ui"), e.bytes ? fmtBytes(e.bytes) : null, when(e.extracted)),
            e.modded ? join(e.modded.icons ? t("settings.art.modItemsIcons", { items: e.modded.items, icons: e.modded.icons }) : t("settings.art.modItems", { items: e.modded.items }),
              e.modded.mods.length ? e.modded.mods.join(", ") : t("settings.art.noMods"), when(e.modded.generated)) : "",
          ])}
          {row(t("settings.art.admin"), badge(a.map, a.current, adminTask), [
            !a.canExtract && !a.map ? t("settings.art.noClient") : join(
              a.source === "game" ? t("settings.art.reliefFromGame", { build: a.build ?? "?" }) : a.map ? t("settings.art.fromLocalFolder") : null,
              n(a.counts, "tiles"), a.counts?.markers ? t("settings.art.markers", { count: a.counts.markers }) : a.markers ? t("settings.art.markersLocal") : a.map ? t("settings.art.noMarkers") : null,
              a.bytes ? fmtBytes(a.bytes) : null, when(a.extracted)),
          ])}
        </div>
      )}
    </Card>
  );
}



/** "Type a mod's name to remove it" (off unless the player turns it on): off = a mod's Remove has a plain button. The
 * dialogs that uninstall Kiln, delete a profile or a server, or restore a backup always ask for the word. */
function TypeToConfirmToggle() {
  const t = useT();
  const [on, setOn] = useState(typeToConfirm());
  return (
    <Toggle checked={on} onChange={(v) => { setTypeToConfirm(v); setOn(v); }} label={t("settings.typeToConfirm")}
      hint={t("settings.typeToConfirmHint")} />
  );
}
