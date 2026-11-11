// Toasts, bottom left: `toast.ok("Saved")`, `toast.error(err)`, `toast.info(...)`; <Toaster/> lives in the shell.
// A small-caps title, one line at most, and buttons when there is something to do (`{ actions: [{ label: "Undo",
// onClick }] }`). They go after 2.6 s (8 s with buttons); errors stay until closed. A button closes its toast.
import { X } from "lucide-react";
import type { ReactNode } from "react";
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
}
export interface ToastOptions {
  actions?: ToastAction[];
  /** How long it stays (ms; 0: until closed). */
  ms?: number;
}

interface ToastStore {
  items: ToastItem[];
  push: (t: Omit<ToastItem, "id" | "persistent">, ms?: number) => number;
  dismiss: (id: number) => void;
}

let seq = 0;
export const useToasts = create<ToastStore>((set, get) => ({
  items: [],
  push: (t, ms) => {
    const id = ++seq;
    const life = ms ?? (t.kind === "err" ? 0 : t.actions?.length ? 8000 : 2600);
    const items = [...get().items, { ...t, id, persistent: life === 0 }];
    const transient = new Set(items.filter((i) => !i.persistent).slice(-4).map((i) => i.id));
    set({ items: items.filter((i) => i.persistent || transient.has(i.id)) });
    if (life > 0) setTimeout(() => get().dismiss(id), life);
    return id;
  },
  dismiss: (id) => set({ items: get().items.filter((i) => i.id !== id) }),
}));

/** Imperative toasts. `error` takes anything a command rejects with (KilnError) and shows its message. */
export const toast = {
  ok: (title: ReactNode, detail?: ReactNode, o: ToastOptions = {}) => useToasts.getState().push({ kind: "ok", title, detail, actions: o.actions }, o.ms),
  info: (title: ReactNode, detail?: ReactNode, o: ToastOptions = {}) => useToasts.getState().push({ kind: "info", title, detail, actions: o.actions }, o.ms),
  error: (e: unknown, title?: ReactNode) => {
    const k = toKilnError(e);
    return useToasts.getState().push({ kind: "err", title: title ?? k.message, detail: title ? k.message : k.code !== "internal" ? k.code : undefined });
  },
  dismiss: (id: number) => useToasts.getState().dismiss(id),
};

/** Retained mod folders are a safety decision, not a clean removal. Keep the paths and recovery steps visible
 * until dismissed (also available in the uninstall task's structured result). */
export function notifyPlacementCleanup(report: PlacementCleanup | null | undefined) {
  if (!report?.retained.length) return;
  const detail = report.retained.map((r) => `${r.path}: ${r.reason}`).join("; ");
  toast.info("Some mod folders were kept", `${detail}. These mods may still run in the standalone loader. With the game closed, review or move the listed folders to stop them; your settings have not been deleted.`, { ms: 0 });
}

export function Toaster() {
  const { items, dismiss } = useToasts();
  return (
    <div className="k-toasts" aria-live="polite">
      {items.map((t) => (
        <div key={t.id} className={`k-toast k-toast-${t.kind}`} role={t.kind === "err" ? "alert" : "status"}>
          <div className="k-toast-body">
            <div className="k-toast-title">{t.title}</div>
            {t.detail && <div className="k-toast-detail">{t.detail}</div>}
          </div>
          {!!t.actions?.length && (
            <div className="k-toast-actions">
              {t.actions.map((a) => (
                <button key={a.label} type="button" className="k-btn k-btn-s" onClick={() => { dismiss(t.id); a.onClick(); }}>{a.label}</button>
              ))}
            </div>
          )}
          <button type="button" className="k-toast-close" aria-label="Dismiss" title="Dismiss" onClick={() => dismiss(t.id)}><X size={14} /></button>
        </div>
      ))}
    </div>
  );
}
