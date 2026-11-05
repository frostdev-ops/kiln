// Cards, panels, page header, key/value lists.
import type { HTMLAttributes, ReactNode } from "react";

export interface CardProps extends Omit<HTMLAttributes<HTMLElement>, "title"> {
  title?: ReactNode;
  /** One short line under the title. */
  sub?: ReactNode;
  actions?: ReactNode;
  footer?: ReactNode;
  /** Ember glow border (the one card that matters on a page). */
  glow?: boolean;
  /** Body without padding (tables, logs). */
  flush?: boolean;
}

export function Card({ title, sub, actions, footer, glow, flush, className, children, ...rest }: CardProps) {
  return (
    <section className={["k-card", glow && "k-card-glow", className].filter(Boolean).join(" ")} {...rest}>
      {(title || actions) && (
        <header className="k-card-head">
          <div className="k-stack" style={{ gap: 2, minWidth: 0 }}>
            {title && <h2 className="k-card-title">{title}</h2>}
            {sub && <div className="k-card-sub">{sub}</div>}
          </div>
          {actions && <div className="k-card-actions">{actions}</div>}
        </header>
      )}
      <div className={"k-card-body" + (flush ? " flush" : "")}>{children}</div>
      {footer && <footer className="k-card-foot">{footer}</footer>}
    </section>
  );
}

export interface PanelProps extends Omit<HTMLAttributes<HTMLElement>, "title"> {
  title?: ReactNode;
  actions?: ReactNode;
}

/** A titled section without a frame (inside cards or pages). */
export function Panel({ title, actions, className, children, ...rest }: PanelProps) {
  return (
    <section className={["k-panel", className].filter(Boolean).join(" ")} {...rest}>
      {(title || actions) && (
        <header className="k-panel-head">
          {title && <h3 className="k-panel-title">{title}</h3>}
          {actions && <div className="k-panel-actions">{actions}</div>}
        </header>
      )}
      {children}
    </section>
  );
}

export interface PageHeaderProps {
  title: ReactNode;
  sub?: ReactNode;
  actions?: ReactNode;
}

export function PageHeader({ title, sub, actions }: PageHeaderProps) {
  return (
    <header className="k-page-head">
      <div style={{ minWidth: 0 }}>
        <h1 className="k-page-title">{title}</h1>
        {sub && <div className="k-page-sub">{sub}</div>}
      </div>
      {actions && <div className="k-page-actions">{actions}</div>}
    </header>
  );
}

/** Page wrapper: padding, column gap, max width. */
export function Page({ wide, children }: { wide?: boolean; children: ReactNode }) {
  return <div className={"k-page" + (wide ? " k-page-wide" : "")}>{children}</div>;
}

export type KeyValueRow = [ReactNode, ReactNode] | null | false | undefined | "" | 0;

/** A two-column definition list; falsy rows are skipped. */
export function KeyValue({ rows }: { rows: KeyValueRow[] }) {
  return (
    <dl className="k-kv">
      {rows.filter(Boolean).map((r, i) => {
        const [k, v] = r as [ReactNode, ReactNode];
        return (
          <div key={i} style={{ display: "contents" }}>
            <dt>{k}</dt>
            <dd>{v}</dd>
          </div>
        );
      })}
    </dl>
  );
}
