// / Play: in front of the stage. The active profile's name (a button: change profile), one live sentence, "Just once"
// (vanilla, without mods, another profile), the boot stages while the game starts, four facts, and the game folder.
// The mouth (shell/AppShell.tsx) is the Play button. Banners (refusal, launch option, Steam update) sit above the name.
// A first start: the launcher looks for the game by itself (shell/firstRun.tsx), then Install Kiln runs right here (the
// mouth and the link open its confirm; the task shows in place of the sentence's links).
// The modpack offer is a working dialog (src/pages/servers/ModpackOffer.tsx, loaded when it exists).
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { lazy, Suspense, useEffect, useRef, useState, type ComponentType } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "../api/commands";
import type { BootState, GameInstall, LastServer, ProxyKind } from "../api/commands";
import {
  invalidateGame, qk, showActive, useActiveProfile, useGame, useGameSel, useGameStatus, useLaunchState, useLibrary, useProfiles, useRuntimeChanges,
} from "../api/hooks";
import { detectGames, useDetect, useInstallPrompt, useKilnInstall } from "../shell/firstRun";
import { clock, usePlayControl, type PlayControl } from "../shell/play";
import {
  ArchText, Banner, Button, Cones, Field, KeyValue, Modal, PathPicker, Select, TaskProgress, errorText, toast,
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

/** No client known: the launcher is looking by itself (once per start), or found nothing: Add a folder, Look again. */
function NoGame({ error, hasOther }: { error: unknown; hasOther: boolean }) {
  const qc = useQueryClient();
  const [adding, setAdding] = useState(false);
  const { busy, tried } = useDetect();
  const looking = busy || (!tried && !error);
  return (
    <>
      <section className="k-hero" aria-label="Play">
        <div className="k-hero-kicker">{looking ? "First start" : hasOther ? "No client found" : "Not found"}</div>
        <h1 className="k-hero-name"><span className="k-title static">Enshrouded</span></h1>
        <div className="k-hero-state">
          <p className="k-hero-say" role="status">{error ? errorText(error) : looking ? "Looking for Enshrouded in your Steam libraries." : "Kiln did not find Enshrouded in your Steam libraries."}</p>
          {!looking && (
            <div className="k-hero-once">
              <button type="button" className="k-link" onClick={() => setAdding(true)}>Add a folder</button>
              <button type="button" className="k-link" onClick={() => void detectGames(qc)}>Look again</button>
            </div>
          )}
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

const PROXY: Record<ProxyKind, string> = {
  kiln: "In place", none: "None", shroudforge: "ShroudForge", emf: "EMF's own", unknown: "Another DLL mod", notdll: "Not a DLL",
  eml: "EML", flame: "Flame (Creative Mode)", shroudlink: "shroudlink", modloaderproxy: "ModLoader Proxy",
  ultimateasiloader: "Ultimate ASI Loader", aldrich: "Aldrich Mod Menu", chainloader: "A chain loader",
  set: "Shrouded Engine Tweaks", enshwick: "Enshwick", greeneye: "Greeneye Loader", shroudtopia: "Shroudtopia",
  ember: "Ember", livemap: "Live Map", sharedxp: "Shared XP", reshade: "ReShade",
};

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
  const kilnInstall = useKilnInstall(game);
  const installTask = kilnInstall.installing ? kilnInstall.change?.taskId ?? null : null;
  const askInstall = () => (kilnInstall.shroudforge ? nav("/runtime") : useInstallPrompt.getState().show());

  // the live sentence
  let say: string;
  const modCount = cur ? cur.counts.kiln + cur.counts.native + cur.counts.emf + cur.counts.plugin : 0;
  if (!s) say = status.error ? errorText(status.error) : "Checking the game.";
  else if (!rt?.installed) say = kilnInstall.installing ? "Installing Kiln." : rt?.proxy?.kind === "shroudforge" ? "ShroudForge is in this game. Switch on Runtime to let Kiln take over its dinput8.dll." : "Kiln is not in this game yet.";
  else if (!cur && !list.length) say = "Drop a mod anywhere on this window to add it.";
  else if (!cur) say = "No profile is active: Kiln loads the mods in the game folder.";
  else if (!modCount) say = "No mods yet. Drop one anywhere on this window.";
  else if (preview.data) {
    const order = preview.data.kiln.order;
    const total = order.length + preview.data.kiln.rejected.filter((r) => r.state !== "disabled").length;
    const away = preview.data.kiln.rejected.filter((r) => r.state === "skipped").map((r) => lib.data?.find((l) => l.id === r.id)?.name ?? r.id);
    say = `${order.length} of ${total} mods load on this client.` + (away.length ? ` ${names(away)} ${away.length === 1 ? "stays" : "stay"} on the server.` : "");
  } else say = `${cur.counts.kiln} Kiln mods in this profile.`;
  const kiln = rt?.receipt?.runtime.version ?? boot?.kiln ?? null;
  const runLine = v.phase === "vanilla" ? "Started without Kiln." : ctl.kilnMissing ? "Running without Kiln: Steam did not load it this start." : !fresh ? "The game is running. No Kiln boot record for this start." : boot?.profile?.mode === "no-mods" ? `Running with Kiln ${boot?.kiln} and no mods.`
    : v.label === "Safe idle" ? `Kiln is in safe idle: ${boot?.safeIdle ?? "mods stay off"}.` : `Running with Kiln ${kiln ?? ""}${preview.data ? ` and ${preview.data.kiln.order.length} mods` : ""}.`;
  const stages = ["Steam", "Game", "Kiln", "Mods"];
  const starting = ctl.starting;
  const stageAt = !ctl.running ? 0 : !fresh ? 1 : 3;
  // the last boot, running or not: boot.json plus what the same run wrote after it (mods.json, native.json)
  const problems = bootProblems(state.data);
  const lastBoot = boot ? `${boot.bootMs ? `${(boot.bootMs / 1000).toFixed(1)} s` : "Done"}, ${problems.text}` : "Not yet";
  const change = useRuntimeChanges((x) => x.last[game.id]);
  // the build date tells two dev builds apart; an update that is ready or failed is said next to it
  const kilnNote = change?.state === "failed" ? { text: "update failed", tone: "err", title: `${change.what} failed: ${change.error?.message ?? ""}` }
    : rt?.upgradeAvailable ? { text: "update ready", tone: "warn", title: `${rt.upgradeAvailable} is ready to install` } : null;
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
        {!rt?.installed && !list.length ? (
          <h1 className="k-hero-name"><span className="k-title static">Enshrouded</span></h1>
        ) : (
          <ProfileName gameId={game.id} name={cur?.name ?? (rt?.installed ? "Drop-in mods" : "Vanilla")} running={ctl.running} />
        )}
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
            ) : installTask ? (
              <div className="k-hero-task"><TaskProgress taskId={installTask} cancel={false} /></div>
            ) : s && (
              <div className="k-hero-once">
                <button type="button" className="k-link" disabled={!ctl.canPlay || kilnInstall.installing} onClick={askInstall}>Install Kiln</button>
                <button type="button" className="k-link" disabled={!ctl.canPlay} onClick={() => setVanillaOpen(true)}>Play vanilla</button>
              </div>
            )}
          </div>
        )}
        <dl className="k-hero-facts">
          <div>
            <dt>Kiln{kilnNote && <> · <button type="button" className={`k-link k-tone-${kilnNote.tone}`} title={kilnNote.title} onClick={() => nav("/runtime")}>{kilnNote.text}</button></>}</dt>
            <dd title={kiln ?? undefined}>{kiln ? kilnLabel(kiln) : "Not installed"}</dd>
          </div>
          <div><dt>Proxy</dt><dd>{rt?.proxy ? PROXY[rt.proxy.kind] ?? rt.proxy.kind : "-"}</dd></div>
          <div><dt>Runs on</dt><dd>{platform(game)}</dd></div>
          <div><dt>Last boot</dt><dd>{boot && problems.count ? (
            <button type="button" className="k-link" style={{ color: "var(--k-err)" }} title={problems.detail} aria-label={`Last boot: ${lastBoot}. Open the logs.`} onClick={() => nav("/logs")}>{lastBoot}</button>
          ) : lastBoot}</dd></div>
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
  const nav = useNavigate();
  const status = useGameStatus(game.id);
  const change = useRuntimeChanges((x) => x.last[game.id]);
  const s = status.data;
  const out = [];
  if (pending?.outcome === "rejected") {
    out.push(
      <Banner key="refused" tone="err" title="Refused" actions={<>
        {pending.modpack && <Button size="s" onClick={onOffer}>Get modpack</Button>}
        <button type="button" className="k-link" onClick={() => api.modpackDismiss(game.id, pending.server.id ?? "").then(() => qc.invalidateQueries({ queryKey: qk.pending(game.id) })).catch((e) => toast.error(e))}>Dismiss</button>
      </>}>
        {pending.server.name ?? "A server"}: {pending.reason ?? (pending.missing.length ? <>missing <span className="k-mono">{pending.missing.join(", ")}</span>.</> : "it needs mods you do not have.")}
      </Banner>,
    );
  }
  if (change?.state === "failed") {
    const has = s?.runtime.receipt?.runtime.version;
    out.push(
      <Banner key="rt" tone="err" title={`${change.what} failed`} actions={<>
        <Button size="s" onClick={() => nav("/runtime")}>Runtime</Button>
        <button type="button" className="k-link" onClick={() => useRuntimeChanges.getState().clear(game.id)}>Dismiss</button>
      </>}>
        <span style={{ overflowWrap: "anywhere" }}>{change.error?.message ?? "It stopped without a reason."}</span>{" "}
        {has ? <>The game still has Kiln <span className="k-mono">{has}</span>.</> : "Kiln is not in the game."}
      </Banner>,
    );
  }
  if (s?.steamUpdating) out.push(<Banner key="upd" tone="warn" title="Updating">Steam is updating Enshrouded. Play waits until it is done.</Banner>);
  // a mod built against a newer Kiln may use something this game's Kiln doesn't have yet: it fails to load until Kiln is updated
  const newer = s?.runtime.installed ? s.newerMods ?? [] : [];
  if (newer.length) {
    out.push(
      <Banner key="newer" tone="warn" title="Update Kiln" actions={<Button size="s" onClick={() => nav("/runtime")}>Update</Button>}>
        {names(newer.map((m) => m.id))} {newer.length === 1 ? "needs" : "need"} a newer Kiln.
      </Banner>,
    );
  }
  const advice = s?.runtime.installed && ctl.view.phase !== "vanilla" ? s.launchOptions?.advice : null;
  if (advice && !s?.launchOptions?.hasDllOverride) {
    out.push(
      <Banner key="adv" tone="warn" title={ctl.kilnMissing ? "Kiln is not loading" : ctl.stuck ? "Kiln did not start" : "One Steam setting"}
        actions={<Button size="s" onClick={async () => {
          try {
            if (!navigator.clipboard) throw new Error("Clipboard unavailable");
            await navigator.clipboard.writeText(advice);
            toast.ok("Copied", "Paste it into Steam's launch options.");
          } catch (e) { toast.error(e, "Could not copy the launch option"); }
        }}>Copy</Button>}>
        {ctl.stuck && "The game started without Kiln, so mods and \"Without mods\" can't apply. Close it, then set "}
        Steam, Enshrouded, Properties, Launch options: <span className="k-mono">{advice}</span>
      </Banner>,
    );
  } else if (ctl.stuck && s?.runtime.installed) {
    // the launch option is set (or not needed here) and Kiln still did not load: say so, never "Starting" forever
    out.push(
      <Banner key="nokiln" tone="warn" title={ctl.kilnMissing ? "Kiln is not loading" : "Kiln did not start"} actions={<Button size="s" onClick={() => nav("/logs")}>Logs</Button>}>
        The game started without Kiln. Close it and send us <span className="k-mono">kiln/logs/proxy.log</span> from the game folder (no such file: Kiln's dinput8.dll never loaded), your Proton version and Steam's launch options for Enshrouded.
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
  const [room, setRoom] = useState(0);
  const ref = useRef<HTMLDivElement>(null);
  const list = profiles.data ?? [];
  const activate = useMutation({
    mutationFn: (pid: string) => (pid ? api.profileActivate(pid, gameId) : api.profileDeactivate(gameId).then(() => null)),
    // the name changes at once (the activation answers after the backend has written it, which takes a moment);
    // a failed activation puts the old one back
    onMutate: (pid) => showActive(qc, gameId, pid || null),
    onSuccess: (r, pid) => {
      qc.setQueryData(qk.active(gameId), r);
      invalidateGame(qc, gameId);
      toast.ok(pid ? `Active profile: ${list.find((p) => p.id === pid)?.name ?? pid}` : "No profile", "Steam's Play button uses it too.");
    },
    onError: (e, _pid, undo) => { undo?.(); toast.error(e); },
  });
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    // the list fits between the title and the window's bottom edge; the profiles scroll inside it
    const fit = () => { const b = ref.current?.getBoundingClientRect(); setRoom(b ? Math.max(180, window.innerHeight - b.bottom - 16) : 0); };
    fit();
    document.addEventListener("mousedown", close);
    document.addEventListener("keydown", esc);
    window.addEventListener("resize", fit);
    ref.current?.querySelector<HTMLElement>('[role="option"][aria-selected="true"], [role="option"]')?.focus();
    return () => { document.removeEventListener("mousedown", close); document.removeEventListener("keydown", esc); window.removeEventListener("resize", fit); };
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
        <div className="k-popover k-hero-pop" role="listbox" aria-label="Profiles" style={room ? { maxHeight: room } : undefined}
          onKeyDown={(e) => {
            if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
            e.preventDefault();
            const items = [...(ref.current?.querySelectorAll<HTMLElement>(".k-hero-pop .k-menu-item") ?? [])];
            const at = items.indexOf(document.activeElement as HTMLElement);
            items[(at + (e.key === "ArrowDown" ? 1 : -1) + items.length) % items.length]?.focus();
          }}>
          <div className="k-pop-scroll">
            {list.map((p) => (
              <button key={p.id} type="button" role="option" aria-selected={p.id === cur} className="k-menu-item" disabled={running} onClick={() => pick(p.id)}>
                <span style={{ fontWeight: p.id === cur ? 700 : 400 }}>{p.name}</span>
                <span className="k-mono">{p.counts.kiln + p.counts.native + p.counts.emf + p.counts.plugin} mods</span>
              </button>
            ))}
            <button type="button" role="option" aria-selected={!cur} className="k-menu-item" disabled={running} onClick={() => pick("")}>
              <span style={{ fontWeight: !cur ? 700 : 400 }}>No profile</span><span className="k-mono">drop-ins</span>
            </button>
          </div>
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

/** "0.2.0-dev (20261104)": the version and the build date of a `<semver>+<yyyymmdd>.<git>` runtime. */
function kilnLabel(v: string): string {
  const [base, build] = v.split("+");
  const date = build?.split(".")[0];
  return date ? `${base} (${date})` : base;
}

/** What went wrong in the last boot. boot.json is written when the boot ends, before Kiln.Host loads the mods, so
 *  its `errors` never hold mod failures: those are in mods.json (and native.json for native mods), counted when the
 *  same run wrote them, i.e. the same pid and written after boot.json (Wine reuses pids from run to run). */
function bootProblems(st: BootState | undefined): { count: number; text: string; detail: string } {
  const boot = st?.boot;
  if (!boot) return { count: 0, text: "no errors", detail: "" };
  const since = Date.parse(boot.written) - 2000;
  const sameRun = (f: { pid: number; written: string } | null | undefined) => !!f && f.pid === boot.pid && Date.parse(f.written) >= since;
  const errors = [
    ...(boot.errors ?? []),
    ...boot.chain.filter((c) => c.status === "failed").map((c) => `${c.path.split(/[\\/]/).pop()} did not load`),
    ...(boot.plugins ?? []).filter((p) => p.status === "failed").map((p) => `plugin ${p.id ?? p.file}: ${p.error ?? "failed"}`),
  ];
  const mods = sameRun(st?.mods) ? st!.mods!.mods.filter((m) => m.state === "failed") : [];
  const natives = sameRun(st?.native) ? st!.native!.mods.filter((m) => m.state === "failed") : [];
  const nativeCount = natives.length || (boot.native?.failed ?? 0);
  // kiln.log's error lines of this run (launch_state checks the run) also count what went wrong after the boot:
  // a service tick that throws at the menu, a mod's later error. Boot errors are logged there too.
  const logged = st?.log ?? null;
  const errorCount = logged ? Math.max(logged.errors, errors.length) : errors.length;
  const parts = [
    mods.length ? `${mods.length} mod${mods.length > 1 ? "s" : ""} failed` : "",
    nativeCount ? `${nativeCount} native mod${nativeCount > 1 ? "s" : ""} failed` : "",
    errorCount ? `${errorCount} error${errorCount > 1 ? "s" : ""}${logged ? " since start" : ""}` : "",
  ].filter(Boolean);
  const detail = [
    ...mods.map((m) => `${m.name ?? m.id}: ${m.reason ?? "failed"}`),
    ...natives.map((m) => `${m.id}: ${m.reason ?? "failed"}`),
    ...(logged?.first.length ? logged.first : errors),
    ...(logged && logged.errors > logged.first.length ? [`… ${logged.errors - logged.first.length} more in kiln.log`] : []),
  ].join("\n");
  return { count: mods.length + nativeCount + errorCount, text: parts.length ? parts.join(", ") : "no errors", detail };
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
