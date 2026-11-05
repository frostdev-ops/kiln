// Status pill, badge, banner, keyboard hint.
import { CircleAlert, CircleCheck, Info, TriangleAlert } from "lucide-react";
import type { ReactNode } from "react";

export type Tone = "ok" | "warn" | "err" | "info" | "idle" | "ember";

export interface StatusPillProps {
  tone?: Tone;
  /** Animated dot (something is in progress). */
  pulse?: boolean;
  title?: string;
  children: ReactNode;
}

export function StatusPill({ tone = "idle", pulse, title, children }: StatusPillProps) {
  return (
    <span className={`k-pill k-tone-${tone}` + (pulse ? " k-pill-pulse" : "")} title={title}>
      <span className="k-dot" aria-hidden />
      {children}
    </span>
  );
}

export function Badge({ tone = "idle", title, children }: { tone?: Tone; title?: string; children: ReactNode }) {
  return (
    <span className={`k-badge k-tone-${tone}`} title={title}>
      {children}
    </span>
  );
}

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd className="k-kbd">{children}</kbd>;
}

const BANNER_ICON: Record<Tone, ReactNode> = {
  ok: <CircleCheck />, warn: <TriangleAlert />, err: <CircleAlert />, info: <Info />, idle: <Info />, ember: <Info />,
};

export interface BannerProps {
  tone?: Tone;
  title?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
}

/** A one-block notice inside a page (refusals, warnings). */
export function Banner({ tone = "info", title, actions, children }: BannerProps) {
  return (
    <div className={`k-banner k-tone-${tone}`} role={tone === "err" ? "alert" : "status"}>
      {BANNER_ICON[tone]}
      <div className="k-banner-body">
        {title && <div className="k-banner-title">{title}</div>}
        {children && <div className="k-banner-text">{children}</div>}
      </div>
      {actions && <div className="k-banner-actions">{actions}</div>}
    </div>
  );
}
