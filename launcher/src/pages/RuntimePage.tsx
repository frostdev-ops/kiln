// /runtime: install / update / uninstall / rollback Kiln in the selected game, the proxy chain
// (displaced proxies), and "switch from ShroudForge". The last change and its outcome stay on the page (and a failure
// on Play) until dismissed: an update that did not happen must not look like one that did.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import * as api from "../api/commands";
import type { BootJson, InstallState, RunSide, RuntimeBundle } from "../api/commands";
import { invalidateGame, qk, useGame, useGameStatus, useLaunchState, useRuntimeChanges, type RuntimeChange } from "../api/hooks";
import {
  Banner, Button, Card, ConfirmDanger, DropZone, EmptyState, Modal, Page, PageHeader, Skeleton, Table,
  TaskProgress, Toggle, errorText, fmtAgo, runTask, toast, useTasks, type Column,
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
  const { put, clear } = useRuntimeChanges.getState();
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
  }, [wantInstall, rt, bundles.data, setParams]);

  if (!game) return <Page><PageHeader title="Runtime" sub="No game yet. Find Enshrouded on Play first." /></Page>;
  const st = fresh.data ?? status.data?.runtime;
  const running = !!status.data?.running;
  const sf = st?.proxy?.kind === "shroudforge";
  const current = st?.receipt?.runtime.version ?? null;
  const newest = bundles.data?.[0];

  // the change is kept per game (useRuntimeChanges): its outcome outlives this page, and Play shows a failure
  const go = async (what: string, version: string | null, label: string, start: () => Promise<string>) => {
    const gid = game.id;
    const base: RuntimeChange = { what, version, taskId: null, state: "running", error: null, at: Date.now() };
    put(gid, base);
    try {
      const r = await runTask<api.InstallReceipt | null>(async () => { const id = await start(); put(gid, { ...base, taskId: id }); return id; }, { done: label });
      put(gid, { ...(useRuntimeChanges.getState().last[gid] ?? base), state: "done" });
      if (r?.runtime) {
        // the task checked the game folder against this receipt: show it now (re-hashing for the page's own status
        // takes a while), the refetch below confirms it
        const newer = (bundles.data ?? []).find((b) => b.sides.includes(game.kind) && isOlder(r.runtime.version, b.version, bundles.data ?? []))?.version ?? null;
        const fix = (s: InstallState | undefined) => s && { ...s, installed: true, receipt: r, modified: [], upgradeAvailable: newer };
        qc.setQueryData<InstallState>(qk.runtimeStatus(gid), fix);
        qc.setQueryData<api.GameStatus>(qk.gameStatus(gid), (s) => s && { ...s, runtime: fix(s.runtime)! });
      }
    } catch (e) {
      put(gid, { ...(useRuntimeChanges.getState().last[gid] ?? base), state: "failed", error: api.toKilnError(e) });
    }
    invalidateGame(qc, gid);
  };
  const install = (b: RuntimeBundle, opts: api.InstallOptions = {}) => {
    const back = !!current && b.version !== current && isOlder(b.version, current, bundles.data ?? []);
    const what = !current ? `Install of ${b.version}` : back ? `Rollback to ${b.version}` : `Update to ${b.version}`;
    return go(what, b.version, `Kiln ${b.version} installed`, () => (back ? api.runtimeRollback(game.id, b.version) : api.runtimeInstall(game.id, b.version, opts)));
  };

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
      const word = b.version === current ? "Installed" : !current ? "Install" : older ? "Roll back" : "Update";
      return (
        <Button size="s" disabled={b.version === current || running || busy || !!status.data?.steamUpdating} onClick={() => (sf ? setSwitchSf(b) : setPick(b))}>{word}</Button>
      );
    } },
  ];

  const files = st?.receipt?.files.length ?? 0;
  // the whole version (the build date is what tells two dev builds apart) and when it last changed
  const when = st?.receipt?.updated ? `updated ${fmtAgo(st.receipt.updated)}` : `installed ${fmtAgo(st?.receipt?.installed)}`;
  const say = !st ? "Reading the game folder." : st.installed
    ? `Kiln ${current ?? ""} is in the game folder: ${files} files, ${when}.${st.upgradeAvailable ? ` ${st.upgradeAvailable} is ready to install.` : ""}`
    : sf ? "ShroudForge is in this game. Kiln replaces it." : "Kiln is not in this game yet.";
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
          Kiln replaces it. Its files go to backups and are never chained.
        </Banner>
      )}
      {!!st?.modified.length && <Banner tone="warn" title="Changed">{st.modified.length} file{st.modified.length > 1 ? "s" : ""} changed since the install: <span className="k-mono">{st.modified.slice(0, 3).join(", ")}{st.modified.length > 3 ? " …" : ""}</span></Banner>}
      {change && <ChangeOutcome change={change} installed={current} onDismiss={() => clear(game.id)} />}

      <Card title="What the game loads, in order" sub={live ? "Live, from this boot." : "Live statuses show after a start."}>
        {status.isLoading ? <Skeleton lines={2} /> : status.error ? <EmptyState title="The game's state is not readable" hint={errorText(status.error)} /> : <ChainView st={st} boot={boot.data?.boot ?? null} live={live} build={game.buildId} />}
      </Card>

      <Card title="Bundles in the launcher" sub={st?.receipt ? `Parts in this game: ${st.receipt.runtime.components.join(", ")}` : `${bundles.data?.length ?? 0} bundle${bundles.data?.length === 1 ? "" : "s"}`}>
        {bundles.isLoading ? <Skeleton /> : bundles.error ? <EmptyState title="Bundles are not readable" hint={errorText(bundles.error)} /> : (
          <Table aria-label="Runtime bundles" columns={cols} rows={bundles.data ?? []} rowKey={(b) => b.version} rowHeight={50}
            empty={<EmptyState title="No bundles. Add a kiln-runtime zip below." />} />
        )}
        <DropZone variant="row" onPaths={(p) => p.forEach((x) => importB.mutate(x))} title="Add a runtime bundle" hint={importB.isPending ? "Reading the bundle…" : "kiln-runtime-<version>.zip"} pick={{ filters: [{ name: "Kiln runtime", extensions: ["zip"] }] }} />
      </Card>

      <InstallModal bundle={pick} side={game.kind} current={st?.receipt?.runtime.components ?? null} onClose={() => setPick(null)}
        onInstall={(b, components) => { setPick(null); install(b, { components }); }} />
      <ConfirmDanger open={uninstall} onClose={() => setUninstall(false)} title="Uninstall Kiln?" word="uninstall" action="Uninstall"
        message="This removes Kiln's files from the game folder. The original dinput8.dll comes back; kiln/ (configs, data, logs) stays."
        onConfirm={() => { setUninstall(false); return go("Uninstall", null, "Kiln removed", () => api.runtimeUninstall(game.id)); }} />
      <ConfirmDanger open={!!switchSf} onClose={() => setSwitchSf(null)} title="Switch from ShroudForge?" word="switch" action="Switch"
        message={<>ShroudForge moves to <span className="k-mono">kiln/backups/</span> and Kiln {switchSf?.kiln} goes in. ShroudForge mods stop loading.</>}
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
  samples: "sample mods",
};

/** Parts that only one side can take (the bundle manifest's `sides`): a client is never offered them. A part not
 *  listed here is offered; the backend refuses a wrong one before it reads the bundle. */
const PART_SIDES: Record<string, RunSide[]> = {
  admin: ["server"], samples: ["server"], "sigdb-server": ["server"], "sigdb-client": ["client"],
};
const forSide = (c: string, side: RunSide) => PART_SIDES[c]?.includes(side) ?? true;

function InstallModal({ bundle, side, current, onClose, onInstall }: {
  bundle: RuntimeBundle | null; side: RunSide; current: string[] | null; onClose: () => void; onInstall: (b: RuntimeBundle, components: string[] | null) => void;
}) {
  const [custom, setCustom] = useState(false);
  const [parts, setParts] = useState<string[]>([]);
  const [seen, setSeen] = useState<RuntimeBundle | null>(null);
  if (bundle !== seen) {
    setSeen(bundle);
    if (bundle) {
      setCustom(!!current);
      setParts((current ?? bundle.components.filter((c) => c === "core" || c.startsWith("sigdb"))).filter((c) => forSide(c, side) && bundle.components.includes(c)));
    }
  }
  if (!bundle) return null;
  const toggle = (c: string, on: boolean) => setParts(on ? [...parts, c] : parts.filter((x) => x !== c));
  return (
    <Modal open onClose={onClose} title={`Install Kiln ${bundle.kiln}`} width={480}
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={custom && !parts.includes("core")} onClick={() => onInstall(bundle, custom ? parts : null)}>Install</Button></>}>
      <span className="k-mono k-muted">{bundle.version}</span>
      <Toggle checked={!custom} onChange={(on) => setCustom(!on)} label="Default parts" hint={current ? "Off keeps your choice below." : "Core and signatures for this side."} />
      {custom && (
        <div className="k-stack">
          {bundle.components.filter((c) => forSide(c, side)).map((c) => (
            <Toggle key={c} checked={parts.includes(c)} onChange={(on) => toggle(c, on)} disabled={c === "core"} label={c} hint={PART_HINT[c]} />
          ))}
        </div>
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

/** `a` is older than `b`. runtime_list sorts newest first (the version, its build date, then when the bundle was
 *  built), so the list decides; a version not in it compares by version and build date. Never by the git id: two
 *  builds of one day differ only in when they were built. */
function isOlder(a: string, b: string, list: RuntimeBundle[]): boolean {
  const i = list.findIndex((x) => x.version === a), j = list.findIndex((x) => x.version === b);
  if (i >= 0 && j >= 0) return i > j;
  const key = (v: string) => { const [base, build = ""] = v.split("+"); return { n: base.split("-")[0].split(".").map(Number), date: build.split(".")[0] }; };
  const [x, y] = [key(a), key(b)];
  for (let k = 0; k < 3; k++) if ((x.n[k] ?? 0) !== (y.n[k] ?? 0)) return (x.n[k] ?? 0) < (y.n[k] ?? 0);
  return x.date < y.date;
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
