// /library: import (drop zip/folder/DLL → detection → import task), versions, remove, clean up.
import { useQueries, useQueryClient } from "@tanstack/react-query";
import { CircleCheck, CircleHelp, Library, MoreHorizontal, Trash2 } from "lucide-react";
import { useMemo, useState } from "react";
import * as api from "../api/commands";
import type { Detected, ImportReport, LibraryEntry, ModKind } from "../api/commands";
import { qk, useLibrary, useProfiles } from "../api/hooks";
import {
  Badge, Button, Card, ConfirmDanger, DropZone, EmptyState, Menu, Modal, Page, PageHeader, Segmented, Skeleton, Table, TextInput,
  errorText, fmtAgo, fmtBytes, runTask, shortHash, toast, type Column,
} from "../ui";

type KindFilter = "all" | ModKind;
const KIND_LABEL: Record<ModKind, string> = { kiln: "Kiln", native: "Native", emf: "EMF", plugin: "Plugin" };
const importable = (d: Detected) => ["kiln", "native", "plugin", "emf", "bare-dll", "modpack"].includes(d.kind);

export default function LibraryPage() {
  const qc = useQueryClient();
  const lib = useLibrary(null);
  const [kind, setKind] = useState<KindFilter>("all");
  const [q, setQ] = useState("");
  const [detected, setDetected] = useState<Detected[] | null>(null);
  const [report, setReport] = useState<ImportReport | null>(null);
  const [rm, setRm] = useState<{ e: LibraryEntry; version: string } | null>(null);
  const [gc, setGc] = useState<{ kind: ModKind; id: string; version: string; hash: string }[] | null>(null);
  const usage = useUsage();

  const rows = useMemo(() => (lib.data ?? []).filter((e) => (kind === "all" || e.kind === kind) && (!q || (e.name + e.id).toLowerCase().includes(q.toLowerCase()))), [lib.data, kind, q]);
  const refresh = () => qc.invalidateQueries({ queryKey: ["library_list"] });

  const onPaths = async (paths: string[]) => {
    try { setDetected(await Promise.all(paths.map((p) => api.libraryDetect(p)))); }
    catch (e) { toast.error(e, "Can't read that"); }
  };
  const doImport = async (paths: string[]) => {
    setDetected(null);
    try {
      const r = await runTask<ImportReport>(() => api.libraryImport(paths), { failed: "Import failed" });
      refresh();
      if (r.skipped.length || r.existing.length) setReport(r);
      else toast.ok(`Imported ${r.imported.length}`);
    } catch { /* toasted */ }
  };

  const cols: Column<LibraryEntry>[] = [
    { key: "name", header: "Mod", width: "minmax(0, 2fr)", sort: (a, b) => a.name.localeCompare(b.name), render: (e) => (
      <span className="k-stack" style={{ gap: 0 }}><span className="k-item-title">{e.name}</span><span className="k-dim k-mono" style={{ fontSize: 11 }}>{e.id}</span></span>
    ) },
    { key: "kind", header: "Kind", width: "90px", sort: (a, b) => a.kind.localeCompare(b.kind), render: (e) => <Badge tone={e.kind === "kiln" ? "ember" : "idle"}>{KIND_LABEL[e.kind]}</Badge> },
    { key: "ver", header: "Versions", width: "minmax(0, 1.2fr)", render: (e) => (
      <span className="k-row" style={{ gap: 4 }}>
        {e.versions.slice(0, 3).map((v) => <span key={v.version + v.hash} className="k-chip" title={`${v.hash}\n${v.folder}`}>{v.version}<span className="k-dim">+{shortHash(v.hash)}</span></span>)}
        {e.versions.length > 3 && <span className="k-dim">+{e.versions.length - 3}</span>}
      </span>
    ) },
    { key: "side", header: "Side", width: "90px", render: (e) => <span className="k-muted">{e.versions[0]?.side ?? "-"}</span> },
    { key: "used", header: "Used", width: "70px", align: "right", render: (e) => <span className="k-num k-muted">{usage.count(e.kind, e.id)}</span> },
    { key: "size", header: "Size", width: "80px", align: "right", render: (e) => <span className="k-num k-muted">{fmtBytes(e.versions[0]?.size)}</span> },
    { key: "when", header: "Added", width: "110px", sort: (a, b) => (a.versions[0]?.imported ?? "").localeCompare(b.versions[0]?.imported ?? ""), render: (e) => <span className="k-muted">{fmtAgo(e.versions[0]?.imported)}</span> },
    { key: "act", header: "", width: "44px", align: "right", render: (e) => (
      <Menu label="Actions" icon={<MoreHorizontal />} items={e.versions.map((v) => ({ label: `Remove ${v.version}`, icon: <Trash2 />, danger: true, onSelect: () => setRm({ e, version: v.version }) }))} />
    ) },
  ];

  const unused = () => (lib.data ?? []).flatMap((e) => e.versions.filter((v) => !usage.uses(e.kind, e.id, v.version)).map((v) => ({ kind: e.kind, id: e.id, version: v.version, hash: v.hash })));

  return (
    <Page>
      <PageHeader title="Library" sub="Every mod you imported, by version." actions={<Button icon={<Trash2 />} onClick={() => setGc(unused())} disabled={!usage.ready}>Clean up</Button>} />
      <DropZone onPaths={onPaths} title="Drop mods to import" hint="Zip, folder or DLL. Kiln, native, EMF, plugins, modpacks." pickFolder
        pick={{ filters: [{ name: "Mods", extensions: ["zip", "dll"] }] }} />
      <Card flush title={`${rows.length} mods`} actions={<>
        <TextInput placeholder="Filter" value={q} onChange={(e) => setQ(e.target.value)} style={{ height: 28, width: 160 }} aria-label="Filter" />
        <Segmented aria-label="Kind" value={kind} onChange={setKind} options={[{ id: "all", label: "All" }, { id: "kiln", label: "Kiln" }, { id: "native", label: "Native" }, { id: "emf", label: "EMF" }, { id: "plugin", label: "Plugin" }]} />
      </>}>
        {lib.isLoading ? <div style={{ padding: 16 }}><Skeleton /></div> : lib.error ? <EmptyState title="Library unavailable" hint={errorText(lib.error)} /> : (
          <Table aria-label="Library" columns={cols} rows={rows} rowKey={(e) => `${e.kind}/${e.id}`} rowHeight={52} maxHeight={600}
            empty={<EmptyState icon={<Library />} title={q || kind !== "all" ? "No match" : "Library is empty"} hint={q || kind !== "all" ? undefined : "Drop a mod above."} />} />
        )}
      </Card>

      <DetectModal detected={detected} onClose={() => setDetected(null)} onImport={doImport} />
      <ReportModal report={report} onClose={() => setReport(null)} />
      <ConfirmDanger open={!!rm} onClose={() => setRm(null)} title={`Remove ${rm?.e.name} ${rm?.version}?`} word={rm?.e.id ?? ""} action="Remove"
        message={rm && usage.uses(rm.e.kind, rm.e.id, rm.version) ? <>Profiles use it: {usage.names(rm.e.kind, rm.e.id).join(", ")}. They will show it as missing.</> : "The files are deleted from the library."}
        onConfirm={async () => {
          const v = rm!.e.versions.find((x) => x.version === rm!.version)!;
          try { await api.libraryRemove({ kind: rm!.e.kind, id: rm!.e.id, version: v.version, hash: v.hash }, usage.uses(rm!.e.kind, rm!.e.id, v.version)); toast.ok("Removed"); refresh(); }
          catch (e) { toast.error(e); throw e; }
        }} />
      <ConfirmDanger open={!!gc && gc.length > 0} onClose={() => setGc(null)} title={`Remove ${gc?.length ?? 0} unused versions?`} word="clean" action="Remove"
        message={<span className="k-mono" style={{ fontSize: 12 }}>{gc?.slice(0, 8).map((g) => `${g.id} ${g.version}`).join(", ")}{(gc?.length ?? 0) > 8 ? " …" : ""}</span>}
        onConfirm={async () => {
          let n = 0;
          for (const g of gc ?? []) { try { await api.libraryRemove(g, false); n++; } catch (e) { toast.error(e, `${g.id} ${g.version}`); } }
          toast.ok(`Removed ${n}`); refresh();
        }} />
      {gc && gc.length === 0 && <Modal open onClose={() => setGc(null)} title="Nothing to clean" footer={<Button onClick={() => setGc(null)}>OK</Button>}>Every version is used by a profile.</Modal>}
    </Page>
  );
}

/** Which profiles use which library versions (loads every profile). */
function useUsage() {
  const profiles = useProfiles();
  const ids = (profiles.data ?? []).map((p) => p.id);
  const each = useQueries({ queries: ids.map((id) => ({ queryKey: qk.profile(id), queryFn: () => api.profileGet(id) })) });
  const list = each.flatMap((q) => (q.data ? [q.data] : []));
  return {
    ready: !!profiles.data && each.every((q) => !q.isLoading),
    count: (k: ModKind, id: string) => list.filter((p) => p.mods[k].some((m) => m.id === id)).length,
    uses: (k: ModKind, id: string, v: string) => list.some((p) => p.mods[k].some((m) => m.id === id && m.version === v)),
    names: (k: ModKind, id: string) => list.filter((p) => p.mods[k].some((m) => m.id === id)).map((p) => p.name),
  };
}

function DetectModal({ detected, onClose, onImport }: { detected: Detected[] | null; onClose: () => void; onImport: (paths: string[]) => void }) {
  const ok = (detected ?? []).filter(importable);
  return (
    <Modal open={!!detected} onClose={onClose} title="Import" width={600}
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!ok.length} onClick={() => onImport(ok.map((d) => d.path))}>Import {ok.length}</Button></>}>
      <div className="k-list" style={{ margin: "0 -4px" }}>
        {(detected ?? []).map((d) => (
          <div key={d.path} className="k-list-item" style={{ padding: "9px 6px", alignItems: "flex-start" }}>
            {importable(d) ? <CircleCheck size={16} color="var(--k-ok)" style={{ marginTop: 2 }} /> : <CircleHelp size={16} color="var(--k-text-3)" style={{ marginTop: 2 }} />}
            <span className="k-stack" style={{ gap: 2, flex: 1, minWidth: 0 }}>
              <span className="k-row" style={{ gap: 6 }}>
                <span className="k-item-title">{d.name ?? d.id ?? d.path.split(/[\\/]/).pop()}</span>
                <Badge tone={importable(d) ? "ember" : "idle"}>{d.kind}</Badge>
                {d.version && <span className="k-mono k-muted">{d.version}</span>}
              </span>
              <span className="k-hint">{d.reason}</span>
              {d.kind === "runtime" && <span className="k-hint">Runtime bundles go on the Runtime page.</span>}
              {d.items.length > 0 && <span className="k-hint">{d.items.length} inside: {d.items.map((i) => i.id ?? i.name).join(", ")}</span>}
            </span>
          </div>
        ))}
      </div>
    </Modal>
  );
}

function ReportModal({ report, onClose }: { report: ImportReport | null; onClose: () => void }) {
  return (
    <Modal open={!!report} onClose={onClose} title="Import done" footer={<Button onClick={onClose}>OK</Button>}>
      {report && (
        <div className="k-stack">
          <span>{report.imported.length} imported · {report.existing.length} already there · {report.skipped.length} skipped</span>
          {report.skipped.map((s) => <div key={s.path} className="k-reject"><span className="k-mono k-ellipsis">{s.path.split(/[\\/]/).pop()}</span><span className="k-muted">{s.reason}</span></div>)}
        </div>
      )}
    </Modal>
  );
}
