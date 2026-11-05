// Toasts: `toast.ok("Saved")`, `toast.error(err)`, `toast.info(...)`; <Toaster/> lives in the shell.
import { CircleAlert, CircleCheck, Info, X } from "lucide-react";
import type { ReactNode } from "react";
import { create } from "zustand";
import { toKilnError } from "../api/commands";

export type ToastKind = "ok" | "err" | "info";
export interface ToastItem {
  id: number;
  kind: ToastKind;
  title: ReactNode;
  detail?: ReactNode;
}

interface ToastStore {
  items: ToastItem[];
  push: (t: Omit<ToastItem, "id">, ms?: number) => number;
  dismiss: (id: number) => void;
}

let seq = 0;
export const useToasts = create<ToastStore>((set, get) => ({
  items: [],
  push: (t, ms) => {
    const id = ++seq;
    set({ items: [...get().items.slice(-4), { ...t, id }] });
    const life = ms ?? (t.kind === "err" ? 8000 : 3500);
    if (life > 0) setTimeout(() => get().dismiss(id), life);
    return id;
  },
  dismiss: (id) => set({ items: get().items.filter((i) => i.id !== id) }),
}));

/** Imperative toasts. `error` takes anything a command rejects with (KilnError) and shows its message. */
export const toast = {
  ok: (title: ReactNode, detail?: ReactNode) => useToasts.getState().push({ kind: "ok", title, detail }),
  info: (title: ReactNode, detail?: ReactNode) => useToasts.getState().push({ kind: "info", title, detail }),
  error: (e: unknown, title?: ReactNode) => {
    const k = toKilnError(e);
    return useToasts.getState().push({ kind: "err", title: title ?? k.message, detail: title ? k.message : k.code !== "internal" ? k.code : undefined });
  },
};

const ICON: Record<ToastKind, ReactNode> = { ok: <CircleCheck />, err: <CircleAlert />, info: <Info /> };

export function Toaster() {
  const { items, dismiss } = useToasts();
  return (
    <div className="k-toasts" aria-live="polite">
      {items.map((t) => (
        <div key={t.id} className={`k-toast k-toast-${t.kind}`} role={t.kind === "err" ? "alert" : "status"}>
          {ICON[t.kind]}
          <div className="k-toast-body">
            <div>{t.title}</div>
            {t.detail && <div className="k-toast-detail">{t.detail}</div>}
          </div>
          <button type="button" className="k-toast-close" aria-label="Dismiss" onClick={() => dismiss(t.id)}><X size={14} /></button>
        </div>
      ))}
    </div>
  );
}
