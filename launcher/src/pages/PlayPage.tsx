// / Play: in front of the stage. The active profile's name (a button: change profile), one live sentence, "Just once"
// (vanilla, without mods, another profile), the boot stages while the game starts, four facts, and the game folder.
// The mouth (shell/AppShell.tsx) is the Play button. Banners (refusal, launch option, Steam update) sit above the name.
// The modpack offer is a working dialog (src/pages/servers/ModpackOffer.tsx, loaded when it exists).
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { lazy, Suspense, useEffect, useRef, useState, type ComponentType } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "../api/commands";
import type { GameInstall, LastServer } from "../api/commands";
import { invalidateGame, qk, useActiveProfile, useGame, useGameSel, useGameStatus, useLaunchState, useLibrary, useProfiles } from "../api/hooks";
import { clock, usePlayControl, type PlayControl } from "../shell/play";
import {
  ArchText, Banner, Button, Cones, Field, KeyValue, Modal, PathPicker, Select, errorText, toast,
} from "../ui";

/** Windows (WebView2): path examples in Windows form. */
const WIN = /Windows/i.test(navigator.userAgent);

type OfferProps = { gameId: string; lastServer: LastServer; open: boolean; onClose: () => void };
const offerSlot = import.meta.glob<{ default: ComponentType<OfferProps> }>("./servers/ModpackOffer.tsx");
const ModpackOffer = offerSlot["./servers/ModpackOffer.tsx"] ? lazy(offerSlot["./servers/ModpackOffer.tsx"]) : null;

export default function PlayPage() {
  const ctl = usePlayControl();
  const { games, loading, error } = useGame();
  if (loading) return <section className="k-hero" aria-label="Play" aria-busy />;
  if (!ctl.game) return <NoGame error={error} hasOther={games.length > 0} />;
  return <PlayFor game={ctl.game} ctl={ctl} />;
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
      <section className="k-hero" aria-label="Play">
        <div className="k-hero-kicker">{hasOther ? "No client found" : "No game yet"}</div>
        <h1 className="k-hero-name"><span className="k-title static">Enshrouded</span></h1>
        <div className="k-hero-state">
          <p className="k-hero-say">{error ? errorText(error) : "Kiln looks for the game in every Steam library."}</p>
          <div className="k-hero-once">
            <button type="button" className="k-link" disabled={detect.isPending} onClick={() => detect.mutate()}>{detect.isPending ? "Looking…" : "Detect"}</button>
            <button type="button" className="k-link" onClick={() => setAdding(true)}>Add a folder</button>
          </div>
        </div>
      </section>
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

const PROXY: Record<string, string> = { kiln: "In place", none: "None", shroudforge: "ShroudForge", emf: "EMF's own", unknown: "Unknown", notdll: "Not a DLL" };

function PlayFor({ game, ctl }: { game: GameInstall; ctl: PlayControl }) {
  const qc = useQueryClient();
  const nav = useNavigate();
  const status = useGameStatus(game.id);
  const state = useLaunchState(game.id);
  const active = useActiveProfile(game.id);
  const profiles = useProfiles();
  const pending = useQuery({ queryKey: qk.pending(game.id), queryFn: () => api.modpackPending(game.id), refetchInterval: 15000 });
  const activeId = active.data?.profileId ?? null;
  const preview = useQuery({ queryKey: qk.resolve(activeId ?? "", "client"), queryFn: () => api.profileResolve(activeId!, "client"), enabled: !!activeId });
  const [onceOpen, setOnceOpen] = useState(false);
  const [vanillaOpen, setVanillaOpen] = useState(false);
  const [offerOpen, setOfferOpen] = useState(false);
  const [adding, setAdding] = useState(false);
  const s = status.data;
  const v = ctl.view;
  const boot = state.data?.boot ?? null;
  const fresh = !!boot && !state.data?.stale;
  const list = profiles.data ?? [];
  const cur = list.find((p) => p.id === activeId) ?? null;
  const rt = s?.runtime;
  const detect = useMutation({ mutationFn: api.gameDetect, onSuccess: (g) => { qc.setQueryData(qk.games, g); toast.ok(`${g.filter((x) => x.kind === "client").length} found`); }, onError: (e) => toast.error(e) });
  const { games } = useGame();
  const setGameId = useGameSel((x) => x.setGameId);
  const clients = games.filter((g) => g.kind === "client");
  const lib = useLibrary("kiln");

  // the live sentence
  let say: string;
  if (!s) say = "Checking the game.";
  else if (!rt?.installed) say = rt?.proxy?.kind === "shroudforge" ? "ShroudForge is in this game. Kiln replaces it on Runtime." : "Kiln is not in this game yet. Install puts it in the game folder.";
  else if (!cur) say = "No profile is active: Kiln loads the mods in the game folder.";
  else if (preview.data) {
    const order = preview.data.kiln.order;
    const total = order.length + preview.data.kiln.rejected.filter((r) => r.state !== "disabled").length;
    const away = preview.data.kiln.rejected.filter((r) => r.state === "skipped").map((r) => lib.data?.find((l) => l.id === r.id)?.name ?? r.id);
    say = `${order.length} of ${total} mods load on this client.` + (away.length ? ` ${names(away)} ${away.length === 1 ? "stays" : "stay"} on the server.` : "");
  } else say = `${cur.counts.kiln} Kiln mods in this profile.`;
  const kiln = rt?.receipt?.runtime.version ?? boot?.kiln ?? null;
  const runLine = v.phase === "vanilla" ? "Started without Kiln." : fresh && boot?.profile?.mode === "no-mods" ? `Running with Kiln ${boot?.kiln} and no mods.`
    : v.label === "Safe idle" ? `Kiln is in safe idle: ${boot?.safeIdle ?? "mods stay off"}.` : `Running with Kiln ${kiln ?? ""}${preview.data ? ` and ${preview.data.kiln.order.length} mods` : ""}.`;
  const stages = ["Steam", "Game", "Kiln", "Mods"];
  const starting = ctl.starting;
  const stageAt = !ctl.running ? 0 : !fresh ? 1 : 3;
  const errors = fresh ? boot?.errors?.length ?? 0 : 0;
  const lastBoot = boot ? `${boot.bootMs ? `${(boot.bootMs / 1000).toFixed(1)} s` : "Done"}, ${errors ? `${errors} error${errors > 1 ? "s" : ""}` : "no errors"}` : "Not yet";
  const build = game.buildId ? `build ${game.buildId}${boot?.exe?.verified ? ", verified" : ""}` : "build unknown";

  return (
    <>
      <section className="k-hero" aria-label="Play">
        <div className="k-hero-kicker">
          Enshrouded · {build}
          {clients.length > 1 && (
            <select className="k-kicker-select" aria-label="Game" value={game.id} onChange={(e) => setGameId(e.target.value)}>
              {clients.map((g) => <option key={g.id} value={g.id}>{g.path.split(/[\\/]/).filter(Boolean).slice(-3).join("/")} · {g.source}</option>)}
            </select>
          )}
        </div>
        <Banners game={game} ctl={ctl} pending={pending.data ?? null} onOffer={() => setOfferOpen(true)} boot={fresh ? boot : null} />
        <ProfileName gameId={game.id} name={cur?.name ?? (rt?.installed ? "Drop-in mods" : "Vanilla")} running={ctl.running} />
        {starting ? (
          <div className="k-hero-state" key="start">
            <p className="k-hero-say">Waiting for Kiln to boot.</p>
            <ol className="k-boot" aria-label="Boot stages">
              {stages.map((t, i) => (
                <li key={t}>
                  <Cones step={i < stageAt ? 1 : i === stageAt ? 0 : -1} count={1} />
                  <span className="k-sc" style={{ color: i <= stageAt ? "var(--k-text-0)" : "var(--k-text-2)" }}>{t}</span>
                </li>
              ))}
            </ol>
          </div>
        ) : ctl.running ? (
          <div className="k-hero-state" key="run">
            <p className="k-hero-say">{runLine}</p>
            {ctl.elapsed !== null && <span className="k-muted">Started {clock(ctl.elapsed)} ago{v.detail && !v.detail.startsWith("Kiln") ? ` · ${v.detail}` : ""}.</span>}
          </div>
        ) : (
          <div className="k-hero-state" key="stop">
            <p className="k-hero-say">{say}</p>
            {rt?.installed ? (
              <div className="k-hero-once">
                <span className="k-sc k-muted" style={{ fontSize: 17 }}>Just once:</span>
                <button type="button" className="k-link" disabled={!ctl.canPlay} onClick={() => setVanillaOpen(true)}>Vanilla</button>
                <button type="button" className="k-link" disabled={!ctl.canPlay} onClick={() => ctl.play("no-mods")}>Without mods</button>
                <button type="button" className="k-link" disabled={!ctl.canPlay || !list.length} onClick={() => setOnceOpen(true)}>Another profile</button>
              </div>
            ) : s && (
              <div className="k-hero-once">
                <button type="button" className="k-link" onClick={() => nav("/runtime?install=1")}>Install Kiln</button>
                <button type="button" className="k-link" disabled={!ctl.canPlay} onClick={() => setVanillaOpen(true)}>Play vanilla</button>
              </div>
            )}
          </div>
        )}
        <dl className="k-hero-facts">
          <div><dt>Kiln</dt><dd title={kiln ?? undefined}>{kiln ? kiln.split("+")[0] : "Not installed"}</dd></div>
          <div><dt>Proxy</dt><dd>{rt?.proxy ? PROXY[rt.proxy.kind] ?? rt.proxy.kind : "-"}</dd></div>
          <div><dt>Runs on</dt><dd>{platform(game)}</dd></div>
          <div><dt>Last boot</dt><dd style={errors ? { color: "var(--k-err)" } : undefined}>{lastBoot}</dd></div>
        </dl>
      </section>

      <div className="k-foot">
        <span className="k-mono" title={game.path}>{shortPath(game.path)}</span>
        <button type="button" className="k-link" onClick={() => api.appOpenPath(game.path).catch((e) => toast.error(e))}>Open</button>
        <button type="button" className="k-link" disabled={detect.isPending} onClick={() => detect.mutate()}>Detect</button>
        <button type="button" className="k-link" onClick={() => setAdding(true)}>Add</button>
      </div>

      <PlayOnceModal open={onceOpen} onClose={() => setOnceOpen(false)} onPlay={(pid) => { setOnceOpen(false); ctl.play("once", pid); }} />
      <VanillaOnceModal open={vanillaOpen} onClose={() => setVanillaOpen(false)} onPlay={() => { setVanillaOpen(false); ctl.play("vanilla"); }} />
      <AddGameModal open={adding} onClose={() => setAdding(false)} />
      {pending.data && offerOpen && (ModpackOffer ? (
        <Suspense fallback={null}><ModpackOffer gameId={game.id} lastServer={pending.data} open={offerOpen} onClose={() => setOfferOpen(false)} /></Suspense>
      ) : (
        <Modal open onClose={() => setOfferOpen(false)} title="Modpack" footer={<Button onClick={() => setOfferOpen(false)}>Close</Button>}>
          <KeyValue rows={[["Server", pending.data.server.name ?? pending.data.server.id], ["Missing", pending.data.missing.join(", ") || "-"], ["URL", <span className="k-mono">{pending.data.modpack?.url}</span>]]} />
          <span className="k-hint">Modpack install is not available in this build.</span>
        </Modal>
      ))}
    </>
  );
}

/** Refusal, Kiln did not start, the Steam launch option, Steam updating, safe idle: flat strips above the name. */
function Banners({ game, ctl, pending, onOffer, boot }: { game: GameInstall; ctl: PlayControl; pending: LastServer | null; onOffer: () => void; boot: api.BootJson | null }) {
  const qc = useQueryClient();
  const status = useGameStatus(game.id);
  const s = status.data;
  const out = [];
  if (pending?.outcome === "rejected") {
    out.push(
      <Banner key="refused" tone="err" title="Refused" actions={<>
        {pending.modpack && <Button size="s" onClick={onOffer}>Get modpack</Button>}
        {pending.server.id && <button type="button" className="k-link" onClick={() => api.modpackDismiss(game.id, pending.server.id!).then(() => qc.invalidateQueries({ queryKey: qk.pending(game.id) })).catch((e) => toast.error(e))}>Dismiss</button>}
      </>}>
        {pending.server.name ?? "A server"}: {pending.reason ?? (pending.missing.length ? <>missing <span className="k-mono">{pending.missing.join(", ")}</span>.</> : "it needs mods you do not have.")}
      </Banner>,
    );
  }
  if (s?.steamUpdating) out.push(<Banner key="upd" tone="warn" title="Updating">Steam is updating Enshrouded. Play waits until it is done.</Banner>);
  const advice = s?.runtime.installed ? s.launchOptions?.advice : null;
  if (advice && (ctl.stuck || !s?.launchOptions?.hasDllOverride)) {
    out.push(
      <Banner key="adv" tone="warn" title={ctl.stuck ? "Kiln did not start" : "One Steam setting"}
        actions={<Button size="s" onClick={() => navigator.clipboard?.writeText(advice).then(() => toast.ok("Copied", "Paste it into Steam's launch options."))}>Copy</Button>}>
        Steam, Enshrouded, Properties, Launch options: <span className="k-mono">{advice}</span>
      </Banner>,
    );
  }
  if (boot?.safeIdle) out.push(<Banner key="safe" tone="warn" title="Safe idle">{boot.safeIdle}</Banner>);
  else if (boot?.profile.mode === "invalid") out.push(<Banner key="inv" tone="err" title="Profile">{boot.profile.error ?? "profile.json is invalid: no mods loaded."}</Banner>);
  if (!out.length) return null;
  return <div className="k-stack k-hero-banner" style={{ gap: 6 }}>{out}</div>;
}

/** The active profile's name: a button that opens the profile list. */
function ProfileName({ gameId, name, running }: { gameId: string; name: string; running: boolean }) {
  const qc = useQueryClient();
  const nav = useNavigate();
  const profiles = useProfiles();
  const active = useActiveProfile(gameId);
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const list = profiles.data ?? [];
  const activate = useMutation({
    mutationFn: (pid: string) => (pid ? api.profileActivate(pid, gameId) : api.profileDeactivate(gameId).then(() => null)),
    onSuccess: (_r, pid) => { invalidateGame(qc, gameId); toast.ok(pid ? `Active profile: ${list.find((p) => p.id === pid)?.name ?? pid}` : "No profile", "Steam's Play button uses it too."); },
    onError: (e) => toast.error(e),
  });
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", close);
    document.addEventListener("keydown", esc);
    ref.current?.querySelector<HTMLElement>('[role="option"][aria-selected="true"], [role="option"]')?.focus();
    return () => { document.removeEventListener("mousedown", close); document.removeEventListener("keydown", esc); };
  }, [open]);
  const pick = (pid: string) => { setOpen(false); if (pid !== (active.data?.profileId ?? "")) activate.mutate(pid); };
  const cur = active.data?.profileId ?? "";
  return (
    <div className="k-hero-name" ref={ref}>
      <h1 style={{ margin: 0, fontWeight: "inherit" }}>
        <button type="button" className="k-title" aria-haspopup="listbox" aria-expanded={open} aria-label={`Active profile: ${name}. Change profile.`} onClick={() => setOpen((o) => !o)}>
          {name}<svg viewBox="0 0 24 24" aria-hidden><path d="M5 9l7 7 7-7" /></svg>
        </button>
      </h1>
      {open && (
        <div className="k-popover k-hero-pop" role="listbox" aria-label="Profiles"
          onKeyDown={(e) => {
            if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
            e.preventDefault();
            const items = [...(ref.current?.querySelectorAll<HTMLElement>(".k-hero-pop .k-menu-item") ?? [])];
            const at = items.indexOf(document.activeElement as HTMLElement);
            items[(at + (e.key === "ArrowDown" ? 1 : -1) + items.length) % items.length]?.focus();
          }}>
          {list.map((p) => (
            <button key={p.id} type="button" role="option" aria-selected={p.id === cur} className="k-menu-item" disabled={running} onClick={() => pick(p.id)}>
              <span style={{ fontWeight: p.id === cur ? 700 : 400 }}>{p.name}</span>
              <span className="k-mono">{p.counts.kiln + p.counts.native + p.counts.emf + p.counts.plugin} mods</span>
            </button>
          ))}
          <button type="button" role="option" aria-selected={!cur} className="k-menu-item" disabled={running} onClick={() => pick("")}>
            <span style={{ fontWeight: !cur ? 700 : 400 }}>No profile</span><span className="k-mono">drop-ins</span>
          </button>
          <div className="k-menu-sep" />
          {cur && <button type="button" className="k-menu-item" style={{ color: "var(--k-ember-300)" }} onClick={() => nav(`/profiles/${cur}/mods`)}>Edit this profile</button>}
          <button type="button" className="k-menu-item" style={{ color: "var(--k-ember-300)" }} onClick={() => nav("/profiles")}>All profiles</button>
          {running && <span className="k-hint" style={{ padding: "4px 10px" }}>Close the game to switch.</span>}
        </div>
      )}
    </div>
  );
}

function VanillaOnceModal({ open, onClose, onPlay }: { open: boolean; onClose: () => void; onPlay: () => void }) {
  return (
    <Modal arch open={open} onClose={onClose} title="Play vanilla once"
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" onClick={onPlay}>Play vanilla</Button></>}>
      <ArchText>A character carrying items from a Kiln mod can crash on a server that lets vanilla clients in, and may lose those items on other servers. Use another character.</ArchText>
    </Modal>
  );
}

function PlayOnceModal({ open, onClose, onPlay }: { open: boolean; onClose: () => void; onPlay: (pid: string) => void }) {
  const profiles = useProfiles();
  const [pid, setPid] = useState("");
  const list = profiles.data ?? [];
  const chosen = pid || list[0]?.id || "";
  return (
    <Modal open={open} onClose={onClose} title="Play a profile once" width={480}
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant="primary" disabled={!chosen} onClick={() => onPlay(chosen)}>Play</Button></>}>
      <Field label="Profile" hint="The next start after this one is back to the active profile." htmlFor="once-profile">
        <Select id="once-profile" value={chosen} onChange={setPid} options={list.map((p) => ({ value: p.id, label: p.name }))} />
      </Field>
    </Modal>
  );
}

function platform(g: GameInstall): string {
  const p = g.platform === "proton" ? "Proton" : g.platform === "wine" ? "Wine" : g.platform === "windows" ? "Windows" : String(g.platform);
  return g.pathMode === "copy" ? `${p}, copy mode` : p;
}
function shortPath(p: string): string {
  const parts = p.split(/[\\/]/).filter(Boolean);
  return parts.length > 3 ? `…/${parts.slice(-3).join("/")}` : p;
}
function names(ids: string[]): string {
  return ids.length <= 2 ? ids.join(" and ") : `${ids.slice(0, 2).join(", ")} and ${ids.length - 2} more`;
}
