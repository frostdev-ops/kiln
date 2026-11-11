// /library: every imported mod by version. Drop a zip, folder or DLL on the niche: with Settings "Add new mods to the
// active profile" on (the default) it goes the way of a drop anywhere on the window (shell/drop.tsx: import, add to the
// active profile, apply); off: detection, then the import task only. Filter by kind; remove a version (typed); clean
// up versions no profile uses (typed).
import { useQueries, useQuery, useQueryClient } from "@tanstack/react-query";
import { MoreHorizontal } from "lucide-react";
import { useMemo, useState } from "react";
import * as api from "../api/commands";
import type { Detected, ImportReport, LibraryEntry, LibraryRef, ModKind } from "../api/commands";
import { qk, useLibrary, useProfiles, useSettings } from "../api/hooks";
import { ENGINE_KINDS, IMPORTED_KINDS, KIND_NAME, KIND_SHORT, KIND_TONE, NOT_RUNNING_NOTE, isEngineKind, runsYet } from "../api/kinds";
import { useAddMods } from "../shell/drop";
import { ImportDetailsModal, sentence, summarize } from "../shell/importResult";
import {
  Badge, Button, ConfirmDanger, DropZone, EmptyState, Menu, Modal, NameId, Page, PageHeader, Segmented, Select, Skeleton, SplitPage, Table, WellInput,
  errorText, fmtAgo, fmtBytes, runTask, shortHash, toast, type Column,
} from "../ui";

// "engines": every other engine's kind (the Engine select narrows it to one)
type KindFilter = "all" | "engines" | ModKind;
// a zip or folder holding mods in subfolders (a KilnPack's <id>/ folder) detects as unknown with items: the import takes them
const importable = (d: Detected): boolean =>
  ["kiln", "native", "plugin", "emf", "bare-dll", "modpack", ...IMPORTED_KINDS].includes(d.kind) || (d.kind === "unknown" && d.items.some(importable));
const kindMatch = (f: KindFilter, k: ModKind) => f === "all" || f === k || (f === "engines" && isEngineKind(k) && k !== "emf");
// Kiln places an XHL companion in the game's mods folder and never runs it (engine_mods: no plugin for the kind)
const XHL_NOTE = "XHL companion: only XHL-Core, installed separately, loads it. Kiln places it in the game's mods folder and never runs it.";

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
  const profiles = useProfiles();
  const settings = useSettings();
  const addMods = useAddMods();
  const toProfile = settings.data?.addToActiveProfile !== false;

  const all = lib.data ?? [];
  const engineKinds = ENGINE_KINDS.filter((k) => all.some((e) => e.kind === k));
  const engineFilter = kind === "engines" || ENGINE_KINDS.includes(kind as ModKind);
  const rows = useMemo(() => all.filter((e) => kindMatch(kind, e.kind) && (!q || (e.name + e.id).toLowerCase().includes(q.toLowerCase()))), [all, kind, q]);
  const refresh = () => qc.invalidateQueries({ queryKey: ["library_list"] });
  const unusedMods = all.filter((e) => usage.count(e.kind, e.id) === 0).length;

  const flash = (refs: { kind: ModKind; id: string }[]) => {
    setFresh(new Set(refs.map((x) => `${x.kind}/${x.id}`)));
    setTimeout(() => setFresh(new Set()), 2200);
  };
  const onPaths = async (paths: string[]) => {
    if (toProfile) {
      setImporting(true);
      try {
        const r = await addMods(paths);
        if (r) { await refresh(); flash([...r.import.imported, ...r.import.existing]); }
      } finally { setImporting(false); }
      return;
    }
    try {
      const found = await Promise.all(paths.map((p) => api.libraryDetect(p)));
      if (found.some((d) => d.kind === "modpack")) await addMods(paths);
      else setDetected(found);
    }
    catch (e) { toast.error(e, "Can't read that"); }
  };
  const doImport = async (paths: string[]) => {
    setDetected(null);
    setImporting(true);
    try {
      const r = await runTask<ImportReport>(() => api.libraryImport(paths), { failed: "Import failed" });
      await refresh();
      flash(r.imported);
      if (r.skipped.length || r.existing.length || r.notes.length) setReport(r);
      else toast.ok(`Imported ${r.imported.length}`, "Add it to a profile to use it.");
    } catch { /* toasted */ } finally { setImporting(false); }
  };

  const cols: Column<LibraryEntry>[] = [
    // an engine mod whose own id is not a Kiln id (Hoarders Helper): its own id is the one in the game's reports
    { key: "name", header: "Mod", sort: (a, b) => a.name.localeCompare(b.name), render: (e) => (
      <span title={e.versions[0]?.manifestId ? `Library id ${e.id}; the mod's own id is ${e.versions[0].manifestId}` : undefined}>
        <NameId name={e.name} id={e.versions[0]?.manifestId ?? e.id} />
      </span>
    ) },
    { key: "kind", header: "Kind", width: engineKinds.some((k) => !runsYet(k)) ? "150px" : engineKinds.length ? "96px" : "56px", sort: (a, b) => a.kind.localeCompare(b.kind),
      render: (e) => (
        <span className="k-row" style={{ gap: 4 }} data-not-running={runsYet(e.kind) ? undefined : true}>
          <Badge tone={KIND_TONE[e.kind]} title={!runsYet(e.kind) ? NOT_RUNNING_NOTE[e.kind] : e.kind === "xhl" ? XHL_NOTE : isEngineKind(e.kind) && e.kind !== "emf" ? `${KIND_NAME[e.kind]} mod, run by Kiln` : undefined}>{KIND_SHORT[e.kind]}</Badge>
          {!runsYet(e.kind) && <Badge tone="warn" title={NOT_RUNNING_NOTE[e.kind]}>not yet</Badge>}
        </span>
      ) },
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

  // The version the Remove dialog is about, and what lists it: the backend sees the games' own profile files too (the
  // game reads those), which the profiles here do not show. Remove goes past exactly what the dialog named (the backend
  // checks it); Confirm waits for the answer.
  const rmRef: LibraryRef | null = useMemo(() => {
    const v = rm?.e.versions.find((x) => x.version === rm.version);
    return rm && v ? { kind: rm.e.kind, id: rm.e.id, version: v.version, hash: v.hash } : null;
  }, [rm]);
  const rmUsers = useQuery({ queryKey: ["library_users", rmRef], queryFn: () => api.libraryUsers(rmRef!), enabled: !!rmRef, staleTime: 0, gcTime: 0, retry: 1 });
  const profileName = (id: string) => profiles.data?.find((p) => p.id === id)?.name ?? id;
  // "work" or "work (active in Enshrouded)": the profile by its name
  const userLabel = (u: string) => {
    const m = /^(.*) \((active|play-once) in (.*)\)$/.exec(u);
    return m ? `${profileName(m[1])} (${m[2]} in ${m[3]})` : profileName(u);
  };
  // what the dialog names and the removal may go past: the backend's list only. A failed check names nothing, so
  // removal stops at any user (a conflict) instead of forcing past users nobody saw.
  const rmShown = rmUsers.data ?? [];
  const rmUsedBy = rmShown.map(userLabel);
  const rmMessage = rmUsers.isError ? <>Kiln could not check what uses it ({errorText(rmUsers.error)}). Remove stops if something does.</>
    : !rmUsers.data ? "Checking what uses it."
    : rmUsedBy.length ? <>Used by {rmUsedBy.join(", ")}. They will show it as missing.</> : "Its files leave the library.";

  // The backend knows what profiles and the games' own profile files list (the game reads those), so it names what is unused.
  const cleanUp = async () => {
    try { setGc(await api.libraryUnused()); }
    catch (e) { toast.error(e); }
  };
  const say = lib.isLoading ? "Reading the library." : lib.error ? errorText(lib.error)
    : !all.length ? "Nothing imported yet. Drop a mod on the niche."
    : `${all.length} mod${all.length === 1 ? "" : "s"} imported.${usage.ready ? ` ${unusedMods ? `${unusedMods} ${unusedMods === 1 ? "is" : "are"} in no profile.` : "Every one is in a profile."}` : ""}`;

  return (
    <Page fill>
      <SplitPage aside={<>
        <DropZone onPaths={onPaths} title="Drop mods" hint="Zip, folder or DLL. Kiln, native, EMF, plugins, modpacks, and other engines' mods." pickFolder busy={importing ? "Firing" : undefined}
          pick={{ filters: [{ name: "Mods", extensions: ["zip", "dll"] }] }} />
        <p className="k-hint" style={{ fontSize: 16 }}>{toProfile ? "It goes into the active profile too. Drops anywhere on the window do the same." : "Kiln reads what it is first, then imports it by version. Nothing reaches a profile until you add it."}</p>
      </>}>
        <PageHeader title="Library" sub={say} actions={<Button onClick={cleanUp} disabled={!all.length}>Clean up</Button>} />
        <div className="k-row" style={{ gap: "8px 20px" }}>
          <Segmented aria-label="Kind" value={engineFilter ? "engines" : kind} onChange={setKind} options={[{ id: "all", label: "All" }, { id: "kiln", label: "Kiln" }, { id: "native", label: "Native" }, { id: "emf", label: "EMF" }, { id: "plugin", label: "Plugin" },
            ...(engineKinds.length ? [{ id: "engines" as const, label: "Other engines" }] : [])]} />
          {engineFilter && engineKinds.length > 1 && (
            <Select aria-label="Engine" value={kind} onChange={(v) => setKind(v as KindFilter)} style={{ height: 32, width: 200 }}
              options={[{ value: "engines", label: "Every engine" }, ...engineKinds.map((k) => ({ value: k, label: KIND_NAME[k] }))]} />
          )}
          <WellInput label="Filter" compact value={q} onChange={(e) => setQ(e.target.value)} wellStyle={{ flex: "1 1 140px", marginLeft: "auto" }} />
        </div>
        {lib.isLoading ? <Skeleton lines={6} /> : lib.error ? <EmptyState title="The library is not readable" hint={errorText(lib.error)} /> : (
          <Table aria-label="Library" fill columns={cols} rows={rows} rowKey={(e) => `${e.kind}/${e.id}`} rowClass={(e) => (fresh.has(`${e.kind}/${e.id}`) ? "fresh" : undefined)}
            empty={<EmptyState title={q || kind !== "all" ? "No mod matches that." : "Nothing imported yet."} />} />
        )}
      </SplitPage>

      <DetectModal detected={detected} onClose={() => setDetected(null)} onImport={doImport} />
      <ReportModal report={report} onClose={() => setReport(null)} />
      <ConfirmDanger open={!!rm} onClose={() => setRm(null)} title={`Remove ${rm?.e.name} ${rm?.version}?`} word={rm?.e.id ?? ""} action="Remove" relaxable
        message={rmMessage} disabled={rmUsers.isPending || rmUsers.isFetching}
        onConfirm={async () => {
          // past only what the dialog named: the backend refuses (conflict) a user the dialog did not show
          try { await api.libraryRemove(rmRef!, false, rmShown); toast.ok("Removed"); refresh(); }
          catch (e) { toast.error(e); void rmUsers.refetch(); throw e; }
        }} />
      <ConfirmDanger open={!!gc && gc.length > 0} onClose={() => setGc(null)} title={`Remove ${gc?.length ?? 0} unused?`} word="clean" action="Remove"
        message="They are in no profile. Profiles and saves stay as they are."
        onConfirm={async () => {
          // one call: the backend looks at the running processes and the profiles once for the whole batch
          try {
            const r = await api.libraryRemoveMany(gc ?? []);
            for (const f of r.failed) toast.error(f.error, `${f.entry.id} ${f.entry.version}`);
            const n = r.removed.length;
            if (n) toast.ok(`Removed ${n}`, `${n} version${n === 1 ? "" : "s"} left the library.`);
          } catch (e) {
            // a running game stops the whole clean up before anything goes ("Close Enshrouded first: ...")
            toast.error(e);
          }
          refresh();
        }} />
      {gc && gc.length === 0 && <Modal open onClose={() => setGc(null)} title="Nothing to clean" width={480} footer={<Button onClick={() => setGc(null)}>OK</Button>}>Every version is in a profile, or in the profile file of a game.</Modal>}
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
              {sentence(d.reason)}
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
  const sum = report ? summarize(report) : null;
  return (
    <ImportDetailsModal onClose={onClose}
      data={sum ? { heading: `${report!.imported.length} imported, ${report!.existing.length} already there`, summary: sum } : null} />
  );
}
