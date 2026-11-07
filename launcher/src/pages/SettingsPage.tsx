// /settings: launcher settings, games, and Kiln's kiln.json for the selected game.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { FolderPlus, Plus, RefreshCw, Trash2, X } from "lucide-react";
import { useState, type ReactNode } from "react";
import * as api from "../api/commands";
import type { ConfigDocument, Json, LauncherSettings } from "../api/commands";
import { qk, useAppInfo, useGame, useGameStatus, useSettings } from "../api/hooks";
import { fallbackEnabled } from "../api/fallback";
import { useAssetsStatus, useExtractTask } from "../components/GameArt";
import { UpdatesCard } from "../components/UpdateSettings";
import { DeckModeCard } from "../deck/mode";
import { NexusCard } from "../components/NexusSettings";
import {
  Badge, Button, Card, ConfirmDanger, EmptyState, Field, IconButton, NumberInput, Page, PageHeader, PathPicker, SchemaForm, Select, Skeleton,
  Toggle, errorText, fmtAgo, fmtBytes, toast,
} from "../ui";

/** Windows (WebView2): path examples in Windows form. */
const WIN = /Windows/i.test(navigator.userAgent);

export default function SettingsPage() {
  return (
    <Page>
      <PageHeader title="Settings" sub="The launcher, the games it knows, and Kiln's own settings for this game." />
      <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
        <LauncherCard />
        <GamesCard />
        <AppearanceCard />
        <UpdatesCard />
        <DeckModeCard />
        <NexusCard />
      </div>
      <KilnJsonCard />
      <AssetsCard />
    </Page>
  );
}

function LauncherCard() {
  const qc = useQueryClient();
  const s = useSettings();
  const { games } = useGame();
  const [root, setRoot] = useState("");
  const save = useMutation({
    mutationFn: (v: LauncherSettings) => api.appSettingsSet(v),
    onSuccess: (v) => qc.setQueryData(qk.settings, v),
    onError: (e) => { toast.error(e); qc.invalidateQueries({ queryKey: qk.settings }); },
  });
  if (s.isLoading) return <Card title="Launcher"><Skeleton /></Card>;
  if (!s.data) return <Card title="Launcher"><EmptyState title="Settings unavailable" hint={errorText(s.error)} /></Card>;
  const v = s.data;
  const set = (patch: Partial<LauncherSettings>) => save.mutate({ ...v, ...patch });
  return (
    <Card title="Launcher">
      <Field label="Default game" htmlFor="st-game">
        <Select id="st-game" value={v.defaultGameId ?? ""} onChange={(id) => set({ defaultGameId: id || null })}
          options={[{ value: "", label: "First found" }, ...games.filter((g) => g.kind === "client").map((g) => ({ value: g.id, label: g.path.split(/[\\/]/).pop() ?? g.id }))]} />
      </Field>
      <Field label="Save backups to keep" hint="Per game." htmlFor="st-keep">
        <NumberInput id="st-keep" integer min={1} max={100} value={v.savesKeep} onCommit={(n) => set({ savesKeep: n })} style={{ width: 120 }} />
      </Field>
      <Toggle checked={v.addToActiveProfile !== false} onChange={(on) => set({ addToActiveProfile: on })} label="Add new mods to the active profile"
        hint="A dropped mod is ready at the next Play. Off: it only goes into the library." />
      <Toggle checked={v.allowHttpModpacks} onChange={(on) => set({ allowHttpModpacks: on })} label="Allow http modpacks" hint="Off: https only." />
      <Field label="Extra Steam folders" hint="Libraries Kiln does not find on its own.">
        <div className="k-stack">
          {v.steamRoots.map((r) => (
            <div key={r} className="k-input-group">
              <span className="k-mono k-ellipsis" style={{ flex: 1 }}>{r}</span>
              <IconButton label="Remove" size="s" icon={<X />} onClick={() => set({ steamRoots: v.steamRoots.filter((x) => x !== r) })} />
            </div>
          ))}
          <div className="k-input-group">
            <div style={{ flex: 1 }}><PathPicker value={root} onChange={setRoot} directory placeholder={WIN ? "D:\\SteamLibrary" : "/mnt/games/SteamLibrary"} /></div>
            <IconButton label="Add" variant="secondary" icon={<Plus />} disabled={!root.trim()} onClick={() => { set({ steamRoots: [...v.steamRoots, root.trim()] }); setRoot(""); }} />
          </div>
        </div>
      </Field>
      <DevCard />
    </Card>
  );
}

/** Appearance: the fire on Play. Off (or KILN_LAUNCHER_NO_WEBGL=1 for one start) draws the plain Play button, the same
 *  one a machine without WebGL gets. */
function AppearanceCard() {
  const qc = useQueryClient();
  const s = useSettings();
  const info = useAppInfo();
  const save = useMutation({
    mutationFn: (v: LauncherSettings) => api.appSettingsSet(v),
    onSuccess: (v) => qc.setQueryData(qk.settings, v),
    onError: (e) => { toast.error(e); qc.invalidateQueries({ queryKey: qk.settings }); },
  });
  if (!s.data) return null;
  const forced = !!info.data?.noWebgl;
  return (
    <Card title="Appearance">
      <Toggle checked={!forced && s.data.animatedFire !== false} disabled={forced} onChange={(on) => save.mutate({ ...s.data!, animatedFire: on })}
        label="Animated fire" hint={forced ? "Off for this start: KILN_LAUNCHER_NO_WEBGL is set." : "The fire on Play (WebGL). Off: a plain Play button, easier on weak GPUs."} />
    </Card>
  );
}

function DevCard() {
  const [on, setOn] = useState(fallbackEnabled());
  if (!api.isTauri()) return null;
  return (
    <Toggle checked={on} onChange={(v) => { localStorage.setItem("kiln.mockFallback", v ? "1" : "0"); setOn(v); }}
      label="Developer: mock data fallback" hint="Read-only screens show sample data where the backend is not done." />
  );
}

function GamesCard() {
  const qc = useQueryClient();
  const { games } = useGame();
  const [path, setPath] = useState("");
  const [rm, setRm] = useState<api.GameInstall | null>(null);
  const detect = useMutation({ mutationFn: api.gameDetect, onSuccess: (g) => { qc.setQueryData(qk.games, g); toast.ok(`${g.length} found`); }, onError: (e) => toast.error(e) });
  const add = useMutation({ mutationFn: () => api.gameAdd(path.trim()), onSuccess: () => { setPath(""); qc.invalidateQueries({ queryKey: qk.games }); toast.ok("Added"); }, onError: (e) => toast.error(e) });
  return (
    <Card title="Games" actions={<Button size="s" icon={<RefreshCw />} loading={detect.isPending} onClick={() => detect.mutate()}>Detect</Button>}>
      {games.length === 0 ? <span className="k-hint">None yet.</span> : (
        <div className="k-list" style={{ margin: "0 -8px" }}>
          {games.map((g) => (
            <div key={g.id} className="k-list-item" style={{ padding: "8px" }}>
              <span className="k-stack" style={{ gap: 1, flex: 1, minWidth: 0 }}>
                <span className="k-row" style={{ gap: 6 }}><span className="k-item-title">{g.path.split(/[\\/]/).pop()}</span><Badge>{g.kind}</Badge><Badge>{g.source}</Badge></span>
                <span className="k-item-sub k-mono k-ellipsis" title={g.path}>{g.path}</span>
              </span>
              <IconButton label="Forget" size="s" icon={<Trash2 />} onClick={() => setRm(g)} />
            </div>
          ))}
        </div>
      )}
      <Field label="Add a folder">
        <div className="k-input-group">
          <div style={{ flex: 1 }}><PathPicker value={path} onChange={setPath} directory placeholder={WIN ? "D:\\SteamLibrary\\steamapps\\common\\Enshrouded" : "…/common/Enshrouded"} /></div>
          <IconButton label="Add" variant="secondary" icon={<FolderPlus />} disabled={!path.trim()} loading={add.isPending} onClick={() => add.mutate()} />
        </div>
      </Field>
      <ConfirmDanger open={!!rm} onClose={() => setRm(null)} title="Forget this game?" word="forget" action="Forget" message="Only the launcher's record goes. The game folder and Kiln stay."
        onConfirm={async () => { try { await api.gameRemove(rm!.id); qc.invalidateQueries({ queryKey: qk.games }); } catch (e) { toast.error(e); throw e; } }} />
    </Card>
  );
}

function KilnJsonCard() {
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
    <Card title="Kiln" sub={<><span className="k-mono">kiln/kiln.json</span> · applies at the next start</>}>
      {status.data && !installed ? <EmptyState title="Kiln is not installed" hint="Install it on Runtime first." /> :
        doc.isLoading ? <Skeleton lines={5} /> : doc.error ? <EmptyState title="kiln.json unavailable" hint={errorText(doc.error)} /> : doc.data?.schema && (
          <SchemaForm schema={doc.data.schema} values={doc.data.values} errors={doc.data.errors} disabled={set.isPending}
            onChange={(path, value) => set.mutate({ path, value })} onReset={(path) => reset.mutate(path)} />
        )}
      {doc.data && <span className="k-hint">"client" and "server" override sections are kept as they are.</span>}
    </Card>
  );
}

function AssetsCard() {
  // status only: the launcher makes the art on its own (components/GameArt.tsx, docs/kiln/launcher/gameart.md)
  const st = useAssetsStatus();
  const editorTask = useExtractTask(st.data, "editor");
  const adminTask = useExtractTask(st.data, "admin");
  const d = st.data;
  const e = d?.editor;
  const a = d?.admin;
  const when = (t?: string | null) => (t ? fmtAgo(t) : null);
  const n = (c: Record<string, number> | undefined, k: string) => (c?.[k] ? `${c[k].toLocaleString()} ${k}` : null);
  const join = (...xs: (string | null | false | undefined)[]) => xs.filter(Boolean).join(" · ");
  const badge = (present: boolean, current: boolean | null | undefined, task: unknown) =>
    task ? <Badge tone="ember">preparing</Badge> : !present ? <Badge tone="idle">missing</Badge>
      : current === false ? <Badge tone="warn">updating</Badge> : <Badge tone="ok">ready</Badge>;
  const row = (label: string, b: ReactNode, lines: string[]) => (
    <div className="k-list-item" style={{ padding: "10px 0" }}>
      <span className="k-stack" style={{ gap: 1, flex: 1 }}>
        <span className="k-row" style={{ gap: 8 }}><span className="k-item-title">{label}</span>{b}</span>
        {lines.filter(Boolean).map((l) => <span key={l} className="k-item-sub">{l}</span>)}
      </span>
    </div>
  );
  return (
    <Card title="Game art" sub="Made from your own game, on its own. Never shipped, never downloaded.">
      {st.error ? <EmptyState title="Unavailable" hint={errorText(st.error)} /> : !d || !e || !a ? <Skeleton /> : (
        <div className="k-list">
          {row("Save editor", badge(e.present, e.current, editorTask), [
            !e.canExtract && !e.present ? "No Enshrouded client found" : join(
              e.source === "game" ? `From your game, build ${e.build ?? "?"}` : e.source === "copy" ? "Copied from a folder" : null,
              n(e.counts, "icons"), n(e.counts, "ui"), e.bytes ? fmtBytes(e.bytes) : null, when(e.extracted)),
            e.modded ? join(`Mod items: ${e.modded.items}${e.modded.icons ? ` (${e.modded.icons} own icons)` : ""}`,
              e.modded.mods.length ? e.modded.mods.join(", ") : "no mods", when(e.modded.generated)) : "",
          ])}
          {row("Admin map", badge(a.map, a.current, adminTask), [
            !a.canExtract && !a.map ? "No Enshrouded client found" : join(
              a.source === "game" ? `Relief from your game, build ${a.build ?? "?"}` : a.map ? "Copied from a local folder" : null,
              n(a.counts, "tiles"), a.counts?.markers ? `${a.counts.markers.toLocaleString()} markers from your game` : a.markers ? "markers from a local copy" : a.map ? "no marker data" : null,
              a.bytes ? fmtBytes(a.bytes) : null, when(a.extracted)),
          ])}
        </div>
      )}
    </Card>
  );
}

