// Tabs: state tabs, route tabs, and a small segmented control.
import type { ReactNode } from "react";
import { NavLink } from "react-router-dom";

export interface TabItem<T extends string = string> {
  id: T;
  label: ReactNode;
  count?: number;
  disabled?: boolean;
}

export interface TabsProps<T extends string> {
  items: TabItem<T>[];
  value: T;
  onChange: (id: T) => void;
  "aria-label"?: string;
}

export function Tabs<T extends string>({ items, value, onChange, ...rest }: TabsProps<T>) {
  const move = (dir: number) => {
    const enabled = items.filter((i) => !i.disabled);
    const at = enabled.findIndex((i) => i.id === value);
    const next = enabled[(at + dir + enabled.length) % enabled.length];
    if (next) onChange(next.id);
  };
  return (
    <div className="k-tabs" role="tablist" aria-label={rest["aria-label"]}
      onKeyDown={(e) => { if (e.key === "ArrowRight") move(1); if (e.key === "ArrowLeft") move(-1); }}>
      {items.map((t) => (
        <button key={t.id} role="tab" type="button" aria-selected={t.id === value} tabIndex={t.id === value ? 0 : -1}
          disabled={t.disabled} className={"k-tab" + (t.id === value ? " active" : "")} onClick={() => onChange(t.id)}>
          {t.label}
          {t.count !== undefined && <span className="k-tab-count">{t.count}</span>}
        </button>
      ))}
    </div>
  );
}

export interface NavTabItem {
  to: string;
  label: ReactNode;
  count?: number;
  end?: boolean;
}

/** Tabs that are links (route per tab). */
export function NavTabs({ items, "aria-label": label }: { items: NavTabItem[]; "aria-label"?: string }) {
  return (
    <nav className="k-tabs" aria-label={label}>
      {items.map((t) => (
        <NavLink key={t.to} to={t.to} end={t.end} className={({ isActive }) => "k-tab" + (isActive ? " active" : "")}>
          {t.label}
          {t.count !== undefined && <span className="k-tab-count">{t.count}</span>}
        </NavLink>
      ))}
    </nav>
  );
}

export interface SegmentedProps<T extends string> {
  options: { id: T; label: ReactNode }[];
  value: T;
  onChange: (id: T) => void;
  "aria-label"?: string;
}

export function Segmented<T extends string>({ options, value, onChange, ...rest }: SegmentedProps<T>) {
  return (
    <div className="k-seg" role="radiogroup" aria-label={rest["aria-label"]}>
      {options.map((o) => (
        <button key={o.id} type="button" role="radio" aria-checked={o.id === value} className={o.id === value ? "on" : ""} onClick={() => onChange(o.id)}>
          {o.label}
        </button>
      ))}
    </div>
  );
}
