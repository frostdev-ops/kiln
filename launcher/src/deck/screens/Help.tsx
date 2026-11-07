// Deck Help: the one Steam setting Valve's Proton needs, how to add the launcher to Steam, and the controls. The
// launcher never writes Steam's files (launch options, shortcuts.vdf, localconfig.vdf): the player sets them, these
// are the steps. Each block is focusable, so the D-pad walks through them; the right stick scrolls.
import { useGame, useGameStatus } from "../../api/hooks";
import { Glyph, useGlyphStyle } from "../glyphs";
import { Screen, Word } from "../ui";
import { LAUNCH_OPTION } from "../status";
import type { PadButton } from "../input";

export default function Help() {
  const { game } = useGame();
  const status = useGameStatus(game?.id);
  const style = useGlyphStyle();
  const lo = status.data?.launchOptions ?? null;
  const option = lo?.advice ?? LAUNCH_OPTION;
  const state = !lo ? null : lo.hasDllOverride ? { tone: "ok" as const, text: "Already set" }
    : lo.advice ? { tone: "warn" as const, text: "Needed for this game" } : { tone: "ok" as const, text: "Not needed with your Proton" };
  const controls: [PadButton | "dpad" | "stick-l" | "stick-r", string][] = [
    ["dpad", "Move"], ["stick-l", "Move"], ["a", "Select"], ["b", "Back"], ["x", "Screen's action"], ["y", "Screen's action"],
    ["lb", "Previous tab"], ["rb", "Next tab"], ["start", "Menu"], ["select", "Help"], ["stick-r", "Scroll"],
  ];
  return (
    <Screen name="help" title="Help" className="deck-help">
      <div className="deck-scroll deck-help-body">
        <section className="deck-block" tabIndex={0} data-deck-id="help-steam" data-deck-a="-" aria-labelledby="h-steam">
          <h2 id="h-steam">One Steam setting {state && <Word tone={state.tone}>{state.text}</Word>}</h2>
          <p>If Kiln does not load with your Proton build, add this to Enshrouded's launch options. GE-Proton 11-11 and newer, and some recent Valve builds, load it without this setting.</p>
          <p className="deck-code" data-testid="deck-launch-option">{option}</p>
          <ol>
            <li>Press the <b>Steam</b> button, open <b>Library</b>.</li>
            <li>Select <b>Enshrouded</b>, press the <b>gear</b> (or <Glyph button="start" style={style} size={1.3} />), then <b>Properties</b>.</li>
            <li>In <b>General</b>, select <b>Launch Options</b>.</li>
            <li>Type the line above. <b>Steam + X</b> opens the keyboard.</li>
            <li>Back out. The next Play uses it.</li>
          </ol>
          <p className="deck-dim">Kiln never changes Steam's settings itself.</p>
        </section>
        <section className="deck-block" tabIndex={0} data-deck-id="help-noload" data-deck-a="-" aria-labelledby="h-noload">
          <h2 id="h-noload">Kiln not loading?</h2>
          <p>Home says <b>No Kiln</b> when the game runs but Kiln didn't load into it. Close the game, check the launch option above, and play again.</p>
          <p>Still no Kiln? Send us, from the game folder: <span className="deck-mono">kiln/logs/proxy.log</span> (no such file: Steam never loaded Kiln) and <span className="deck-mono">kiln/logs/kiln.log</span>, and the launcher's log (<span className="deck-mono">~/.local/share/kiln/logs</span>), with your Proton version and Game or Desktop Mode.</p>
        </section>
        <section className="deck-block" tabIndex={0} data-deck-id="help-add" data-deck-a="-" aria-labelledby="h-add">
          <h2 id="h-add">Kiln in Game Mode</h2>
          <ol>
            <li>Press <b>Steam</b>, <b>Power</b>, <b>Switch to Desktop</b>.</li>
            <li>Open Steam. In <b>Games</b>, choose <b>Add a Non-Steam Game to My Library</b>.</li>
            <li><b>Browse</b> to the Kiln Launcher AppImage, tick it, <b>Add Selected Programs</b>.</li>
            <li>Back in Game Mode it is in <b>Library</b>, <b>Non-Steam</b>. Deck mode opens by itself.</li>
          </ol>
          <p className="deck-dim">Controller layout: Steam's default <b>Gamepad</b> template. Optional launch option: <span className="deck-mono">--deck</span>.</p>
        </section>
        <section className="deck-block" tabIndex={0} data-deck-id="help-controls" data-deck-a="-" aria-labelledby="h-controls">
          <h2 id="h-controls">Controls</h2>
          <dl className="deck-controls">
            {controls.map(([b, what]) => (
              <div key={b + what}><dt><Glyph button={b} style={style} size={1.6} /></dt><dd>{what}</dd></div>
            ))}
          </dl>
          <p className="deck-dim">The touchscreen works everywhere. Keyboard: arrows, Enter, Escape, Q and E for tabs.</p>
        </section>
      </div>
    </Screen>
  );
}
