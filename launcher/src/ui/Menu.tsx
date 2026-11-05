// A small dropdown menu: a trigger button and a list of actions.
import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { createPortal } from "react-dom";
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
  const [place, setPlace] = useState<CSSProperties>({});
  const ref = useRef<HTMLDivElement>(null);
  const list = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => {
      const at = e.target as Node;
      if (!ref.current?.contains(at) && !list.current?.contains(at)) setOpen(false);
    };
    const esc = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    const gone = () => setOpen(false);
    document.addEventListener("mousedown", close);
    document.addEventListener("keydown", esc);
    window.addEventListener("resize", gone);
    window.addEventListener("scroll", gone, true);
    list.current?.querySelector<HTMLElement>(".k-menu-item")?.focus();
    return () => {
      document.removeEventListener("mousedown", close);
      document.removeEventListener("keydown", esc);
      window.removeEventListener("resize", gone);
      window.removeEventListener("scroll", gone, true);
    };
  }, [open]);
  // The list lives on the body at a fixed place, so a parent with overflow: hidden can't cut it off.
  const toggle = () => {
    const r = ref.current?.getBoundingClientRect();
    if (r) {
      const side = align === "right" ? { right: window.innerWidth - r.right } : { left: r.left };
      const flip = r.bottom + 240 > window.innerHeight && r.top > 240;
      setPlace({ position: "fixed", ...side, ...(flip ? { bottom: window.innerHeight - r.top + 6 } : { top: r.bottom + 6 }) });
    }
    setOpen((o) => !o);
  };
  return (
    <div ref={ref} style={{ position: "relative" }}>
      <IconButton label={label} icon={icon} variant={variant} size={size} onClick={toggle} aria-haspopup="menu" aria-expanded={open} />
      {open && createPortal(
        <div ref={list} className="k-popover" role="menu" style={place}>
          {items.map((it, i) => (
            <button key={i} type="button" role="menuitem" className={"k-menu-item" + (it.danger ? " danger" : "")} disabled={it.disabled}
              onClick={() => { setOpen(false); it.onSelect(); }}>
              {it.icon}{it.label}
            </button>
          ))}
        </div>,
        document.body,
      )}
    </div>
  );
}
