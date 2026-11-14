// Update notices for installed mods, shared by the Catalog page and the main screen's mod list: one result set
// (api/updates.ts, the updates_check command and its kiln://mod-updates event) in one query. A notice is words and an
// "open page" link; nothing here downloads, installs or switches a mod. Also the dialog where a player sets, confirms
// or removes a mod's update source, and the daily check (Settings, off by default).
import { useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { ExternalLink } from "lucide-react";
import { useEffect, useState } from "react";
import * as api from "../api/commands";
import type { ModKind } from "../api/commands";
import { useGame, useGameStatus, useSettings } from "../api/hooks";
import * as up from "../api/updates";
import type { CheckReport, ModUpdateStatus, UpdatesRequest, UpdatesSpec } from "../api/updates";
import { Badge, Banner, Button, Field, KeyValue, Modal, Select, TextInput, errorText, fmtAgo, toast, useTasks } from "../ui";

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
    if (n) toast.info(n === 1 ? "1 mod has an update" : `${n} mods have an update`, "Each one's page is a click away in the list. Nothing was downloaded.");
    else toast.ok("No updates found", unknown ? `${unknown} mod${unknown === 1 ? "" : "s"} could not be checked.` : undefined);
  } catch (e) { toast.error(e, "The update check failed"); }
}

/** Whether a status is worth a line in a mod's row: an update, a new upload, or (for a mod with a source) a check
 *  that could not tell. Up to date, no source and not checked yet stay quiet; the mod's Updates dialog says them. */
export const noticeable = (s: ModUpdateStatus | undefined): s is ModUpdateStatus =>
  !!s && !!s.source && (up.hasUpdate(s) || (s.state === "unknown" && s.reason !== "not_checked"));

/** A mod row's update notice, in the row's note line. An update is a link to its page (the browser opens it; nothing
 *  is downloaded); a check that could not tell is words only, and never says "up to date". */
export function UpdateBadge({ status }: { status: ModUpdateStatus | undefined }) {
  if (!noticeable(status)) return null;
  const w = up.updateWords(status);
  const url = up.pageOf(status);
  if (!up.hasUpdate(status) || !url) return <span className="k-tone-idle k-mod-update" title={w.detail}>{status.state === "unknown" ? (status.stale ? "Update check is stale" : "Update unknown") : w.word}</span>;
  return (
    <button type="button" className="k-link k-mod-update" title={`${w.detail} Opens its page; nothing is downloaded.`} aria-label={`${w.word} for ${status.name}: open its page`}
      onClick={() => openUpdatePage(status)}>{w.word}</button>
  );
}

/** The dialog of one mod's update source: what the last check said, the page, and where Kiln looks. */
export function UpdateSourceModal({ target, onClose }: { target: { kind: ModKind; id: string; name: string } | null; onClose: () => void }) {
  const qc = useQueryClient();
  const report = useModUpdates();
  const status = target ? statusOf(report.data, target.kind, target.id) : undefined;
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

  const spec = (): UpdatesSpec => (provider === "github"
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
  const save = (confirm: boolean) => send({ source: { action: "map", kind: target.kind, id: target.id, spec: spec(), confirm } }, `${target.name} is checked there now`);
  const words = status ? up.updateWords(status) : null;
  const page = status ? up.pageOf(status) : null;
  const filled = provider === "github" ? repository.trim().length > 0 : modId.trim().length > 0 && fileChainId.trim().length > 0;

  return (
    <Modal open onClose={onClose} width={620} locked={busy} title={`Updates of ${target.name}`} footer={<>
      {page && status && <Button variant="ghost" icon={<ExternalLink />} onClick={() => openUpdatePage(status)}>Open page</Button>}
      {status?.source?.origin === "manual" && <Button variant="ghost" disabled={busy} onClick={() => void send({ offline: true, source: { action: "unmap", kind: target.kind, id: target.id } }, "Your source is removed")}>Remove my source</Button>}
      <Button variant="primary" loading={busy} disabled={!filled || !!conflict} onClick={() => void save(false)}>Save and check</Button>
    </>}>
      <div className="k-stack" data-update-source={key}>
        {status && words && (
          <KeyValue rows={[
            ["State", <span key="s"><Badge tone={words.tone}>{words.word}</Badge> <span className="k-hint">{words.detail}</span></span>],
            ["Installed", status.installedVersion],
            status.candidateVersion && ["Published", status.candidateVersion],
            ["Source", status.source ? `${status.source.provider === "github" ? "GitHub" : "Nexus Mods"}: ${status.source.destination} (${status.source.origin === "manual" ? "set by you" : status.source.origin === "receipt" ? "from its install" : "from its manifest"}${status.source.authenticated ? ", with your account" : ", anonymous"})` : "None"],
            status.checkedAt && ["Last answer", fmtAgo(status.checkedAt)],
          ]} />
        )}
        {status?.reason === "source_conflict" && status.source && (
          <Banner tone="warn" title="Its update source changed" actions={<Button size="s" loading={busy} onClick={() => void send({ source: { action: "confirm", kind: target.kind, id: target.id } }, "The new source is accepted")}>Use the new source</Button>}>
            This version names {status.source.destination}. Kiln checks it there only after you say so: a copy of a mod can point at someone else's releases.
          </Banner>
        )}
        {conflict && (
          <Banner tone="warn" title="Change where this mod is checked?" actions={<>
            <Button size="s" onClick={() => setConflict(null)}>Keep the old source</Button>
            <Button size="s" variant="primary" loading={busy} onClick={() => void save(true)}>Change it</Button>
          </>}>{conflict}</Banner>
        )}
        {error && <Banner tone="err" title="Not saved">{error}</Banner>}
        <Field label="Where it is published" htmlFor="us-provider" hint="Kiln only reads there and shows a notice with a link. It never downloads or installs a version.">
          <Select id="us-provider" value={provider} onChange={(v) => { setProvider(v as "github" | "nexus"); setConflict(null); }} options={[{ value: "github", label: "GitHub releases" }, { value: "nexus", label: "A file on Nexus Mods" }]} />
        </Field>
        {provider === "github" ? (
          <>
            <Field label="Repository" htmlFor="us-repo" hint="owner/repo, as in github.com/owner/repo."><TextInput id="us-repo" mono value={repository} onChange={(e) => { setRepository(e.target.value); setConflict(null); }} placeholder="example-org/my-mod" /></Field>
            <Field label="Release tag" htmlFor="us-tag" hint="With {version} where the version stands. Empty: v{version}."><TextInput id="us-tag" mono value={tagPattern} onChange={(e) => setTagPattern(e.target.value)} placeholder="v{version}" /></Field>
            <Field label="File name" htmlFor="us-asset" hint="The mod's zip in a release, {version} allowed. Empty: a link and a version only."><TextInput id="us-asset" mono value={asset} onChange={(e) => setAsset(e.target.value)} placeholder="my-mod-{version}.zip" /></Field>
            <Field label="Versions" htmlFor="us-channel"><Select id="us-channel" value={channel} onChange={(v) => setChannel(v as "stable" | "prerelease")} options={[{ value: "stable", label: "Stable only" }, { value: "prerelease", label: "Pre-releases too" }]} /></Field>
          </>
        ) : (
          <>
            <Field label="Mod page number" htmlFor="us-mod" hint="The number in nexusmods.com/enshrouded/mods/<number>."><TextInput id="us-mod" mono value={modId} onChange={(e) => { setModId(e.target.value); setConflict(null); }} /></Field>
            <Field label="File id" htmlFor="us-file" hint="The id of the file whose versions are followed. Checked with your Nexus Mods sign-in, for mods installed from the Catalog."><TextInput id="us-file" mono value={fileChainId} onChange={(e) => { setFileChainId(e.target.value); setConflict(null); }} /></Field>
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
  useEffect(() => {
    if (!daily) return;
    const test = (window as unknown as { __kilnDailyMs?: number }).__kilnDailyMs;
    let timer: ReturnType<typeof setTimeout>;
    const idle = () => !running && !Object.values(useTasks.getState().tasks).some((t) => t.state === "queued" || t.state === "running");
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
