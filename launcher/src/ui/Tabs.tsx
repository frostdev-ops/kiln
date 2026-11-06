// Tabs (small caps with an ember line), route tabs, and the segmented switch: words with an ember line, or the large
// "side" form with a sliding gold diamond ("Load order for: Client Server").
import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
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
  const box = useRef<HTMLDivElement>(null);
  const move = (dir: number) => {
    const enabled = items.filter((i) => !i.disabled);
    const at = enabled.findIndex((i) => i.id === value);
    const next = enabled[(at + dir + enabled.length) % enabled.length];
    if (next) {
      onChange(next.id);
      requestAnimationFrame(() => box.current?.querySelector<HTMLElement>('[aria-selected="true"]')?.focus());
    }
  };
  return (
    <div ref={box} className="k-tabs" role="tablist" aria-label={rest["aria-label"]}
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
  /** "side": the large small-caps switch with a sliding gold diamond. */
  variant?: "line" | "side";
}

export function Segmented<T extends string>({ options, value, onChange, variant = "line", ...rest }: SegmentedProps<T>) {
  const box = useRef<HTMLDivElement>(null);
  const [dia, setDia] = useState<number | null>(null);
  useLayoutEffect(() => {
    if (variant !== "side") return;
    const place = () => {
      const b = box.current?.querySelector<HTMLElement>("button.on");
      setDia(b ? b.offsetLeft + b.offsetWidth / 2 : null);
    };
    place();
    document.fonts?.ready.then(place).catch(() => {});
  }, [value, variant, options.length]);
  const key = (e: React.KeyboardEvent) => {
    const at = options.findIndex((o) => o.id === value);
    const step = e.key === "ArrowRight" || e.key === "ArrowDown" ? 1 : e.key === "ArrowLeft" || e.key === "ArrowUp" ? -1 : 0;
    if (!step) return;
    e.preventDefault();
    const next = options[(at + step + options.length) % options.length];
    onChange(next.id);
    requestAnimationFrame(() => box.current?.querySelector<HTMLElement>("button.on")?.focus());
  };
  return (
    <div ref={box} className={variant === "side" ? "k-side" : "k-seg"} role="radiogroup" aria-label={rest["aria-label"]} onKeyDown={key}>
      {options.map((o) => (
        <button key={o.id} type="button" role="radio" aria-checked={o.id === value} tabIndex={o.id === value ? 0 : -1}
          className={o.id === value ? "on" : ""} onClick={() => onChange(o.id)}>
          {o.label}
        </button>
      ))}
      {variant === "side" && dia !== null && (
        <span className="k-side-dia" aria-hidden style={{ transform: `translateX(${dia - 3.5}px)` }}><i className="k-dia" /></span>
      )}
    </div>
  );
}
