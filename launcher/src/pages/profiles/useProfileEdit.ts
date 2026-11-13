// Shared state for the profile tabs: load a profile and save edits right away (meta.json).
// Edits queue up: each one is applied to the profile the backend last returned (so its generation is current) and
// saved in order; the UI shows that profile plus every edit still waiting. Rapid toggles never conflict.
import { useQueryClient } from "@tanstack/react-query";
import { createContext, useContext, useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import * as api from "../../api/commands";
import type { ModKind, Profile, ProfileMod } from "../../api/commands";
import { qk, useProfile } from "../../api/hooks";
import { toast } from "../../ui";

/** The profile the pages below edit when no route names one (the main list's views edit the loaded profile). */
export const ProfileIdContext = createContext<string | null>(null);

export function useProfileId(): string {
  const loaded = useContext(ProfileIdContext);
  const param = useParams().id;
  return loaded ?? param ?? "";
}

type Edit = (p: Profile) => void;
const clone = (p: Profile): Profile => JSON.parse(JSON.stringify(p));

/** One save queue per profile, shared by every tab and the layout that edit it. */
interface Q { server: Profile | null; queue: Edit[]; busy: boolean; listeners: Set<(busy: boolean) => void>; waiters: ((saved: boolean) => void)[] }
const queues = new Map<string, Q>();
const queueOf = (id: string): Q => {
  let q = queues.get(id);
  if (!q) { q = { server: null, queue: [], busy: false, listeners: new Set(), waiters: [] }; queues.set(id, q); }
  return q;
};

/** Does the save queue take `fetched` (what the backend returned) as the profile its edits apply to? Not an older copy
 *  of the one it holds (an optimistic copy never has a newer generation), but always another profile under the same
 *  id: one deleted and made again (same name, same id) starts at generation 1, and nothing of the deleted one may be
 *  saved over it (a player's "generation 3 on disk, 29 here", Nexus 2026-11-13). `created` tells them apart. */
export function adopts(held: Profile | null, fetched: Profile): boolean {
  return !held || fetched.created !== held.created || fetched.generation >= held.generation;
}

/** A deleted profile: its save queue goes with it, so a profile made again under its id starts from nothing. */
export function forgetProfileEdits(id: string): void {
  const q = queues.get(id);
  if (q && !q.busy && !q.queue.length) queues.delete(id);
}

export function useProfileEdit(id: string) {
  const qc = useQueryClient();
  const q = useProfile(id);
  const key = qk.profile(id);
  const st = queueOf(id);
  const [saving, setSaving] = useState(st.busy);
  useEffect(() => { st.listeners.add(setSaving); return () => { st.listeners.delete(setSaving); }; }, [st]);

  // adopt what the backend says whenever nothing is queued or in flight (an optimistic copy never has a newer generation)
  useEffect(() => {
    if (q.data && !st.busy && st.queue.length === 0 && adopts(st.server, q.data)) st.server = q.data;
  }, [q.data, st]);

  const setBusy = (b: boolean) => { st.busy = b; st.listeners.forEach((f) => f(b)); };
  const show = () => {
    if (!st.server) return;
    const v = clone(st.server);
    for (const e of st.queue) e(v);
    qc.setQueryData(key, v);
  };

  const pump = async () => {
    if (st.busy) return;
    setBusy(true);
    while (st.queue.length && st.server) {
      const next = clone(st.server);
      st.queue[0](next);
      try {
        st.server = await api.profileSave(next);
        st.queue.shift();
      } catch (e) {
        toast.error(e);
        st.queue = [];
        st.server = null;
        setBusy(false);
        st.waiters.splice(0).forEach((w) => w(false));
        await qc.invalidateQueries({ queryKey: key });
        return;
      }
      show();
    }
    setBusy(false);
    st.waiters.splice(0).forEach((w) => w(true));
    qc.invalidateQueries({ queryKey: qk.profiles });
    qc.invalidateQueries({ queryKey: ["profile_resolve", id] });
    // drop-ins and the profile's own mods decide what loads from the game folder
    qc.invalidateQueries({ queryKey: ["found_scan"] });
  };

  /** Applies `edit` (to a copy) and saves it after any edits before it. */
  const update = (edit: Edit) => {
    if (q.data && !st.busy && st.queue.length === 0 && adopts(st.server, q.data)) st.server = q.data;
    if (!st.server) return;
    st.queue.push(edit);
    show();
    void pump();
  };

  const setMods = (kind: ModKind, mods: ProfileMod[]) => update((p) => { p.mods[kind] = mods; });
  // `?? []`: an older backend's profile may lack the other engines' lists
  const toggle = (kind: ModKind, modId: string, enabled: boolean) =>
    update((p) => { p.mods[kind] = (p.mods[kind] ?? []).map((m) => (m.id === modId ? { ...m, enabled } : m)); });
  const remove = (kind: ModKind, modId: string) => update((p) => { p.mods[kind] = (p.mods[kind] ?? []).filter((m) => m.id !== modId); });
  const add = (kind: ModKind, mod: ProfileMod) => update((p) => { p.mods[kind] = [...(p.mods[kind] ?? []).filter((m) => m.id !== mod.id), mod]; });

  /** Resolves once every edit so far is saved (true), or the queue stopped at a failed save (false, toasted). */
  const flush = () => new Promise<boolean>((done) => {
    if (!st.busy && !st.queue.length) done(true);
    else st.waiters.push(done);
  });

  return { profile: q.data, query: q, saving, update, setMods, toggle, remove, add, flush };
}
