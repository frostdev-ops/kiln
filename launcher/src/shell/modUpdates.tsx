// Update notices for installed mods, shared by the Catalog page and the main screen's mod list: one result set
// (api/updates.ts, the updates_check command and its kiln://mod-updates event) in one query. A notice is words and an
// "open page" link; nothing here downloads, installs or switches a mod. Also the dialog where a player sets, confirms
// or removes a mod's update source, and the daily check (Settings, off by default).
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { ExternalLink } from "lucide-react";
import { useEffect, useState } from "react";
import * as api from "../api/commands";
import type { ModKind } from "../api/commands";
import { useGame, useGameStatus, useNexusEnabled, useSettings } from "../api/hooks";
import * as nx from "../api/nexus";
import * as up from "../api/updates";
import type { CheckReport, ModUpdateStatus, UpdatesRequest, UpdatesSpec } from "../api/updates";
import { Badge, Banner, Button, Field, KeyValue, Modal, Select, TextInput, errorText, fmtAgo, toast, useTasks } from "../ui";
import { t, useT } from "../i18n";

const inFlight = new Map<string, Promise<CheckReport>>();

/** Runs a check and puts its result where both pages read it. A second caller with the same request while one runs
 *  gets the first one's result; a source change always runs. */
export function checkModUpdates(qc: QueryClient, request: UpdatesRequest = {}): Promise<CheckReport> {
  const key = request.source ? null : JSON.stringify(request);
  const running = key === null ? undefined : inFlight.get(key);
  if (running) return running;
  const run = up.updatesCheck(request).then((r) => { qc.setQueryData(up.uk.report, r); return r; })
    .finally(() => { if (key !== null && inFlight.get(key) === run) inFlight.delete(key); });
  if (key !== null) inFlight.set(key, run);
  return run;
}

/** The last result set. The first read makes no request to any provider (an offline check: what is remembered). */
export function useModUpdates() {
  return useQuery({ queryKey: up.uk.report, queryFn: () => up.updatesCheck({ offline: true }), staleTime: Infinity, retry: false });
}

/** The status of one Library mod in a result set. */
export const statusOf = (r: CheckReport | undefined, kind: ModKind | null, id: string): ModUpdateStatus | undefined =>
  (kind ? r?.mods.find((m) => m.libraryRef.kind === kind && m.libraryRef.id === id) : undefined);

/** Opens a status's page in the browser: the only thing a notice can do. */
export function openUpdatePage(s: ModUpdateStatus) {
  const url = up.pageOf(s);
  if (url) void api.appOpenUrl(url).catch((e) => toast.error(e));
}

/** "Check for updates": asks the providers now and says what came back. */
export async function checkNow(qc: QueryClient) {
  try {
    const r = await checkModUpdates(qc, {});
    const n = r.mods.filter(up.hasUpdate).length;
    const unknown = r.mods.filter((m) => m.state === "unknown").length;
    if (n) toast.info(t("shell.modUpdates.found", { count: n }), t("shell.modUpdates.foundDetail"));
    else toast.ok(t("shell.modUpdates.none"), unknown ? t("shell.modUpdates.unchecked", { count: unknown }) : undefined);
  } catch (e) { toast.error(e, t("shell.modUpdates.checkFailed")); }
}

/** Whether a status is worth a line in a mod's row: an update, a new upload, or (for a mod with a source) a check
 *  that could not tell. Up to date, no source and not checked yet stay quiet; the mod's Updates dialog says them.
 *  `installed`: the version the row shows. An update the row's copy already has (a result from before the player
 *  updated) is no notice: "Update 0.2.10" never stands beside 0.2.10. */
export const noticeable = (s: ModUpdateStatus | undefined, installed?: string): s is ModUpdateStatus =>
  !!s && !!s.source && ((up.hasUpdate(s) && rowOffered(s, installed)) || (s.state === "unknown" && s.reason !== "not_checked"));

/** Whether an update is still something for the row's copy. Only where the status counts in the Library's versions
 *  (GitHub: tags against manifests); Nexus Mods numbers its uploads its own way, which says nothing about the row's. */
const rowOffered = (s: ModUpdateStatus, installed?: string) =>
  installed === undefined || s.installedVersion !== s.libraryRef.version || up.offeredFor(s, installed);

/** Keeps the shared result in step with the Library: when a Library list query reads other entries than before (a
 *  drop, Add mod, the Library's import, a catalog install, a roll back, a removal, a take-over), or the Catalog's list
 *  of what it installed reads other install records (an install from the Catalog page, where no Library list is
 *  read), `run` works the
 *  update result out again; by default an offline check (no request to any provider: what is remembered, compared
 *  with what is installed now), and the Catalog's list of what it installed is read again. Returns the unsubscribe.
 *  One run at a time: checkModUpdates hands a second offline check the running one's answer, read before the change
 *  that asked for it, so a change while a run is going on is worked out again once it is done (once, however many). */
export function watchLibrary(qc: QueryClient, run: () => unknown = () => {
  void qc.invalidateQueries({ queryKey: nx.nk.installed });
  return checkModUpdates(qc, { offline: true });
}, delay = 300): () => void {
  const seen = new Map<string, string>();
  let timer: ReturnType<typeof setTimeout> | undefined;
  let live = true, running = false, again = false;
  const fire = () => {
    if (!live) return;
    if (running) { again = true; return; }
    running = true;
    void Promise.resolve().then(run).catch(() => {}).finally(() => {
      running = false;
      if (again) { again = false; fire(); }
    });
  };
  const off = qc.getQueryCache().subscribe((e) => {
    if (e.type !== "updated" || e.action.type !== "success") return;
    const what = e.query.queryKey[0];
    // the Library's entries; and the Catalog's install records, the one list the Catalog page reads (an update from the
    // Catalog changes the Library while no page that reads the Library may be open). Not what the run itself reads
    // again: a record's newest version and whether it is an update are left out, so a run never sets off another.
    let print: string;
    if (what === "library_list") print = JSON.stringify(((e.query.state.data ?? []) as api.LibraryEntry[]).map((l) => [l.kind, l.id, l.versions.map((v) => [v.version, v.hash])]));
    else if (what === nx.nk.installed[0]) print = JSON.stringify(((e.query.state.data ?? []) as nx.InstalledCatalogMod[]).map((m) => [m.modId, m.fileId, m.versionGsid, m.refs.map((r) => [r.kind, r.id, r.version, r.hash])]));
    else return;
    const before = seen.get(e.query.queryHash);
    seen.set(e.query.queryHash, print);
    if (before === undefined || before === print) return;
    clearTimeout(timer);
    timer = setTimeout(fire, delay);
  });
  return () => { live = false; clearTimeout(timer); off(); };
}

/** A mod row's update notice, in the row's note line. An update is a link to its page (the browser opens it; nothing
 *  is downloaded); a check that could not tell is words only, and never says "up to date". */
export function UpdateBadge({ status, installed }: { status: ModUpdateStatus | undefined; installed?: string }) {
  const t = useT();
  if (!noticeable(status, installed)) return null;
  const w = up.updateWords(status);
  const url = up.pageOf(status);
  if (!up.hasUpdate(status) || !rowOffered(status, installed) || !url) return <span className="k-tone-idle k-mod-update" title={w.detail}>{status.state === "unknown" ? (status.stale ? t("shell.modUpdates.stale") : t("shell.modUpdates.unknown")) : w.word}</span>;
  return (
    <button type="button" className="k-link k-mod-update" title={t("shell.modUpdates.badgeTip", { detail: w.detail })} aria-label={t("shell.modUpdates.badgeAria", { word: w.word, name: status.name })}
      onClick={() => openUpdatePage(status)}>{w.word}</button>
  );
}

/** The dialog of one mod's update source: what the last check said, the page, and where Kiln looks. */
export function UpdateSourceModal({ target, onClose }: { target: { kind: ModKind; id: string; name: string } | null; onClose: () => void }) {
  const t = useT();
  const qc = useQueryClient();
  const report = useModUpdates();
  const status = target ? statusOf(report.data, target.kind, target.id) : undefined;
  // Official builds run with Nexus Mods switched off: the form offers GitHub only. A Nexus Mods source saved before is
  // shown above (state and source rows) and stays saved; the form starts at GitHub for a new choice.
  const nexusEnabled = useNexusEnabled();
  const savedNexusOff = !nexusEnabled && status?.source?.provider === "nexus";
  const [provider, setProvider] = useState<"github" | "nexus">("github");
  const [repository, setRepository] = useState("");
  const [tagPattern, setTagPattern] = useState("");
  const [asset, setAsset] = useState("");
  const [channel, setChannel] = useState<"stable" | "prerelease">("stable");
  const [modId, setModId] = useState("");
  const [fileChainId, setFileChainId] = useState("");
  const [busy, setBusy] = useState(false);
  const [conflict, setConflict] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const key = target ? `${target.kind}/${target.id}` : "";
  useEffect(() => {
    // a fresh dialog for each mod: the form starts from the source it has
    const src = status?.source;
    setProvider(src?.provider ?? "github");
    setRepository(src?.provider === "github" ? src.destination : "");
    const nx = src?.provider === "nexus" ? src.destination.split("/") : [];
    setModId(nx[1] ?? ""); setFileChainId(nx[2] ?? "");
    const gh = src?.spec?.provider === "github" ? src.spec : undefined;
    setTagPattern(gh?.tagPattern ?? ""); setAsset(gh?.asset ?? ""); setChannel(gh?.channel ?? "stable"); setConflict(null); setError(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, status?.source]);
  if (!target) return null;
  // what the form edits: GitHub whenever Nexus Mods is off here, whatever was picked before
  const chosen = nexusEnabled ? provider : "github";

  const spec = (): UpdatesSpec => (chosen === "github"
    ? { provider: "github", repository: repository.trim(), ...(tagPattern.trim() ? { tagPattern: tagPattern.trim() } : {}), ...(asset.trim() ? { asset: asset.trim() } : {}), channel }
    : { provider: "nexus", gameId: 5811, modId: Number(modId.trim()), fileChainId: fileChainId.trim() });
  const send = async (request: UpdatesRequest, done: string) => {
    setBusy(true); setError(null);
    try {
      await checkModUpdates(qc, request);
      setConflict(null);
      toast.ok(done);
    } catch (e) {
      const err = e as api.KilnError;
      if (err.code === "conflict") setConflict(err.message);
      else setError(errorText(e));
    } finally { setBusy(false); }
  };
  const save = (confirm: boolean) => send({ source: { action: "map", kind: target.kind, id: target.id, spec: spec(), confirm } }, t("shell.modUpdates.checkedThere", { name: target.name }));
  const words = status ? up.updateWords(status) : null;
  const page = status ? up.pageOf(status) : null;
  const providers = [{ value: "github", label: t("shell.modUpdates.githubReleases") }, ...(nexusEnabled ? [{ value: "nexus", label: t("shell.modUpdates.nexusFile") }] : [])];
  const filled = chosen === "github" ? repository.trim().length > 0 : modId.trim().length > 0 && fileChainId.trim().length > 0;

  return (
    <Modal open onClose={onClose} width={620} locked={busy} title={t("shell.modUpdates.title", { name: target.name })} footer={<>
      {page && status && <Button variant="ghost" icon={<ExternalLink />} onClick={() => openUpdatePage(status)}>{t("shell.modUpdates.openPage")}</Button>}
      {status?.source?.origin === "manual" && <Button variant="ghost" disabled={busy} onClick={() => void send({ offline: true, source: { action: "unmap", kind: target.kind, id: target.id } }, t("shell.modUpdates.sourceRemoved"))}>{t("shell.modUpdates.removeSource")}</Button>}
      <Button variant="primary" loading={busy} disabled={!filled || !!conflict} onClick={() => void save(false)}>{t("shell.modUpdates.saveCheck")}</Button>
    </>}>
      <div className="k-stack" data-update-source={key}>
        {status && words && (
          <KeyValue rows={[
            [t("shell.modUpdates.state"), <span key="s"><Badge tone={words.tone}>{words.word}</Badge> <span className="k-hint">{words.detail}</span></span>],
            [t("shell.modUpdates.installed"), status.installedVersion],
            status.candidateVersion && [t("shell.modUpdates.published"), status.candidateVersion],
            [t("shell.modUpdates.source"), status.source ? t("shell.modUpdates.sourceValue", {
              provider: status.source.provider === "github" ? "GitHub" : "Nexus Mods",
              destination: status.source.destination,
              origin: status.source.origin === "manual" ? t("shell.modUpdates.originManual") : status.source.origin === "receipt" ? t("shell.modUpdates.originReceipt") : t("shell.modUpdates.originManifest"),
              auth: status.source.authenticated ? t("shell.modUpdates.withAccount") : t("shell.modUpdates.anonymous"),
            }) : t("shell.modUpdates.noSource")],
            status.checkedAt && [t("shell.modUpdates.lastAnswer"), fmtAgo(status.checkedAt)],
          ]} />
        )}
        {status?.reason === "source_conflict" && status.source && (
          <Banner tone="warn" title={t("shell.modUpdates.sourceChanged")} actions={<Button size="s" loading={busy} onClick={() => void send({ source: { action: "confirm", kind: target.kind, id: target.id } }, t("shell.modUpdates.sourceAccepted"))}>{t("shell.modUpdates.useNewSource")}</Button>}>
            {t("shell.modUpdates.sourceChangedBody", { destination: status.source.destination })}
          </Banner>
        )}
        {conflict && (
          <Banner tone="warn" title={t("shell.modUpdates.changeWhere")} actions={<>
            <Button size="s" onClick={() => setConflict(null)}>{t("shell.modUpdates.keepOld")}</Button>
            <Button size="s" variant="primary" loading={busy} onClick={() => void save(true)}>{t("shell.modUpdates.changeIt")}</Button>
          </>}>{conflict}</Banner>
        )}
        {error && <Banner tone="err" title={t("shell.modUpdates.notSaved")}>{error}</Banner>}
        {savedNexusOff && status.source && <Banner tone="info" title={t("shell.modUpdates.nexusOff")}>{t("shell.modUpdates.nexusOffBody", { destination: status.source.destination })}</Banner>}
        <Field label={t("shell.modUpdates.where")} htmlFor="us-provider" hint={t("shell.modUpdates.whereHint")}>
          <Select id="us-provider" value={chosen} onChange={(v) => { setProvider(v as "github" | "nexus"); setConflict(null); }} options={providers} />
        </Field>
        {chosen === "github" ? (
          <>
            <Field label={t("shell.modUpdates.repository")} htmlFor="us-repo" hint={t("shell.modUpdates.repositoryHint")}><TextInput id="us-repo" mono value={repository} onChange={(e) => { setRepository(e.target.value); setConflict(null); }} placeholder="example-org/my-mod" /></Field>
            <Field label={t("shell.modUpdates.releaseTag")} htmlFor="us-tag" hint={t("shell.modUpdates.releaseTagHint")}><TextInput id="us-tag" mono value={tagPattern} onChange={(e) => setTagPattern(e.target.value)} placeholder="v{version}" /></Field>
            <Field label={t("shell.modUpdates.fileName")} htmlFor="us-asset" hint={t("shell.modUpdates.fileNameHint")}><TextInput id="us-asset" mono value={asset} onChange={(e) => setAsset(e.target.value)} placeholder="my-mod-{version}.zip" /></Field>
            <Field label={t("shell.modUpdates.versions")} htmlFor="us-channel"><Select id="us-channel" value={channel} onChange={(v) => setChannel(v as "stable" | "prerelease")} options={[{ value: "stable", label: t("shell.modUpdates.stableOnly") }, { value: "prerelease", label: t("shell.modUpdates.preToo") }]} /></Field>
          </>
        ) : (
          <>
            <Field label={t("shell.modUpdates.modPage")} htmlFor="us-mod" hint={t("shell.modUpdates.modPageHint")}><TextInput id="us-mod" mono value={modId} onChange={(e) => { setModId(e.target.value); setConflict(null); }} /></Field>
            <Field label={t("shell.modUpdates.fileId")} htmlFor="us-file" hint={t("shell.modUpdates.fileIdHint")}><TextInput id="us-file" mono value={fileChainId} onChange={(e) => { setFileChainId(e.target.value); setConflict(null); }} /></Field>
          </>
        )}
      </div>
    </Modal>
  );
}

/** Mounted once in the shell: keeps the shared result in step with the backend's event, and runs the daily check
 *  when Settings asks for it (off by default): a while after the start, then about once a day, and only while the
 *  launcher is idle (no task running, the game not running). */
export function ModUpdatesWatcher() {
  const qc = useQueryClient();
  const settings = useSettings();
  const { game } = useGame();
  const running = useGameStatus(game?.id).data?.running === true;
  const daily = settings.data?.modUpdatesDaily === true;
  useEffect(() => {
    let live = true;
    let off: (() => void) | undefined;
    api.onKilnEvent("kiln://mod-updates", (r) => qc.setQueryData(up.uk.report, r)).then((f) => (live ? (off = f) : f())).catch(() => {});
    return () => { live = false; off?.(); };
  }, [qc]);
  // a Library change (any way it happens): the notices are worked out again, offline
  useEffect(() => watchLibrary(qc), [qc]);
  useEffect(() => {
    if (!daily) return;
    const test = (window as unknown as { __kilnDailyMs?: number }).__kilnDailyMs;
    let timer: ReturnType<typeof setTimeout>;
    const idle = () => !running && !Object.values(useTasks.getState().tasks).some((task) => task.state === "queued" || task.state === "running");
    const tick = () => {
      if (!idle()) { timer = setTimeout(tick, test ?? up.dailyDelay("busy", 0)); return; }
      void checkModUpdates(qc, { automatic: true }).catch(() => {});
      timer = setTimeout(tick, up.dailyDelay("next", Math.random()));
    };
    timer = setTimeout(tick, test ?? up.dailyDelay("first", Math.random()));
    return () => clearTimeout(timer);
  }, [daily, running, qc]);
  return null;
}
