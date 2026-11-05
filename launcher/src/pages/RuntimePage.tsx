// /runtime: install / update / uninstall / rollback Kiln in the selected game, the proxy chain
// (displaced proxies), and "switch from ShroudForge".
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowDown, Flame, Package, RotateCcw, ShieldAlert, Trash2, Upload } from "lucide-react";
import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import * as api from "../api/commands";
import type { BootJson, InstallState, RuntimeBundle } from "../api/commands";
import { invalidateGame, qk, useGame, useGameStatus, useLaunchState } from "../api/hooks";
import {
  Badge, Banner, Button, Card, ConfirmDanger, DropZone, EmptyState, KeyValue, Modal, Page, PageHeader, Skeleton, StatusPill, Table,
  TaskProgress, Toggle, errorText, fmtAgo, runTask, shortHash, toast, useTasks, type Column,
} from "../ui";

export default function RuntimePage() {
  const { game } = useGame();
  const qc = useQueryClient();
  const status = useGameStatus(game?.id);
  const boot = useLaunchState(game?.id);
  const bundles = useQuery({ queryKey: qk.runtimeList, queryFn: api.runtimeList });
  // fresh install state (lists changed files); the top bar's status uses a cached one
  const fresh = useQuery({ queryKey: qk.runtimeStatus(game?.id ?? ""), queryFn: () => api.runtimeStatus(game!.id), enabled: !!game });
  const [task, setTask] = useState<string | null>(null);
  const [uninstall, setUninstall] = useState(false);
  const [switchSf, setSwitchSf] = useState<RuntimeBundle | null>(null);
  const [pick, setPick] = useState<RuntimeBundle | null>(null);
  const tasks = useTasks((s) => s.tasks);
  const busy = !!task && !!tasks[task] && ["running", "queued"].includes(tasks[task].state);

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

  if (!game) return <Page><PageHeader title="Runtime" /><EmptyState title="No game" hint="Pick a game on Play." /></Page>;
  const st = fresh.data ?? status.data?.runtime;
  const running = !!status.data?.running;
  const sf = st?.proxy?.kind === "shroudforge";
  const current = st?.receipt?.runtime.version ?? null;
  const newest = bundles.data?.[0];

  const go = async (label: string, start: () => Promise<string>) => {
    try {
      await runTask(async () => { const id = await start(); setTask(id); return id; }, { done: label });
    } catch { /* toasted */ }
    invalidateGame(qc, game.id);
  };
  const install = (b: RuntimeBundle, opts: api.InstallOptions = {}) =>
    go(current ? `Kiln ${b.kiln} installed` : "Kiln installed", () => (current && b.version !== current && isOlder(b.version, current) ? api.runtimeRollback(game.id, b.version) : api.runtimeInstall(game.id, b.version, opts)));

  const cols: Column<RuntimeBundle>[] = [
    { key: "v", header: "Version", width: "minmax(0, 1.6fr)", render: (b) => (
      <span className="k-row" style={{ gap: 8 }}>
        <span className="k-mono" style={{ color: "var(--k-text-0)" }}>{b.version}</span>
        {b.version === current && <StatusPill tone="ember">installed</StatusPill>}
      </span>
    ) },
    { key: "c", header: "Built", width: "120px", render: (b) => <span className="k-muted">{fmtAgo(b.created)}</span> },
    { key: "comp", header: "Parts", width: "minmax(0, 1fr)", render: (b) => <span className="k-muted">{b.components.join(", ")}</span> },
    { key: "act", header: "", width: "130px", align: "right", render: (b) => b.version === current ? null : (
      <Button size="s" variant={current && isOlder(b.version, current) ? "secondary" : "primary"} icon={current && isOlder(b.version, current) ? <RotateCcw /> : <ArrowDown />}
        disabled={running || busy || !!status.data?.steamUpdating} onClick={() => (sf ? setSwitchSf(b) : setPick(b))}>
        {!current ? "Install" : isOlder(b.version, current) ? "Roll back" : "Update"}
      </Button>
    ) },
  ];

  return (
    <Page>
      <PageHeader title="Runtime" sub="Kiln's files in the game folder." actions={st?.installed && (
        <Button variant="danger" icon={<Trash2 />} disabled={running || busy} onClick={() => setUninstall(true)}>Uninstall</Button>
      )} />
      {running && <Banner tone="warn">The game is running. Close it to install or remove Kiln.</Banner>}
      {status.data?.steamUpdating && <Banner tone="warn" title="Steam has an update queued">Let Steam finish updating Enshrouded first. Installs wait until then.</Banner>}
      {sf && (
        <Banner tone="err" title="ShroudForge is installed" actions={newest && <Button size="s" variant="danger" icon={<ShieldAlert />} disabled={running || busy} onClick={() => setSwitchSf(newest)}>Switch from ShroudForge</Button>}>
          Kiln replaces it. Its files go to backups and are never chained.
        </Banner>
      )}
      {task && <TaskProgress taskId={task} />}

      <div className="k-grid k-grid-2">
        <Card title="This game" glow={!!st?.installed}>
          {status.isLoading ? <Skeleton /> : status.error ? <EmptyState title="Status unavailable" hint={errorText(status.error)} /> : st && <InstallSummary st={st} />}
        </Card>
        <Card title="Proxy chain" sub="What dinput8.dll loads, in order">
          <ChainView st={st} boot={boot.data?.boot ?? null} />
        </Card>
      </div>

      <Card flush title="Bundles" actions={<span className="k-hint">{bundles.data?.length ?? 0} in the launcher</span>}>
        {bundles.isLoading ? <Skeleton /> : bundles.error ? <EmptyState title="Bundles unavailable" hint={errorText(bundles.error)} /> : (
          <Table aria-label="Runtime bundles" columns={cols} rows={bundles.data ?? []} rowKey={(b) => b.version} rowHeight={48}
            empty={<EmptyState icon={<Package />} title="No bundles" hint="Drop a kiln-runtime zip below." />} />
        )}
      </Card>
      <DropZone onPaths={(p) => p.forEach((x) => importB.mutate(x))} title="Add a runtime bundle" hint="kiln-runtime-<version>.zip" pick={{ filters: [{ name: "Kiln runtime", extensions: ["zip"] }] }} />

      <InstallModal bundle={pick} current={st?.receipt?.runtime.components ?? null} onClose={() => setPick(null)}
        onInstall={(b, components) => { setPick(null); install(b, { components }); }} />
      <ConfirmDanger open={uninstall} onClose={() => setUninstall(false)} title="Uninstall Kiln?" word="uninstall" action="Uninstall"
        message="The original dinput8.dll comes back byte for byte. kiln/ (configs, data, logs) stays."
        onConfirm={() => { setUninstall(false); return go("Kiln removed", () => api.runtimeUninstall(game.id)); }} />
      <ConfirmDanger open={!!switchSf} onClose={() => setSwitchSf(null)} title="Switch from ShroudForge?" word="switch" action="Switch"
        message={<>ShroudForge's proxy and runtime move to <span className="k-mono">kiln/backups/</span>. Kiln {switchSf?.kiln} goes in. ShroudForge mods stop loading.</>}
        onConfirm={() => { const b = switchSf!; setSwitchSf(null); return install(b, { switchFromShroudforge: true }); }} />
      {importB.isPending && <span className="k-hint"><Upload size={12} /> Reading bundle…</span>}
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

function InstallModal({ bundle, current, onClose, onInstall }: {
  bundle: RuntimeBundle | null; current: string[] | null; onClose: () => void; onInstall: (b: RuntimeBundle, components: string[] | null) => void;
}) {
  const [custom, setCustom] = useState(false);
  const [parts, setParts] = useState<string[]>([]);
  const [seen, setSeen] = useState<RuntimeBundle | null>(null);
  if (bundle !== seen) {
    setSeen(bundle);
    if (bundle) { setCustom(!!current); setParts(current ?? bundle.components.filter((c) => c === "core" || c.startsWith("sigdb"))); }
  }
  if (!bundle) return null;
  const toggle = (c: string, on: boolean) => setParts(on ? [...parts, c] : parts.filter((x) => x !== c));
  return (
    <Modal open onClose={onClose} title={`Kiln ${bundle.kiln}`} width={480}
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="primary" icon={<ArrowDown />} disabled={custom && !parts.includes("core")} onClick={() => onInstall(bundle, custom ? parts : null)}>Install</Button></>}>
      <span className="k-mono k-muted" style={{ fontSize: 12 }}>{bundle.version}</span>
      <Toggle checked={!custom} onChange={(on) => setCustom(!on)} label="Default parts" hint={current ? "Off keeps your choice below." : "Core and signatures for this side."} />
      {custom && (
        <div className="k-stack">
          {bundle.components.map((c) => (
            <Toggle key={c} checked={parts.includes(c)} onChange={(on) => toggle(c, on)} disabled={c === "core"} label={c} hint={PART_HINT[c]} />
          ))}
        </div>
      )}
    </Modal>
  );
}

function isOlder(a: string, b: string): boolean {
  const n = (v: string) => v.split(/[+-]/)[0].split(".").map(Number);
  const [x, y] = [n(a), n(b)];
  for (let i = 0; i < 3; i++) if ((x[i] ?? 0) !== (y[i] ?? 0)) return (x[i] ?? 0) < (y[i] ?? 0);
  return a.localeCompare(b) < 0;
}

function InstallSummary({ st }: { st: InstallState }) {
  if (!st.installed) {
    return <EmptyState icon={<Flame />} title="Kiln is not installed" hint={st.proxy && st.proxy.kind !== "none" ? `Found a ${st.proxy.kind} proxy. It will be chained.` : "Pick a bundle below."} />;
  }
  const r = st.receipt;
  return (
    <>
      <KeyValue rows={[
        ["Version", <span className="k-mono">{r?.runtime.version}</span>],
        ["Parts", r?.runtime.components.join(", ")],
        ["Installed", fmtAgo(r?.installed)],
        r?.updated && ["Updated", fmtAgo(r.updated)],
        ["Files", `${r?.files.length ?? 0}`],
        ["Proxy", st.proxy ? <><Badge tone="ember">{st.proxy.kind}</Badge> <span className="k-dim k-mono">{st.proxy.version ?? ""} {shortHash(st.proxy.sha256)}</span></> : "-"],
        st.upgradeAvailable && ["Update", <Badge tone="ember">{st.upgradeAvailable}</Badge>],
      ]} />
      {st.modified.length > 0 && <Banner tone="warn" title={`${st.modified.length} files changed since install`}><span className="k-mono" style={{ fontSize: 12 }}>{st.modified.slice(0, 4).join(", ")}{st.modified.length > 4 ? " …" : ""}</span></Banner>}
    </>
  );
}

function ChainView({ st, boot }: { st: InstallState | undefined; boot: BootJson | null }) {
  const chained = st?.receipt?.replaced.filter((r) => r.action === "chained") ?? [];
  const backed = st?.receipt?.replaced.filter((r) => r.action === "backed-up") ?? [];
  const live = boot?.chain ?? [];
  const nodes: { label: string; sub: string; tone: "ember" | "idle" | "err" | "ok"; kiln?: boolean }[] = [
    { label: "dinput8.dll", sub: st?.installed ? `Kiln proxy ${st.proxy?.version ?? ""}` : st?.proxy && st.proxy.kind !== "none" ? `${st.proxy.kind} proxy` : "system DLL", tone: st?.installed ? "ember" : "idle", kiln: st?.installed },
  ];
  const seen = new Set<string>();
  for (const c of live) {
    seen.add(c.path.toLowerCase());
    nodes.push({ label: c.path.split(/[\\/]/).pop() ?? c.path, sub: `${c.source}${c.detail ? ` · ${c.detail}` : ""}`, tone: c.status === "loaded" ? "ok" : c.status === "refused" || c.status === "failed" ? "err" : "idle" });
  }
  for (const r of chained) {
    const name = (r.chainPath ?? r.path).split(/[\\/]/).pop() ?? r.path;
    if ([...seen].some((s) => s.endsWith(name.toLowerCase()))) continue;
    nodes.push({ label: name, sub: `${r.class ?? "unknown"} · chained`, tone: "idle" });
  }
  if (st?.installed) nodes.push({ label: "kiln-core.dll", sub: boot ? `Kiln ${boot.kiln}` : "Kiln", tone: "ember", kiln: true });
  return (
    <div className="k-chain">
      {nodes.map((n, i) => (
        <div key={i}>
          {i > 0 && <div className="k-chain-link" />}
          <div className={"k-chain-node" + (n.kiln ? " kiln" : "")}>
            <span className="k-stack" style={{ gap: 0, flex: 1, minWidth: 0 }}>
              <span className="k-mono k-ellipsis" style={{ color: "var(--k-text-0)" }}>{n.label}</span>
              <span className="k-hint">{n.sub}</span>
            </span>
            {n.tone !== "idle" && <Badge tone={n.tone}>{n.tone === "ok" ? "loaded" : n.tone === "err" ? "refused" : "kiln"}</Badge>}
          </div>
        </div>
      ))}
      {backed.length > 0 && <span className="k-hint" style={{ marginTop: 10 }}>Backed up: {backed.map((b) => b.path).join(", ")}</span>}
      {!live.length && st?.installed && <span className="k-hint" style={{ marginTop: 10 }}>Live statuses show after a start.</span>}
    </div>
  );
}
