// A small dropdown menu: a trigger button and a list of actions.
import { useEffect, useRef, useState, type ReactNode } from "react";
import { IconButton, type ButtonSize, type ButtonVariant } from "./Button";

export interface MenuItem {
  label: ReactNode;
  icon?: ReactNode;
  onSelect: () => void;
  danger?: boolean;
  disabled?: boolean;
}

export function Menu({ label, icon, items, align = "right", variant, size }: { label: string; icon: ReactNode; items: MenuItem[]; align?: "left" | "right"; variant?: ButtonVariant; size?: ButtonSize }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", close);
    document.addEventListener("keydown", esc);
    ref.current?.querySelector<HTMLElement>(".k-menu-item")?.focus();
    return () => { document.removeEventListener("mousedown", close); document.removeEventListener("keydown", esc); };
  }, [open]);
  return (
    <div ref={ref} style={{ position: "relative" }}>
      <IconButton label={label} icon={icon} variant={variant} size={size} onClick={() => setOpen((o) => !o)} aria-haspopup="menu" aria-expanded={open} />
      {open && (
        <div className="k-popover" role="menu" style={{ top: "calc(100% + 6px)", [align]: 0 }}>
          {items.map((it, i) => (
            <button key={i} type="button" role="menuitem" className={"k-menu-item" + (it.danger ? " danger" : "")} disabled={it.disabled}
              onClick={() => { setOpen(false); it.onSelect(); }}>
              {it.icon}{it.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
