// A small dropdown menu: a trigger button and a list of actions. Right-click anywhere on a row that holds a menu opens
// that menu at the pointer (useRowContextMenu, mounted once in the shell): every row with a "More" menu has the same
// actions on its right click.
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
    // Only scrolling an ancestor moves the trigger away from this fixed popover. Scrolling the list itself or an
    // unrelated pane (including the toaster's automatic scroll) must not dismiss an action under the pointer.
    // A scroll that was already under way when the menu opened (a list still coasting from a touchpad, a click that
    // first scrolled its row into view) is not the player moving away: it does not close the menu.
    const opened = performance.now();
    const scrolled = (e: Event) => {
      if (performance.now() - opened < 300) return;
      if (e.target === window || (e.target instanceof Node && ref.current && e.target.contains(ref.current))) gone();
    };
    document.addEventListener("mousedown", close);
    document.addEventListener("keydown", esc);
    window.addEventListener("resize", gone);
    window.addEventListener("scroll", scrolled, true);
    list.current?.querySelector<HTMLElement>(".k-menu-item")?.focus();
    return () => {
      document.removeEventListener("mousedown", close);
      document.removeEventListener("keydown", esc);
      window.removeEventListener("resize", gone);
      window.removeEventListener("scroll", scrolled, true);
    };
  }, [open]);
  // The list lives on the body at a fixed place, so a parent with overflow: hidden can't cut it off.
  // a right click on the row: the same list at the pointer, kept inside the window
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const at = (e: Event) => {
      const { x, y } = (e as CustomEvent<{ x: number; y: number }>).detail;
      const below = window.innerHeight - y - 16, above = y - 16;
      const flip = below < 240 && above > below;
      const right = x > window.innerWidth - 260;
      setPlace({ position: "fixed", ...(right ? { right: Math.max(8, window.innerWidth - x) } : { left: x }), maxHeight: Math.max(120, flip ? above : below), overflowY: "auto",
        ...(flip ? { bottom: window.innerHeight - y } : { top: y }) });
      setOpen(true);
    };
    el.addEventListener("k-context", at);
    return () => el.removeEventListener("k-context", at);
  }, []);
  const toggle = () => {
    const r = ref.current?.getBoundingClientRect();
    if (r) {
      const side = align === "right" ? { right: window.innerWidth - r.right } : { left: r.left };
      const below = window.innerHeight - r.bottom - 22, above = r.top - 22;
      const flip = below < 240 && above > below;
      // never past the window's edge: a long list scrolls inside
      setPlace({ position: "fixed", ...side, maxHeight: Math.max(120, flip ? above : below), overflowY: "auto",
        ...(flip ? { bottom: window.innerHeight - r.top + 6 } : { top: r.bottom + 6 }) });
    }
    setOpen((o) => !o);
  };
  return (
    <div ref={ref} style={{ position: "relative" }} data-k-menu="">
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

/** Mount once (the desktop shell): a right click on a row (a list item, a table row) that holds a Menu opens that menu
 *  at the pointer instead of the webview's own menu. Fields keep their own (copy, paste). */
export function useRowContextMenu() {
  useEffect(() => {
    const onCtx = (e: MouseEvent) => {
      const t = e.target instanceof Element ? e.target : null;
      if (!t || t.closest("input, textarea, select, [contenteditable], .k-popover")) return;
      const row = t.closest("[data-menu-row], li, tr, [role='row'], [role='listitem'], .k-list-item");
      const trigger = row?.querySelector<HTMLElement>("[data-k-menu]");
      if (!trigger || trigger.querySelector("button")?.disabled) return;
      e.preventDefault();
      trigger.dispatchEvent(new CustomEvent("k-context", { detail: { x: e.clientX, y: e.clientY } }));
    };
    document.addEventListener("contextmenu", onCtx);
    return () => document.removeEventListener("contextmenu", onCtx);
  }, []);
}
