// / Play: in front of the stage. The game's name, one live sentence (how many mods are on, over every kind, and only the
// exceptions a player must know), "Just once" (vanilla, without mods), the list of every mod with a switch
// (pages/mods/MainList.tsx), the boot stages while the game starts, four facts, and the game folder. The mouth
// (shell/AppShell.tsx) is the Play button. Banners (refusal, launch option, Steam update) sit above the name.
// A first start: the launcher looks for the game by itself (shell/firstRun.tsx), then Install Kiln runs right here (the
// mouth and the link open its confirm; the task shows in place of the sentence's links).
// Profiles are a background feature (Settings, Profiles): one is always loaded, "My mods" made by itself when there is
// none; only another one than that is named here, in one small line that leads to Settings.
// The modpack offer is a working dialog (src/pages/servers/ModpackOffer.tsx, loaded when it exists).
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { lazy, Suspense, useState, type ComponentType } from "react";
import { Link, useNavigate } from "react-router-dom";
import * as api from "../api/commands";
import type { BootState, GameInstall, LastServer, ProxyKind } from "../api/commands";
import { OTHER_FOLDER_PROFILE, otherFolderTitle } from "../shell/dataFolderText";
import { qk, useGame, useGameSel, useGameStatus, useLaunchState, usePoll, usePreStart, useProfiles, useRuntimeChanges } from "../api/hooks";
import { detectGames, useDetect, useEnsureProfile, useInstallPrompt, useKilnInstall } from "../shell/firstRun";
import { clock, usePlayControl, type PlayControl } from "../shell/play";
import { isDefaultProfile, useMods } from "../shell/useMods";
import { MainList } from "./mods/MainList";
import { StartProgress } from "../shell/StartPanel";
import { ArchText, Banner, Button, Cones, Field, KeyValue, Modal, PathPicker, TaskProgress, errorText, toast } from "../ui";

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
  const profiles = useProfiles();
  const mods = useMods(game);
  useEnsureProfile(game);
  const pending = useQuery({ queryKey: qk.pending(game.id), queryFn: () => api.modpackPending(game.id), refetchInterval: usePoll(15000) });
  const [vanillaOpen, setVanillaOpen] = useState(false);
  const [offerOpen, setOfferOpen] = useState(false);
  const [adding, setAdding] = useState(false);
  const s = status.data;
  const v = ctl.view;
  const boot = state.data?.boot ?? null;
  const fresh = !!boot && !state.data?.stale;
  // what kiln-eml does before the game's window exists (fingerprinting the game's data files): said instead of "Waiting"
  const prep = usePreStart(game.id, (ctl.starting || ctl.running) && !fresh).data ?? null;
  const list = profiles.data ?? [];
  const cur = mods.summary;
  const rt = s?.runtime;
  const detect = useMutation({ mutationFn: api.gameDetect, onSuccess: (g) => { qc.setQueryData(qk.games, g); toast.ok(`${g.filter((x) => x.kind === "client").length} found`); }, onError: (e) => toast.error(e) });
  const { games } = useGame();
  const setGameId = useGameSel((x) => x.setGameId);
  const clients = games.filter((g) => g.kind === "client");
  const kilnInstall = useKilnInstall(game);
  const installTask = kilnInstall.installing ? kilnInstall.change?.taskId ?? null : null;
  const askInstall = () => (kilnInstall.shroudforge ? nav("/runtime") : useInstallPrompt.getState().show());

  // the live sentence: every kind counts
  let say: string;
  if (!s) say = status.error ? errorText(status.error) : "Checking the game.";
  else if (!rt?.installed) say = kilnInstall.installing ? "Installing Kiln." : rt?.proxy?.kind === "shroudforge" ? "ShroudForge is in this game. Switch on Runtime to let Kiln take over its dinput8.dll." : "Kiln is not in this game yet.";
  else if (!mods.profileId && !list.length) say = profiles.isLoading ? "Checking the game." : "Getting your mod list ready.";
  else if (!mods.profileId) say = "No profile is loaded: the mods in the game folder load as they are.";
  else if (mods.error) say = `Your mods are not readable: ${errorText(mods.error)}`;
  else if (mods.loading) say = "Reading your mods.";
  else if (!mods.rows.length) say = "No mods yet.";
  else say = mods.count.text;
  const kiln = rt?.receipt?.runtime.version ?? boot?.kiln ?? null;
  const on = mods.count.on;
  // a start the runtime ended in safe idle or could not go on with says so, boot.json or not (progress.json's `why`)
  const runLine = v.label === "No word from Kiln" ? v.detail ?? "" : prep && !fresh ? prep.message
    : v.label === "Kiln stopped" ? `Kiln could not go on: ${ctl.lastTimeline?.why ?? "see kiln.log on the Logs page"}.`
    : v.label === "Safe idle" && !fresh ? `Kiln is in safe idle: ${ctl.lastTimeline?.why ?? "mods stay off"}.` : v.phase === "vanilla" ? "Started without Kiln." : ctl.kilnMissing ? "Running without Kiln: Steam did not load it this start." : !fresh ? "The game is running. No Kiln boot record for this start." : boot?.profile?.mode === "no-mods" ? `Running with Kiln ${boot?.kiln} and no mods.`
    : v.label === "Safe idle" ? `Kiln is in safe idle: ${boot?.safeIdle ?? ctl.lastTimeline?.why ?? "mods stay off"}.` : `Running with Kiln ${kiln ?? ""}${mods.profile ? ` and ${on} mod${on === 1 ? "" : "s"}` : ""}.`;
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
  // only a profile other than the default is named on the main screen
  const other = rt?.installed && cur && !isDefaultProfile(cur) ? cur : null;
  const showList = !!rt?.installed && (!!mods.profileId || list.length > 0);

  return (
    <>
      <section className={"k-hero" + (showList ? " with-mods" : "")} aria-label="Play">
        <div className="k-hero-kicker">
          <span>Enshrouded · {build}</span>
          {clients.length > 1 && (
            <select className="k-kicker-select" aria-label="Game" value={game.id} onChange={(e) => setGameId(e.target.value)}>
              {clients.map((g) => <option key={g.id} value={g.id}>{g.path.split(/[\\/]/).filter(Boolean).slice(-3).join("/")} · {g.source}</option>)}
            </select>
          )}
          {s?.otherFolderProfile ? (
            <Link className="k-link k-hero-profile k-tone-warn" to="/settings#profiles" title={otherFolderTitle(s.otherFolderProfile)}>{OTHER_FOLDER_PROFILE}</Link>
          ) : other ? (
            <Link className="k-link k-hero-profile" to="/settings#profiles" title="The loaded profile: Settings, Profiles">Profile: {other.name}</Link>
          ) : rt?.installed && !mods.profileId && list.length > 0 && !profiles.isLoading && (
            <Link className="k-link k-hero-profile" to="/settings#profiles">No profile loaded</Link>
          )}
        </div>
        <Banners game={game} ctl={ctl} pending={pending.data ?? null} onOffer={() => setOfferOpen(true)} boot={fresh ? boot : null} />
        <h1 className="k-hero-name"><span className="k-title static">Enshrouded</span></h1>
        {ctl.timeline ? (
          <div className="k-hero-state k-hero-start" key="progress">
            <StartProgress t={ctl.timeline} prep={prep} />
          </div>
        ) : starting ? (
          <div className="k-hero-state" key="start">
            <p className="k-hero-say" role="status">{prep ? prep.message : "Waiting for Kiln to boot."}</p>
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
            {ctl.elapsed !== null && <span className="k-muted">Started {clock(ctl.elapsed)} ago{v.detail && !v.detail.startsWith("Kiln") ? ` · ${v.detail}` : ""}. Switches apply at the next start.</span>}
          </div>
        ) : (
          <div className="k-hero-state" key="stop">
            <p className="k-hero-say">{say}</p>
            {rt?.installed ? (
              <div className="k-hero-once">
                <span className="k-sc k-muted" style={{ fontSize: 17 }}>Just once:</span>
                <button type="button" className="k-link" disabled={!ctl.canPlay} onClick={() => setVanillaOpen(true)}>Vanilla</button>
                <button type="button" className="k-link" disabled={!ctl.canPlay} onClick={() => ctl.play("no-mods")}>Without mods</button>
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
        {showList && !ctl.timeline && <MainList game={game} mods={mods} running={ctl.running} />}
        <dl className="k-hero-facts">
          <div>
            <dt>Kiln{kilnNote && <> · <button type="button" className={`k-link k-tone-${kilnNote.tone}`} title={kilnNote.title} onClick={() => nav("/runtime")}>{kilnNote.text}</button></>}</dt>
            <dd title={kiln ?? undefined}>{kiln ? kilnLabel(kiln) : "Not installed"}</dd>
          </div>
          <div><dt>Proxy</dt><dd>{rt?.proxy ? PROXY[rt.proxy.kind] ?? rt.proxy.kind : "-"}</dd></div>
          <div><dt>Runs on</dt><dd>{platform(game)}</dd></div>
          <div><dt>Last boot</dt><dd>{boot && problems.count ? (
            <button type="button" className="k-link" style={{ color: "var(--k-err)" }} title={problems.detail} aria-label={`Last boot: ${lastBoot}. Open the logs.`} onClick={() => nav("/logs")}>{lastBoot}</button>
          ) : <span title={lastBoot}>{lastBoot}</span>}</dd></div>
        </dl>
      </section>

      <div className="k-foot">
        <span className="k-mono" title={game.path}>{shortPath(game.path)}</span>
        <button type="button" className="k-link" onClick={() => api.appOpenPath(game.path).catch((e) => toast.error(e))}>Open</button>
        <button type="button" className="k-link" disabled={detect.isPending} onClick={() => detect.mutate()}>Detect</button>
        <button type="button" className="k-link" onClick={() => setAdding(true)} title="Add a game folder">Add</button>
      </div>

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
      <Banner key="nokiln" tone="warn" title={ctl.kilnMissing ? "Kiln is not loading" : "Kiln did not start"} actions={<Button size="s" onClick={() => nav("/logs")}>Support report</Button>}>
        The game started without Kiln. Close it, then save a support report on the Logs page and send us that zip: it explains most causes itself.
      </Banner>,
    );
  }
  if (boot?.safeIdle) out.push(<Banner key="safe" tone="warn" title="Safe idle">{boot.safeIdle}</Banner>);
  else if (boot?.profile.mode === "invalid") out.push(<Banner key="inv" tone="err" title="No mods loaded">{boot.profile.error ?? "profile.json is invalid: no mods loaded."}</Banner>);
  if (!out.length) return null;
  return <div className="k-stack k-hero-banner" style={{ gap: 6 }}>{out}</div>;
}

function VanillaOnceModal({ open, onClose, onPlay }: { open: boolean; onClose: () => void; onPlay: () => void }) {
  return (
    <Modal arch open={open} onClose={onClose} title="Play vanilla once"
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" onClick={onPlay}>Play vanilla</Button></>}>
      <ArchText>A character carrying items from a Kiln mod can crash on a server that lets vanilla clients in, and may lose those items on other servers. Use another character.</ArchText>
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
