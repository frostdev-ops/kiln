// What a drop (or an import) did, in words a player reads: the names that were added, what was left out and why, and
// what was not needed. The toast stays one short line; "Details" opens the whole list. kiln_manage::library::ImportReport
// has `skipped` (a reason, no final period: what happened and, when the player has to do something, what) and `notes`
// (information: a loader the package carries, which Kiln does not need).
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { create } from "zustand";
import * as api from "../api/commands";
import type { GameInstall, ImportReport, LibraryRef } from "../api/commands";
import { qk, useProfiles } from "../api/hooks";
import { Button, Field, Modal, TextInput, toast } from "../ui";

/** A reason or note as a sentence: one final period (the backend writes none). */
export function sentence(s: string): string {
  const t = s.trim();
  return !t || /[.!?]$/.test(t) ? t : `${t}.`;
}

/** The file or folder name of a path (a zip entry shows as `<zip>/<entry>`). */
export function baseName(path: string): string {
  return path.split(/[\\/]/).filter(Boolean).pop() ?? path;
}

/** "Night Owl", "Night Owl and Dawn Lamp", "3 mods". */
export function names(m: string[]): string {
  return m.length === 1 ? m[0] : m.length === 2 ? `${m[0]} and ${m[1]}` : `${m.length} mods`;
}

export interface ImportSummary {
  /** Everything the import took (new or already in the Library), by name. */
  taken: string[];
  /** Left out, with why. */
  left: { file: string; why: string }[];
  /** Not needed, as sentences. */
  notes: string[];
}

export function summarize(r: ImportReport): ImportSummary {
  const nameOf = (x: LibraryRef) => r.names?.[`${x.kind}/${x.id}`] ?? x.id;
  return {
    taken: [...r.imported, ...r.existing].map(nameOf),
    left: r.skipped.map((s) => ({ file: baseName(s.path), why: sentence(s.reason) })),
    notes: (r.notes ?? []).map((n) => sentence(n.text)),
  };
}

/** One short line for a toast: what was left out or not needed, or undefined when nothing was. Long reasons stay for
 *  the details. */
export function leftOutLine(s: ImportSummary): string | undefined {
  if (s.left.length === 1) {
    const line = `${s.left[0].file}: ${s.left[0].why}`;
    return line.length <= 110 ? line : `${s.left[0].file} was left out.`;
  }
  if (s.left.length > 1) return `${s.left.length} files were left out.`;
  if (s.notes.length === 1) return s.notes[0];
  if (s.notes.length > 1) return `${s.notes.length} things in it are not needed.`;
  return undefined;
}

export interface DetailsData {
  /** "Added to My mods", "Imported": what the first list is. */
  heading: string;
  summary: ImportSummary;
  /** Why the mods are not in a profile, when they are not. */
  libraryOnly?: string | null;
}

export const useImportDetails = create<{ open: DetailsData | null }>(() => ({ open: null }));
export const showImportDetails = (d: DetailsData) => useImportDetails.setState({ open: d });

/** The toast action that opens the details; only when there is something to read. */
export function detailsAction(d: DetailsData): { label: string; onClick: () => void }[] {
  return d.summary.left.length || d.summary.notes.length ? [{ label: "Details", onClick: () => showImportDetails(d) }] : [];
}

/** The details of an import: what was taken, what was left out and why, what was not needed. Mounted once in the shell. */
export function ImportDetails() {
  const d = useImportDetails((s) => s.open);
  const close = () => useImportDetails.setState({ open: null });
  return <ImportDetailsModal data={d} onClose={close} />;
}

export function ImportDetailsModal({ data, onClose }: { data: DetailsData | null; onClose: () => void }) {
  return (
    <Modal open={!!data} onClose={onClose} title="What was added" width={560} footer={<Button onClick={onClose}>OK</Button>}>
      {data && (
        <div className="k-stack">
          <div>
            <div className="k-hint">{data.heading}</div>
            {data.summary.taken.length ? <div>{data.summary.taken.join(", ")}</div> : <div className="k-muted">Nothing.</div>}
          </div>
          {data.libraryOnly && <span className="k-hint">{sentence(data.libraryOnly)}</span>}
          {data.summary.left.length > 0 && (
            <div className="k-stack" style={{ gap: 4 }}>
              <div className="k-hint">Left out</div>
              {data.summary.left.map((l, i) => (
                <div key={l.file + i} className="k-list-item" style={{ flexWrap: "wrap", alignItems: "baseline", rowGap: 0 }}>
                  <span className="k-mono k-ellipsis">{l.file}</span>
                  <span className="k-muted" style={{ flexBasis: "100%" }}>{l.why}</span>
                </div>
              ))}
            </div>
          )}
          {data.summary.notes.length > 0 && (
            <div className="k-stack" style={{ gap: 4 }}>
              <div className="k-hint">Not needed</div>
              {data.summary.notes.map((n) => <span key={n} className="k-muted">{n}</span>)}
            </div>
          )}
        </div>
      )}
    </Modal>
  );
}

// ---- a drop that only reached the Library: which profile should take it? ----

export interface PendingProfile {
  game: GameInstall;
  entries: LibraryRef[];
  /** What the mods are called, for the toast. */
  label: string;
  /** Called with the report of the add that followed (the shell's refresh and Undo toast). */
  onAdded: (r: api.AddReport, game: GameInstall) => void;
}

export const usePickProfile = create<{ pending: PendingProfile | null }>(() => ({ pending: null }));

/** "Which profile?": the launcher's profiles, when the drop could not know, and "New profile…" (an empty one, named
 *  here, that takes the mods). Mounted once in the shell. */
export function PickProfile() {
  const qc = useQueryClient();
  const pending = usePickProfile((s) => s.pending);
  const profiles = useProfiles();
  const [naming, setNaming] = useState(false);
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const close = () => { usePickProfile.setState({ pending: null }); setNaming(false); setName(""); };
  const list = profiles.data ?? [];
  /** Adds the drop to profile `id`; the dialog (and the drop it holds) stays until that worked. */
  const add = async (id: string): Promise<boolean> => {
    if (!pending) return false;
    const p = pending;
    try {
      const r = await api.libraryAddToProfile(p.game.id, id, p.entries);
      close();
      p.onAdded(r, p.game);
      return true;
    } catch (e) { toast.error(e, "Not added"); return false; }
  };
  const pick = async (id: string) => {
    if (busy) return;
    setBusy(true);
    try { await add(id); } finally { setBusy(false); }
  };
  const create = async () => {
    if (!pending || !name.trim() || busy) return;
    setBusy(true);
    try {
      let made: api.Profile;
      try { made = await api.profileCreate(name.trim(), null); } catch (e) { toast.error(e, "No profile made"); return; }
      await qc.invalidateQueries({ queryKey: qk.profiles });
      if (!(await add(made.id))) {
        // the profile exists, the mods are in the Library: back to the list, where it is now, to try again
        setNaming(false);
        setName("");
        toast.info(`${made.name} was made, without ${pending.label}`, `${pending.label} is in your Library. Pick ${made.name} here to add it again, or switch it on in the list.`);
      }
    } finally { setBusy(false); }
  };
  return (
    <Modal open={!!pending} onClose={close} locked={busy} title="Add to which profile?" width={440}
      footer={naming
        ? <><Button variant="ghost" onClick={() => setNaming(false)} disabled={busy}>Back</Button><Button variant="primary" disabled={!name.trim()} loading={busy} onClick={() => void create()}>Make and add</Button></>
        : <Button variant="ghost" onClick={close}>Not now</Button>}>
      {pending && (naming ? (
        <Field label="Name of the new profile" hint={`An empty profile with ${pending.label} in it, on.`} htmlFor="pick-new-profile">
          <TextInput id="pick-new-profile" data-autofocus value={name} onChange={(e) => setName(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Enter") void create(); }} />
        </Field>
      ) : (
        <div className="k-stack">
          <span className="k-hint">{pending.label} is in your list, off. It loads once it is on in the profile you play.</span>
          {profiles.isLoading ? null : list.map((p) => (
            <button key={p.id} type="button" className="k-menu-item" disabled={busy} onClick={() => void pick(p.id)}>
              <span>{p.name}</span>
              {pending.game && p.active.includes(pending.game.id) && <span className="k-muted"> loaded</span>}
            </button>
          ))}
          {!profiles.isLoading && <button type="button" className="k-menu-item" onClick={() => setNaming(true)}>New profile…</button>}
        </div>
      ))}
    </Modal>
  );
}
