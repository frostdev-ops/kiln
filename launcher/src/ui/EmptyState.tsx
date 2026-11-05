// Empty state: one icon, one title, one hint line, an optional action.
import type { ReactNode } from "react";

export interface EmptyStateProps {
  icon?: ReactNode;
  title: ReactNode;
  hint?: ReactNode;
  action?: ReactNode;
}

export function EmptyState({ icon, title, hint, action }: EmptyStateProps) {
  return (
    <div className="k-empty">
      {icon && <div className="k-empty-icon">{icon}</div>}
      <div className="k-empty-title">{title}</div>
      {hint && <div className="k-empty-hint">{hint}</div>}
      {action && <div className="k-empty-action">{action}</div>}
    </div>
  );
}

/** Placeholder bars while a query loads. */
export function Skeleton({ lines = 3, width }: { lines?: number; width?: number | string }) {
  return (
    <div className="k-stack" aria-busy>
      {Array.from({ length: lines }, (_, i) => (
        <div key={i} className="k-skeleton" style={{ width: width ?? `${90 - i * 15}%` }} />
      ))}
    </div>
  );
}
