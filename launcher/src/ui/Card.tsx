// Page scaffolding on the sheet: Page, PageHeader (a blackletter name and one live sentence), titled sections
// (Card keeps its name and props, but draws no frame: no cards), Panel, Aside, KeyValue facts.
import type { HTMLAttributes, ReactNode } from "react";

export interface CardProps extends Omit<HTMLAttributes<HTMLElement>, "title"> {
  title?: ReactNode;
  /** One short line beside the title. */
  sub?: ReactNode;
  actions?: ReactNode;
  footer?: ReactNode;
  /** Kept for old callers; sections have no frame or glow. */
  glow?: boolean;
  /** Kept for old callers. */
  flush?: boolean;
}

/** A titled group on the sheet: small-caps head, the hint beside it, actions on the right. */
export function Card({ title, sub, actions, footer, glow: _glow, flush: _flush, className, children, ...rest }: CardProps) {
  return (
    <section className={["k-card", className].filter(Boolean).join(" ")} {...rest}>
      {(title || sub || actions) && (
        <header className="k-card-head">
          <div className="k-card-titles">
            {title && <h2 className="k-card-title">{title}</h2>}
            {sub && <div className="k-card-sub">{sub}</div>}
          </div>
          {actions && <div className="k-card-actions">{actions}</div>}
        </header>
      )}
      <div className="k-card-body">{children}</div>
      {footer && <footer className="k-card-foot">{footer}</footer>}
    </section>
  );
}

export { Card as Section };

export interface PanelProps extends Omit<HTMLAttributes<HTMLElement>, "title"> {
  title?: ReactNode;
  actions?: ReactNode;
}

/** A smaller titled group inside a section. */
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
  /** The page's name (Grenze Gotisch). */
  title: ReactNode;
  /** The live sentence: what is the state? */
  sub?: ReactNode;
  actions?: ReactNode;
  /** Beside the name: an id, a state word. */
  meta?: ReactNode;
}

export function PageHeader({ title, sub, actions, meta }: PageHeaderProps) {
  return (
    <header className="k-page-head">
      <div>
        <div className="k-page-name">
          <h1 className="k-page-title">{title}</h1>
          {meta && <span className="k-page-meta">{meta}</span>}
        </div>
        {sub && <p className="k-page-sub">{sub}</p>}
      </div>
      {actions && <div className="k-page-actions">{actions}</div>}
    </header>
  );
}

/** A page on the sheet. `fill`: the page does not scroll; its lists scroll inside it. */
export function Page({ wide, fill, className, children }: { wide?: boolean; fill?: boolean; className?: string; children: ReactNode }) {
  return <div className={["k-page", wide && "k-page-wide", fill && "k-page-fill", className].filter(Boolean).join(" ")}>{children}</div>;
}

/** Main column plus a fixed aside (facts 204-232 wide, forms 300). */
export function SplitPage({ children, aside, form }: { children: ReactNode; aside: ReactNode; form?: boolean }) {
  return (
    <div className="k-split-page">
      <div className="k-split-main">{children}</div>
      <aside className={"k-aside" + (form ? " k-aside-form" : "")}>{aside}</aside>
    </div>
  );
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
