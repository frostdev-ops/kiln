// Deck Catalog: Kiln mods on Nexus Mods without typing (docs/kiln/launcher/deck.md). A row per mod (its state: on
// the Deck, installed, update ready); X switches what is shown (Kiln mods, all Enshrouded mods, installed), Y the order.
// A opens the mod: a short page with Add (premium accounts download here), Update when a newer file is out, and Open
// on Nexus Mods (free accounts download there; its Mod Manager Download comes back to the launcher). An added mod's
// dialog has Undo and Get for what it needs. Notices are a title and buttons.
import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import * as api from "../../api/commands";
import * as nx from "../../api/nexus";
import type { CatalogFilter, CatalogMod, CatalogSort, InstallReport } from "../../api/nexus";
import { offered, useCatalogInstall, useNexusAccount, useNxmLinks } from "../../shell/catalogInstall";
import { toast } from "../../ui";
import type { DeckAction } from "../layers";
import { DeckButton, Dialog, Row, Screen, Word } from "../ui";

const FILTERS: { id: CatalogFilter; label: string }[] = [{ id: "kiln", label: "Kiln mods" }, { id: "all", label: "All Enshrouded" }, { id: "installed", label: "Installed" }];
const SORTS: { id: CatalogSort; label: string }[] = [{ id: "endorsements", label: "Most endorsed" }, { id: "updated", label: "Recently updated" }, { id: "name", label: "Name" }];

function end(m: CatalogMod) {
  if (m.installed?.updateAvailable) return <Word tone="warn">Update</Word>;
  if (m.installed?.inLibrary) return <Word tone="ok">Installed</Word>;
  if (m.kind && m.kind !== "kiln" && m.kind !== "other") return <Word tone="idle">{nx.TYPE_WORD[m.kind].replace(" mod", "")}</Word>;
  return undefined;
}

export default function Catalog() {
  const loc = useLocation();
  const nav = useNavigate();
  const account = useNexusAccount();
  const [f, setF] = useState(0);
  const [s, setS] = useState(0);
  const [open, setOpen] = useState<string | null>(new URLSearchParams(loc.search).get("mod"));
  const [page, setPage] = useState(1);
  const query = { filter: FILTERS[f].id, sort: SORTS[s].id, page: 1, pageSize: 30 * page };
  const list = useQuery({ queryKey: nx.nk.search(query), queryFn: () => nx.nexusSearch(query), retry: false, placeholderData: (p) => p });
  const unchecked = list.data?.unchecked ?? 0;
  useEffect(() => {
    if (!unchecked || list.isFetching) return;
    const t = setTimeout(() => list.refetch(), 400);
    return () => clearTimeout(t);
  }, [unchecked, list.isFetching]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { const m = new URLSearchParams(loc.search).get("mod"); if (m) setOpen(m); }, [loc.search]);

  const actions: DeckAction[] = [
    { button: "x", label: FILTERS[(f + 1) % FILTERS.length].label, run: () => { setF((f + 1) % FILTERS.length); setPage(1); } },
    { button: "y", label: SORTS[(s + 1) % SORTS.length].label, run: () => setS((s + 1) % SORTS.length) },
  ];
  const p = list.data;
  const say = list.isLoading ? "Asking Nexus Mods…" : list.error ? api.toKilnError(list.error).message
    : p?.needsSignIn ? "Sign in to Nexus Mods to find Kiln mods"
    : p ? `${FILTERS[f].label} · ${SORTS[s].label} · ${p.total}${unchecked ? ` · looking at ${unchecked} more` : ""}` : "";

  return (
    <Screen name="catalog" title="Catalog" say={say} actions={actions}>
      {account.data && !account.data.signedIn && (
        <DeckButton id="nexus-sign-in" onClick={() => nav("/deck/settings")}>Sign in to Nexus Mods</DeckButton>
      )}
      {p?.needsSignIn ? (
        <DeckButton id="show-all" primary onClick={() => setF(1)}>Show all Enshrouded mods</DeckButton>
      ) : (
        <div className="deck-list deck-scroll" role="list" aria-label="Catalog">
          {p?.mods.map((m) => (
            <Row key={m.id} id={`cat-${m.id}`} a="Open" title={m.name} end={end(m)}
              sub={`${m.author}${m.version ? ` · ${m.version}` : ""} · ♥ ${nx.count(m.endorsements)}`} onClick={() => setOpen(m.id)} />
          ))}
          {p && p.total > p.mods.length && <Row id="cat-more" title="More" dim a="Load" onClick={() => setPage(page + 1)} />}
          {p && !p.mods.length && <p className="deck-note">{FILTERS[f].id === "installed" ? "Nothing installed from Nexus Mods yet." : "No mods here yet."}</p>}
        </div>
      )}
      {open && <ModDialog id={open} onClose={() => { setOpen(null); if (loc.search) nav("/deck/catalog", { replace: true }); }} onOpen={setOpen} />}
    </Screen>
  );
}

function ModDialog({ id, onClose, onOpen }: { id: string; onClose: () => void; onOpen: (id: string) => void }) {
  const account = useNexusAccount();
  const d = useQuery({ queryKey: nx.nk.mod(id), queryFn: () => nx.nexusMod(id), retry: false });
  const { add } = useCatalogInstall();
  const [busy, setBusy] = useState(false);
  const [report, setReport] = useState<InstallReport | null>(null);
  const m = d.data;
  const acc = account.data;
  const update = !!m?.installed?.updateAvailable;
  if (report) return <AddedDialog report={report} onClose={() => { setReport(null); onClose(); }} onGet={(mid) => { setReport(null); onOpen(mid); }} />;
  const go = async () => {
    if (!m) return;
    setBusy(true);
    try {
      const r = await add(m, m.bestFile, acc, m.filesUrl);
      if (r.kind === "added") setReport(r.report);
      else if (r.kind === "opened") toast.info("Opened on Nexus Mods");
    } finally { setBusy(false); }
  };
  const need = m?.requirements.filter((r) => !r.kiln && !r.otherGame) ?? [];
  return (
    <Dialog name="catalog-mod" wide title={m?.name ?? "Mod"} onClose={onClose} locked={busy}
      // both loaded first: the dialog's first focus goes to its primary button, which needs the account
      buttons={m && acc && <>
        <DeckButton id="mod-close" disabled={busy} onClick={onClose}>Close</DeckButton>
        {id !== "301" && (acc?.premium
          ? <DeckButton id="mod-add" primary autoFocus disabled={busy || !m.bestFile} a={update ? "Update" : "Add"} onClick={go}>{busy ? "Adding…" : update ? `Update to ${m.installed?.latestVersion ?? ""}` : m.installed?.inLibrary ? "Add again" : "Add"}</DeckButton>
          : <DeckButton id="mod-site" primary autoFocus disabled={busy || !acc?.signedIn} a="Open" onClick={go}>Open on Nexus Mods</DeckButton>)}
      </>}>
      {d.isLoading ? <p className="deck-note">Asking Nexus Mods…</p> : d.error ? <p className="deck-note">{api.toKilnError(d.error).message}</p> : m && (
        <div className="deck-catalog-mod">
          <p className="deck-line">{m.author}{m.version ? ` · ${m.version}` : ""} · ♥ {nx.count(m.endorsements)}{m.kind ? ` · ${nx.TYPE_WORD[m.kind]}` : ""}</p>
          {m.installed && <p className="deck-line">{update ? <Word tone="warn">Update ready: {m.installed.latestVersion}</Word> : <Word tone="ok">Installed {m.installed.version}</Word>}</p>}
          {m.summary && <p className="deck-line">{m.summary}</p>}
          {need.length > 0 && <p className="deck-line">Needs {need.map((r) => `${r.name}${r.installed ? " (installed)" : ""}`).join(", ")}</p>}
          {!acc?.premium && acc?.signedIn && <p className="deck-note">Free accounts download on the site.</p>}
        </div>
      )}
    </Dialog>
  );
}

/** An added mod: Undo, and Get for each mod it needs from Nexus Mods. */
export function AddedDialog({ report, onClose, onGet }: { report: InstallReport; onClose: () => void; onGet: (modId: string) => void }) {
  const { undo } = useCatalogInstall();
  const need = offered(report).slice(0, 2);
  const title = report.add.added.length ? `Added ${report.name}` : report.add.already.length ? `${report.name} is on` : `${report.name} is in your list, off`;
  return (
    <Dialog name="catalog-added" title={title} onClose={onClose} backLabel="OK"
      buttons={<>
        <DeckButton id="added-ok" primary autoFocus onClick={onClose}>OK</DeckButton>
        {report.add.changes.length > 0 && <DeckButton id="added-undo" onClick={() => { void undo(report); onClose(); }}>Undo</DeckButton>}
        {need.map((q) => <DeckButton key={q.modId} id={`added-get-${q.modId}`} onClick={() => onGet(q.modId)}>Get {q.name}</DeckButton>)}
      </>} />
  );
}

/** Mod Manager Download links in Deck mode: each added mod shows its dialog (Get opens the Catalog on that mod). */
export function DeckNxm() {
  const nav = useNavigate();
  const [reports, setReports] = useState<InstallReport[]>([]);
  useNxmLinks((r) => setReports((pending) => [...pending, r]));
  const report = reports[0];
  const dismiss = () => setReports((pending) => pending.slice(1));
  if (!report) return null;
  return <AddedDialog report={report} onClose={dismiss} onGet={(mid) => { dismiss(); nav(`/deck/catalog?mod=${mid}`); }} />;
}
