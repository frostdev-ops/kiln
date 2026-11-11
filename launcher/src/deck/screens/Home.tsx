// Deck Home: the game's name, how many mods are on (every kind counts), the big Play button (the kiln's mouth), Kiln's
// state with its one next step (Install, Update, Find game), and the last boot in one line. Only a profile other than
// the default is named; profiles are switched in Settings (the desktop UI's, or the Deck's).
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useGame, useGameStatus, useLaunchState, useRuntimeChanges } from "../../api/hooks";
import { errorText } from "../../ui";
import { clock, usePlayControl } from "../../shell/play";
import { detectGames, useDetect, useEnsureProfile, useKilnInstall } from "../../shell/firstRun";
import { isDefaultProfile, useMods } from "../../shell/useMods";
import { FindGameDialog, InstallDialog, PlayOnceDialog } from "../dialogs";
import { DeckButton, Screen, Word } from "../ui";
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
  const [open, setOpen] = useState<Open>(null);
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
  else if (ctl.starting) { word = "Starting"; sub = "Waiting for Kiln"; heat = "flare"; }
  else if (ctl.running) { word = ctl.elapsed !== null ? clock(ctl.elapsed) : v.label; sub = v.label; heat = "embers"; }
  else { word = "Play"; if (ctl.canPlay && !kiln.installing) act = () => ctl.play("profile"); }

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
        {game && <p className="deck-line" data-testid="deck-last-boot"><span className="deck-label">Last boot</span> <Word tone={boot.tone}>{boot.text}</Word></p>}
        <div className="deck-home-actions">
          {!game && !looking && tried && <DeckButton id="pick-folder" a="Open" onClick={() => setOpen("find")}>Pick folder</DeckButton>}
          {game && rt && !rt.installed && !kiln.shroudforge && !kiln.installing && ctl.canPlay && (
            <DeckButton id="install" onClick={() => setOpen("install")}>Install Kiln</DeckButton>
          )}
          {game && rt?.installed && rt.upgradeAvailable && <DeckButton id="update" disabled={!ctl.canPlay || kiln.installing} onClick={() => setOpen("update")}>Update Kiln</DeckButton>}
          {(advice || (ctl.stuck && rt?.installed)) && <DeckButton id="steam-setting" a="How" onClick={() => nav("/deck/help")}>{ctl.stuck ? "Kiln is not loading" : "One Steam setting"}</DeckButton>}
          {change?.state === "failed" && <DeckButton id="kiln-failed" onClick={() => nav("/deck/kiln")}>Kiln</DeckButton>}
        </div>
      </div>
      <button type="button" className={`deck-mouth heat-${heat}`} disabled={!act} onClick={() => act?.()} data-deck-autofocus="" data-deck-id="mouth"
        data-deck-a={word} aria-label={sub ? `${word}: ${sub}` : word}
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
    </Screen>
  );
}
