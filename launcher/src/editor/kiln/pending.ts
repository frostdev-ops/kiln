// Whether the save editor holds changes that are not written yet, and for which character: a tiny store with no editor
// data behind it, so the shells (the Deck's Quit dialog) can ask without loading the editor's chunk. session.js keeps it.
import { create } from "zustand";

export const usePendingEdits = create<{ dirty: boolean; name: string | null }>(() => ({ dirty: false, name: null }));
