// Empty states. Inside a table or a section: one italic line in Ash 2 (and an optional hint and action).
// A whole page with nothing yet (`page`): a small unlit arch, a Grenze 36 name, one line, one action.
// Skeleton: rows at real height in Soot 2, no shimmer.
import type { ReactNode } from "react";

export interface EmptyStateProps {
  /** Kept for old callers: empty states have no icons. */
  icon?: ReactNode;
  title: ReactNode;
  hint?: ReactNode;
  action?: ReactNode;
  /** The whole page has nothing yet. */
  page?: boolean;
}

export function EmptyState({ title, hint, action, page }: EmptyStateProps) {
  return (
    <div className={"k-empty" + (page ? " page" : "")}>
      <svg className="k-empty-arch" viewBox="0 0 56 72" aria-hidden><path d="M1 71V31C1 18 15 8 28 1C41 8 55 18 55 31V71Z" /></svg>
      <div className="k-empty-title">{title}</div>
      {hint && <div className="k-empty-hint">{hint}</div>}
      {action && <div className="k-empty-action">{action}</div>}
    </div>
  );
}

/** Placeholder rows while a query loads. */
export function Skeleton({ lines = 3, width, height = 18 }: { lines?: number; width?: number | string; height?: number }) {
  return (
    <div className="k-stack" aria-busy style={{ gap: Math.max(10, 46 - height) }}>
      {Array.from({ length: lines }, (_, i) => (
        <div key={i} className="k-skeleton" style={{ width: width ?? `${90 - i * 15}%`, height }} />
      ))}
    </div>
  );
}
