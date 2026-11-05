// /servers/:id/admin/mods: the Kiln mods in the running server (mods): state, tick cost,
// faults, errors; reload / unload / load / run tests (mod_reload, mod_unload, mod_load, mod_test); folders in kiln/mods
// that are not loaded; and on/off for the next start through the launcher's server_mods / server_mod_enable (local
// servers). Kiln.Admin can't reload or unload itself.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Link } from "react-router-dom";
import { adminBase } from "../index";
import { create } from "zustand";
import { invalidateAdmin, useAdminCall, useAdminQuery, useAdminState } from "../../api/admin";
import { serverModEnable, serverMods, type ServerMod } from "../../api/commands";
import { NeedsOp, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import type { CrashReportInfo, KilnMod, ModOpResult, TestReport } from "../types";
import { Badge, Banner, Button, Card, EmptyState, Page, PageHeader, Stat, Stats, TextInput, Toggle, confirm, fmtAgo, toastError, toastOk } from "../ui";

const SELF = "kiln-admin";
const TICK_MS = 1000 / 24.5;
const STATE_TONE: Record<string, "ok" | "err" | "warn" | "idle"> = { Loaded: "ok", Failed: "err", Disabled: "warn", Unloaded: "idle", Skipped: "idle", Pending: "idle" };

/** Test reports per server and mod, kept while the launcher runs. */
const useTests = create<{ by: Record<string, TestReport & { at: number }> }>(() => ({ by: {} }));

function ModsPage() {
  const id = useServerId();
  const qc = useQueryClient();
  const st = useAdminState();
  const mods = useAdminQuery<KilnMod[]>("mods", {}, { refetchInterval: 5000 });
  const crashes = useAdminQuery<CrashReportInfo[]>("crash_reports", {}, { refetchInterval: 30_000 });
  const disk = useQuery({ queryKey: ["server_mods", id], queryFn: () => serverMods(id), retry: false, refetchInterval: 15_000 });
  const [filter, setFilter] = useState("");
  const [busy, setBusy] = useState<Set<string>>(new Set());
  const reload = useAdminCall<ModOpResult>("mod_reload");
  const unload = useAdminCall<ModOpResult>("mod_unload");
  const load = useAdminCall<ModOpResult>("mod_load");
  const test = useAdminCall<TestReport>("mod_test");
  const list = mods.data ?? [];
  const q = filter.toLowerCase();
  const shown = q ? list.filter((m) => `${m.id} ${m.name} ${m.state}`.toLowerCase().includes(q)) : list;
  const n = (s: string) => list.filter((m) => m.state === s).length;
  const cost = list.reduce((a, m) => a + (m.preMsPerStep ?? 0) + (m.postMsPerStep ?? 0), 0);
  const loadedFolders = new Set([...list.map((m) => m.folder), ...list.map((m) => (m.id.startsWith("folder:") ? m.id.slice(7) : null))].filter(Boolean));
  const newFolders = (disk.data ?? []).filter((d) => !loadedFolders.has(d.folder) && !list.some((m) => m.id === d.id));
  const byFolder = new Map((disk.data ?? []).map((d) => [d.folder, d]));

  const run = async (key: string, f: () => Promise<unknown>) => {
    setBusy((b) => new Set(b).add(key));
    try {
      await f();
    } catch (e) {
      toastError(e);
    } finally {
      setBusy((b) => { const x = new Set(b); x.delete(key); return x; });
      invalidateAdmin(qc, id, ["mods"]);
      disk.refetch();
    }
  };
  const opDone = (r: ModOpResult, key: string) => {
    const also = (r.affected ?? []).filter((x) => x !== key);
    if (r.ok) toastOk(r.message || "done", also.length ? `also ${also.join(", ")}` : undefined);
    else toastError({ message: r.message || "failed" });
  };
  const act = async (a: "reload" | "unload" | "load" | "test", m: KilnMod) => {
    const key = a === "load" ? m.folder ?? m.id : m.id;
    if (a === "unload" && !(await confirm({ title: `Unload ${m.name}?`, body: <><p>It stops at the start of the next tick. Mods that depend on it unload too.</p><p>Load it again from this page, or restart the server.</p></>, ok: "Unload", danger: true }))) return;
    run(key, async () => {
      if (a === "test") {
        toastOk(`Running the tests of ${m.name}…`);
        const r = await test.mutateAsync({ id: m.id });
        useTests.setState((s) => ({ by: { ...s.by, [`${id}|${m.id}`]: { ...r, at: Date.now() } } }));
        if (r.failed) toastError({ message: `${m.name}: ${r.passed} passed, ${r.failed} failed, ${r.skipped} skipped` });
        else toastOk(`${m.name}: ${r.passed} passed, ${r.failed} failed, ${r.skipped} skipped`);
      } else if (a === "load") opDone(await load.mutateAsync({ folder: m.folder! }), key);
      else opDone(await (a === "reload" ? reload : unload).mutateAsync({ id: m.id }), key);
    });
  };
  const enable = (folder: string, on: boolean, self: boolean) => {
    if (self && !on) return toastError({ message: "Kiln.Admin serves these pages; switching it off would cut the live tools." });
    run(folder, async () => {
      await serverModEnable(id, folder, on);
      toastOk(`${on ? "Switched on" : "Switched off"} ${folder}`, st.session ? "Restart the server to apply." : "Applies at the next start.");
    });
  };
  return (
    <Page>
      <PageHeader title="Mods" sub="Kiln mods on the server. Reload, unload, load and tests act on the running server at once; the switch decides what loads at the next start."
        actions={<Button size="s" variant="ghost" onClick={() => { invalidateAdmin(qc, id, ["mods", "crash_reports"]); disk.refetch(); }}>Refresh</Button>} />
      <OfflineNote />
      <NeedsOp ops="mods" what="The mod list">
        <Stats>
          <Stat k="Loaded" v={n("Loaded")} s={`of ${list.length} found`} />
          <Stat k="Didn't load" v={n("Failed")} s="missing dependency, bad manifest, error" />
          <Stat k="Switched off" v={n("Disabled") + n("Skipped") + n("Unloaded")} s={`${n("Disabled")} by faults · ${n("Skipped")} skipped · ${n("Unloaded")} unloaded`} />
          <Stat k="Tick cost" v={<>{cost.toFixed(2)}<small> ms</small></>} s={`${((cost / TICK_MS) * 100).toFixed(1)}% of a tick, all mods`} />
          <Stat k="Crash reports" v={(crashes.data ?? []).length} s={<Link to={`${adminBase(id)}/crashes`} className="k-link">open the list</Link>} />
        </Stats>
        {newFolders.length ? (
          <Card title="New in kiln/mods" sub="On disk but not loaded (copied in after the server started)">
            <div className="ka-list">
              {newFolders.map((f) => (
                <div key={f.folder} className="ka-row-item">
                  <b>{f.id}</b><span className="k-muted">{f.version ?? ""}</span><span className="k-mono k-muted">{f.folder}</span>
                  <span className="k-spacer" />
                  <Button size="s" variant="primary" loading={busy.has(f.folder)} onClick={() => run(f.folder, async () => opDone(await load.mutateAsync({ folder: f.folder }), f.folder))}>Load now</Button>
                </div>
              ))}
            </div>
          </Card>
        ) : null}
        <Card title="Kiln mods" actions={<TextInput aria-label="Filter mods" placeholder="Filter…" value={filter} onChange={(e) => setFilter(e.target.value)} style={{ width: 200 }} />}>
          {mods.error ? <Banner tone="warn" title="Couldn't read the mod list">{mods.error.message}</Banner> : null}
          <div className="ka-list">
            {shown.map((m) => <ModRow key={m.id} m={m} disk={m.folder ? byFolder.get(m.folder) ?? null : null} diskKnown={!!disk.data} busy={busy.has(m.id) || busy.has(m.folder ?? "")} act={act} enable={enable} serverId={id} />)}
            {mods.data && !shown.length ? <EmptyState title="No Kiln mods" hint="Put a mod folder (with its kiln.mod.json) into kiln/mods." /> : null}
          </div>
        </Card>
        {disk.error ? <div className="k-hint">On/off for the next start: {disk.error.message}</div> : null}
      </NeedsOp>
    </Page>
  );
}

function ModRow({ m, disk, diskKnown, busy, act, enable, serverId }: {
  m: KilnMod; disk: ServerMod | null; diskKnown: boolean; busy: boolean; serverId: string;
  act: (a: "reload" | "unload" | "load" | "test", m: KilnMod) => void; enable: (folder: string, on: boolean, self: boolean) => void;
}) {
  const self = m.id === SELF;
  const t = useTests((s) => s.by[`${serverId}|${m.id}`]);
  const per = (m.preMsPerStep ?? 0) + (m.postMsPerStep ?? 0);
  const disabledFile = disk ? !disk.enabled : false;
  const skippedByFile = m.state === "Skipped" && /\.disabled/.test(m.error ?? "");
  const restart = disk ? (disabledFile ? !skippedByFile && m.state !== "Unloaded" : skippedByFile) : false;
  const meta = [<span key="id" className="k-mono">{m.id}</span>];
  if (m.folder && m.folder !== m.id) meta.push(<span key="f">folder <span className="k-mono">{m.folder}</span></span>);
  if (m.loadIndex >= 0) meta.push(<span key="i">load #{m.loadIndex}</span>);
  if (m.state === "Loaded") meta.push(<span key="c">{per.toFixed(3)} ms per tick · peak {(m.maxMs ?? 0).toFixed(1)} ms</span>);
  if (m.faults) meta.push(<b key="fa">{m.faults} fault{m.faults > 1 ? "s" : ""}</b>);
  if (m.dependencies?.length) meta.push(<span key="d">needs {m.dependencies.join(", ")}</span>);
  return (
    <div className="ka-mod">
      <div>
        <div className="ka-mod-title">
          <h3>{m.name}</h3>
          {m.version ? <span className="k-muted">v{m.version}</span> : null}
          <Badge tone={STATE_TONE[m.state] ?? "idle"}>{String(m.state).toLowerCase()}</Badge>
          {m.advanced ? <Badge tone="warn" title="Uses hooks or memory patches">advanced</Badge> : null}
          {self ? <Badge tone="ember" title="Serves these admin pages">admin bridge</Badge> : null}
          {restart ? <Badge tone="ember">restart to apply</Badge> : null}
        </div>
        {m.description ? <div className="ka-mod-desc">{m.description}</div> : null}
        <div className="ka-meta">{meta.map((x, i) => <span key={i}>{i ? " · " : ""}{x}</span>)}</div>
      </div>
      <div className="k-row" style={{ alignSelf: "start", justifyContent: "flex-end" }}>
        {m.state === "Loaded" ? <Button size="s" disabled={busy} onClick={() => act("test", m)} title="Run this mod's [KilnTest] tests in the running server">Run tests</Button> : null}
        {!self && ["Loaded", "Disabled"].includes(m.state) ? <Button size="s" disabled={busy} onClick={() => act("reload", m)} title="Unload and load again from disk, with the mods that depend on it">Reload</Button> : null}
        {!self && m.state === "Loaded" ? <Button size="s" disabled={busy} onClick={() => act("unload", m)}>Unload</Button> : null}
        {!self && ["Unloaded", "Failed"].includes(m.state) && m.folder ? <Button size="s" variant={m.state === "Unloaded" ? "primary" : "secondary"} disabled={busy} onClick={() => act("load", m)}>{m.state === "Failed" ? "Try again" : "Load"}</Button> : null}
        {diskKnown && disk ? <Toggle checked={!disabledFile} disabled={busy || (self && !disabledFile)} label={disabledFile ? "Off" : "On"} onChange={(on) => enable(disk.folder, on, self)} /> : null}
      </div>
      {m.error && !skippedByFile ? <div className="ka-mod-wide"><Banner tone={m.state === "Failed" ? "err" : "warn"}>{m.error}</Banner></div> : null}
      {m.lastFault ? <div className="ka-mod-wide"><Banner tone="warn" title="Last fault">{m.lastFault}</Banner></div> : null}
      {t ? <div className="ka-mod-wide"><Tests t={t} /></div> : null}
    </div>
  );
}

function Tests({ t }: { t: TestReport & { at: number } }) {
  const bad = t.results.filter((r) => r.outcome !== "Pass");
  const icon: Record<string, string> = { Pass: "✓", Fail: "✕", Timeout: "⏱", Skip: "–" };
  const line = (r: TestReport["results"][number]) => (
    <div key={r.name} className={r.outcome}><span>{icon[r.outcome] ?? "?"}</span><span>{r.name}{r.outcome === "Timeout" ? " (timed out)" : ""}</span>{r.message ? <span className="k-hint">{r.message}</span> : null}</div>
  );
  return (
    <Banner tone={t.failed ? "err" : "ok"} title={`${t.passed} passed, ${t.failed} failed, ${t.skipped} skipped in ${(t.ms / 1000).toFixed(2)} s`}>
      <span className="k-hint">{fmtAgo(t.at)}{t.results.length ? "" : " · this mod has no tests"}{t.file ? ` · ${t.file}` : ""}</span>
      {bad.length ? <div className="ka-tests">{bad.map(line)}</div> : null}
      {t.results.length ? <details><summary className="k-hint" style={{ cursor: "pointer" }}>All {t.results.length} tests</summary><div className="ka-tests">{t.results.map(line)}</div></details> : null}
    </Banner>
  );
}

export default withAdmin(ModsPage);
