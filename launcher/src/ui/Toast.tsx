// Toasts, bottom left: `toast.ok("Saved")`, `toast.error(err)`, `toast.info(...)`; <Toaster/> lives in the shell.
// A small-caps title, one line at most, and buttons when there is something to do (`{ actions: [{ label: "Undo",
// onClick }] }`). They go after 2.6 s (8 s with buttons); errors stay until closed. A button closes its toast.
// Notices that stay (errors, `ms: 0`): the same one again shows once, as the newest; past eight, the oldest fold into
// one "N earlier notices" item that keeps all of their text. The newest is scrolled into view. A notice never takes
// the screen: a long detail shows its first lines and "Show all" (the stack itself is capped too, and scrolls).
// `close` is the player closing a notice (its close button or one of its buttons; the Deck's notice dialog): its
// `onClosed` runs then, with those of the notices it took in (the same text pushed again, the ones folded into it).
// Rendering a notice, or the window going away, never runs it.
import { X } from "lucide-react";
import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { create } from "zustand";
import { toKilnError, type PlacementCleanup } from "../api/commands";

export type ToastKind = "ok" | "err" | "info";
export interface ToastAction {
  label: string;
  onClick: () => void;
}
export interface ToastItem {
  id: number;
  kind: ToastKind;
  title: ReactNode;
  detail?: ReactNode;
  actions?: ToastAction[];
  /** Persistent recovery instructions and errors are never evicted by later notifications. */
  persistent?: boolean;
  /** A persistent notice pushed again with the same text shows once, as the newest: how many times it came. */
  count?: number;
  /** The oldest persistent notices beyond MAX_PERSISTENT, folded into this one item, oldest first: its detail holds
   *  every one's title and detail, so nothing a notice said is lost. */
  folded?: ToastItem[];
  /** Runs when the player closes the notice (`close`): shell/notices.ts remembers its sentences as seen then. A notice
   *  that took in others (the same text again, a fold) runs theirs too. */
  onClosed?: () => void;
  /** What the notice says, as keys (shell/notices.ts: game and sentence): `openLines` tells what is open now. */
  lines?: string[];
}
export interface ToastOptions {
  actions?: ToastAction[];
  /** How long it stays (ms; 0: until closed). */
  ms?: number;
  /** Runs when the player closes it (see ToastItem.onClosed). */
  onClosed?: () => void;
  /** Its keys (see ToastItem.lines). */
  lines?: string[];
}

interface ToastStore {
  items: ToastItem[];
  push: (t: Omit<ToastItem, "id" | "persistent" | "count" | "folded">, ms?: number) => number;
  dismiss: (id: number) => void;
  close: (id: number) => void;
}

/** Persistent notices shown one by one; the older ones fold into one "N earlier notices" item. */
const MAX_PERSISTENT = 8;

/** A title or detail as text, when it is plain text (only those can be compared; anything else is never "the same"). */
const plain = (n: ReactNode): string | null => (n == null || n === false ? "" : typeof n === "string" || typeof n === "number" ? String(n) : null);
const same = (a: Pick<ToastItem, "kind" | "title" | "detail">, b: Pick<ToastItem, "kind" | "title" | "detail">) => {
  const t = plain(a.title);
  const d = plain(a.detail);
  return a.kind === b.kind && t !== null && d !== null && t === plain(b.title) && d === plain(b.detail);
};

const times = (i: ToastItem) => ((i.count ?? 1) > 1 ? ` (${i.count} times)` : "");

/** The folded notices, each with its title, detail and buttons. */
function Earlier({ items }: { items: ToastItem[] }) {
  return (
    <>
      {items.map((i) => (
        <div key={i.id} className="k-toast-earlier" data-kind={i.kind}>
          <span className="k-toast-earlier-title">{i.title}{times(i)}</span>
          {!!i.detail && <> {i.detail}</>}
          {!!i.actions?.length && (
            <span className="k-toast-actions">
              {i.actions.map((a) => (
                <button key={a.label} type="button" className="k-btn k-btn-s" onClick={() => { useToasts.getState().close(i.id); a.onClick(); }}>{a.label}</button>
              ))}
            </span>
          )}
        </div>
      ))}
    </>
  );
}

const fold = (folded: ToastItem[], id: number): ToastItem => ({
  id, kind: folded.some((i) => i.kind === "err") ? "err" : "info", persistent: true, folded,
  title: `${folded.length} earlier notice${folded.length === 1 ? "" : "s"}`, detail: <Earlier items={folded} />,
});

/** `items` without the notices `drop` matches, top level or folded (a fold left empty goes); `found`: what went. */
function without(items: ToastItem[], drop: (i: ToastItem) => boolean, found: ToastItem[] = []): ToastItem[] {
  return items.flatMap((i) => {
    if (!i.folded) {
      if (!drop(i)) return [i];
      found.push(i);
      return [];
    }
    if (drop(i)) { found.push(i); return []; }
    const rest = i.folded.filter((f) => {
      if (!drop(f)) return true;
      found.push(f);
      return false;
    });
    return rest.length === i.folded.length ? [i] : rest.length ? [fold(rest, i.id)] : [];
  });
}

let seq = 0;
export const useToasts = create<ToastStore>((set, get) => ({
  items: [],
  push: (t, ms) => {
    const id = ++seq;
    const life = ms ?? (t.kind === "err" ? 0 : t.actions?.length ? 8000 : 2600);
    const persistent = life === 0;
    // the same persistent notice again: one copy, the newest, that counts how often it came
    const repeats: ToastItem[] = [];
    let items = persistent ? without(get().items, (i) => !!i.persistent && !i.folded && same(i, t), repeats) : get().items;
    const count = repeats.reduce((n, i) => n + (i.count ?? 1), 1);
    // the new copy takes in what the ones it replaces would have done when closed, and what they said
    const closers = [...repeats.map((r) => r.onClosed), t.onClosed].filter((f): f is () => void => !!f);
    const lines = [...new Set([...repeats.flatMap((r) => r.lines ?? []), ...(t.lines ?? [])])];
    const merged = { onClosed: closers.length ? () => closers.forEach((f) => f()) : undefined, lines: lines.length ? lines : undefined };
    items = [...items, { ...t, ...merged, id, persistent, ...(count > 1 ? { count } : {}) }];
    // at most MAX_PERSISTENT persistent entries: the oldest fold into one, whose text keeps all of theirs
    const old = items.find((i) => i.folded);
    const loose = items.filter((i) => i.persistent && !i.folded);
    if (loose.length + (old ? 1 : 0) > MAX_PERSISTENT) {
      const moving = new Set(loose.slice(0, loose.length - (MAX_PERSISTENT - 1)).map((i) => i.id));
      const folded = fold([...(old?.folded ?? []), ...loose.filter((i) => moving.has(i.id))], old?.id ?? ++seq);
      items = [folded, ...items.filter((i) => i !== old && !moving.has(i.id))];
    }
    const transient = new Set(items.filter((i) => !i.persistent).slice(-4).map((i) => i.id));
    set({ items: items.filter((i) => i.persistent || transient.has(i.id)) });
    if (life > 0) setTimeout(() => get().dismiss(id), life);
    return id;
  },
  dismiss: (id) => set({ items: without(get().items, (i) => i.id === id) }),
  close: (id) => {
    const found: ToastItem[] = [];
    set({ items: without(get().items, (i) => i.id === id, found) });
    for (const f of found) for (const t of [f, ...(f.folded ?? [])]) t.onClosed?.();
  },
}));

/** The keys (ToastItem.lines) of every notice open now, folded ones too. */
export function openLines(): Set<string> {
  return new Set(useToasts.getState().items.flatMap((i) => [i, ...(i.folded ?? [])]).flatMap((t) => t.lines ?? []));
}

/** Imperative toasts. `error` takes anything a command rejects with (KilnError) and shows its message. */
export const toast = {
  ok: (title: ReactNode, detail?: ReactNode, o: ToastOptions = {}) => useToasts.getState().push({ kind: "ok", title, detail, actions: o.actions, onClosed: o.onClosed, lines: o.lines }, o.ms),
  info: (title: ReactNode, detail?: ReactNode, o: ToastOptions = {}) => useToasts.getState().push({ kind: "info", title, detail, actions: o.actions, onClosed: o.onClosed, lines: o.lines }, o.ms),
  error: (e: unknown, title?: ReactNode) => {
    const k = toKilnError(e);
    return useToasts.getState().push({ kind: "err", title: title ?? k.message, detail: title ? k.message : k.code !== "internal" ? k.code : undefined });
  },
  dismiss: (id: number) => useToasts.getState().dismiss(id),
};

/** Retained mod folders are a safety decision, not a clean removal. Keep the paths and recovery steps visible
 * until dismissed (also available in the uninstall task's structured result). */
export function notifyPlacementCleanup(report: PlacementCleanup | null | undefined) {
  // what the mods wrote into the copies that went is kept, and the player is told where (the backend's sentences)
  if (report?.keptFiles?.length) toast.info("Files your mods wrote were kept", report.keptFiles.join(" "), { ms: 0 });
  if (!report?.retained.length) return;
  const detail = report.retained.map((r) => `${r.path}: ${r.reason}`).join("; ");
  toast.info("Some mod folders were kept", `${detail}. These mods may still run through Kiln or another loader. With the game closed, review or move the listed folders to stop them; your settings have not been deleted.`, { ms: 0 });
}

/** A toast's detail: its first lines (ui.css caps the height), and "Show all" when there is more, which opens all
 *  of it (scrolling inside the toast when even that is long). */
function Detail({ children }: { children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);
  const [long, setLong] = useState(false);
  const [open, setOpen] = useState(false);
  useLayoutEffect(() => {
    const el = ref.current;
    if (el && !open) setLong(el.scrollHeight > el.clientHeight + 1);
  }, [children, open]);
  return (
    <>
      <div ref={ref} className="k-toast-detail" data-open={open ? "" : undefined} data-cut={long && !open ? "" : undefined}>{children}</div>
      {(long || open) && (
        <button type="button" className="k-link k-toast-more" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? "Show less" : "Show all"}</button>
      )}
    </>
  );
}

export function Toaster() {
  const { items, close } = useToasts();
  // the newest is at the bottom: keep it in view when the stack is taller than the window
  const box = useRef<HTMLDivElement>(null);
  const newest = items.reduce((n, i) => Math.max(n, i.id), 0);
  useLayoutEffect(() => {
    if (box.current) box.current.scrollTop = box.current.scrollHeight;
  }, [newest]);
  return (
    <div ref={box} className="k-toasts" aria-live="polite">
      {items.map((t) => (
        <div key={t.id} className={`k-toast k-toast-${t.kind}`} role={t.kind === "err" ? "alert" : "status"}>
          <div className="k-toast-body">
            <div className="k-toast-title">{t.title}{times(t)}</div>
            {t.detail && <Detail>{t.detail}</Detail>}
          </div>
          {!!t.actions?.length && (
            <div className="k-toast-actions">
              {t.actions.map((a) => (
                <button key={a.label} type="button" className="k-btn k-btn-s" onClick={() => { close(t.id); a.onClick(); }}>{a.label}</button>
              ))}
            </div>
          )}
          <button type="button" className="k-toast-close" aria-label="Dismiss" title="Dismiss" onClick={() => close(t.id)}><X size={14} /></button>
        </div>
      ))}
    </div>
  );
}
