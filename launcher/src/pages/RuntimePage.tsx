// /runtime: install / update / uninstall / rollback Kiln in the selected game, the proxy chain
// (displaced proxies), and "switch from ShroudForge". The last change and its outcome stay on the page (and a failure
// on Play) until dismissed: an update that did not happen must not look like one that did.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import * as api from "../api/commands";
import type { BootJson, InstallReceipt, InstallState, RunSide, RuntimeBundle } from "../api/commands";
import { qk, useGame, useGameStatus, useLaunchState, useRuntimeChanges, type RuntimeChange } from "../api/hooks";
import { partRows, partsFor, resolveParts, selectParts, type PartState } from "../shell/parts";
import { installBundle, isOlder, runRuntimeChange } from "../shell/runtimeChange";
import { rich, t, useT } from "../i18n";
import {
  Banner, Button, Card, ConfirmDanger, DropZone, EmptyState, Modal, Page, PageHeader, Skeleton, Table,
  TaskProgress, Toggle, errorText, fmtAgo, toast, useTasks, type Column,
} from "../ui";

export default function RuntimePage() {
  const t = useT();
  const { game } = useGame();
  const qc = useQueryClient();
  const status = useGameStatus(game?.id);
  const boot = useLaunchState(game?.id);
  const bundles = useQuery({ queryKey: qk.runtimeList, queryFn: api.runtimeList });
  // fresh install state (lists changed files); the top bar's status uses a cached one
  const fresh = useQuery({ queryKey: qk.runtimeStatus(game?.id ?? ""), queryFn: () => api.runtimeStatus(game!.id), enabled: !!game });
  const [uninstall, setUninstall] = useState(false);
  const [switchSf, setSwitchSf] = useState<RuntimeBundle | null>(null);
  const [pick, setPick] = useState<RuntimeBundle | null>(null);
  const change = useRuntimeChanges((s) => (game ? s.last[game.id] : undefined));
  const { clear } = useRuntimeChanges.getState();
  const busy = change?.state === "running";

  const importB = useMutation({
    mutationFn: (path: string) => api.runtimeImport(path),
    onSuccess: (b) => { qc.invalidateQueries({ queryKey: qk.runtimeList }); toast.ok(t("runtime.bundleAdded", { version: b.version })); },
    onError: (e) => toast.error(e),
  });

  // Play's "Install Kiln" lands here: straight to the install dialog with the newest bundle (the one the launcher ships)
  const [params, setParams] = useSearchParams();
  const wantInstall = params.get("install") === "1";
  const rt = fresh.data ?? status.data?.runtime;
  useEffect(() => {
    if (!wantInstall || !rt || !bundles.data) return;
    setParams({}, { replace: true });
    if (!rt.receipt && rt.proxy?.kind !== "shroudforge" && bundles.data[0]) setPick(bundles.data[0]);
    // after a launcher update: the runtime it brought
    else if (rt.receipt && rt.upgradeAvailable) { const b = bundles.data.find((x) => x.version === rt.upgradeAvailable); if (b) setPick(b); }
  }, [wantInstall, rt, bundles.data, setParams]);

  if (!game) return <Page><PageHeader title={t("runtime.title")} sub={t("runtime.noGame")} /></Page>;
  const st = fresh.data ?? status.data?.runtime;
  const running = !!status.data?.running;
  const sf = st?.proxy?.kind === "shroudforge";
  const current = st?.receipt?.runtime.version ?? null;
  const newest = bundles.data?.[0];
  // default parts of the installed bundle that are not in the game (an update that kept an old list skipped them)
  const missing = st?.missingParts ?? [];
  const installedBundle = bundles.data?.find((b) => b.version === current) ?? null;

  // the change is kept per game (useRuntimeChanges): its outcome outlives this page, and Play shows a failure
  // (shell/runtimeChange.ts, shared with Play's Install Kiln)
  const go = (what: string, version: string | null, label: string, start: () => Promise<string>) =>
    runRuntimeChange(qc, game, bundles.data ?? [], what, version, label, start).then(() => undefined);
  const install = (b: RuntimeBundle, opts: api.InstallOptions = {}) => installBundle(qc, game, b, bundles.data ?? [], current, opts).then(() => undefined);

  const cols: Column<RuntimeBundle>[] = [
    { key: "v", header: t("runtime.version"), width: "minmax(0, 1.3fr)", render: (b) => (
      <span className="k-row" style={{ gap: "2px 10px", flexWrap: "nowrap" }}>
        <span className="k-mono k-ellipsis" style={{ fontSize: 15.5 }} title={b.version}>{b.version}</span>
        {b.version === current && <span className="k-pill k-tone-gold">{t("runtime.installed")}</span>}
      </span>
    ) },
    { key: "c", header: t("runtime.built"), width: "100px", render: (b) => <span style={{ fontSize: 16, color: "#d0beac" }}>{fmtAgo(b.created)}</span> },
    { key: "comp", header: t("runtime.parts"), width: "minmax(0, 1.4fr)", render: (b) => <span style={{ fontSize: 16, color: "#d0beac" }} title={b.components.join(", ")}>{b.components.join(", ")}</span> },
    { key: "act", header: <span className="k-sr-only">{t("runtime.action")}</span>, width: "112px", align: "right", render: (b) => {
      const older = !!current && isOlder(b.version, current, bundles.data ?? []);
      // the installed bundle can be installed again when parts of it are missing (that adds them)
      const repair = b.version === current && missing.length > 0;
      const word = repair ? t("runtime.reinstall") : b.version === current ? t("runtime.installed") : !current ? t("common.install") : older ? t("runtime.rollBack") : t("common.update");
      return (
        <Button size="s" disabled={(b.version === current && !repair) || running || busy || !!status.data?.steamUpdating} onClick={() => (sf ? setSwitchSf(b) : setPick(b))}>{word}</Button>
      );
    } },
  ];

  const files = st?.receipt?.files.length ?? 0;
  // the whole version (the build date is what tells two dev builds apart) and when it last changed
  const inGame = st?.receipt?.updated
    ? t("runtime.inGameUpdated", { version: current ?? "", files, ago: fmtAgo(st.receipt.updated) })
    : t("runtime.inGameInstalled", { version: current ?? "", files, ago: fmtAgo(st?.receipt?.installed) });
  const say = !st ? t("runtime.reading") : st.installed
    ? (st.upgradeAvailable ? `${inGame} ${t("runtime.readyToInstall", { version: st.upgradeAvailable })}` : inGame)
    : sf ? t("runtime.shroudforge") : t("runtime.notInstalled");
  const live = !!boot.data?.boot && !boot.data.stale;
  return (
    <Page>
      <PageHeader title={t("runtime.title")} sub={say} actions={st?.installed ? (
        <Button variant="danger" disabled={running || busy} onClick={() => setUninstall(true)}>{t("runtime.uninstall")}</Button>
      ) : newest && !sf ? (
        <Button variant="primary" disabled={running || busy || !!status.data?.steamUpdating} onClick={() => setPick(newest)}>{t("common.install")}</Button>
      ) : undefined} />
      {running && <Banner tone="warn" title={t("runtime.running")}>{t("runtime.runningText")}</Banner>}
      {status.data?.steamUpdating && <Banner tone="warn" title={t("runtime.updating")}>{t("runtime.updatingText")}</Banner>}
      {sf && (
        <Banner tone="err" title="ShroudForge" actions={newest && <Button size="s" variant="danger" disabled={running || busy} onClick={() => setSwitchSf(newest)}>{t("runtime.switchSf")}</Button>}>
          {t("runtime.sfText")}
        </Banner>
      )}
      {!!st?.modified.length && <Banner tone="warn" title={t("runtime.changed")}>{rich("runtime.changedText", { count: st.modified.length, files: <span className="k-mono">{st.modified.slice(0, 3).join(", ")}{st.modified.length > 3 ? " …" : ""}</span> })}</Banner>}
      {change && <ChangeOutcome change={change} installed={current} onDismiss={() => clear(game.id)} />}
      {missing.length > 0 && change?.state !== "running" && (
        <Banner tone="warn" title={t("runtime.partsMissing")} actions={installedBundle && (
          <Button size="s" disabled={running || busy || !!status.data?.steamUpdating} onClick={() => setPick(installedBundle)}>{t("runtime.reinstall")}</Button>
        )}>
          {rich("runtime.partsMissingText", { version: current, parts: <span className="k-mono">{missing.join(", ")}</span> })}
        </Banner>
      )}

      <Card title={t("runtime.chainTitle")} sub={live ? t("runtime.chainLive") : t("runtime.chainNotLive")}>
        {status.isLoading ? <Skeleton lines={2} /> : status.error ? <EmptyState title={t("runtime.stateUnreadable")} hint={errorText(status.error)} /> : <ChainView st={st} boot={boot.data?.boot ?? null} live={live} build={game.buildId} />}
      </Card>

      {st?.receipt && (
        <Card title={t("runtime.parts")} sub={installedBundle ? t("runtime.partsSub", { version: current }) : t("runtime.partsSubNoBundle")}>
          <PartsList rows={partRows(installedBundle, game.kind, st.receipt)} />
        </Card>
      )}

      <Card title={t("runtime.bundles")} sub={t("runtime.bundleCount", { count: bundles.data?.length ?? 0 })}>
        {bundles.isLoading ? <Skeleton /> : bundles.error ? <EmptyState title={t("runtime.bundlesUnreadable")} hint={errorText(bundles.error)} /> : (
          <Table aria-label={t("runtime.bundlesLabel")} columns={cols} rows={bundles.data ?? []} rowKey={(b) => b.version} rowHeight={50}
            empty={<EmptyState title={t("runtime.noBundles")} />} />
        )}
        <DropZone variant="row" onPaths={(p) => p.forEach((x) => importB.mutate(x))} title={t("runtime.addBundle")} hint={importB.isPending ? t("runtime.readingBundle") : "kiln-runtime-<version>.zip"} pick={{ filters: [{ name: t("runtime.bundleFilter"), extensions: ["zip"] }] }} />
      </Card>

      <InstallModal bundle={pick} side={game.kind} receipt={st?.receipt ?? null} onClose={() => setPick(null)}
        onInstall={(b, components) => { setPick(null); install(b, { components }); }} />
      <ConfirmDanger open={uninstall} onClose={() => setUninstall(false)} title={t("runtime.uninstallAsk")} word={t("common.confirmWord.uninstall")} action={t("runtime.uninstall")}
        message={<>
          <p>{t("runtime.uninstallFiles")}</p>
          <p>{t("runtime.uninstallEml")}</p>
        </>}
        onConfirm={() => { setUninstall(false); return go(t("runtime.uninstall"), null, t("runtime.removed"), () => api.runtimeUninstall(game.id)); }} />
      <ConfirmDanger open={!!switchSf} onClose={() => setSwitchSf(null)} title={t("runtime.switchSfAsk")} word={t("common.confirmWord.switch")} action={t("runtime.switch")}
        message={rich("runtime.switchSfText", { folder: <span className="k-mono">kiln/backups/</span>, version: switchSf?.kiln })}
        onConfirm={() => { const b = switchSf!; setSwitchSf(null); return install(b, { switchFromShroudforge: true }); }} />
    </Page>
  );
}

/** What a part of the bundle does, in words (undefined: the bundle's own description). */
function partHint(name: string): string | undefined {
  switch (name) {
    case "core": return t("runtime.part.core");
    case "sigdb-client": return t("runtime.part.sigdbClient");
    case "sigdb-server": return t("runtime.part.sigdbServer");
    case "admin": return t("runtime.part.admin");
    case "emf": return t("runtime.part.emf");
    case "eml": return t("runtime.part.eml");
    case "shroudforge": return t("runtime.part.shroudforge");
    case "enshwick": return t("runtime.part.enshwick");
    case "samples": return t("runtime.part.samples");
    default: return undefined;
  }
}

/** A part's state in the game: its word and tone. */
function partState(state: PartState): [string, string] {
  switch (state) {
    case "installed": return [t("runtime.installed"), "k-tone-ok"];
    case "missing": return [t("runtime.state.missing"), "k-tone-warn"];
    case "off": return [t("runtime.state.off"), "k-tone-idle"];
    case "optional": return [t("runtime.state.optional"), "k-tone-idle"];
    default: return [String(state), "k-tone-idle"];
  }
}

/** The installed bundle's parts against the game folder: installed, missing, turned off, optional. */
function PartsList({ rows }: { rows: ReturnType<typeof partRows> }) {
  const t = useT();
  return (
    <ul className="k-stack" aria-label={t("runtime.parts")} style={{ listStyle: "none", margin: 0, padding: 0, gap: 6 }}>
      {rows.map((r) => (
        <li key={r.name} className="k-row" aria-label={`${r.name}: ${partState(r.state)[0]}`} style={{ gap: 10, flexWrap: "nowrap" }}>
          <span className="k-mono" style={{ fontSize: 15.5, minWidth: 120 }}>{r.name}</span>
          <span className={`k-pill ${partState(r.state)[1]}`}>{partState(r.state)[0]}</span>
          <span className="k-hint k-ellipsis">{partHint(r.name) ?? r.part?.description ?? ""}</span>
        </li>
      ))}
    </ul>
  );
}

/** What the install will do with each part, when the player does not choose: keep, add, leave off. */
function planWord(c: string, plan: string[], receipt: InstallReceipt | null, isDefault: boolean): [string, string] {
  const inGame = !!receipt?.runtime.components.includes(c);
  if (plan.includes(c)) return !receipt ? [t("runtime.plan.installs"), "k-tone-ok"] : inGame ? [t("runtime.plan.keeps"), "k-tone-ok"] : [t("runtime.plan.adds"), "k-tone-gold"];
  if (receipt?.runtime.deselected?.includes(c)) return [t("runtime.plan.offChosen"), "k-tone-idle"];
  return inGame ? [t("runtime.plan.removes"), "k-tone-warn"] : isDefault ? [t("runtime.plan.off"), "k-tone-idle"] : [t("runtime.state.optional"), "k-tone-idle"];
}

function InstallModal({ bundle, side, receipt, onClose, onInstall }: {
  bundle: RuntimeBundle | null; side: RunSide; receipt: InstallReceipt | null; onClose: () => void; onInstall: (b: RuntimeBundle, components: string[] | null) => void;
}) {
  const t = useT();
  const [custom, setCustom] = useState(false);
  const [parts, setParts] = useState<string[]>([]);
  const [seen, setSeen] = useState<RuntimeBundle | null>(null);
  // no choice: what the backend installs (a first install: the defaults; an update: the player's parts plus every
  // part that is new or became a default, unless they turned it off)
  const plan = bundle ? resolveParts(bundle, side, receipt) : [];
  if (bundle !== seen) {
    setSeen(bundle);
    if (bundle) {
      setCustom(false);
      setParts(plan);
    }
  }
  if (!bundle) return null;
  const offered = partsFor(bundle, side);
  const toggle = (c: string, on: boolean) => setParts(on ? selectParts(bundle, side, [...parts, c]) : parts.filter((x) => x !== c));
  return (
    <Modal open onClose={onClose} title={t("runtime.installTitle", { version: bundle.kiln })} width={480}
      footer={<><Button variant="ghost" onClick={onClose}>{t("common.cancel")}</Button>
        <Button variant="primary" disabled={custom && !parts.includes("core")} onClick={() => onInstall(bundle, custom ? parts : null)}>{t("common.install")}</Button></>}>
      <span className="k-mono k-muted">{bundle.version}</span>
      <Toggle checked={!custom} onChange={(on) => setCustom(!on)} label={receipt ? t("runtime.keepParts") : t("runtime.defaultParts")}
        hint={receipt ? t("runtime.keepPartsHint") : t("runtime.defaultPartsHint")} />
      {custom ? (
        <div className="k-stack">
          {offered.map((p) => (
            <Toggle key={p.name} checked={parts.includes(p.name)} onChange={(on) => toggle(p.name, on)}
              disabled={p.name === "core" || selectParts(bundle, side, parts.filter((c) => c !== p.name)).includes(p.name)}
              label={p.name} hint={partHint(p.name) ?? p.description} />
          ))}
        </div>
      ) : (
        <ul className="k-stack" aria-label={t("runtime.planLabel")} style={{ listStyle: "none", margin: 0, padding: 0, gap: 6 }}>
          {offered.map((p) => {
            const [word, tone] = planWord(p.name, plan, receipt, p.default);
            return (
              <li key={p.name} className="k-row" style={{ gap: 10, flexWrap: "nowrap" }}>
                <span className="k-mono" style={{ fontSize: 15.5, minWidth: 120 }}>{p.name}</span>
                <span className={`k-pill ${tone}`}>{word}</span>
                <span className="k-hint k-ellipsis">{partHint(p.name) ?? p.description ?? ""}</span>
              </li>
            );
          })}
        </ul>
      )}
    </Modal>
  );
}

/** The last change on this page and how it ended: the task while it runs and when done (its last line says what is
 *  in the game folder now), a plain failure otherwise. Stays until dismissed. */
function ChangeOutcome({ change, installed, onDismiss }: { change: RuntimeChange; installed: string | null; onDismiss: () => void }) {
  const t = useT();
  const task = useTasks((s) => (change.taskId ? s.tasks[change.taskId] : undefined));
  const dismiss = <button type="button" className="k-link" onClick={onDismiss}>{t("common.dismiss")}</button>;
  if (change.state === "failed") {
    return (
      <Banner tone="err" title={t("runtime.changeFailed", { what: change.what })} actions={dismiss}>
        <span style={{ overflowWrap: "anywhere" }}>{change.error?.message ?? t("runtime.noReason")}</span>{" "}
        {installed ? rich("runtime.keeps", { version: <span className="k-mono">{installed}</span> }) : t("runtime.notInGame")}
      </Banner>
    );
  }
  if (change.state === "done") {
    // the task's last line is the backend's check of the game folder ("Kiln … is in the game folder (N files, parts)")
    return <Banner tone="ok" title={t("runtime.changeDone", { what: change.what })} actions={dismiss}>{task?.message ?? (installed ? t("runtime.isInGame", { version: installed }) : t("runtime.notInGame"))}</Banner>;
  }
  return task ? <TaskProgress task={task} /> : (
    <TaskProgress cancel={false} task={{ id: "starting", kind: "runtime", title: change.what, state: "running", progress: null, message: t("runtime.starting"), started: new Date(change.at).toISOString(), finished: null }} />
  );
}

function ChainView({ st, boot, live, build }: { st: InstallState | undefined; boot: BootJson | null; live: boolean; build: string | null }) {
  const t = useT();
  const chained = st?.receipt?.replaced.filter((r) => r.action === "chained") ?? [];
  const backed = st?.receipt?.replaced.filter((r) => r.action === "backed-up") ?? [];
  const chain = boot?.chain ?? [];
  type Node = { file: string; what: string; kind: "game" | "kiln" | "other" | "bad" | "end" };
  const nodes: Node[] = [{ file: "enshrouded.exe", what: build ? boot?.exe?.verified ? t("runtime.chain.buildVerified", { build }) : t("runtime.chain.build", { build }) : t("runtime.chain.game"), kind: "game" }];
  nodes.push({ file: "dinput8.dll", what: st?.installed ? t("runtime.chain.kilnProxy", { version: st.proxy?.version ?? "" }) : st?.proxy && st.proxy.kind !== "none" ? t("runtime.chain.proxy", { kind: st.proxy.kind }) : t("runtime.chain.system"), kind: st?.installed ? "kiln" : "other" });
  const seen = new Set<string>();
  for (const c of chain) {
    seen.add(c.path.toLowerCase());
    nodes.push({ file: c.path.split(/[\\/]/).pop() ?? c.path, what: `${c.source}, ${c.status}`, kind: c.status === "refused" || c.status === "failed" ? "bad" : "other" });
  }
  for (const r of chained) {
    const name = (r.chainPath ?? r.path).split(/[\\/]/).pop() ?? r.path;
    if ([...seen].some((x) => x.endsWith(name.toLowerCase()))) continue;
    nodes.push({ file: name, what: t("runtime.chain.chained", { class: r.class ?? t("runtime.chain.unknown") }), kind: "other" });
  }
  if (st?.installed) {
    nodes.push({ file: "kiln-core.dll", what: boot ? `Kiln ${boot.kiln}` : "Kiln", kind: "kiln" });
    nodes.push({ file: t("runtime.chain.services"), what: t("runtime.chain.mods"), kind: "end" });
  }
  const STYLE: Record<Node["kind"], [string, string]> = {
    game: ["rgba(10,7,5,.8)", "var(--k-line-hot)"], kiln: ["rgba(255,106,26,.22)", "var(--k-ember-400)"], other: ["rgba(140,187,227,.14)", "var(--k-info)"],
    bad: ["rgba(255,125,102,.16)", "var(--k-err)"], end: ["rgba(249,199,79,.16)", "var(--k-gold-400)"],
  };
  return (
    <>
      <ol className="k-chain" style={{ gridTemplateColumns: `repeat(${nodes.length}, minmax(0, 1fr))` }}>
        {nodes.map((n, i) => (
          <li key={i}>
            <div className="k-chain-top">
              <svg viewBox="0 0 36 52" aria-hidden><path d="M1 51V26C1 15 11 7 18 1C25 7 35 15 35 26V51Z" style={{ fill: STYLE[n.kind][0], stroke: STYLE[n.kind][1] }} /></svg>
              {i < nodes.length - 1 && <div className="k-chain-link">{live && <i />}</div>}
            </div>
            <span className="k-mono k-ellipsis" style={{ fontSize: 16 }} title={n.file}>{n.file}</span>
            <span className="k-hint" style={{ marginTop: -8 }}>{n.what}</span>
          </li>
        ))}
      </ol>
      {backed.length > 0 && <span className="k-hint">{rich("runtime.chain.backedUp", { files: <span className="k-mono">{backed.map((b) => b.path).join(", ")}</span> })}</span>}
    </>
  );
}
