// State words (glaze colours, always with a word: no pills, no chips), the banner strip, a keyboard hint.
// StatusPill and Badge keep their names and the tone prop; both render a word in its tone.
import type { ReactNode } from "react";

export type Tone = "ok" | "warn" | "err" | "info" | "idle" | "ember";

export interface StatusPillProps {
  tone?: Tone;
  /** Kept for old callers (no animation: nothing glows unless it is hot). */
  pulse?: boolean;
  title?: string;
  children: ReactNode;
}

/** A state word: Connected (ok), Vanilla (info), Updating (warn), Refused (err), Stopped (idle), Running (ember). */
export function StatusPill({ tone = "idle", title, children }: StatusPillProps) {
  return <span className={`k-pill k-tone-${tone}`} title={title}>{cap(children)}</span>;
}

/** A smaller state word (a kind, a tag). */
export function Badge({ tone = "idle", title, children }: { tone?: Tone; title?: string; children: ReactNode }) {
  return <span className={`k-badge k-tone-${tone}`} title={title}>{cap(children)}</span>;
}

/** Words read as words: "running" shows as "Running". */
function cap(c: ReactNode): ReactNode {
  return typeof c === "string" && c.length ? c.charAt(0).toUpperCase() + c.slice(1) : c;
}

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd className="k-kbd">{children}</kbd>;
}

export interface BannerProps {
  tone?: Tone;
  /** The state word or short lead, in its glaze colour. */
  title?: ReactNode;
  /** One action on the right. */
  actions?: ReactNode;
  /** One sentence. */
  children?: ReactNode;
}

/** A flat Soot 3 strip at the top of the sheet: refusals, warnings, Steam updates. */
export function Banner({ tone = "info", title, actions, children }: BannerProps) {
  return (
    <div className="k-banner" role={tone === "err" ? "alert" : "status"}>
      <div className="k-banner-body">
        {title && <div className={`k-banner-title k-tone-${tone}`}>{title}</div>}
        {children && <div className="k-banner-text">{children}</div>}
      </div>
      {actions && <div className="k-banner-actions">{actions}</div>}
    </div>
  );
}
