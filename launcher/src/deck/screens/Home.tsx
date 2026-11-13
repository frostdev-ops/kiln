// Deck Home: the game's name, how many mods are on (every kind counts), the big Play button (the kiln's mouth), Kiln's
// state with its one next step (Install, Update, Find game), and the last boot in one line. Only a profile other than
// the default is named; profiles are switched in Settings (the desktop UI's, or the Deck's).
// When ShroudForge's startup pass failed at the last start (engine_status `startupFailed`, sf_startup.rs), a line says
// so and a button opens the whole text (what happened, the mods it left out, what to change) in a dialog the right stick
// scrolls: on a Deck nothing else shows it (player report 327).
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "../../api/commands";
import type { SfStartupNote } from "../../api/commands";
import { useGame, useGameStatus, useLaunchState, usePoll, useRuntimeChanges } from "../../api/hooks";
import { engineStatusKey } from "../../components/EngineStatus";
import { errorText } from "../../ui";
import { clock, usePlayControl } from "../../shell/play";
import { startWords, steadyFraction } from "../../shell/startProgress";
import { StartBar } from "../../shell/StartPanel";
import { detectGames, useDetect, useEnsureProfile, useKilnInstall } from "../../shell/firstRun";
import { isDefaultProfile, useMods } from "../../shell/useMods";
import { FindGameDialog, InstallDialog, PlayOnceDialog } from "../dialogs";
import { DeckButton, Dialog, Screen, Word } from "../ui";
import { bootLine, kilnLabel } from "../status";
import { deckFire } from "../Fire";
import type { DeckAction } from "../layers";

type Open = null | "install" | "update" | "once" | "find";

export default function Home() {
  const qc = useQueryClient();
  const nav = useNavigate();
  const ctl = usePlayControl();
  const { game, loading, error } = useGame();
  const looking = useDetect((s) => s.busy);
  const tried = useDetect((s) => s.tried);
  const kiln = useKilnInstall(game);
  const status = useGameStatus(game?.id);
  const launch = useLaunchState(game?.id);
  const mods = useMods(game);
  useEnsureProfile(game);
  const change = useRuntimeChanges((s) => (game ? s.last[game.id] : undefined));
  const engines = useQuery({ queryKey: engineStatusKey(game?.id ?? ""), queryFn: () => api.engineStatus(game!.id), enabled: !!game,
    refetchInterval: usePoll(10_000) });
  const sfStartup = engines.data?.find((e) => e.kind === "shroudforge")?.startupFailed ?? null;
  const [open, setOpen] = useState<Open>(null);
  // the note the dialog shows: the one A was pressed on (a poll that changes it neither closes nor reopens the dialog)
  const [sfOpen, setSfOpen] = useState<SfStartupNote | null>(null);
  const s = status.data;
  const rt = s?.runtime;
  const v = ctl.view;

  // the mouth: one word and what A does
  let word: string, sub = "", act: (() => void) | null = null, heat = "idle";
  if (!game && (looking || loading || (!tried && !error))) { word = "Looking"; sub = "for Enshrouded"; heat = "unlit"; }
  else if (!game) { word = "Find game"; sub = "In your Steam libraries"; heat = "unlit"; act = () => void detectGames(qc).then((g) => { if (!g?.some((x) => x.kind === "client")) setOpen("find"); }); }
  else if (v.phase === "none") { word = "Play"; sub = status.error ? errorText(status.error) : "Checking the game"; heat = "unlit"; }
  else if (v.phase === "updating") { word = "Updating"; sub = "Steam is updating the game"; heat = "low"; }
  else if (!ctl.installed && !ctl.running && kiln.installing) { word = "Installing"; sub = "Kiln goes into the game folder"; heat = "low"; }
  else if (!ctl.installed && !ctl.running && kiln.shroudforge) { word = "ShroudForge"; sub = "Kiln can take over: switch in the desktop UI"; heat = "unlit"; act = () => nav("/deck/kiln"); }
  else if (!ctl.installed && !ctl.running) { word = "Install"; sub = "Kiln goes into the game folder"; act = () => setOpen("install"); }
  else if (ctl.timeline) { word = "Starting"; heat = "flare"; }
  else if (ctl.starting) { word = "Starting"; sub = "Waiting for Kiln"; heat = "flare"; }
  else if (ctl.running) { word = ctl.elapsed !== null ? clock(ctl.elapsed) : v.label; sub = v.label; heat = "embers"; }
  else { word = "Play"; if (ctl.canPlay && !kiln.installing) act = () => ctl.play("profile"); }

  // a start that says what it does: the step and the time left under the mouth's word, the bar and the step's line in
  // the text column
  const t = ctl.timeline;
  const tw = t ? startWords(t, steadyFraction(t)) : null;
  if (tw) sub = tw.first ? tw.stepOf : tw.short;
  // the fire: the mouth stands on Home, at this heat (Fire.tsx)
  deckFire.heat = heat as typeof deckFire.heat;
  useEffect(() => { deckFire.play = true; return () => { deckFire.play = false; deckFire.hot = false; }; }, []);
  const other = rt?.installed && mods.summary && !isDefaultProfile(mods.summary) ? mods.summary.name : null;
  const count = !rt?.installed || !mods.profileId || mods.loading ? null
    : `${mods.count.on} mod${mods.count.on === 1 ? "" : "s"} on${mods.count.off ? `, ${mods.count.off} off` : ""}`;
  const boot = bootLine(launch.data);
  const kilnVer = rt?.receipt?.runtime.version ?? null;
  // Kiln's state and its next step
  let kilnWord: { text: string; tone: "ok" | "warn" | "err" | "idle" };
  if (!game) kilnWord = { text: "Game not found", tone: "warn" };
  else if (change?.state === "failed") kilnWord = { text: `${change.what} failed`, tone: "err" };
  else if (!rt) kilnWord = { text: "Checking", tone: "idle" };
  else if (!rt.installed) kilnWord = { text: kiln.shroudforge ? "ShroudForge is in this game" : "Kiln not installed", tone: "warn" };
  else if (rt.upgradeAvailable) kilnWord = { text: `Kiln ${kilnLabel(kilnVer ?? "")} · update ready`, tone: "warn" };
  else kilnWord = { text: `Kiln ${kilnLabel(kilnVer ?? "")}`, tone: "ok" };
  const advice = rt?.installed && s?.launchOptions?.advice && (ctl.stuck || !s.launchOptions.hasDllOverride);

  const actions: DeckAction[] = [];
  if (game && rt?.installed) actions.push({ button: "x", label: "Play once", run: () => setOpen("once"), disabled: !ctl.canPlay || kiln.installing });

  return (
    <Screen name="home" className="deck-home" actions={actions}>
      <div className="deck-home-text">
        <div className="deck-kicker">Enshrouded{game?.buildId ? ` · build ${game.buildId}` : ""}</div>
        <h1 className="deck-name">Enshrouded</h1>
        {count && <p className="deck-line" data-testid="deck-mods"><span className="deck-label">Mods</span> {count}{other && <span data-testid="deck-profile"> · Profile: {other}</span>}</p>}
        <p className="deck-line" data-testid="deck-kiln-state"><Word tone={kilnWord.tone}>{kilnWord.text}</Word></p>
        {t && tw && (
          <div className="deck-start" data-testid="deck-start">
            <p className="deck-line deck-start-line" role="status">{tw.line}</p>
            <StartBar t={t} words={tw} className="k-start-bar deck-start-bar" />
            <p className={"deck-line deck-start-left" + (t.estimate.slow ? " k-tone-warn" : "")}>{tw.left}{tw.basis && <span className="deck-start-basis"> {tw.basis}.</span>}</p>
          </div>
        )}
        {game && !t && <p className="deck-line" data-testid="deck-last-boot"><span className="deck-label">Last boot</span> <Word tone={boot.tone}>{boot.text}</Word></p>}
        {game && sfStartup && (
          <p className="deck-line" data-testid="deck-sf-startup"><span className="deck-label">ShroudForge</span> <Word tone="warn">{sfStartup.mods.length ? "mods did not load" : "startup pass failed"}</Word></p>
        )}
        <div className="deck-home-actions">
          {/* once the game's state is known, as the other actions: the arch takes the first focus (it is disabled before) */}
          {game && rt && v.phase !== "none" && sfStartup && <DeckButton id="sf-startup" a="Read" onClick={() => setSfOpen(sfStartup)}>ShroudForge: why, and what to do</DeckButton>}
          {!game && !looking && tried && <DeckButton id="pick-folder" a="Open" onClick={() => setOpen("find")}>Pick folder</DeckButton>}
          {game && rt && !rt.installed && !kiln.shroudforge && !kiln.installing && ctl.canPlay && (
            <DeckButton id="install" onClick={() => setOpen("install")}>Install Kiln</DeckButton>
          )}
          {game && rt?.installed && rt.upgradeAvailable && <DeckButton id="update" disabled={!ctl.canPlay || kiln.installing} onClick={() => setOpen("update")}>Update Kiln</DeckButton>}
          {(advice || (ctl.stuck && rt?.installed)) && <DeckButton id="steam-setting" a="How" onClick={() => nav("/deck/help")}>{ctl.stuck ? "Kiln is not loading" : "One Steam setting"}</DeckButton>}
          {change?.state === "failed" && <DeckButton id="kiln-failed" onClick={() => nav("/deck/kiln")}>Kiln</DeckButton>}
        </div>
      </div>
      {/* while the game starts or runs the mouth keeps the focus (aria-disabled, A does nothing): a gamepad is never left with
          nothing focused for the two minutes of a start, or after it */}
      <button type="button" className={`deck-mouth heat-${heat}`} disabled={!act && !ctl.starting && !ctl.running} aria-disabled={!act || undefined} onClick={() => act?.()}
        data-deck-autofocus="" data-deck-id="mouth" data-deck-a={act ? word : undefined} aria-label={sub ? `${word}: ${sub}` : word}
        onFocus={() => { deckFire.hot = !!act; }} onBlur={() => { deckFire.hot = false; }}>
        <span className="deck-mouth-fire" aria-hidden />
        <svg className="deck-mouth-ring" viewBox="0 0 100 88" preserveAspectRatio="none" aria-hidden>
          <path d="M0.6 88V64C1.5 50 4 40 8.8 31 14 22 18 17 23.6 13.5 29 9.5 33 6.5 38.1 4.2 43 2.2 46 1 50 0.6 54 1 57 2.2 61.9 4.2 67 6.5 71 9.5 76.4 13.5 82 17 86 22 91.2 31 96 40 98.5 50 99.4 64V88" />
        </svg>
        <span className="deck-mouth-lab" aria-hidden>
          <span className={"deck-mouth-word" + (word.length > 7 ? " long" : "")}>{word}</span>
          {sub && <span className="deck-mouth-sub">{sub}</span>}
        </span>
      </button>
      {(open === "install" || open === "update") && game && <InstallDialog game={game} update={open === "update"} onClose={() => setOpen(null)} />}
      {open === "once" && <PlayOnceDialog onClose={() => setOpen(null)} onPlay={(m) => ctl.play(m)} />}
      {open === "find" && <FindGameDialog onClose={() => setOpen(null)} />}
      {sfOpen && <SfStartupDialog note={sfOpen} onClose={() => setSfOpen(null)} />}
    </Screen>
  );
}

/** ShroudForge's failed startup pass in full: a long text (the mods it left out, the cure), so the body scrolls with the
 *  right stick, as the Deck's notices do; A on OK or B closes it. */
function SfStartupDialog({ note, onClose }: { note: SfStartupNote; onClose: () => void }) {
  return (
    <Dialog name="sf-startup" title={note.mods.length ? "ShroudForge mods did not load" : "ShroudForge's startup pass failed"} wide onClose={onClose}
      backLabel="Close" buttons={<DeckButton primary onClick={onClose}>OK</DeckButton>}>
      <div className="deck-scroll" data-kind="warn" style={{ overflowWrap: "anywhere" }}>{note.text}.</div>
    </Dialog>
  );
}
