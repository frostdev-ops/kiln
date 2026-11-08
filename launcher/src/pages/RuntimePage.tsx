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
import {
  Banner, Button, Card, ConfirmDanger, DropZone, EmptyState, Modal, Page, PageHeader, Skeleton, Table,
  TaskProgress, Toggle, errorText, fmtAgo, toast, useTasks, type Column,
} from "../ui";

export default function RuntimePage() {
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
    onSuccess: (b) => { qc.invalidateQueries({ queryKey: qk.runtimeList }); toast.ok(`Bundle ${b.version} added`); },
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

  if (!game) return <Page><PageHeader title="Runtime" sub="No game yet. Find Enshrouded on Play first." /></Page>;
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
    { key: "v", header: "Version", width: "minmax(0, 1.3fr)", render: (b) => (
      <span className="k-row" style={{ gap: "2px 10px", flexWrap: "nowrap" }}>
        <span className="k-mono k-ellipsis" style={{ fontSize: 15.5 }} title={b.version}>{b.version}</span>
        {b.version === current && <span className="k-pill k-tone-gold">Installed</span>}
      </span>
    ) },
    { key: "c", header: "Built", width: "100px", render: (b) => <span style={{ fontSize: 16, color: "#d0beac" }}>{fmtAgo(b.created)}</span> },
    { key: "comp", header: "Parts", width: "minmax(0, 1.4fr)", render: (b) => <span style={{ fontSize: 16, color: "#d0beac" }} title={b.components.join(", ")}>{b.components.join(", ")}</span> },
    { key: "act", header: <span className="k-sr-only">Action</span>, width: "112px", align: "right", render: (b) => {
      const older = !!current && isOlder(b.version, current, bundles.data ?? []);
      // the installed bundle can be installed again when parts of it are missing (that adds them)
      const repair = b.version === current && missing.length > 0;
      const word = repair ? "Reinstall" : b.version === current ? "Installed" : !current ? "Install" : older ? "Roll back" : "Update";
      return (
        <Button size="s" disabled={(b.version === current && !repair) || running || busy || !!status.data?.steamUpdating} onClick={() => (sf ? setSwitchSf(b) : setPick(b))}>{word}</Button>
      );
    } },
  ];

  const files = st?.receipt?.files.length ?? 0;
  // the whole version (the build date is what tells two dev builds apart) and when it last changed
  const when = st?.receipt?.updated ? `updated ${fmtAgo(st.receipt.updated)}` : `installed ${fmtAgo(st?.receipt?.installed)}`;
  const say = !st ? "Reading the game folder." : st.installed
    ? `Kiln ${current ?? ""} is in the game folder: ${files} files, ${when}.${st.upgradeAvailable ? ` ${st.upgradeAvailable} is ready to install.` : ""}`
    : sf ? "ShroudForge is in this game. Kiln can take over its dinput8.dll." : "Kiln is not in this game yet.";
  const live = !!boot.data?.boot && !boot.data.stale;
  return (
    <Page>
      <PageHeader title="Runtime" sub={say} actions={st?.installed ? (
        <Button variant="danger" disabled={running || busy} onClick={() => setUninstall(true)}>Uninstall</Button>
      ) : newest && !sf ? (
        <Button variant="primary" disabled={running || busy || !!status.data?.steamUpdating} onClick={() => setPick(newest)}>Install</Button>
      ) : undefined} />
      {running && <Banner tone="warn" title="Running">Close the game to install or remove Kiln.</Banner>}
      {status.data?.steamUpdating && <Banner tone="warn" title="Updating">Steam has an update queued for Enshrouded. Installs wait until it is done.</Banner>}
      {sf && (
        <Banner tone="err" title="ShroudForge" actions={newest && <Button size="s" variant="danger" disabled={running || busy} onClick={() => setSwitchSf(newest)}>Switch from ShroudForge</Button>}>
          Kiln takes over its dinput8.dll (kept in backups, put back on uninstall). ShroudForge's own dbghelp.dll, winmm.dll and folder stay where they are.
        </Banner>
      )}
      {!!st?.modified.length && <Banner tone="warn" title="Changed">{st.modified.length} file{st.modified.length > 1 ? "s" : ""} changed since the install: <span className="k-mono">{st.modified.slice(0, 3).join(", ")}{st.modified.length > 3 ? " …" : ""}</span></Banner>}
      {change && <ChangeOutcome change={change} installed={current} onDismiss={() => clear(game.id)} />}
      {missing.length > 0 && change?.state !== "running" && (
        <Banner tone="warn" title="Parts missing" actions={installedBundle && (
          <Button size="s" disabled={running || busy || !!status.data?.steamUpdating} onClick={() => setPick(installedBundle)}>Reinstall</Button>
        )}>
          Kiln {current} has parts this game does not: <span className="k-mono">{missing.join(", ")}</span>. Mods that need them do not load until they are in.
        </Banner>
      )}

      <Card title="What the game loads, in order" sub={live ? "Live, from this boot." : "Live statuses show after a start."}>
        {status.isLoading ? <Skeleton lines={2} /> : status.error ? <EmptyState title="The game's state is not readable" hint={errorText(status.error)} /> : <ChainView st={st} boot={boot.data?.boot ?? null} live={live} build={game.buildId} />}
      </Card>

      {st?.receipt && (
        <Card title="Parts" sub={installedBundle ? `Kiln ${current}: what is in this game and what else the bundle has.` : "What is in this game (the bundle it came from is not in the launcher)."}>
          <PartsList rows={partRows(installedBundle, game.kind, st.receipt)} />
        </Card>
      )}

      <Card title="Bundles in the launcher" sub={`${bundles.data?.length ?? 0} bundle${bundles.data?.length === 1 ? "" : "s"}`}>
        {bundles.isLoading ? <Skeleton /> : bundles.error ? <EmptyState title="Bundles are not readable" hint={errorText(bundles.error)} /> : (
          <Table aria-label="Runtime bundles" columns={cols} rows={bundles.data ?? []} rowKey={(b) => b.version} rowHeight={50}
            empty={<EmptyState title="No bundles. Add a kiln-runtime zip below." />} />
        )}
        <DropZone variant="row" onPaths={(p) => p.forEach((x) => importB.mutate(x))} title="Add a runtime bundle" hint={importB.isPending ? "Reading the bundle…" : "kiln-runtime-<version>.zip"} pick={{ filters: [{ name: "Kiln runtime", extensions: ["zip"] }] }} />
      </Card>

      <InstallModal bundle={pick} side={game.kind} receipt={st?.receipt ?? null} onClose={() => setPick(null)}
        onInstall={(b, components) => { setPick(null); install(b, { components }); }} />
      <ConfirmDanger open={uninstall} onClose={() => setUninstall(false)} title="Uninstall Kiln?" word="uninstall" action="Uninstall"
        message="This removes Kiln's files from the game folder. The original dinput8.dll comes back; kiln/ (configs, data, logs) stays."
        onConfirm={() => { setUninstall(false); return go("Uninstall", null, "Kiln removed", () => api.runtimeUninstall(game.id)); }} />
      <ConfirmDanger open={!!switchSf} onClose={() => setSwitchSf(null)} title="Switch from ShroudForge?" word="switch" action="Switch"
        message={<>ShroudForge's dinput8.dll moves to <span className="k-mono">kiln/backups/</span> and Kiln {switchSf?.kiln} goes in. The rest of ShroudForge stays in place.</>}
        onConfirm={() => { const b = switchSf!; setSwitchSf(null); return install(b, { switchFromShroudforge: true }); }} />
    </Page>
  );
}

const PART_HINT: Record<string, string> = {
  core: "proxy, core, .NET host",
  "sigdb-client": "signatures for the game",
  "sigdb-server": "signatures for servers",
  admin: "Kiln.Admin (servers)",
  emf: "runs EMF mods (kiln-emf)",
  eml: "runs EML mods (kiln-eml, GPL-3.0)",
  shroudforge: "runs ShroudForge mods (its runtime, GPL-3.0)",
  enshwick: "runs Enshwick mods (Enshwick's loader)",
  samples: "sample mods",
};

const STATE: Record<PartState, [string, string]> = {
  installed: ["Installed", "k-tone-ok"], missing: ["Missing", "k-tone-warn"], off: ["Turned off", "k-tone-idle"], optional: ["Optional", "k-tone-idle"],
};

/** The installed bundle's parts against the game folder: installed, missing, turned off, optional. */
function PartsList({ rows }: { rows: ReturnType<typeof partRows> }) {
  return (
    <ul className="k-stack" aria-label="Parts" style={{ listStyle: "none", margin: 0, padding: 0, gap: 6 }}>
      {rows.map((r) => (
        <li key={r.name} className="k-row" aria-label={`${r.name}: ${STATE[r.state][0]}`} style={{ gap: 10, flexWrap: "nowrap" }}>
          <span className="k-mono" style={{ fontSize: 15.5, minWidth: 120 }}>{r.name}</span>
          <span className={`k-pill ${STATE[r.state][1]}`}>{STATE[r.state][0]}</span>
          <span className="k-hint k-ellipsis">{PART_HINT[r.name] ?? r.part?.description ?? ""}</span>
        </li>
      ))}
    </ul>
  );
}

/** What the install will do with each part, when the player does not choose: keep, add, leave off. */
function planWord(c: string, plan: string[], receipt: InstallReceipt | null, isDefault: boolean): [string, string] {
  const inGame = !!receipt?.runtime.components.includes(c);
  if (plan.includes(c)) return !receipt ? ["Installs", "k-tone-ok"] : inGame ? ["Keeps", "k-tone-ok"] : ["Adds", "k-tone-gold"];
  if (receipt?.runtime.deselected?.includes(c)) return ["Off, your choice", "k-tone-idle"];
  return inGame ? ["Removes", "k-tone-warn"] : isDefault ? ["Off", "k-tone-idle"] : ["Optional", "k-tone-idle"];
}

function InstallModal({ bundle, side, receipt, onClose, onInstall }: {
  bundle: RuntimeBundle | null; side: RunSide; receipt: InstallReceipt | null; onClose: () => void; onInstall: (b: RuntimeBundle, components: string[] | null) => void;
}) {
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
    <Modal open onClose={onClose} title={`Install Kiln ${bundle.kiln}`} width={480}
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={custom && !parts.includes("core")} onClick={() => onInstall(bundle, custom ? parts : null)}>Install</Button></>}>
      <span className="k-mono k-muted">{bundle.version}</span>
      <Toggle checked={!custom} onChange={(on) => setCustom(!on)} label={receipt ? "Keep my parts" : "Default parts"}
        hint={receipt ? "Keeps your parts and adds defaults you have not turned off. Off: choose below."
          : "Core, signatures and the parts for EMF, EML, ShroudForge and Enshwick mods, for this side."} />
      {custom ? (
        <div className="k-stack">
          {offered.map((p) => (
            <Toggle key={p.name} checked={parts.includes(p.name)} onChange={(on) => toggle(p.name, on)}
              disabled={p.name === "core" || selectParts(bundle, side, parts.filter((c) => c !== p.name)).includes(p.name)}
              label={p.name} hint={PART_HINT[p.name] ?? p.description} />
          ))}
        </div>
      ) : (
        <ul className="k-stack" aria-label="Parts this install puts in" style={{ listStyle: "none", margin: 0, padding: 0, gap: 6 }}>
          {offered.map((p) => {
            const [word, tone] = planWord(p.name, plan, receipt, p.default);
            return (
              <li key={p.name} className="k-row" style={{ gap: 10, flexWrap: "nowrap" }}>
                <span className="k-mono" style={{ fontSize: 15.5, minWidth: 120 }}>{p.name}</span>
                <span className={`k-pill ${tone}`}>{word}</span>
                <span className="k-hint k-ellipsis">{PART_HINT[p.name] ?? p.description ?? ""}</span>
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
  const task = useTasks((s) => (change.taskId ? s.tasks[change.taskId] : undefined));
  const dismiss = <button type="button" className="k-link" onClick={onDismiss}>Dismiss</button>;
  if (change.state === "failed") {
    return (
      <Banner tone="err" title={`${change.what} failed`} actions={dismiss}>
        <span style={{ overflowWrap: "anywhere" }}>{change.error?.message ?? "It stopped without a reason."}</span>{" "}
        {installed ? <>The game keeps Kiln <span className="k-mono">{installed}</span>.</> : "Kiln is not in the game."}
      </Banner>
    );
  }
  if (change.state === "done") {
    // the task's last line is the backend's check of the game folder ("Kiln … is in the game folder (N files, parts)")
    return <Banner tone="ok" title={`${change.what}: done`} actions={dismiss}>{task?.message ?? (installed ? `Kiln ${installed} is in the game folder.` : "Kiln is not in the game.")}</Banner>;
  }
  return task ? <TaskProgress task={task} /> : (
    <TaskProgress cancel={false} task={{ id: "starting", kind: "runtime", title: change.what, state: "running", progress: null, message: "Starting", started: new Date(change.at).toISOString(), finished: null }} />
  );
}

function ChainView({ st, boot, live, build }: { st: InstallState | undefined; boot: BootJson | null; live: boolean; build: string | null }) {
  const chained = st?.receipt?.replaced.filter((r) => r.action === "chained") ?? [];
  const backed = st?.receipt?.replaced.filter((r) => r.action === "backed-up") ?? [];
  const chain = boot?.chain ?? [];
  type Node = { file: string; what: string; kind: "game" | "kiln" | "other" | "bad" | "end" };
  const nodes: Node[] = [{ file: "enshrouded.exe", what: build ? `Build ${build}${boot?.exe?.verified ? ", verified" : ""}` : "The game", kind: "game" }];
  nodes.push({ file: "dinput8.dll", what: st?.installed ? `Kiln proxy ${st.proxy?.version ?? ""}` : st?.proxy && st.proxy.kind !== "none" ? `${st.proxy.kind} proxy` : "System DLL", kind: st?.installed ? "kiln" : "other" });
  const seen = new Set<string>();
  for (const c of chain) {
    seen.add(c.path.toLowerCase());
    nodes.push({ file: c.path.split(/[\\/]/).pop() ?? c.path, what: `${c.source}, ${c.status}`, kind: c.status === "refused" || c.status === "failed" ? "bad" : "other" });
  }
  for (const r of chained) {
    const name = (r.chainPath ?? r.path).split(/[\\/]/).pop() ?? r.path;
    if ([...seen].some((x) => x.endsWith(name.toLowerCase()))) continue;
    nodes.push({ file: name, what: `${r.class ?? "unknown"}, chained`, kind: "other" });
  }
  if (st?.installed) {
    nodes.push({ file: "kiln-core.dll", what: boot ? `Kiln ${boot.kiln}` : "Kiln", kind: "kiln" });
    nodes.push({ file: "services, Kiln.Host", what: "Then your mods", kind: "end" });
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
      {backed.length > 0 && <span className="k-hint">Backed up, not chained: <span className="k-mono">{backed.map((b) => b.path).join(", ")}</span></span>}
    </>
  );
}
