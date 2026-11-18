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
import { t, useT } from "../../i18n";

const FILTERS: CatalogFilter[] = ["kiln", "all", "installed"];
const SORTS = ["endorsements", "updated", "name"] as const satisfies readonly CatalogSort[];

function filterLabel(f: CatalogFilter): string {
  switch (f) {
    case "kiln": return t("deck.catalog.filterKiln");
    case "all": return t("deck.catalog.filterAll");
    case "installed": return t("deck.catalog.filterInstalled");
  }
}

function sortLabel(s: (typeof SORTS)[number]): string {
  switch (s) {
    case "endorsements": return t("deck.catalog.sortEndorsed");
    case "updated": return t("deck.catalog.sortUpdated");
    case "name": return t("deck.catalog.sortName");
  }
}

function end(m: CatalogMod) {
  if (m.installed?.updateAvailable) return <Word tone="warn">{t("common.update")}</Word>;
  if (m.installed?.inLibrary) return <Word tone="ok">{t("deck.catalog.installed")}</Word>;
  const word = typeShort(m.kind);
  if (word) return <Word tone="idle">{word}</Word>;
  return undefined;
}

/** A Nexus mod's type in one word (Kiln's own and "other" say nothing). */
function typeShort(kind: nx.ModType | null | undefined): string | null {
  switch (kind) {
    case "native": return t("deck.catalog.typeNative");
    case "emf": return t("deck.catalog.typeEmf");
    case "dll": return t("deck.catalog.typeDll");
    case "modpack": return t("deck.catalog.typeModpack");
    default: return null;
  }
}

export default function Catalog() {
  const t = useT();
  const loc = useLocation();
  const nav = useNavigate();
  const account = useNexusAccount();
  const [f, setF] = useState(0);
  const [s, setS] = useState(0);
  const [open, setOpen] = useState<string | null>(new URLSearchParams(loc.search).get("mod"));
  const [page, setPage] = useState(1);
  const query = { filter: FILTERS[f], sort: SORTS[s], page: 1, pageSize: 30 * page };
  const list = useQuery({ queryKey: nx.nk.search(query), queryFn: () => nx.nexusSearch(query), retry: false, placeholderData: (p) => p });
  const unchecked = list.data?.unchecked ?? 0;
  useEffect(() => {
    if (!unchecked || list.isFetching) return;
    const timer = setTimeout(() => list.refetch(), 400);
    return () => clearTimeout(timer);
  }, [unchecked, list.isFetching]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { const m = new URLSearchParams(loc.search).get("mod"); if (m) setOpen(m); }, [loc.search]);

  const actions: DeckAction[] = [
    { button: "x", label: filterLabel(FILTERS[(f + 1) % FILTERS.length]), run: () => { setF((f + 1) % FILTERS.length); setPage(1); } },
    { button: "y", label: sortLabel(SORTS[(s + 1) % SORTS.length]), run: () => setS((s + 1) % SORTS.length) },
  ];
  const p = list.data;
  const say = list.isLoading ? t("deck.catalog.asking") : list.error ? api.toKilnError(list.error).message
    : p?.needsSignIn ? t("deck.catalog.signInToFind")
    : p ? (unchecked ? t("deck.catalog.sayLooking", { filter: filterLabel(FILTERS[f]), sort: sortLabel(SORTS[s]), total: p.total, more: unchecked })
      : t("deck.catalog.say", { filter: filterLabel(FILTERS[f]), sort: sortLabel(SORTS[s]), total: p.total })) : "";

  return (
    <Screen name="catalog" title={t("deck.app.tabs.catalog")} say={say} actions={actions}>
      {account.data && !account.data.signedIn && (
        <DeckButton id="nexus-sign-in" onClick={() => nav("/deck/settings")}>{t("deck.catalog.signIn")}</DeckButton>
      )}
      {p?.needsSignIn ? (
        <DeckButton id="show-all" primary onClick={() => setF(1)}>{t("deck.catalog.showAll")}</DeckButton>
      ) : (
        <div className="deck-list deck-scroll" role="list" aria-label={t("deck.app.tabs.catalog")}>
          {p?.mods.map((m) => (
            <Row key={m.id} id={`cat-${m.id}`} a={t("common.open")} title={m.name} end={end(m)}
              sub={`${m.author}${m.version ? ` · ${m.version}` : ""} · ♥ ${nx.count(m.endorsements)}`} onClick={() => setOpen(m.id)} />
          ))}
          {p && p.total > p.mods.length && <Row id="cat-more" title={t("deck.catalog.more")} dim a={t("deck.dialogs.load")} onClick={() => setPage(page + 1)} />}
          {p && !p.mods.length && <p className="deck-note">{FILTERS[f] === "installed" ? t("deck.catalog.nothingInstalled") : t("deck.catalog.noModsYet")}</p>}
        </div>
      )}
      {open && <ModDialog id={open} onClose={() => { setOpen(null); if (loc.search) nav("/deck/catalog", { replace: true }); }} onOpen={setOpen} />}
    </Screen>
  );
}

function ModDialog({ id, onClose, onOpen }: { id: string; onClose: () => void; onOpen: (id: string) => void }) {
  const t = useT();
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
      else if (r.kind === "opened") toast.info(t("deck.catalog.opened"));
    } finally { setBusy(false); }
  };
  const need = m?.requirements.filter((r) => !r.kiln && !r.otherGame) ?? [];
  return (
    <Dialog name="catalog-mod" wide title={m?.name ?? t("deck.catalog.mod")} onClose={onClose} locked={busy}
      // both loaded first: the dialog's first focus goes to its primary button, which needs the account
      buttons={m && acc && <>
        <DeckButton id="mod-close" disabled={busy} onClick={onClose}>{t("common.close")}</DeckButton>
        {id !== "301" && (acc?.premium
          ? <DeckButton id="mod-add" primary autoFocus disabled={busy || !m.bestFile} a={update ? t("common.update") : t("common.add")} onClick={go}>{busy ? t("deck.catalog.adding") : update ? t("deck.shared.updateTo", { version: m.installed?.latestVersion ?? "" }) : m.installed?.inLibrary ? t("deck.shared.addAgain") : t("common.add")}</DeckButton>
          : <DeckButton id="mod-site" primary autoFocus disabled={busy || !acc?.signedIn} a={t("common.open")} onClick={go}>{t("deck.catalog.openOnNexus")}</DeckButton>)}
      </>}>
      {d.isLoading ? <p className="deck-note">{t("deck.catalog.asking")}</p> : d.error ? <p className="deck-note">{api.toKilnError(d.error).message}</p> : m && (
        <div className="deck-catalog-mod">
          <p className="deck-line">{m.author}{m.version ? ` · ${m.version}` : ""} · ♥ {nx.count(m.endorsements)}{m.kind ? ` · ${nx.TYPE_WORD[m.kind]}` : ""}</p>
          {m.installed && <p className="deck-line">{update ? <Word tone="warn">{t("deck.catalog.updateReady", { version: m.installed.latestVersion ?? "" })}</Word> : <Word tone="ok">{t("deck.catalog.installedVersion", { version: m.installed.version ?? "" })}</Word>}</p>}
          {m.summary && <p className="deck-line">{m.summary}</p>}
          {need.length > 0 && <p className="deck-line">{t("deck.catalog.needs", { mods: need.map((r) => r.installed ? t("deck.catalog.needInstalled", { name: r.name }) : r.name).join(", ") })}</p>}
          {!acc?.premium && acc?.signedIn && <p className="deck-note">{t("deck.catalog.freeAccounts")}</p>}
        </div>
      )}
    </Dialog>
  );
}

/** An added mod: Undo, and Get for each mod it needs from Nexus Mods. */
export function AddedDialog({ report, onClose, onGet }: { report: InstallReport; onClose: () => void; onGet: (modId: string) => void }) {
  const t = useT();
  const { undo } = useCatalogInstall();
  const need = offered(report).slice(0, 2);
  const title = report.add.added.length ? t("deck.catalog.added", { name: report.name }) : report.add.already.length ? t("deck.shared.isOn", { name: report.name }) : t("deck.catalog.inListOff", { name: report.name });
  return (
    <Dialog name="catalog-added" title={title} onClose={onClose} backLabel={t("common.ok")}
      buttons={<>
        <DeckButton id="added-ok" primary autoFocus onClick={onClose}>{t("common.ok")}</DeckButton>
        {report.add.changes.length > 0 && <DeckButton id="added-undo" onClick={() => { void undo(report); onClose(); }}>{t("common.undo")}</DeckButton>}
        {need.map((q) => <DeckButton key={q.modId} id={`added-get-${q.modId}`} onClick={() => onGet(q.modId)}>{t("deck.catalog.get", { name: q.name })}</DeckButton>)}
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
