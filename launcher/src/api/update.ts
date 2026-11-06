// The launcher's self-update state for the UI: one store fed by `update_status` and `kiln://update`
// (useUpdateFeed, mounted once by the shell's UpdateTray), plus which notices this session already opened.
import { useEffect } from "react";
import { create } from "zustand";
import * as api from "./commands";
import type { UpdateStatus } from "./commands";

interface UpdateStore {
  status: UpdateStatus | null;
  /** The notice under the header is open. */
  open: boolean;
  /** Versions (and "updated:<to>") whose notice opened on its own this session: it opens once, then the word stays. */
  shown: string[];
  setStatus: (s: UpdateStatus) => void;
  setOpen: (open: boolean) => void;
}

/** The notice opens by itself for something new: an update found, an update just installed. */
function autoKey(s: UpdateStatus): string | null {
  if (s.justUpdated) return `updated:${s.justUpdated.to}`;
  if (s.phase === "available" && s.available) return `available:${s.available.version}`;
  return null;
}

export const useUpdateStore = create<UpdateStore>((set, get) => ({
  status: null,
  open: false,
  shown: [],
  setStatus: (s) => {
    const key = autoKey(s);
    if (key && !get().shown.includes(key)) set({ status: s, open: true, shown: [...get().shown, key] });
    else set({ status: s });
  },
  setOpen: (open) => set({ open }),
}));

/** Mount once: the first status, then every `kiln://update`. */
export function useUpdateFeed() {
  useEffect(() => {
    let off: (() => void) | undefined;
    let live = true;
    api.onKilnEvent("kiln://update", (s) => useUpdateStore.getState().setStatus(s)).then((f) => (live ? (off = f) : f()));
    api.updateStatus().then((s) => live && useUpdateStore.getState().setStatus(s)).catch(() => {});
    return () => { live = false; off?.(); };
  }, []);
}

export const useUpdate = () => useUpdateStore((s) => s.status);
