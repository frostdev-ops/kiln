// / Play: install card, active profile, Play / Play vanilla once / Play profile once, live boot
// status (state/boot.json), refusal banner (state/last-server.json). The modpack offer is a modal
// (src/pages/servers/ModpackOffer.tsx, loaded when it exists).
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, ChevronDown, Copy, FolderOpen, FolderPlus, Gamepad2, Play, RefreshCw, Search, ShieldCheck } from "lucide-react";
import { lazy, Suspense, useState, type ComponentType } from "react";
import { Link, useNavigate } from "react-router-dom";
import * as api from "../api/commands";
import type { BootJson, GameInstall, LastServer, LaunchMode } from "../api/commands";
import { invalidateGame, qk, useActiveProfile, useGame, useGameSel, useGameStatus, useLaunchState, useProfiles } from "../api/hooks";
import { gameState } from "../shell/gameState";
import {
  Badge, Banner, Button, Card, EmptyState, Field, IconButton, KeyValue, Menu, Modal, Page, PathPicker, Select, Skeleton, StatusPill,
  errorText, fmtAgo, shortHash, toast,
} from "../ui";

/** Windows (WebView2): path examples in Windows form. */
const WIN = /Windows/i.test(navigator.userAgent);

type OfferProps = { gameId: string; lastServer: LastServer; open: boolean; onClose: () => void };
const offerSlot = import.meta.glob<{ default: ComponentType<OfferProps> }>("./servers/ModpackOffer.tsx");
const ModpackOffer = offerSlot["./servers/ModpackOffer.tsx"] ? lazy(offerSlot["./servers/ModpackOffer.tsx"]) : null;

/** When and how this launcher last started each game (this session). */
const launchedAt: Record<string, number> = {};
const launchedMode: Record<string, LaunchMode> = {};

export default function PlayPage() {
  const { game, games, loading, error } = useGame();
  if (loading) return <Page><Skeleton lines={4} /></Page>;
  if (!game) return <Page><NoGame error={error} hasOther={games.length > 0} /></Page>;
  return <PlayFor game={game} />;
}

function NoGame({ error, hasOther }: { error: unknown; hasOther: boolean }) {
  const qc = useQueryClient();
  const [adding, setAdding] = useState(false);
  const detect = useMutation({
    mutationFn: api.gameDetect,
    onSuccess: (g) => { qc.setQueryData(qk.games, g); if (!g.some((x) => x.kind === "client")) toast.info("No Enshrouded found", "Add its folder by hand."); },
    onError: (e) => toast.error(e),
  });
  return (
    <>
      <EmptyState icon={<Gamepad2 />} title={hasOther ? "No client found" : "Find Enshrouded"}
        hint={error ? errorText(error) : "Kiln looks in every Steam library."}
        action={<>
          <Button variant="primary" icon={<Search />} loading={detect.isPending} onClick={() => detect.mutate()}>Detect</Button>
          <Button icon={<FolderPlus />} onClick={() => setAdding(true)}>Add folder</Button>
        </>} />
      <AddGameModal open={adding} onClose={() => setAdding(false)} />
    </>
  );
}

function AddGameModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const qc = useQueryClient();
  const setGameId = useGameSel((s) => s.setGameId);
  const [path, setPath] = useState("");
  const add = useMutation({
    mutationFn: () => api.gameAdd(path.trim()),
    onSuccess: (g) => { qc.invalidateQueries({ queryKey: qk.games }); setGameId(g.id); toast.ok("Game added"); onClose(); },
    onError: (e) => toast.error(e),
  });
  return (
    <Modal open={open} onClose={onClose} title="Add a game folder"
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!path.trim()} loading={add.isPending} onClick={() => add.mutate()}>Add</Button></>}>
      <Field label="Folder" hint="The folder with enshrouded.exe." htmlFor="add-game">
        <PathPicker id="add-game" value={path} onChange={setPath} directory placeholder={WIN ? "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Enshrouded" : "~/…/steamapps/common/Enshrouded"} />
      </Field>
    </Modal>
  );
}

function PlayFor({ game }: { game: GameInstall }) {
  const qc = useQueryClient();
  const nav = useNavigate();
  const status = useGameStatus(game.id);
  const state = useLaunchState(game.id);
  const active = useActiveProfile(game.id);
  const profiles = useProfiles();
  const pending = useQuery({ queryKey: qk.pending(game.id), queryFn: () => api.modpackPending(game.id), refetchInterval: 15000 });
  const [onceOpen, setOnceOpen] = useState(false);
  const [vanillaOpen, setVanillaOpen] = useState(false);
  const [offerOpen, setOfferOpen] = useState(false);
  const raw = gameState(status.data, state.data, status.isLoading);
  // a vanilla start writes no boot.json: say so when this launcher started it that way
  const view = raw.phase === "running" && !raw.detail?.startsWith("Kiln") && launchedMode[game.id] === "vanilla"
    ? { ...raw, phase: "vanilla" as const, label: "Vanilla", tone: "info" as const, detail: "Started without Kiln" } : raw;
  const s = status.data;
  const installed = !!s?.runtime.installed;
  const activeProfile = profiles.data?.find((p) => p.id === active.data?.profileId) ?? null;

  const launch = useMutation({
    mutationFn: (req: api.LaunchRequest) => api.launchPlay(game.id, req),
    onSuccess: (r) => {
      launchedAt[game.id] = Date.now();
      launchedMode[game.id] = r.mode;
      invalidateGame(qc, game.id);
      toast.ok(r.mode === "vanilla" ? "Starting vanilla" : r.mode === "no-mods" ? "Starting without mods" : "Starting", r.backup ? "Saves backed up first." : undefined);
    },
    onError: (e) => toast.error(e),
  });
  const play = (mode: LaunchMode, profileId?: string) => launch.mutate({ mode, profileId: profileId ?? null });
  const busy = launch.isPending || view.phase === "starting";
  // play-once.ini still there 120 s after our launch: the proxy did not run (contracts.md, launch flow)
  const at = launchedAt[game.id];
  const stuck = !!s?.playOncePending && !s.running && !!at && Date.now() - at > 120_000;
  const canPlay = !!s && !s.running && !s.steamUpdating;

  return (
    <Page>
      {pending.data?.outcome === "rejected" && (
        <Banner tone="err" title={`${pending.data.server.name ?? "A server"} refused you`}
          actions={<>
            {pending.data.modpack && <Button size="s" variant="primary" onClick={() => setOfferOpen(true)}>Get modpack</Button>}
            {pending.data.server.id && <Button size="s" variant="ghost" onClick={() => api.modpackDismiss(game.id, pending.data!.server.id!).then(() => qc.invalidateQueries({ queryKey: qk.pending(game.id) })).catch((e) => toast.error(e))}>Dismiss</Button>}
          </>}>
          {pending.data.reason ?? (pending.data.missing.length > 0 ? <>Missing: <span className="k-mono">{pending.data.missing.join(", ")}</span>.</> : "It needs mods you do not have.")}
        </Banner>
      )}

      <section className="k-hero" aria-label="Play">
        <div className="k-hero-main">
          <span className="k-hero-kicker">{activeProfile ? activeProfile.name : installed ? "Drop-in mods" : "Vanilla"}</span>
          <h1 className="k-hero-title">Enshrouded</h1>
          <div className="k-row">
            <StatusPill tone={view.tone} pulse={view.pulse}>{view.label}</StatusPill>
            {view.detail && <span className="k-muted" style={{ fontSize: 13 }}>{view.detail}</span>}
          </div>
        </div>
        <div className="k-hero-actions">
          {!s ? (
            <Button variant="molten" size="l" icon={<Play />} loading={status.isLoading} disabled>Play</Button>
          ) : installed ? (
            <div className="k-split">
              <Button variant="molten" size="l" icon={<Play />} loading={busy} disabled={!canPlay} onClick={() => play("profile")}>
                {s?.running ? view.label : "Play"}
              </Button>
              <Menu label="More ways to play" icon={<ChevronDown />} variant="molten" size="l" items={[
                { label: "Play vanilla once…", onSelect: () => setVanillaOpen(true), disabled: !canPlay },
                { label: "Play without mods once", onSelect: () => play("no-mods"), disabled: !canPlay },
                { label: "Play a profile once…", onSelect: () => setOnceOpen(true), disabled: !canPlay },
              ]} />
            </div>
          ) : (
            <>
              <Button variant="molten" size="l" icon={<ShieldCheck />} onClick={() => nav("/runtime?install=1")}>Install Kiln</Button>
              <Button variant="ghost" icon={<Play />} disabled={!canPlay} loading={busy} onClick={() => play("vanilla")}>Play vanilla</Button>
            </>
          )}
          <span className="k-dim" style={{ fontSize: 11.5, textAlign: "center" }}>Opens through Steam</span>
        </div>
      </section>

      {installed && s?.launchOptions?.advice && (stuck || !s.launchOptions.hasDllOverride) && <LaunchAdvice advice={s.launchOptions.advice} stuck={stuck} />}

      <div className="k-grid k-grid-main">
        <div className="k-stack" style={{ gap: 16 }}>
          <BootCard boot={state.data?.boot ?? null} stale={!!state.data?.stale} running={!!s?.running} mods={state.data?.mods ?? null} native={state.data?.native ?? null} />
        </div>
        <div className="k-stack" style={{ gap: 16 }}>
          <InstallCard game={game} />
          <ProfileCard gameId={game.id} running={!!s?.running} />
        </div>
      </div>

      <PlayOnceModal open={onceOpen} onClose={() => setOnceOpen(false)} onPlay={(pid) => { setOnceOpen(false); play("once", pid); }} />
      <VanillaOnceModal open={vanillaOpen} onClose={() => setVanillaOpen(false)} onPlay={() => { setVanillaOpen(false); play("vanilla"); }} />
      {pending.data && offerOpen && (ModpackOffer ? (
        <Suspense fallback={null}><ModpackOffer gameId={game.id} lastServer={pending.data} open={offerOpen} onClose={() => setOfferOpen(false)} /></Suspense>
      ) : (
        <Modal open onClose={() => setOfferOpen(false)} title="Modpack" footer={<Button onClick={() => setOfferOpen(false)}>Close</Button>}>
          <KeyValue rows={[["Server", pending.data.server.name ?? pending.data.server.id], ["Missing", pending.data.missing.join(", ") || "-"], ["URL", <span className="k-mono">{pending.data.modpack?.url}</span>]]} />
          <span className="k-hint">Modpack install is not available in this build.</span>
        </Modal>
      ))}
    </Page>
  );
}

function LaunchAdvice({ advice, stuck }: { advice: string; stuck: boolean }) {
  return (
    <Banner tone="warn" title={stuck ? "Kiln did not start" : "One Steam setting needed"} actions={<IconButton label="Copy" icon={<Copy />} onClick={() => navigator.clipboard?.writeText(advice).then(() => toast.ok("Copied"))} />}>
      Set this in Steam → Enshrouded → Properties → Launch options:
      <div className="k-code-block" style={{ marginTop: 6 }}>{advice}</div>
    </Banner>
  );
}

const STEPS: [string, (b: BootJson) => boolean][] = [
  ["Proxy", () => true],
  ["Core", () => true],
  ["Build", (b) => !!b.exe?.verified || b.policy === "any"],
  ["Hooks", (b) => b.hooks],
  [".NET", (b) => !!b.clr?.started],
  ["Mods", (b) => (b.native?.failed ?? 0) === 0 && (b.errors?.length ?? 0) === 0],
];

function BootCard({ boot, stale, running, mods, native }: { boot: BootJson | null; stale: boolean; running: boolean; mods: api.ModsJson | null; native: api.NativeJson | null }) {
  if (!boot) {
    return (
      <Card title="Boot">
        <EmptyState title={running ? "Waiting for Kiln" : "No boot yet"} hint={running ? "boot.json appears when Kiln is up." : "Start the game to see what loads."} />
      </Card>
    );
  }
  const loaded = mods?.mods.filter((m) => m.state === "loaded").length ?? 0;
  const failed = mods?.mods.filter((m) => m.state === "failed").length ?? 0;
  const chainBad = boot.chain.filter((c) => c.status !== "loaded" && c.status !== "duplicate" && c.status !== "skipped");
  return (
    <Card title="Boot" sub={stale ? `Last run · ${fmtAgo(boot.written)}` : `Kiln ${boot.kiln} · ${boot.bootMs ? `${Math.round(boot.bootMs)} ms` : fmtAgo(boot.written)}`}
      actions={stale ? <Badge>last run</Badge> : <Badge tone="ok">live</Badge>} glow={!stale && running}>
      <div className="k-boot-steps">
        {STEPS.map(([name, ok]) => (
          <span key={name} className={"k-step" + (ok(boot) ? " on" : "")}>{ok(boot) ? <Check /> : <span style={{ width: 13 }}>·</span>}{name}</span>
        ))}
      </div>
      {boot.safeIdle && <Banner tone="warn" title="Safe idle">{boot.safeIdle}</Banner>}
      {boot.profile.mode === "invalid" && <Banner tone="err" title="profile.json is invalid">{boot.profile.error ?? "No mods loaded."}</Banner>}
      {(boot.errors?.length ?? 0) > 0 && <Banner tone="err" title={`${boot.errors!.length} boot error${boot.errors!.length > 1 ? "s" : ""}`}>{boot.errors!.slice(0, 3).join(" · ")}</Banner>}
      <KeyValue rows={[
        ["Profile", boot.profile.name ? <>{boot.profile.name} <span className="k-dim">gen {boot.profile.generation}</span></> : boot.profile.mode],
        boot.playOnce && boot.playOnce.mode !== "none" && ["Play once", <Badge tone={boot.playOnce.mode.startsWith("ignored") ? "warn" : "ember"}>{boot.playOnce.mode}</Badge>],
        ["Build", boot.exe?.verified ? <span>verified <span className="k-dim k-mono">{shortHash(boot.exe.sha256)}</span></span> : <span style={{ color: "var(--k-warn)" }}>not verified</span>],
        ["Managed", mods ? <>{loaded} loaded{failed ? <span style={{ color: "var(--k-err)" }}> · {failed} failed</span> : null}</> : boot.clr?.error ?? "-"],
        ["Native", native ? `${native.mods.filter((m) => m.state === "loaded").length} loaded · policy ${native.policy}` : boot.native ? `${boot.native.loaded} loaded` : "-"],
        ["Chain", boot.chain.length ? `${boot.chain.filter((c) => c.status === "loaded").length} loaded${chainBad.length ? ` · ${chainBad.length} refused` : ""}` : "none"],
        ["Services", <span className="k-muted">{boot.services?.length ?? 0}</span>],
      ]} />
      {mods && mods.mods.some((m) => m.state === "failed" || m.state === "skipped") && (
        <div className="k-stack">
          {mods.mods.filter((m) => m.state === "failed" || m.state === "skipped").map((m) => (
            <div key={m.id} className="k-reject"><span className="k-mono">{m.id}</span><span className="k-muted">{m.reason ?? m.state}</span></div>
          ))}
        </div>
      )}
    </Card>
  );
}

function InstallCard({ game }: { game: GameInstall }) {
  const qc = useQueryClient();
  const { games } = useGame();
  const setGameId = useGameSel((s) => s.setGameId);
  const status = useGameStatus(game.id);
  const [adding, setAdding] = useState(false);
  const detect = useMutation({ mutationFn: api.gameDetect, onSuccess: (g) => { qc.setQueryData(qk.games, g); toast.ok(`${g.length} found`); }, onError: (e) => toast.error(e) });
  const rt = status.data?.runtime;
  const clients = games.filter((g) => g.kind === "client");
  return (
    <Card title="Install" actions={<>
      <IconButton label="Detect games" icon={<RefreshCw />} onClick={() => detect.mutate()} loading={detect.isPending} />
      <IconButton label="Add folder" icon={<FolderPlus />} onClick={() => setAdding(true)} />
      <IconButton label="Open folder" icon={<FolderOpen />} onClick={() => api.appOpenPath(game.path).catch((e) => toast.error(e))} />
    </>}>
      {clients.length > 1 && (
        <Select aria-label="Game" value={game.id} onChange={setGameId} options={clients.map((g) => ({ value: g.id, label: `${g.path.split(/[\\/]/).pop()} · ${g.source}` }))} />
      )}
      <KeyValue rows={[
        ["Folder", <span className="k-mono k-ellipsis" title={game.path} style={{ display: "block" }}>{game.path}</span>],
        ["Kiln", rt?.installed ? <>{rt.receipt?.runtime.version ?? "installed"}{rt.upgradeAvailable && <> · <Link to="/runtime" className="k-link">update {rt.upgradeAvailable}</Link></>}</> : <Link to="/runtime" className="k-link">not installed</Link>],
        ["Proxy", rt?.proxy ? <Badge tone={rt.proxy.kind === "kiln" ? "ember" : rt.proxy.kind === "none" ? "idle" : "warn"}>{rt.proxy.kind}</Badge> : "-"],
        ["Build", game.buildId ?? "-"],
        ["Runs on", `${game.platform}${game.pathMode === "copy" ? " · copy mode" : ""}`],
        rt && rt.modified.length > 0 && ["Changed", <span style={{ color: "var(--k-warn)" }}>{rt.modified.length} files</span>],
      ]} />
      <AddGameModal open={adding} onClose={() => setAdding(false)} />
    </Card>
  );
}

function ProfileCard({ gameId, running }: { gameId: string; running: boolean }) {
  const qc = useQueryClient();
  const profiles = useProfiles();
  const active = useActiveProfile(gameId);
  const activate = useMutation({
    mutationFn: (pid: string) => (pid ? api.profileActivate(pid, gameId) : api.profileDeactivate(gameId).then(() => null)),
    onSuccess: () => { invalidateGame(qc, gameId); toast.ok("Profile set"); },
    onError: (e) => toast.error(e),
  });
  const list = profiles.data ?? [];
  const cur = list.find((p) => p.id === active.data?.profileId);
  return (
    <Card title="Profile" actions={cur && <Link to={`/profiles/${cur.id}/mods`}><Button size="s" variant="ghost">Edit</Button></Link>}>
      <Select aria-label="Active profile" value={active.data?.profileId ?? ""} disabled={running || activate.isPending}
        onChange={(v) => activate.mutate(v)} options={[{ value: "", label: "None (drop-in folders)" }, ...list.map((p) => ({ value: p.id, label: p.name }))]} />
      {cur ? (
        <div className="k-row" style={{ fontSize: 12.5 }}>
          {(["kiln", "native", "emf", "plugin"] as const).filter((k) => cur.counts[k]).map((k) => <Badge key={k}>{cur.counts[k]} {k}</Badge>)}
          {cur.server && <Badge tone="info">{cur.server.name ?? "server"}</Badge>}
          <span className="k-dim">set {fmtAgo(active.data?.activated)}</span>
        </div>
      ) : <span className="k-hint">Steam's Play button uses this too.</span>}
      {running && <span className="k-hint">Close the game to switch.</span>}
    </Card>
  );
}

function VanillaOnceModal({ open, onClose, onPlay }: { open: boolean; onClose: () => void; onPlay: () => void }) {
  return (
    <Modal open={open} onClose={onClose} title="Play vanilla once"
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" icon={<Play />} onClick={onPlay}>Play vanilla</Button></>}>
      <p>Your game runs without Kiln. Servers whose mods need Kiln will refuse you after loading, with a message about a game update; that&apos;s expected.</p>
      <p>A character carrying items from a Kiln mod can crash on a server that lets vanilla clients in, and may lose those items on other servers. Use another character.</p>
    </Modal>
  );
}

function PlayOnceModal({ open, onClose, onPlay }: { open: boolean; onClose: () => void; onPlay: (pid: string) => void }) {
  const profiles = useProfiles();
  const [pid, setPid] = useState("");
  const list = profiles.data ?? [];
  const chosen = pid || list[0]?.id || "";
  return (
    <Modal open={open} onClose={onClose} title="Play a profile once"
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" icon={<Play />} disabled={!chosen} onClick={() => onPlay(chosen)}>Play</Button></>}>
      <Field label="Profile" hint="The next start after this one is back to the active profile." htmlFor="once-profile">
        <Select id="once-profile" value={chosen} onChange={setPid} options={list.map((p) => ({ value: p.id, label: p.name }))} />
      </Field>
    </Modal>
  );
}
