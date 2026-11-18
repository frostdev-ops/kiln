// /servers/:id/mods: the server's mods, one page for the files and the running server. The table lists the folders in
// kiln/mods and kiln/native (server_mods) with what the running Kiln says about each (admin op `mods`): the flame decides
// what loads at the next start (a .disabled file); the aside acts on the selected mod (reload, unload, load, run its
// tests: mod_reload, mod_unload, mod_load, mod_test) and shows its facts. "Sync from the library" copies exact library
// versions in; folders put there by hand are listed but never replaced or deleted. Kiln.Admin can't unload itself.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo, useState } from "react";
import { useParams } from "react-router-dom";
import { create } from "zustand";
import { invalidateAdmin, useAdminCall, useAdminQuery, useHasOp, useInAdminProvider } from "../../api/admin";
import * as api from "../../api/commands";
import type { LibraryEntry, LibraryRef } from "../../api/commands";
import { useLibrary } from "../../api/hooks";
import { defaultVersion } from "../../shell/mods";
import { confirm } from "../../admin/ui";
import type { KilnMod, ModOpResult, TestReport } from "../../admin/types";
import {
  Banner, Button, FlameToggle, KeyValue, Modal, NameId, Page, PageHeader, Select, Table, Toggle, WellInput, fmtAgo, runTask, shortHash, toast, type Column, type Tone,
} from "../../ui";
import { sk, useServerStatus, type ServerModX } from "./serverApi";
import { useT } from "../../i18n";

const SELF = "kiln-admin";
const TICK_MS = 1000 / 24.5;
const STATE_TONE: Record<string, Tone> = { Loaded: "ok", Failed: "err", Disabled: "warn", Unloaded: "idle", Skipped: "idle", Pending: "idle" };
const refKey = (r: LibraryRef) => `${r.kind}:${r.id}`;

/** Test reports per server and mod, kept while the launcher runs. */
const useTests = create<{ by: Record<string, TestReport & { at: number }> }>(() => ({ by: {} }));

interface Row {
  key: string;
  id: string;
  name: string;
  folder: string | null;
  disk: ServerModX | null;
  live: KilnMod | null;
}

export default function ServerModsPage() {
  const { id = "" } = useParams();
  const st = useServerStatus(id);
  const inside = useInAdminProvider();
  if (st.data?.state === "running" && inside) return <LiveMods id={id} />;
  return <ModsView id={id} live={null} />;
}

function LiveMods({ id }: { id: string }) {
  const has = useHasOp("mods");
  const mods = useAdminQuery<KilnMod[]>("mods", {}, { refetchInterval: 5000, enabled: has });
  return <ModsView id={id} live={has ? mods.data ?? [] : null} liveError={mods.error?.message} />;
}

function ModsView({ id, live, liveError }: { id: string; live: KilnMod[] | null; liveError?: string }) {
  const t = useT();
  const qc = useQueryClient();
  const disk = useQuery({ queryKey: sk.mods(id), queryFn: () => api.serverMods(id) as Promise<ServerModX[]>, retry: false, refetchInterval: 15_000 });
  const [q, setQ] = useState("");
  const [sel, setSel] = useState<string | null>(null);
  const [sync, setSync] = useState(false);
  const rows = useMemo<Row[]>(() => {
    const out: Row[] = [];
    const used = new Set<KilnMod>();
    for (const d of disk.data ?? []) {
      const l = live?.find((m) => m.folder === d.folder || m.id === d.id) ?? null;
      if (l) used.add(l);
      out.push({ key: `${d.kind ?? "kiln"}:${d.folder}`, id: d.id, name: l?.name ?? d.id, folder: d.folder, disk: d, live: l });
    }
    for (const l of live ?? []) if (!used.has(l)) out.push({ key: `live:${l.id}`, id: l.id, name: l.name, folder: l.folder ?? null, disk: null, live: l });
    return out;
  }, [disk.data, live]);
  const shown = q ? rows.filter((r) => `${r.id} ${r.name} ${r.live?.state ?? ""}`.toLowerCase().includes(q.toLowerCase())) : rows;
  const cur = rows.find((r) => r.key === sel) ?? shown[0] ?? null;
  const refresh = () => { qc.invalidateQueries({ queryKey: sk.mods(id) }); invalidateAdmin(qc, id, ["mods"]); };

  const enable = async (r: Row, on: boolean) => {
    if (!r.disk) return;
    if (r.id === SELF && !on) return toast.error({ message: t("servers.mods.selfOff") });
    try {
      await api.serverModEnable(id, r.disk.folder, on);
      toast.ok(on ? t("servers.mods.litName", { name: r.name }) : t("servers.mods.putOutName", { name: r.name }), live ? t("servers.mods.restartToApply") : t("servers.mods.nextStart"));
      refresh();
    } catch (e) { toast.error(e); }
  };
  const loaded = live?.filter((m) => m.state === "Loaded").length ?? 0;
  const cost = (live ?? []).reduce((a, m) => a + (m.preMsPerStep ?? 0) + (m.postMsPerStep ?? 0), 0);
  const say = live
    ? t("servers.mods.sayLive", { loaded, total: rows.length, ms: cost.toFixed(2), pct: ((cost / TICK_MS) * 100).toFixed(1) })
    : t("servers.mods.sayStopped");
  const cols: Column<Row>[] = [
    { key: "on", header: <span className="k-sr-only">{t("servers.mods.litNext")}</span>, width: "40px", render: (r) => (
      r.disk ? <FlameToggle on={r.disk.enabled} label={r.name} disabled={r.id === SELF && r.disk.enabled} onChange={(on) => enable(r, on)} /> : null
    ) },
    { key: "name", header: t("servers.modpack.mod"), sort: (a, b) => a.name.localeCompare(b.name), render: (r) => <NameId name={r.name} id={r.id} dim={!!r.disk && !r.disk.enabled} /> },
    { key: "state", header: t("servers.list.state"), width: "86px", render: (r) => {
      if (r.live) return <span className={`k-badge k-tone-${STATE_TONE[r.live.state] ?? "idle"}`}>{r.live.state}</span>;
      return <span className={`k-badge k-tone-${r.disk?.enabled ? "ember" : "idle"}`}>{r.disk?.enabled ? t("servers.mods.lit") : t("servers.mods.out")}</span>;
    } },
    ...(live ? [{ key: "cost", header: t("servers.mods.msTick"), width: "64px", align: "right" as const, render: (r: Row) => (r.live?.state === "Loaded" ? <span className="k-mono">{((r.live.preMsPerStep ?? 0) + (r.live.postMsPerStep ?? 0)).toFixed(2)}</span> : null) }] : []),
  ];
  return (
    <Page fill>
      <PageHeader title={t("servers.mods.title")} sub={say} actions={<Button size="s" onClick={() => setSync(true)}>{t("servers.mods.syncTitle")}</Button>} />
      {liveError && <Banner tone="warn" title="Kiln.Admin">{liveError}</Banner>}
      {disk.error && <Banner tone="warn" title={t("servers.mods.files")}>{api.toKilnError(disk.error).message}</Banner>}
      <div className="k-split-page" style={{ gap: 24 }}>
        <div className="k-split-main" style={{ gap: 10 }}>
          <WellInput label={t("servers.mods.filter")} value={q} onChange={(e) => setQ(e.target.value)} onClear={() => setQ("")} note={t("servers.mods.shownOf", { shown: shown.length, total: rows.length })} />
          <Table aria-label={t("servers.mods.serverMods")} fill columns={cols} rows={shown} rowKey={(r) => r.key} selectedKey={cur?.key ?? null} onRowClick={(r) => setSel(r.key)}
            empty={disk.isLoading ? t("servers.mods.reading") : t("servers.mods.empty")} />
        </div>
        <aside className="k-aside k-aside-form" style={{ paddingTop: 0 }} aria-label={t("servers.mods.selected")}>
          {cur ? <ModDetail id={id} row={cur} live={!!live} onEnable={(on) => enable(cur, on)} onDone={refresh} /> : <span className="k-empty-title">{t("servers.mods.pick")}</span>}
        </aside>
      </div>
      <SyncDialog id={id} open={sync} onClose={() => setSync(false)} mods={disk.data ?? []} />
    </Page>
  );
}

function ModDetail({ id, row, live, onEnable, onDone }: { id: string; row: Row; live: boolean; onEnable: (on: boolean) => void; onDone: () => void }) {
  const m = row.live;
  const t = useT();
  const self = row.id === SELF;
  const report = useTests((s) => s.by[`${id}|${row.id}`]);
  const per = m ? (m.preMsPerStep ?? 0) + (m.postMsPerStep ?? 0) : 0;
  const d = row.disk;
  return (
    <div className="k-stack" style={{ gap: 14 }}>
      <div>
        <div style={{ fontSize: 20, fontWeight: 500, color: "var(--k-text-0)", overflowWrap: "anywhere" }}>{row.name}</div>
        <div className="k-mono k-muted" style={{ fontSize: 14.5 }}>{row.id}{(m?.version ?? d?.version) ? `, ${m?.version ?? d?.version}` : ""}</div>
      </div>
      {m?.description && <p style={{ fontSize: 16, color: "var(--k-text-1)" }}>{m.description}</p>}
      {live && m ? <LiveActions id={id} m={m} self={self} onDone={onDone} /> : live ? null : <span className="k-hint">{t("servers.mods.startToAct")}</span>}
      {m?.error && !/\.disabled/.test(m.error) && <Banner tone={m.state === "Failed" ? "err" : "warn"} title={m.state === "Failed" ? t("servers.mods.failed") : t("servers.mods.note")}>{m.error}</Banner>}
      {m?.lastFault && <Banner tone="warn" title={t("servers.mods.lastFault")}>{m.lastFault}</Banner>}
      {report && <Tests report={report} />}
      <KeyValue rows={[
        d && [t("servers.mods.nextStartLabel"), <Toggle checked={d.enabled} disabled={self && d.enabled} onChange={onEnable} label={d.enabled ? t("servers.mods.loads") : t("servers.mods.off")} />],
        m && [t("servers.list.state"), <span className={`k-tone-${STATE_TONE[m.state] ?? "idle"}`}>{m.state}</span>],
        m && m.loadIndex >= 0 && [t("servers.mods.loadOrder"), <span className="k-mono">{m.loadIndex + 1}</span>],
        m?.state === "Loaded" && [t("servers.mods.cost"), <span className="k-mono">{t("servers.mods.costValue", { ms: per.toFixed(3), peak: (m.maxMs ?? 0).toFixed(1) })}</span>],
        !!m?.faults && [t("servers.mods.faults"), <span className="k-tone-warn">{m.faults}</span>],
        !!m?.dependencies?.length && [t("servers.mods.needs"), m.dependencies.join(", ")],
        d && [t("servers.mods.playersNeed"), d.clients === "mod" ? t("servers.mods.theMod") : d.clients === "kiln" ? "Kiln" : t("servers.mods.nothing")],
        d && [t("servers.mods.from"), d.source ? <span className="k-mono" title={d.source.hash}>{t("servers.mods.fromLibrary", { hash: shortHash(d.source.hash) })}</span> : t("servers.mods.byHand")],
        row.folder && [t("servers.mods.folder"), <span className="k-mono">{row.folder}</span>],
        m?.advanced && [t("servers.modpack.kind"), <span className="k-tone-warn">{t("servers.mods.advanced")}</span>],
        self && [t("servers.mods.role"), <span className="k-tone-ember">{t("servers.mods.servesLive")}</span>],
      ]} />
    </div>
  );
}

function LiveActions({ id, m, self, onDone }: { id: string; m: KilnMod; self: boolean; onDone: () => void }) {
  const t = useT();
  const [busy, setBusy] = useState(false);
  const reload = useAdminCall<ModOpResult>("mod_reload");
  const unload = useAdminCall<ModOpResult>("mod_unload");
  const load = useAdminCall<ModOpResult>("mod_load");
  const test = useAdminCall<TestReport>("mod_test");
  const opDone = (r: ModOpResult) => {
    const also = (r.affected ?? []).filter((x) => x !== m.id);
    if (r.ok) toast.ok(r.message || t("common.done"), also.length ? t("servers.mods.also", { list: also.join(", ") }) : undefined);
    else toast.error({ message: r.message || t("servers.mods.failed") });
  };
  const run = async (f: () => Promise<void>) => {
    setBusy(true);
    try { await f(); } catch (e) { toast.error(e); } finally { setBusy(false); onDone(); }
  };
  const doUnload = async () => {
    if (!(await confirm({ title: t("servers.mods.unloadTitle", { name: m.name }), body: <p>{t("servers.mods.unloadText")}</p>, ok: t("servers.mods.unload"), danger: true }))) return;
    run(async () => opDone(await unload.mutateAsync({ id: m.id })));
  };
  const doTest = () => run(async () => {
    const r = await test.mutateAsync({ id: m.id });
    useTests.setState((s) => ({ by: { ...s.by, [`${id}|${m.id}`]: { ...r, at: Date.now() } } }));
    if (testCount(r) === 0) return void toast.info(t("servers.mods.noTestsFor", { name: m.name }));
    const line = t("servers.mods.testLine", { name: m.name, passed: r.passed, failed: r.failed, skipped: r.skipped });
    if (r.failed) toast.error({ message: line }); else toast.ok(line);
  });
  return (
    <div className="k-row" style={{ gap: 8 }}>
      {m.state === "Loaded" && <Button size="s" disabled={busy} onClick={doTest} title={t("servers.mods.runTestsHint")}>{t("servers.mods.runTests")}</Button>}
      {!self && ["Loaded", "Disabled"].includes(m.state) && <Button size="s" disabled={busy} onClick={() => run(async () => opDone(await reload.mutateAsync({ id: m.id })))} title={t("servers.mods.reloadHint")}>{t("servers.mods.reload")}</Button>}
      {!self && m.state === "Loaded" && <Button size="s" variant="danger" disabled={busy} onClick={doUnload}>{t("servers.mods.unload")}</Button>}
      {!self && ["Unloaded", "Failed"].includes(m.state) && m.folder && <Button size="s" variant="primary" disabled={busy} onClick={() => run(async () => opDone(await load.mutateAsync({ folder: m.folder! })))}>{m.state === "Failed" ? t("servers.mods.tryAgain") : t("servers.mods.load")}</Button>}
    </div>
  );
}

/** How many tests a run found (the counts, or the results when a report has no counts). */
const testCount = (rep: TestReport) => Math.max(rep.passed + rep.failed + rep.skipped, rep.results.length);

function Tests({ report }: { report: TestReport & { at: number } }) {
  const t = useT();
  const bad = report.results.filter((r) => r.outcome !== "Pass");
  if (testCount(report) === 0) return <span className="k-word-sm k-tone-idle">{t("servers.mods.noTests")}</span>;
  const [word, tone] = report.failed ? [t("servers.mods.failing"), "err"] : report.passed ? [t("servers.mods.passing"), "ok"] : [t("servers.mods.skipped"), "idle"];
  return (
    <div className="k-stack" style={{ gap: 4 }}>
      <span><span className={`k-word-sm k-tone-${tone}`}>{word}</span> {t("servers.mods.testSummary", { passed: report.passed, failed: report.failed, skipped: report.skipped, sec: (report.ms / 1000).toFixed(2), ago: fmtAgo(report.at) })}</span>
      {bad.map((r) => <span key={r.name} className="k-hint"><span className="k-tone-err">{r.outcome}</span> {r.name}{r.message ? `: ${r.message}` : ""}</span>)}
    </div>
  );
}

/** The library versions the server should have: the server gets exactly the checked ones; folders put there by hand stay. */
function SyncDialog({ id, open, onClose, mods }: { id: string; open: boolean; onClose: () => void; mods: ServerModX[] }) {
  const t = useT();
  const qc = useQueryClient();
  const kiln = useLibrary("kiln");
  const native = useLibrary("native");
  const library: LibraryEntry[] = useMemo(() => [...(kiln.data ?? []), ...(native.data ?? [])], [kiln.data, native.data]);
  const [want, setWant] = useState<Map<string, LibraryRef>>(new Map());
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (!open) return;
    const m = new Map<string, LibraryRef>();
    for (const s of mods) if (s.source) m.set(refKey(s.source), s.source);
    setWant(m);
  }, [mods, open]);
  const synced = new Set(mods.filter((m) => m.source).map((m) => refKey(m.source!)));
  const dirty = want.size !== synced.size || [...want.keys()].some((k) => !synced.has(k)) || mods.some((m) => m.source && want.get(refKey(m.source))?.hash !== m.source.hash);
  const sync = async () => {
    setBusy(true);
    try { await runTask(() => api.serverModsSync(id, [...want.values()]), { done: t("servers.mods.synced") }); onClose(); }
    catch { /* toasted */ } finally { setBusy(false); qc.invalidateQueries({ queryKey: sk.mods(id) }); }
  };
  return (
    <Modal open={open} onClose={onClose} locked={busy} title={t("servers.mods.syncTitle")} width={600}
      footer={<><Button variant="ghost" onClick={onClose} disabled={busy}>{t("common.cancel")}</Button><Button variant="primary" disabled={!dirty} loading={busy} onClick={sync}>{t("servers.mods.sync")}</Button></>}>
      <p className="k-hint">{t("servers.mods.syncHint")}</p>
      {library.length === 0 && <span className="k-empty-title">{t("servers.mods.libraryEmpty")}</span>}
      <div className="k-list" style={{ maxHeight: "48vh", overflow: "auto" }}>
        {library.map((e) => {
          const k = `${e.kind}:${e.id}`;
          const cur = want.get(k);
          const handPlaced = mods.some((m) => m.id === e.id && !m.source);
          return (
            <div key={k} className="k-list-item">
              <Toggle checked={!!cur} disabled={handPlaced} label={e.name} hint={handPlaced ? t("servers.mods.handPlaced") : `${e.kind}, ${e.id}`}
                onChange={(on) => {
                  const m = new Map(want);
                  const v = defaultVersion(e);
                  if (on && v) m.set(k, { kind: e.kind, id: e.id, version: v.version, hash: v.hash });
                  else m.delete(k);
                  setWant(m);
                }} />
              <span className="k-spacer" />
              {cur && e.versions.length > 1 && (
                <Select aria-label={t("servers.mods.versionOf", { name: e.name })} value={cur.hash} style={{ width: 200 }} onChange={(hash) => {
                  const v = e.versions.find((x) => x.hash === hash)!;
                  setWant(new Map(want).set(k, { ...cur, version: v.version, hash }));
                }} options={e.versions.map((v) => ({ value: v.hash, label: `${v.version} +${shortHash(v.hash)}` }))} />
              )}
            </div>
          );
        })}
      </div>
    </Modal>
  );
}
