// /library: every imported mod by version. Drop a zip, folder or DLL on the niche (detection, then the import task);
// filter by kind; remove a version (typed); clean up versions no profile uses (typed).
import { useQueries, useQueryClient } from "@tanstack/react-query";
import { MoreHorizontal } from "lucide-react";
import { useMemo, useState } from "react";
import * as api from "../api/commands";
import type { Detected, ImportReport, LibraryEntry, ModKind } from "../api/commands";
import { qk, useLibrary, useProfiles } from "../api/hooks";
import {
  Badge, Button, ConfirmDanger, DropZone, EmptyState, Menu, Modal, NameId, Page, PageHeader, Segmented, Skeleton, SplitPage, Table, WellInput,
  errorText, fmtAgo, fmtBytes, runTask, shortHash, toast, type Column, type Tone,
} from "../ui";

type KindFilter = "all" | ModKind;
const KIND_LABEL: Record<ModKind, string> = { kiln: "Kiln", native: "Native", emf: "EMF", plugin: "Plugin" };
const KIND_TONE: Record<ModKind, Tone> = { kiln: "ember", native: "info", emf: "ok", plugin: "idle" };
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
  const [importing, setImporting] = useState(false);
  const [fresh, setFresh] = useState<Set<string>>(new Set());
  const usage = useUsage();

  const all = lib.data ?? [];
  const rows = useMemo(() => all.filter((e) => (kind === "all" || e.kind === kind) && (!q || (e.name + e.id).toLowerCase().includes(q.toLowerCase()))), [all, kind, q]);
  const refresh = () => qc.invalidateQueries({ queryKey: ["library_list"] });
  const unusedMods = all.filter((e) => usage.count(e.kind, e.id) === 0).length;

  const onPaths = async (paths: string[]) => {
    try { setDetected(await Promise.all(paths.map((p) => api.libraryDetect(p)))); }
    catch (e) { toast.error(e, "Can't read that"); }
  };
  const doImport = async (paths: string[]) => {
    setDetected(null);
    setImporting(true);
    try {
      const r = await runTask<ImportReport>(() => api.libraryImport(paths), { failed: "Import failed" });
      await refresh();
      setFresh(new Set(r.imported.map((x) => `${x.kind}/${x.id}`)));
      setTimeout(() => setFresh(new Set()), 2200);
      if (r.skipped.length || r.existing.length) setReport(r);
      else toast.ok(`Imported ${r.imported.length}`, "Add it to a profile to use it.");
    } catch { /* toasted */ } finally { setImporting(false); }
  };

  const cols: Column<LibraryEntry>[] = [
    { key: "name", header: "Mod", sort: (a, b) => a.name.localeCompare(b.name), render: (e) => <NameId name={e.name} id={e.id} /> },
    { key: "kind", header: "Kind", width: "56px", sort: (a, b) => a.kind.localeCompare(b.kind), render: (e) => <Badge tone={KIND_TONE[e.kind]}>{KIND_LABEL[e.kind]}</Badge> },
    { key: "ver", header: "Version", width: "150px", render: (e) => {
      const v = e.versions[0];
      return v ? (
        <span className="k-mono" style={{ fontSize: 14.5 }} title={e.versions.map((x) => `${x.version} +${shortHash(x.hash)}`).join("\n")}>
          {v.version} <span className="k-muted">+{shortHash(v.hash)}</span>{e.versions.length > 1 && <span className="k-muted"> +{e.versions.length - 1}</span>}
        </span>
      ) : "-";
    } },
    { key: "side", header: "Side", width: "52px", render: (e) => <span style={{ fontSize: 15.5, color: "#d0beac" }}>{e.versions[0]?.side ?? "-"}</span> },
    { key: "used", header: "Used", width: "38px", align: "right", sort: (a, b) => usage.count(a.kind, a.id) - usage.count(b.kind, b.id), render: (e) => {
      const n = usage.count(e.kind, e.id);
      return <span className="k-mono" style={{ color: n ? "var(--k-text-0)" : "#937f6d" }} title={n ? usage.names(e.kind, e.id).join(", ") : "In no profile"}>{n}</span>;
    } },
    { key: "size", header: "Size", width: "62px", align: "right", render: (e) => <span className="k-mono" style={{ color: "#d0beac" }}>{fmtBytes(e.versions[0]?.size)}</span> },
    { key: "when", header: <span style={{ paddingLeft: 12 }}>Added</span>, width: "96px", sort: (a, b) => (a.versions[0]?.imported ?? "").localeCompare(b.versions[0]?.imported ?? ""),
      render: (e) => <span className="k-muted" style={{ paddingLeft: 12, fontSize: 15.5 }}>{fresh.has(`${e.kind}/${e.id}`) ? "just now" : fmtAgo(e.versions[0]?.imported)}</span> },
    { key: "act", header: <span className="k-sr-only">Actions</span>, width: "30px", align: "right", render: (e) => (
      <Menu label={`More for ${e.name}`} icon={<MoreHorizontal />} size="s" items={e.versions.map((v) => ({ label: `Remove ${v.version}`, danger: true, onSelect: () => setRm({ e, version: v.version }) }))} />
    ) },
  ];

  const unused = () => all.flatMap((e) => e.versions.filter((v) => !usage.uses(e.kind, e.id, v.version)).map((v) => ({ kind: e.kind, id: e.id, version: v.version, hash: v.hash })));
  const say = lib.isLoading ? "Reading the library." : lib.error ? errorText(lib.error)
    : !all.length ? "Nothing imported yet. Drop a mod on the niche."
    : `${all.length} mod${all.length === 1 ? "" : "s"} imported.${usage.ready ? ` ${unusedMods ? `${unusedMods} ${unusedMods === 1 ? "is" : "are"} in no profile.` : "Every one is in a profile."}` : ""}`;

  return (
    <Page fill>
      <SplitPage aside={<>
        <DropZone onPaths={onPaths} title="Drop mods" hint="Zip, folder or DLL. Kiln, native, EMF, plugins, modpacks." pickFolder busy={importing ? "Firing" : undefined}
          pick={{ filters: [{ name: "Mods", extensions: ["zip", "dll"] }] }} />
        <p className="k-hint" style={{ fontSize: 16 }}>Kiln reads what it is first, then imports it by version. Nothing reaches a profile until you add it.</p>
      </>}>
        <PageHeader title="Library" sub={say} actions={<Button onClick={() => setGc(unused())} disabled={!usage.ready || !all.length}>Clean up</Button>} />
        <div className="k-row" style={{ gap: "8px 20px" }}>
          <Segmented aria-label="Kind" value={kind} onChange={setKind} options={[{ id: "all", label: "All" }, { id: "kiln", label: "Kiln" }, { id: "native", label: "Native" }, { id: "emf", label: "EMF" }, { id: "plugin", label: "Plugin" }]} />
          <WellInput label="Filter" compact value={q} onChange={(e) => setQ(e.target.value)} wellStyle={{ flex: "1 1 140px", marginLeft: "auto" }} />
        </div>
        {lib.isLoading ? <Skeleton lines={6} /> : lib.error ? <EmptyState title="The library is not readable" hint={errorText(lib.error)} /> : (
          <Table aria-label="Library" fill columns={cols} rows={rows} rowKey={(e) => `${e.kind}/${e.id}`} rowClass={(e) => (fresh.has(`${e.kind}/${e.id}`) ? "fresh" : undefined)}
            empty={<EmptyState title={q || kind !== "all" ? "No mod matches that." : "Nothing imported yet."} />} />
        )}
      </SplitPage>

      <DetectModal detected={detected} onClose={() => setDetected(null)} onImport={doImport} />
      <ReportModal report={report} onClose={() => setReport(null)} />
      <ConfirmDanger open={!!rm} onClose={() => setRm(null)} title={`Remove ${rm?.e.name} ${rm?.version}?`} word={rm?.e.id ?? ""} action="Remove"
        message={rm && usage.uses(rm.e.kind, rm.e.id, rm.version) ? <>Profiles use it: {usage.names(rm.e.kind, rm.e.id).join(", ")}. They will show it as missing.</> : "Its files leave the library."}
        onConfirm={async () => {
          const v = rm!.e.versions.find((x) => x.version === rm!.version)!;
          try { await api.libraryRemove({ kind: rm!.e.kind, id: rm!.e.id, version: v.version, hash: v.hash }, usage.uses(rm!.e.kind, rm!.e.id, v.version)); toast.ok("Removed"); refresh(); }
          catch (e) { toast.error(e); throw e; }
        }} />
      <ConfirmDanger open={!!gc && gc.length > 0} onClose={() => setGc(null)} title={`Remove ${gc?.length ?? 0} unused?`} word="clean" action="Remove"
        message="They are in no profile. Profiles and saves stay as they are."
        onConfirm={async () => {
          let n = 0;
          for (const g of gc ?? []) { try { await api.libraryRemove(g, false); n++; } catch (e) { toast.error(e, `${g.id} ${g.version}`); } }
          toast.ok(`Removed ${n}`, `${n} version${n === 1 ? "" : "s"} left the library.`); refresh();
        }} />
      {gc && gc.length === 0 && <Modal open onClose={() => setGc(null)} title="Nothing to clean" width={480} footer={<Button onClick={() => setGc(null)}>OK</Button>}>Every version is in a profile.</Modal>}
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
      <div className="k-list">
        {(detected ?? []).map((d) => (
          <div key={d.path} className="k-list-item" style={{ alignItems: "baseline", flexWrap: "wrap", rowGap: 0 }}>
            <NameId name={d.name ?? d.id ?? d.path.split(/[\\/]/).pop()} id={d.version ?? undefined} />
            <Badge tone={importable(d) ? "ember" : "idle"}>{d.kind}</Badge>
            <span className="k-hint" style={{ flexBasis: "100%" }}>
              {d.reason}
              {d.kind === "runtime" && " Runtime bundles go on the Runtime page."}
              {d.items.length > 0 && ` ${d.items.length} inside: ${d.items.map((i) => i.id ?? i.name).join(", ")}.`}
            </span>
          </div>
        ))}
      </div>
    </Modal>
  );
}

function ReportModal({ report, onClose }: { report: ImportReport | null; onClose: () => void }) {
  return (
    <Modal open={!!report} onClose={onClose} title="Imported" width={520} footer={<Button onClick={onClose}>OK</Button>}>
      {report && (
        <div className="k-stack">
          <span>{report.imported.length} imported, {report.existing.length} already there, {report.skipped.length} skipped.</span>
          {report.skipped.map((s) => (
            <div key={s.path} className="k-list-item"><span className="k-mono k-ellipsis">{s.path.split(/[\\/]/).pop()}</span><span className="k-muted">{s.reason}</span></div>
          ))}
        </div>
      )}
    </Modal>
  );
}
