// Deck Help: the one Steam setting Valve's Proton needs, how to add the launcher to Steam, and the controls. The
// launcher never writes Steam's files (launch options, shortcuts.vdf, localconfig.vdf): the player sets them, these
// are the steps. Each block is focusable, so the D-pad walks through them; the right stick scrolls.
import { useGame, useGameStatus } from "../../api/hooks";
import { Glyph, useGlyphStyle } from "../glyphs";
import { Screen, Word } from "../ui";
import { LAUNCH_OPTION } from "../status";
import type { PadButton } from "../input";
import { rich, useT } from "../../i18n";

export default function Help() {
  const t = useT();
  const { game } = useGame();
  const status = useGameStatus(game?.id);
  const style = useGlyphStyle();
  const lo = status.data?.launchOptions ?? null;
  const option = lo?.advice ?? LAUNCH_OPTION;
  const state = !lo ? null : lo.chainedOverrideMissing ? { tone: "warn" as const, text: lo.hasDllOverride ? t("deck.help.addXinput") : t("deck.help.needed") } : lo.hasDllOverride ? { tone: "ok" as const, text: t("deck.help.alreadySet") }
    : lo.advice ? { tone: "warn" as const, text: t("deck.help.needed") } : { tone: "ok" as const, text: t("deck.help.notNeeded") };
  const controls: [PadButton | "dpad" | "stick-l" | "stick-r", string][] = [
    ["dpad", t("deck.app.move")], ["stick-l", t("deck.app.move")], ["a", t("deck.app.select")], ["b", t("common.back")], ["x", t("deck.help.screenAction")], ["y", t("deck.help.screenAction")],
    ["lb", t("deck.app.previousTab")], ["rb", t("deck.app.nextTab")], ["start", t("deck.app.menu")], ["select", t("deck.app.tabs.help")], ["stick-r", t("deck.help.scroll")],
  ];
  return (
    <Screen name="help" title={t("deck.app.tabs.help")} className="deck-help">
      <div className="deck-scroll deck-help-body">
        <section className="deck-block" tabIndex={0} data-deck-id="help-steam" data-deck-a="-" aria-labelledby="h-steam">
          <h2 id="h-steam">{t("deck.shared.steamSetting")} {state && <Word tone={state.tone}>{state.text}</Word>}</h2>
          <p>{lo?.chainedOverrideMissing ? t("deck.help.asiNeeds") : t("deck.help.ifNotLoading")}</p>
          <p className="deck-code" data-testid="deck-launch-option">{option}</p>
          <ol>
            <li>{rich("deck.help.steamStep1", { steam: <b>Steam</b>, library: <b>{t("deck.help.library")}</b> })}</li>
            <li>{rich("deck.help.steamStep2", { game: <b>Enshrouded</b>, gear: <b>{t("deck.help.gear")}</b>, glyph: <Glyph button="start" style={style} size={1.3} />, properties: <b>{t("deck.help.properties")}</b> })}</li>
            <li>{rich("deck.help.steamStep3", { general: <b>{t("deck.help.general")}</b>, launchOptions: <b>{t("deck.help.launchOptions")}</b> })}</li>
            <li>{rich("deck.help.steamStep4", { keys: <b>Steam + X</b> })}</li>
            <li>{t("deck.help.steamStep5")}</li>
          </ol>
          <p className="deck-dim">{t("deck.help.neverChanges")}</p>
        </section>
        <section className="deck-block" tabIndex={0} data-deck-id="help-noload" data-deck-a="-" aria-labelledby="h-noload">
          <h2 id="h-noload">{t("deck.help.notLoadingTitle")}</h2>
          <p>{rich("deck.help.homeSaysNoKiln", { noKiln: <b>{t("deck.help.noKiln")}</b> })}</p>
          <p>{rich("deck.help.sendUs", { proxyLog: <span className="deck-mono">kiln/logs/proxy.log</span>, kilnLog: <span className="deck-mono">kiln/logs/kiln.log</span>, launcherLogs: <span className="deck-mono">~/.local/share/kiln/logs</span> })}</p>
        </section>
        <section className="deck-block" tabIndex={0} data-deck-id="help-add" data-deck-a="-" aria-labelledby="h-add">
          <h2 id="h-add">{t("deck.help.gameModeTitle")}</h2>
          <ol>
            <li>{rich("deck.help.gameModeStep1", { steam: <b>Steam</b>, power: <b>{t("deck.help.power")}</b>, toDesktop: <b>{t("deck.help.switchToDesktop")}</b> })}</li>
            <li>{rich("deck.help.gameModeStep2", { games: <b>{t("deck.help.games")}</b>, addNonSteam: <b>{t("deck.help.addNonSteam")}</b> })}</li>
            <li>{rich("deck.help.gameModeStep3", { browse: <b>{t("deck.help.browse")}</b>, addSelected: <b>{t("deck.help.addSelected")}</b> })}</li>
            <li>{rich("deck.help.gameModeStep4", { library: <b>{t("deck.help.library")}</b>, nonSteam: <b>{t("deck.help.nonSteam")}</b> })}</li>
          </ol>
          <p className="deck-dim">{rich("deck.help.layout", { gamepad: <b>{t("deck.help.gamepad")}</b>, flag: <span className="deck-mono">--deck</span> })}</p>
        </section>
        <section className="deck-block" tabIndex={0} data-deck-id="help-controls" data-deck-a="-" aria-labelledby="h-controls">
          <h2 id="h-controls">{t("deck.help.controls")}</h2>
          <dl className="deck-controls">
            {controls.map(([b, what]) => (
              <div key={b + what}><dt><Glyph button={b} style={style} size={1.6} /></dt><dd>{what}</dd></div>
            ))}
          </dl>
          <p className="deck-dim">{t("deck.help.touch")}</p>
        </section>
      </div>
    </Screen>
  );
}
