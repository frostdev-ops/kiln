// The start's bar on the Play page (and the bar alone on the Deck's Home): the running step's line (a status), the bar
// (a progressbar with the step and the time left as its text), the time left or the first start's sentence (where the
// number comes from on hover), and, where there is room, every step: the done ones with what they took, the running
// one, the ones to come with what they usually take. shell/startProgress.ts has the words.
import type { PreStart, StartTimeline } from "../api/commands";
import { startWords, stepTime } from "./startProgress";
import { useT } from "../i18n";

export function StartBar({ t: tl, words, className = "k-start-bar" }: { t: StartTimeline; words: ReturnType<typeof startWords>; className?: string }) {
  const t = useT();
  return (
    <div className={className + (words.first ? " by-steps" : "")} role="progressbar" aria-label={t("shell.startPanel.barAria")} aria-valuemin={0} aria-valuemax={100}
      aria-valuenow={words.pct} aria-valuetext={words.valueText} data-phase={tl.phase}>
      <i style={{ width: `${Math.max(1.5, words.pct)}%` }} />
    </div>
  );
}

export function StartProgress({ t: tl, prep }: { t: StartTimeline; prep?: PreStart | null }) {
  const t = useT();
  const words = startWords(tl, prep);
  const steps = tl.steps.filter((s) => s.state !== "skipped");
  return (
    <div className="k-start" data-testid="start-progress">
      <p className="k-hero-say k-start-line" role="status" title={words.line}>{words.line}</p>
      <StartBar t={tl} words={words} />
      <p className={"k-start-left" + (tl.estimate.slow ? " k-tone-warn" : "")} title={words.basis ?? undefined} data-testid="start-left">
        {words.left}
        {words.basis && <span className="k-start-basis"> {words.basis}.</span>}
      </p>
      <ol className="k-start-steps" aria-label={t("shell.startPanel.stepsAria")}>
        {steps.map((s) => (
          <li key={s.id} className={`k-start-step is-${s.state}`} aria-current={s.state === "running" ? "step" : undefined}>
            <span className="k-start-dot" aria-hidden />
            <span className="k-start-name">{s.state === "running" ? s.label : s.state === "done" && s.id === "cook" ? t("shell.startPanel.cookingMods") : s.label}</span>
            <span className="k-start-time">{stepTime(s)}</span>
          </li>
        ))}
      </ol>
    </div>
  );
}
