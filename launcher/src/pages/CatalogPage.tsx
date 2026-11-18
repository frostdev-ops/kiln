// /catalog: Enshrouded's mods on Nexus Mods, Kiln mods first (docs/kiln/launcher/catalog.md). Search, filter (Kiln
// mods, every Enshrouded mod, installed), sort; a card per mod with its state (installed, update ready); the details
// dialog (pictures, description, requirements, files) with Add: premium accounts download here, free accounts get the
// file's page on Nexus Mods (its Mod Manager Download comes back to the launcher). Updates are a click, never automatic.
// `?mod=<id>` opens a mod's details (the "Get <requirement>" toast action).
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ExternalLink, Heart, Download, RefreshCw } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import * as api from "../api/commands";
import * as nx from "../api/nexus";
import type { CatalogFilter, CatalogMod, CatalogSort } from "../api/nexus";
import { offered, useCatalogInstall, useNexusAccount, useReportToast } from "../shell/catalogInstall";
import { checkModUpdates, openUpdatePage, useModUpdates } from "../shell/modUpdates";
import { hasUpdate, pageOf } from "../api/updates";
import { t, useT } from "../i18n";
import { Badge, Banner, Button, EmptyState, KeyValue, Modal, Page, PageHeader, Segmented, Select, Skeleton, WellInput, errorText, fmtAgo, toast, type Tone } from "../ui";
import "./catalog.css";

const TYPE_TONE: Record<nx.ModType, Tone> = { kiln: "ember", native: "info", emf: "ok", dll: "idle", modpack: "info", other: "idle" };
const PAGE = 24;

export function stateWord(m: CatalogMod): { word: string; tone: Tone } | null {
  if (m.installed?.updateAvailable) {
    const v = (m.installed.latestVersion ?? "").trim();
    return { word: v ? t("catalog.updateTo", { version: v }) : t("common.update"), tone: "warn" };
  }
  if (m.installed?.inLibrary) return { word: t("catalog.installed"), tone: "ok" };
  return null;
}

export default function CatalogPage() {
  const t = useT();
  const qc = useQueryClient();
  const nav = useNavigate();
  const [params, setParams] = useSearchParams();
  const account = useNexusAccount();
  const [filter, setFilter] = useState<CatalogFilter>("kiln");
  const [sort, setSort] = useState<CatalogSort>("endorsements");
  const [text, setText] = useState("");
  const [typed, setTyped] = useState("");
  const [page, setPage] = useState(1);
  const open = params.get("mod");
  const setOpen = (id: string | null) => setParams(id ? { mod: id } : {}, { replace: !id });

  useEffect(() => { const timer = setTimeout(() => { setText(typed); setPage(1); }, 250); return () => clearTimeout(timer); }, [typed]);
  const query = useMemo(() => ({ text, filter, sort, page, pageSize: PAGE }), [text, filter, sort, page]);
  const list = useQuery({ queryKey: nx.nk.search(query), queryFn: () => nx.nexusSearch(query), retry: false, placeholderData: (p) => p });
  // the mods installed from the catalog (their count and the Installed filter)
  const installed = useQuery({ queryKey: nx.nk.installed, queryFn: () => nx.nexusInstalled(false), retry: false, staleTime: 5 * 60_000 });
  // their newest versions come from the shared update check (shell/modUpdates.tsx; Nexus answers are cached 30 min per
  // file in the backend): Nexus Mods is asked once when the page opens, as before; GitHub sources only show what is
  // remembered (they are asked by "Check for updates" or the daily check, never by opening this page)
  const modUpdates = useModUpdates();
  useEffect(() => { void checkModUpdates(qc, { providers: ["nexus"] }).then(() => qc.invalidateQueries({ queryKey: nx.nk.installed })).catch(() => {}); }, [qc]);
  // the Kiln filter looks at a batch of mods per call: ask again until none waits
  const unchecked = list.data?.unchecked ?? 0;
  useEffect(() => {
    if (!unchecked || list.isFetching) return;
    const timer = setTimeout(() => list.refetch(), 400);
    return () => clearTimeout(timer);
  }, [unchecked, list.isFetching]); // eslint-disable-line react-hooks/exhaustive-deps

  const refresh = async () => {
    try {
      await nx.nexusSearch({ ...query, refresh: true });
      qc.invalidateQueries({ queryKey: ["nexus_search"] });
      qc.invalidateQueries({ queryKey: nx.nk.installed });
      void checkModUpdates(qc, { refresh: true, providers: ["nexus"] }).catch(() => {});
    } catch (e) { toast.error(e, t("catalog.noAnswer")); }
  };

  const acc = account.data;
  // the banner: the Nexus Mods mods of the shared result with a newer version or a new upload
  const updates = (modUpdates.data?.mods ?? []).filter((m) => m.source?.provider === "nexus" && hasUpdate(m));
  const p = list.data;
  const pages = p ? Math.max(1, Math.ceil(p.total / p.pageSize)) : 1;
  const total = !p ? "" : filter === "kiln" ? t("catalog.countKiln", { count: p.total }) : filter === "installed" ? t("catalog.countInstalled", { count: p.total }) : t("catalog.countAll", { count: p.total });
  const say = list.isLoading ? t("catalog.asking") : list.error ? errorText(list.error)
    : p?.needsSignIn ? t("catalog.signInToFind")
    : p ? (unchecked ? `${total} ${t("catalog.lookingMore", { count: unchecked })}` : total) : "";

  return (
    <Page fill>
      <PageHeader title={t("catalog.title")} sub={say} actions={<>
        <Button icon={<RefreshCw />} loading={list.isFetching && !list.isLoading} onClick={refresh}>{t("common.refresh")}</Button>
        <Button variant="ghost" icon={<ExternalLink />} onClick={() => void api.appOpenUrl("https://www.nexusmods.com/games/enshrouded/mods").catch((e) => toast.error(e))}>Nexus Mods</Button>
      </>} />

      {acc && !acc.signedIn && (
        <Banner tone="info" title={t("catalog.signIn")} actions={<Button size="s" onClick={() => nav("/settings#nexus")}>{t("catalog.settings")}</Button>}>
          {t("catalog.connect")}
        </Banner>
      )}
      {acc?.error && <Banner tone="warn" title={acc.error.code === "auth_failed" ? t("catalog.signInAgain") : t("catalog.accountCheckFailed")} actions={<Button size="s" onClick={() => nav("/settings#nexus")}>{t("catalog.settings")}</Button>}>{acc.error.message}</Banner>}
      {updates.length > 0 && (
        <Banner tone="warn" title={updates.length === 1 ? t("catalog.updateReady", { name: updates[0].name }) : t("catalog.updatesReady", { count: updates.length })}
          actions={<>
            {updates.length === 1 && pageOf(updates[0]) && <Button size="s" variant="ghost" icon={<ExternalLink />} onClick={() => openUpdatePage(updates[0])}>{t("catalog.openPage")}</Button>}
            <Button size="s" onClick={() => { setFilter("installed"); setPage(1); }}>{t("common.show")}</Button>
          </>}>
          {updates.map((u) => {
            const a = { name: u.name, from: u.installedVersion, to: u.candidateVersion ?? t("catalog.newUpload") };
            return u.stale ? t("catalog.updateLineStale", a) : t("catalog.updateLine", a);
          }).join(" · ")}
        </Banner>
      )}

      <div className="k-row" style={{ gap: "8px 20px", flexWrap: "wrap" }}>
        <Segmented aria-label={t("catalog.showLabel")} value={filter} onChange={(f) => { setFilter(f); setPage(1); }} options={[
          { id: "kiln", label: t("catalog.filterKiln") }, { id: "all", label: t("catalog.filterAll") }, { id: "installed", label: installed.data?.length ? t("catalog.filterInstalledCount", { count: installed.data.length }) : t("catalog.installed") },
        ]} />
        <Select aria-label={t("catalog.sort")} value={sort} onChange={(v) => { setSort(v as CatalogSort); setPage(1); }} style={{ width: 170 }} options={[
          { value: "endorsements", label: t("catalog.sortEndorsed") }, { value: "updated", label: t("catalog.sortUpdated") }, { value: "new", label: t("catalog.sortNew") },
          { value: "downloads", label: t("catalog.sortDownloads") }, { value: "name", label: t("catalog.sortName") },
        ]} />
        <WellInput label={t("catalog.search")} compact value={typed} onChange={(e) => setTyped(e.target.value)} onClear={() => setTyped("")} wellStyle={{ flex: "1 1 180px", marginLeft: "auto" }} />
      </div>

      {list.isLoading ? <Skeleton lines={6} /> : list.error ? <EmptyState title={t("catalog.noAnswer")} hint={errorText(list.error)} action={<Button onClick={() => list.refetch()}>{t("catalog.tryAgain")}</Button>} />
        : p?.needsSignIn ? <EmptyState title={t("catalog.signInShort")} hint={t("catalog.signInShortHint")} action={<Button onClick={() => setFilter("all")}>{t("catalog.showAll")}</Button>} />
        : !p?.mods.length ? <EmptyState title={text ? t("catalog.noMatch") : filter === "installed" ? t("catalog.noneInstalled") : unchecked ? t("catalog.looking") : t("catalog.noMods")} />
        : (
          <div className="k-cat-grid" role="list" aria-label={t("catalog.mods")}>
            {p.mods.map((m) => <ModCard key={m.id} m={m} onOpen={() => setOpen(m.id)} />)}
          </div>
        )}
      {p && pages > 1 && (
        <div className="k-row" style={{ justifyContent: "center", gap: 12 }}>
          <Button size="s" disabled={page <= 1} onClick={() => setPage(page - 1)}>{t("catalog.previous")}</Button>
          <span className="k-muted">{t("catalog.page", { page, pages })}</span>
          <Button size="s" disabled={page >= pages} onClick={() => setPage(page + 1)}>{t("catalog.next")}</Button>
        </div>
      )}
      {open && <DetailsModal id={open} onClose={() => setOpen(null)} />}
    </Page>
  );
}

function ModCard({ m, onOpen }: { m: CatalogMod; onOpen: () => void }) {
  const t = useT();
  const s = stateWord(m);
  return (
    <button type="button" role="listitem" className="k-cat-card" onClick={onOpen} aria-label={m.name} data-mod={m.id}>
      <span className="k-cat-thumb">{m.thumbnail ? <img src={m.thumbnail} alt="" loading="lazy" /> : null}</span>
      <span className="k-cat-body">
        <span className="k-cat-name">{m.name}</span>
        <span className="k-cat-by">{m.author}{m.version ? ` · ${m.version}` : ""}</span>
        <span className="k-cat-sum">{m.summary}</span>
        <span className="k-cat-foot">
          <span className="k-cat-stat" title={t("catalog.endorsements")}><Heart size={14} />{nx.count(m.endorsements)}</span>
          <span className="k-cat-stat" title={t("catalog.downloads")}><Download size={14} />{nx.count(m.downloads)}</span>
          <span className="k-cat-badges">
            {m.kind && m.kind !== "other" && <Badge tone={TYPE_TONE[m.kind]}>{nx.TYPE_WORD[m.kind]}</Badge>}
            {s && <Badge tone={s.tone}>{s.word}</Badge>}
          </span>
        </span>
      </span>
    </button>
  );
}

function DetailsModal({ id, onClose }: { id: string; onClose: () => void }) {
  const t = useT();
  const account = useNexusAccount();
  const d = useQuery({ queryKey: nx.nk.mod(id), queryFn: () => nx.nexusMod(id), retry: false });
  const { add } = useCatalogInstall();
  const showReport = useReportToast();
  const [busy, setBusy] = useState(false);
  const [img, setImg] = useState(0);
  const m = d.data;
  const acc = account.data;
  const isKiln = id === "301";
  const update = !!m?.installed?.updateAvailable;
  const installedNow = !!m?.installed?.inLibrary && !update;
  const loadable = !!m?.bestFile && m.bestFile.kind !== "other";

  const go = async () => {
    if (!m) return;
    setBusy(true);
    try {
      const r = await add(m, m.bestFile, acc, m.filesUrl);
      if (r.kind === "added") { showReport(r.report, update); if (!offered(r.report).length) onClose(); }
      else if (r.kind === "opened") toast.info(t("catalog.opened"), t("catalog.openedDetail"));
    } finally { setBusy(false); }
  };
  const to = (m?.installed?.latestVersion ?? m?.bestFile?.version ?? "").trim();
  const label = !acc?.premium ? (update ? t("catalog.updateOnNexus") : t("catalog.downloadOnNexus")) : update ? (to ? t("catalog.updateToVersion", { version: to }) : t("catalog.updateToPlain")) : installedNow ? t("catalog.addAgain") : t("common.add");

  return (
    <Modal open onClose={onClose} width={780} locked={busy} title={m?.name ?? t("catalog.mod")} footer={<>
      <Button variant="ghost" icon={<ExternalLink />} onClick={() => void api.appOpenUrl(m?.url ?? `https://www.nexusmods.com/enshrouded/mods/${id}`).catch((e) => toast.error(e))}>{t("catalog.openOnNexus")}</Button>
      {!isKiln && m && <Button variant="primary" loading={busy} disabled={!acc?.signedIn || (!m.bestFile && !!acc?.premium)} onClick={go}>{label}</Button>}
    </>}>
      {d.isLoading ? <Skeleton lines={8} /> : d.error ? <EmptyState title={t("catalog.notAvailable")} hint={errorText(d.error)} /> : m && (
        <div className="k-cat-detail">
          {m.images.length > 0 && (
            <div className="k-cat-gallery">
              <img className="k-cat-hero" src={m.images[Math.min(img, m.images.length - 1)].url} alt={m.images[img]?.title ?? ""} />
              {m.images.length > 1 && (
                <div className="k-cat-thumbs">
                  {m.images.slice(0, 8).map((x, i) => (
                    <button key={x.url + i} type="button" className={i === img ? "on" : ""} onClick={() => setImg(i)} aria-label={t("catalog.picture", { n: i + 1 })}><img src={x.thumb ?? x.url} alt="" /></button>
                  ))}
                </div>
              )}
            </div>
          )}
          <div className="k-row" style={{ gap: 8, flexWrap: "wrap" }}>
            {m.kind && <Badge tone={TYPE_TONE[m.kind]}>{nx.TYPE_WORD[m.kind]}</Badge>}
            {stateWord(m) && <Badge tone={stateWord(m)!.tone}>{stateWord(m)!.word}</Badge>}
            {m.tags.slice(0, 4).map((tag) => <Badge key={tag}>{tag}</Badge>)}
          </div>
          <KeyValue rows={[
            [t("catalog.author"), m.credit && m.credit !== m.author ? t("catalog.uploadedBy", { credit: m.credit, author: m.author }) : m.author],
            [t("catalog.version"), m.version ?? "-"],
            m.installed && [t("catalog.installed"), m.installed.inLibrary ? `${m.installed.version}` : t("catalog.notInLibrary", { version: m.installed.version })],
            [t("catalog.endorsements"), nx.count(m.endorsements)],
            [t("catalog.downloads"), t("catalog.downloadsUnique", { downloads: nx.count(m.downloads), unique: nx.count(m.uniqueDownloads) })],
            m.updated && [t("catalog.updated"), fmtAgo(m.updated)],
          ]} />
          {m.summary && <p className="k-cat-lead">{m.summary}</p>}
          {m.needsSignIn ? <Banner tone="info" title={t("catalog.needsAuth")}>{t("catalog.needsAuthText")}</Banner> : (
            <>
              {m.requirements.filter((r) => !r.kiln).length > 0 && (
                <section>
                  <h3 className="k-label">{t("catalog.requires")}</h3>
                  <ul className="k-cat-reqs">
                    {m.requirements.filter((r) => !r.kiln).map((r) => (
                      <li key={r.modId}>
                        <span>{r.name}{r.minVersion ? ` ${r.minVersion}+` : ""}</span>
                        {r.installed ? <Badge tone="ok">{t("catalog.installed")}</Badge> : r.otherGame ? <Badge>{t("catalog.otherGame")}</Badge> : <Badge tone="warn">{t("catalog.notInstalled")}</Badge>}
                      </li>
                    ))}
                  </ul>
                </section>
              )}
              {m.files.length > 0 && (
                <section>
                  <h3 className="k-label">{t("catalog.files")}</h3>
                  <ul className="k-cat-files">
                    {m.files.map((f) => (
                      <li key={f.fileId}><span>{f.fileName}</span><span className="k-mono k-muted">{f.version}</span><span className="k-muted">{f.category}</span>{f === m.bestFile || f.fileId === m.bestFile?.fileId ? <Badge tone="ember">{t("catalog.addTakesThis")}</Badge> : null}</li>
                    ))}
                  </ul>
                </section>
              )}
              {!loadable && m.files.length > 0 && <Banner tone="warn" title={t("catalog.mayNotLoad")}>{t("catalog.mayNotLoadText")}</Banner>}
            </>
          )}
          {!acc?.premium && acc?.signedIn && !isKiln && <p className="k-hint">{t("catalog.freeAccounts")}</p>}
          {m.description && <div className="k-cat-desc">{m.description}</div>}
        </div>
      )}
    </Modal>
  );
}
