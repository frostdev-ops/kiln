import { StoppedStartNotice } from "../shell/StoppedStartNotice";
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
import { otherFolderProfile, otherFolderTitle } from "../shell/dataFolderText";
import { qk, useGame, useGameSel, useGameStatus, useLaunchState, usePoll, usePreStart, useProfiles, useRuntimeChanges } from "../api/hooks";
import { detectGames, useDetect, useEnsureProfile, useInstallPrompt, useKilnInstall } from "../shell/firstRun";
import { clock, usePlayControl, type PlayControl } from "../shell/play";
import { isDefaultProfile, useMods } from "../shell/useMods";
import { MainList } from "./mods/MainList";
import { StartProgress } from "../shell/StartPanel";
import { channelNotice } from "../api/channel";
import { useUpdate } from "../api/update";
import { rich, t, useT } from "../i18n";
import { ArchText, Banner, Button, Cones, Field, KeyValue, Modal, PathPicker, TaskProgress, errorText, toast } from "../ui";
import { StartModal, UnknownBuild, useStartSetup, useStartWord } from "../components/GameStart";

/** Windows (WebView2): path examples in Windows form. */
const WIN = /Windows/i.test(navigator.userAgent);

type OfferProps = { gameId: string; lastServer: LastServer; open: boolean; onClose: () => void };
const offerSlot = import.meta.glob<{ default: ComponentType<OfferProps> }>("./servers/ModpackOffer.tsx");
const ModpackOffer = offerSlot["./servers/ModpackOffer.tsx"] ? lazy(offerSlot["./servers/ModpackOffer.tsx"]) : null;

export default function PlayPage() {
  const t = useT();
  const ctl = usePlayControl();
  const { games, loading, error } = useGame();
  // a custom folder just added: its start dialog lives here, so it stays when the page turns from "no game" to Play
  const [added, setAdded] = useState<GameInstall | null>(null);
  const dialog = added && <StartModal game={added} added open onClose={() => setAdded(null)} />;
  if (loading) return <section className="k-hero" aria-label={t("play.label")} aria-busy />;
  if (!ctl.game) return <><NoGame error={error} hasOther={games.length > 0} onAdded={setAdded} />{dialog}</>;
  return <><PlayFor game={ctl.game} ctl={ctl} onAdded={setAdded} />{dialog}</>;
}

/** No client known: the launcher is looking by itself (once per start), or found nothing: Add a folder, Look again. */
function NoGame({ error, hasOther, onAdded }: { error: unknown; hasOther: boolean; onAdded: (g: GameInstall) => void }) {
  const t = useT();
  const qc = useQueryClient();
  const [adding, setAdding] = useState(false);
  const { busy, tried } = useDetect();
  const looking = busy || (!tried && !error);
  return (
    <>
      <section className="k-hero" aria-label={t("play.label")}>
        <div className="k-hero-kicker">{looking ? t("play.noGame.firstStart") : hasOther ? t("play.noGame.noClient") : t("play.noGame.notFound")}</div>
        <h1 className="k-hero-name"><span className="k-title static">Enshrouded</span></h1>
        <div className="k-hero-state">
          <p className="k-hero-say" role="status">{error ? errorText(error) : looking ? t("play.noGame.looking") : t("play.noGame.notFoundText")}</p>
          {!looking && (
            <div className="k-hero-once">
              <button type="button" className="k-link" onClick={() => setAdding(true)}>{t("play.noGame.addFolder")}</button>
              <button type="button" className="k-link" onClick={() => void detectGames(qc)}>{t("play.noGame.lookAgain")}</button>
            </div>
          )}
        </div>
      </section>
      <AddGameModal open={adding} onClose={() => setAdding(false)} onAdded={onAdded} />
    </>
  );
}

/** Add a game folder: any folder with enshrouded.exe, in a Steam library or not. A folder no Steam library knows is a
 *  custom install: right after adding it, the start dialog shows (Kiln starts it directly by default). */
function AddGameModal({ open, onClose, onAdded }: { open: boolean; onClose: () => void; onAdded: (g: GameInstall) => void }) {
  const t = useT();
  const qc = useQueryClient();
  const setGameId = useGameSel((s) => s.setGameId);
  const [path, setPath] = useState("");
  const add = useMutation({
    mutationFn: () => api.gameAdd(path.trim()),
    onSuccess: (g) => {
      qc.invalidateQueries({ queryKey: qk.games });
      setGameId(g.id);
      const isCustom = g.kind === "client" && g.source === "manual" && !g.library;
      toast.ok(isCustom ? t("play.addGame.addedCustom") : t("play.addGame.added"));
      setPath("");
      onClose();
      if (isCustom) onAdded(g);
    },
    onError: (e) => toast.error(e),
  });
  return (
    <Modal open={open} onClose={onClose} title={t("play.addGame.title")}
      footer={<><Button variant="ghost" onClick={onClose}>{t("common.cancel")}</Button><Button variant="primary" disabled={!path.trim()} loading={add.isPending} onClick={() => add.mutate()}>{t("common.add")}</Button></>}>
      <Field label={t("play.addGame.folder")} hint={t("play.addGame.folderHint")} htmlFor="add-game">
        <PathPicker id="add-game" value={path} onChange={setPath} directory placeholder={WIN ? "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Enshrouded" : "~/…/steamapps/common/Enshrouded"} />
      </Field>
    </Modal>
  );
}

/** The proxy DLL's owner in words: product names as they are, the launcher's own words translated. */
function proxyName(kind: ProxyKind): string | undefined {
  switch (kind) {
    case "kiln": return t("play.proxy.kiln");
    case "none": return t("play.proxy.none");
    case "emf": return t("play.proxy.emf");
    case "unknown": return t("play.proxy.unknown");
    case "notdll": return t("play.proxy.notdll");
    case "chainloader": return t("play.proxy.chainloader");
    case "nativefolders": return t("play.proxy.nativefolders");
    default: return PROXY_PRODUCT[kind];
  }
}
const PROXY_PRODUCT: Partial<Record<ProxyKind, string>> = {
  shroudforge: "ShroudForge",
  eml: "EML", flame: "Flame (Creative Mode)", shroudlink: "shroudlink", modloaderproxy: "ModLoader Proxy",
  ultimateasiloader: "Ultimate ASI Loader", aldrich: "Aldrich Mod Menu",
  set: "Shrouded Engine Tweaks", enshwick: "Enshwick", greeneye: "Greeneye Loader", shroudtopia: "Shroudtopia",
  ember: "Ember", livemap: "Live Map", sharedxp: "Shared XP", reshade: "ReShade", optiscaler: "OptiScaler",
};

function PlayFor({ game, ctl, onAdded }: { game: GameInstall; ctl: PlayControl; onAdded: (g: GameInstall) => void }) {
  const t = useT();
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
  const [startOpen, setStartOpen] = useState(false);
  const startWord = useStartWord();
  const s = status.data;
  const direct = s?.start === "direct";
  const v = ctl.view;
  const boot = state.data?.boot ?? null;
  const fresh = !!boot && !state.data?.stale;
  // what kiln-eml does before the game's window exists (fingerprinting the game's data files): said instead of "Waiting"
  const prep = usePreStart(game.id, (ctl.starting || ctl.running) && !fresh).data ?? null;
  const list = profiles.data ?? [];
  const cur = mods.summary;
  const rt = s?.runtime;
  const detect = useMutation({ mutationFn: api.gameDetect, onSuccess: (g) => { qc.setQueryData(qk.games, g); toast.ok(t("play.detected", { count: g.filter((x) => x.kind === "client").length })); }, onError: (e) => toast.error(e) });
  const { games } = useGame();
  const setGameId = useGameSel((x) => x.setGameId);
  const clients = games.filter((g) => g.kind === "client");
  const kilnInstall = useKilnInstall(game);
  const installTask = kilnInstall.installing ? kilnInstall.change?.taskId ?? null : null;
  const askInstall = () => (kilnInstall.shroudforge ? nav("/runtime") : useInstallPrompt.getState().show());

  // the live sentence: every kind counts
  let say: string;
  if (!s) say = status.error ? errorText(status.error) : t("play.say.checking");
  else if (!rt?.installed) say = kilnInstall.installing ? t("play.say.installing") : rt?.proxy?.kind === "shroudforge" ? t("play.say.shroudforge") : t("play.say.notInstalled");
  else if (!mods.profileId && !list.length) say = profiles.isLoading ? t("play.say.checking") : t("play.say.gettingReady");
  else if (!mods.profileId) say = t("play.say.noProfile");
  else if (mods.error) say = t("play.say.unreadable", { error: errorText(mods.error) });
  else if (mods.loading) say = t("play.say.reading");
  else if (!mods.rows.length) say = t("play.say.noMods");
  else say = mods.count.text;
  const kiln = rt?.receipt?.runtime.version ?? boot?.kiln ?? null;
  const on = mods.count.on;
  // a start the runtime ended in safe idle or could not go on with says so, boot.json or not (progress.json's `why`)
  const safeIdle = (why: string | null | undefined) => (why != null ? t("play.run.safeIdle", { why }) : t("play.run.safeIdleOff"));
  const runLine = v.word === "noWord" ? v.detail ?? "" : prep && !fresh ? prep.message
    : v.word === "kilnStopped" ? (ctl.lastTimeline?.why != null ? t("play.run.stopped", { why: ctl.lastTimeline.why }) : t("play.run.stoppedSeeLog"))
    : v.word === "safeIdle" && !fresh ? safeIdle(ctl.lastTimeline?.why) : v.phase === "vanilla" ? t("play.run.vanilla") : ctl.kilnMissing ? (direct ? t("play.run.kilnMissingDirect") : t("play.run.kilnMissing")) : !fresh ? t("play.run.noBootRecord") : boot?.profile?.mode === "no-mods" ? t("play.run.noMods", { version: String(boot?.kiln) })
    : v.word === "safeIdle" ? safeIdle(boot?.safeIdle ?? ctl.lastTimeline?.why) : mods.profile ? t("play.run.withMods", { version: kiln ?? "", count: on }) : t("play.run.with", { version: kiln ?? "" });
  const stages = [{ id: "steam", label: direct ? t("play.stage.start") : "Steam" }, { id: "game", label: t("play.stage.game") }, { id: "kiln", label: "Kiln" }, { id: "mods", label: t("play.stage.mods") }];
  const starting = ctl.starting;
  const stageAt = !ctl.running ? 0 : !fresh ? 1 : 3;
  // the last boot, running or not: boot.json plus what the same run wrote after it (mods.json, native.json)
  const problems = bootProblems(state.data);
  const lastBoot = boot ? boot.bootMs ? t("play.lastBoot.time", { seconds: (boot.bootMs / 1000).toFixed(1), problems: problems.text }) : t("play.lastBoot.done", { problems: problems.text }) : t("play.lastBoot.notYet");
  const change = useRuntimeChanges((x) => x.last[game.id]);
  // the build date tells two dev builds apart; an update that is ready or failed is said next to it
  const kilnNote = change?.state === "failed" ? { text: t("play.kilnNote.failed"), tone: "err", title: t("play.kilnNote.failedTip", { what: change.what, error: change.error?.message ?? "" }) }
    : rt?.switchBack ? { text: t("play.kilnNote.beta"), tone: "warn", title: t("play.kilnNote.betaTip", { version: rt.switchBack.to }) }
    : rt?.upgradeAvailable ? { text: t("play.kilnNote.ready"), tone: "warn", title: t("play.kilnNote.readyTip", { version: rt.upgradeAvailable }) } : null;
  const build = game.buildId ? boot?.exe?.verified ? t("play.buildVerified", { build: game.buildId }) : t("play.build", { build: game.buildId }) : t("play.buildUnknown");
  // only a profile other than the default is named on the main screen
  const other = rt?.installed && cur && !isDefaultProfile(cur) ? cur : null;
  const showList = !!rt?.installed && (!!mods.profileId || list.length > 0);

  return (
    <>
      <section className={"k-hero" + (showList ? " with-mods" : "")} aria-label={t("play.label")}>
        <div className="k-hero-kicker">
          <span>Enshrouded · {build}</span>
          {clients.length > 1 && (
            <select className="k-kicker-select" aria-label={t("play.game")} value={game.id} onChange={(e) => setGameId(e.target.value)}>
              {clients.map((g) => <option key={g.id} value={g.id}>{g.path.split(/[\\/]/).filter(Boolean).slice(-3).join("/")} · {sourceWord(g)}</option>)}
            </select>
          )}
          {s?.otherFolderProfile ? (
            <Link className="k-link k-hero-profile k-tone-warn" to="/settings#profiles" title={otherFolderTitle(s.otherFolderProfile)}>{otherFolderProfile()}</Link>
          ) : other ? (
            <Link className="k-link k-hero-profile" to="/settings#profiles" title={t("play.profileTip")}>{t("play.profile", { name: other.name })}</Link>
          ) : rt?.installed && !mods.profileId && list.length > 0 && !profiles.isLoading && (
            <Link className="k-link k-hero-profile" to="/settings#profiles">{t("play.noProfileLoaded")}</Link>
          )}
        </div>
        <StoppedStartNotice ctl={ctl} />
        <Banners game={game} ctl={ctl} pending={pending.data ?? null} onOffer={() => setOfferOpen(true)} boot={fresh ? boot : null} />
        <h1 className="k-hero-name"><span className="k-title static">Enshrouded</span></h1>
        {ctl.timeline ? (
          <div className="k-hero-state k-hero-start" key="progress">
            <StartProgress t={ctl.timeline} prep={prep} />
          </div>
        ) : starting ? (
          <div className="k-hero-state" key="start">
            <p className="k-hero-say" role="status">{prep ? prep.message : t("play.waiting")}</p>
            <ol className="k-boot" aria-label={t("play.bootStages")}>
              {stages.map((stage, i) => (
                <li key={stage.id}>
                  <Cones step={i < stageAt ? 1 : i === stageAt ? 0 : -1} count={1} />
                  <span className="k-sc" style={{ color: i <= stageAt ? "var(--k-text-0)" : "var(--k-text-2)" }}>{stage.label}</span>
                </li>
              ))}
            </ol>
          </div>
        ) : ctl.running ? (
          <div className="k-hero-state" key="run">
            <p className="k-hero-say">{runLine}</p>
            {ctl.elapsed !== null && <span className="k-muted">{v.detail && !(v.detailKiln ?? v.detail.startsWith("Kiln"))
              ? t("play.startedAgoDetail", { time: clock(ctl.elapsed), detail: v.detail })
              : t("play.startedAgo", { time: clock(ctl.elapsed) })}</span>}
          </div>
        ) : (
          <div className="k-hero-state" key="stop">
            <p className="k-hero-say">{say}</p>
            {rt?.installed ? (
              <div className="k-hero-once">
                <span className="k-sc k-muted" style={{ fontSize: 17 }}>{t("play.justOnce")}</span>
                <button type="button" className="k-link" disabled={!ctl.canPlay} onClick={() => setVanillaOpen(true)}>{t("play.vanilla")}</button>
                <button type="button" className="k-link" disabled={!ctl.canPlay} onClick={() => ctl.play("no-mods")}>{t("play.withoutMods")}</button>
              </div>
            ) : installTask ? (
              <div className="k-hero-task"><TaskProgress taskId={installTask} cancel={false} /></div>
            ) : s && (
              <div className="k-hero-once">
                <button type="button" className="k-link" disabled={!ctl.canPlay || kilnInstall.installing} onClick={askInstall}>{t("play.installKiln")}</button>
                <button type="button" className="k-link" disabled={!ctl.canPlay} onClick={() => setVanillaOpen(true)}>{t("play.playVanilla")}</button>
              </div>
            )}
          </div>
        )}
        {showList && !ctl.timeline && <MainList game={game} mods={mods} running={ctl.running} />}
        <dl className="k-hero-facts">
          <div>
            <dt>Kiln{kilnNote && <> · <button type="button" className={`k-link k-tone-${kilnNote.tone}`} title={kilnNote.title} onClick={() => nav("/runtime")}>{kilnNote.text}</button></>}</dt>
            <dd title={kiln ?? undefined}>{kiln ? kilnLabel(kiln) : t("play.notInstalled")}</dd>
          </div>
          <div><dt>{t("play.proxyLabel")}</dt><dd>{rt?.proxy ? proxyName(rt.proxy.kind) ?? rt.proxy.kind : "-"}</dd></div>
          <div><dt>{t("play.runsOn")}</dt><dd>{platform(game)}</dd></div>
          <div><dt>{t("play.lastBootLabel")}</dt><dd>{boot && problems.count ? (
            <button type="button" className="k-link" style={{ color: "var(--k-err)" }} title={problems.detail} aria-label={t("play.lastBootOpen", { text: lastBoot })} onClick={() => nav("/logs")}>{lastBoot}</button>
          ) : <span title={lastBoot}>{lastBoot}</span>}</dd></div>
        </dl>
      </section>

      <div className="k-foot">
        <span className="k-mono" title={game.path}>{shortPath(game.path)}</span>
        <button type="button" className="k-link" onClick={() => api.appOpenPath(game.path).catch((e) => toast.error(e))}>{t("common.open")}</button>
        <button type="button" className="k-link" disabled={detect.isPending} onClick={() => detect.mutate()}>{t("play.detect")}</button>
        <button type="button" className="k-link" onClick={() => setAdding(true)} title={t("play.addGame.title")}>{t("common.add")}</button>
        {s?.start && (
          <button type="button" className="k-link" data-testid="start-mode" onClick={() => setStartOpen(true)}
            title={direct ? t("gameStart.directHint") : t("gameStart.steamHint")}>{t("play.startsVia", { how: startWord(s.start) })}</button>
        )}
      </div>

      <VanillaOnceModal open={vanillaOpen} onClose={() => setVanillaOpen(false)} onPlay={() => { setVanillaOpen(false); ctl.play("vanilla"); }} />
      <AddGameModal open={adding} onClose={() => setAdding(false)} onAdded={onAdded} />
      <StartModal game={game} open={startOpen} onClose={() => setStartOpen(false)} />
      {pending.data && offerOpen && (ModpackOffer ? (
        <Suspense fallback={null}><ModpackOffer gameId={game.id} lastServer={pending.data} open={offerOpen} onClose={() => setOfferOpen(false)} /></Suspense>
      ) : (
        <Modal open onClose={() => setOfferOpen(false)} title={t("play.modpack.title")} footer={<Button onClick={() => setOfferOpen(false)}>{t("common.close")}</Button>}>
          <KeyValue rows={[[t("play.modpack.server"), pending.data.server.name ?? pending.data.server.id], [t("play.modpack.missing"), pending.data.missing.join(", ") || "-"], [t("play.modpack.url"), <span className="k-mono">{pending.data.modpack?.url}</span>]]} />
          <span className="k-hint">{t("play.modpack.notAvailable")}</span>
        </Modal>
      ))}
    </>
  );
}

/** Refusal, Kiln did not start, the Steam launch option, Steam updating, safe idle: flat strips above the name. */
function Banners({ game, ctl, pending, onOffer, boot }: { game: GameInstall; ctl: PlayControl; pending: LastServer | null; onOffer: () => void; boot: api.BootJson | null }) {
  const t = useT();
  const qc = useQueryClient();
  const nav = useNavigate();
  const status = useGameStatus(game.id);
  const change = useRuntimeChanges((x) => x.last[game.id]);
  const update = useUpdate();
  const s = status.data;
  const direct = s?.start === "direct";
  const setup = useStartSetup(game.id, direct);
  const out = [];
  // (dev.29.17) the exe's build is one Kiln has no verified record of: Kiln starts with the game and stays idle
  if (s?.build && !s.build.known) out.push(<UnknownBuild key="build" build={s.build} />);
  if (direct && setup.data?.problem && !ctl.running) {
    out.push(<Banner key="start-problem" tone="err" title={t("gameStart.cannotStart")}>{setup.data.problem}</Banner>);
  }
  if (pending?.outcome === "rejected") {
    out.push(
      <Banner key="refused" tone="err" title={t("play.banner.refused")} actions={<>
        {pending.modpack && <Button size="s" onClick={onOffer}>{t("play.banner.getModpack")}</Button>}
        <button type="button" className="k-link" onClick={() => api.modpackDismiss(game.id, pending.server.id ?? "").then(() => qc.invalidateQueries({ queryKey: qk.pending(game.id) })).catch((e) => toast.error(e))}>{t("common.dismiss")}</button>
      </>}>
        {pending.reason != null ? t("play.banner.refusedReason", { server: pending.server.name ?? t("play.banner.aServer"), reason: pending.reason })
          : pending.missing.length ? rich("play.banner.refusedMissing", { server: pending.server.name ?? t("play.banner.aServer"), mods: <span className="k-mono">{pending.missing.join(", ")}</span> })
          : t("play.banner.refusedNeeds", { server: pending.server.name ?? t("play.banner.aServer") })}
      </Banner>,
    );
  }
  if (change?.state === "failed") {
    const has = s?.runtime.receipt?.runtime.version;
    out.push(
      <Banner key="rt" tone="err" title={t("play.banner.changeFailed", { what: change.what })} actions={<>
        <Button size="s" onClick={() => nav("/runtime")}>{t("play.banner.runtime")}</Button>
        <button type="button" className="k-link" onClick={() => useRuntimeChanges.getState().clear(game.id)}>{t("common.dismiss")}</button>
      </>}>
        <span style={{ overflowWrap: "anywhere" }}>{change.error?.message ?? t("play.banner.noReason")}</span>{" "}
        {has ? rich("play.banner.stillHas", { version: <span className="k-mono">{has}</span> }) : t("play.banner.notInGame")}
      </Banner>,
    );
  }
  if (s?.steamUpdating) out.push(<Banner key="upd" tone="warn" title={t("play.banner.updating")}>{t("play.banner.steamUpdating")}</Banner>);
  // (dev.29) the game's Kiln and the launcher's channel differ: Kiln from a test build after a switch back (Play puts the
  // launcher's own in first: shell/play.ts), or a test build of the launcher with the release's Kiln in the game
  const channel = channelNotice(s?.runtime, update?.buildChannel === "beta", kilnLabel);
  if (channel) {
    out.push(
      <Banner key="channel" tone="warn" title={s?.runtime.switchBack ? t("play.banner.fromBeta") : t("play.banner.fromRelease")} actions={!s?.runtime.switchBack ? <Button size="s" onClick={() => nav("/runtime")}>{t("play.banner.runtime")}</Button> : undefined}>
        <span data-testid="channel-notice">{channel}</span>
      </Banner>,
    );
  }
  // a mod built against a newer Kiln may use something this game's Kiln doesn't have yet: it fails to load until Kiln is updated
  const newer = s?.runtime.installed ? s.newerMods ?? [] : [];
  if (newer.length) {
    out.push(
      <Banner key="newer" tone="warn" title={t("play.banner.updateKiln")} actions={<Button size="s" onClick={() => nav("/runtime")}>{t("common.update")}</Button>}>
        {t("play.banner.needsNewer", { count: newer.length, names: names(newer.map((m) => m.id)) })}
      </Banner>,
    );
  }
  const options = s?.launchOptions;
  // Suppress routine advice for known-native runners, but restore the explicit fix
  // if Kiln actually failed to start (including GE-Proton 11-11+).
  const advice = s?.runtime.installed && ctl.view.phase !== "vanilla"
    ? ((ctl.kilnMissing || ctl.stuck) ? options?.advice ?? options?.fallbackAdvice : options?.advice) : null;
  if (advice && (!s?.launchOptions?.hasDllOverride || s.launchOptions.chainedOverrideMissing)) {
    out.push(
      <Banner key="adv" tone="warn" title={s?.launchOptions?.hasDllOverride && s.launchOptions.chainedOverrideMissing ? t("play.banner.settingAsi") : ctl.kilnMissing ? t("play.banner.notLoading") : ctl.stuck ? t("play.banner.didNotStart") : t("play.banner.setting")}
        actions={<Button size="s" onClick={async () => {
          try {
            if (!navigator.clipboard) throw new Error(t("play.banner.noClipboard"));
            await navigator.clipboard.writeText(advice);
            toast.ok(t("play.banner.copied"), t("play.banner.copiedDetail"));
          } catch (e) { toast.error(e, t("play.banner.copyFailed")); }
        }}>{t("common.copy")}</Button>}>
        {!s?.launchOptions?.hasDllOverride && ctl.stuck ? rich("play.banner.adviceStuck", { option: <span className="k-mono">{advice}</span> })
          : s?.launchOptions?.hasDllOverride && s.launchOptions.chainedOverrideMissing ? rich("play.banner.adviceAsi", { option: <span className="k-mono">{advice}</span> })
          : rich("play.banner.advice", { option: <span className="k-mono">{advice}</span> })}
      </Banner>,
    );
  } else if (ctl.stuck && s?.runtime.installed) {
    // the launch option is set (or not needed here) and Kiln still did not load: say so, never "Starting" forever
    out.push(
      <Banner key="nokiln" tone="warn" title={ctl.kilnMissing ? t("play.banner.notLoading") : t("play.banner.didNotStart")} actions={<Button size="s" onClick={() => nav("/logs")}>{t("play.banner.supportReport")}</Button>}>
        {t("play.banner.noKiln")}
      </Banner>,
    );
  }
  // (dev.29.18) a graphics tool next to the game that changes what Kiln's overlay does (OptiScaler): what to expect
  for (const g of s?.runtime.installed ? s.graphicsTools ?? [] : []) {
    out.push(
      <Banner key={`gfx-${g.engine}`} tone="info" title={t("play.banner.graphicsTool", { name: g.engine === "optiscaler" ? "OptiScaler" : g.engine, file: g.file })}>
        <span data-testid="graphics-tool-note">{g.text}</span>
      </Banner>,
    );
  }
  if (boot?.safeIdle) out.push(<Banner key="safe" tone="warn" title={t("play.banner.safeIdle")}>{boot.safeIdle}</Banner>);
  else if (boot?.profile.mode === "invalid") out.push(<Banner key="inv" tone="err" title={t("play.banner.noModsLoaded")}>{boot.profile.error ?? t("play.banner.profileInvalid")}</Banner>);
  if (!out.length) return null;
  return <div className="k-stack k-hero-banner" style={{ gap: 6 }}>{out}</div>;
}

function VanillaOnceModal({ open, onClose, onPlay }: { open: boolean; onClose: () => void; onPlay: () => void }) {
  const t = useT();
  return (
    <Modal arch open={open} onClose={onClose} title={t("play.vanillaOnce.title")}
      footer={<><Button onClick={onClose}>{t("common.cancel")}</Button><Button variant="primary" onClick={onPlay}>{t("play.playVanilla")}</Button></>}>
      <ArchText>{t("play.vanillaOnce.text")}</ArchText>
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
  if (!boot) return { count: 0, text: t("play.problems.none"), detail: "" };
  const since = Date.parse(boot.written) - 2000;
  const sameRun = (f: { pid: number; written: string } | null | undefined) => !!f && f.pid === boot.pid && Date.parse(f.written) >= since;
  const errors = [
    ...(boot.errors ?? []),
    ...boot.chain.filter((c) => c.status === "failed").map((c) => t("play.problems.didNotLoad", { file: c.path.split(/[\\/]/).pop() })),
    ...(boot.plugins ?? []).filter((p) => p.status === "failed").map((p) => t("play.problems.plugin", { id: p.id ?? p.file, error: p.error ?? t("play.problems.failed") })),
  ];
  const mods = sameRun(st?.mods) ? st!.mods!.mods.filter((m) => m.state === "failed") : [];
  const natives = sameRun(st?.native) ? st!.native!.mods.filter((m) => m.state === "failed") : [];
  const nativeCount = natives.length || (boot.native?.failed ?? 0);
  // kiln.log's error lines of this run (launch_state checks the run) also count what went wrong after the boot:
  // a service tick that throws at the menu, a mod's later error. Boot errors are logged there too.
  const logged = st?.log ?? null;
  const errorCount = logged ? Math.max(logged.errors, errors.length) : errors.length;
  const parts = [
    mods.length ? t("play.problems.modsFailed", { count: mods.length }) : "",
    nativeCount ? t("play.problems.nativeFailed", { count: nativeCount }) : "",
    errorCount ? logged ? t("play.problems.errorsSinceStart", { count: errorCount }) : t("play.problems.errors", { count: errorCount }) : "",
  ].filter(Boolean);
  const detail = [
    ...mods.map((m) => `${m.name ?? m.id}: ${m.reason ?? t("play.problems.failed")}`),
    ...natives.map((m) => `${m.id}: ${m.reason ?? t("play.problems.failed")}`),
    ...(logged?.first.length ? logged.first : errors),
    ...(logged && logged.errors > logged.first.length ? [t("play.problems.more", { count: logged.errors - logged.first.length })] : []),
  ].join("\n");
  return { count: mods.length + nativeCount + errorCount, text: parts.length ? parts.join(", ") : t("play.problems.none"), detail };
}

function platform(g: GameInstall): string {
  const p = g.platform === "proton" ? "Proton" : g.platform === "wine" ? "Wine" : g.platform === "windows" ? "Windows" : String(g.platform);
  return g.pathMode === "copy" ? t("play.copyMode", { platform: p }) : p;
}
/** Where an install came from, for the game picker: a folder no Steam library knows says "custom". */
function sourceWord(g: GameInstall): string {
  if (g.source === "manual" && !g.library) return t("play.source.custom");
  return g.source === "steam" ? "Steam" : g.source;
}
function shortPath(p: string): string {
  const parts = p.split(/[\\/]/).filter(Boolean);
  return parts.length > 3 ? `…/${parts.slice(-3).join("/")}` : p;
}
function names(ids: string[]): string {
  if (ids.length <= 1) return ids.join("");
  return ids.length === 2 ? t("play.names.pair", ids[0], ids[1]) : t("play.names.more", { a: ids[0], b: ids[1], count: ids.length - 2 });
}
