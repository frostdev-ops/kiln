// A profile's mods of one kind as a table: flame toggle, name and id, version, row actions (other versions, remove);
// and the add-from-library dialog.
import { MoreHorizontal } from "lucide-react";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import type { LibraryEntry, ModKind, ProfileMod } from "../../api/commands";
import { useLibrary } from "../../api/hooks";
import { KIND_SHORT } from "../../api/kinds";
import { Badge, Button, EmptyState, FlameToggle, Menu, Modal, NameId, Table, WellInput, errorText, type Column, type MenuItem } from "../../ui";

export function libName(lib: LibraryEntry[] | undefined, kind: ModKind, id: string) {
  return lib?.find((l) => l.kind === kind && l.id === id)?.name ?? id;
}

export interface ModListProps {
  kind: ModKind;
  mods: ProfileMod[];
  onToggle: (id: string, on: boolean) => void;
  onRemove: (id: string) => void;
  onAdd: (mods: ProfileMod[]) => void;
  /** Version change: same id, another library version. */
  onVersion: (id: string, version: string, hash: string) => void;
  /** Extra cell per row (e.g. resolver state). */
  extra?: { header: ReactNode; width?: string; render: (m: ProfileMod) => ReactNode };
  emptyHint?: string;
  /** Fill the parent's height (the list scrolls inside). */
  fill?: boolean;
  /** More row menu items (before Remove), e.g. Settings. */
  actions?: (m: ProfileMod) => MenuItem[];
  /** The table's accessible name (default "<kind> mods"). */
  label?: string;
}

export function ModList({ kind, mods, onToggle, onRemove, onAdd, onVersion, extra, emptyHint, fill, actions, label }: ModListProps) {
  const lib = useLibrary(kind);
  const [adding, setAdding] = useState(false);
  useEffect(() => {
    if (adding) void lib.refetch();
  }, [adding, lib.refetch]);
  const columns: Column<ProfileMod>[] = [
    { key: "on", header: <span className="k-sr-only">Lit</span>, width: "40px", render: (m) => <FlameToggle on={m.enabled} label={libName(lib.data, kind, m.id)} onChange={(on) => onToggle(m.id, on)} /> },
    { key: "name", header: "Mod", sort: (a, b) => a.id.localeCompare(b.id), render: (m) => <NameId name={libName(lib.data, kind, m.id)} id={m.id} dim={!m.enabled} /> },
    { key: "version", header: "Version", width: "120px", render: (m) => {
      const e = lib.data?.find((l) => l.id === m.id);
      if (lib.data && !e) return <Badge tone="err" title="Not in the library">Missing</Badge>;
      return <span className="k-mono" title={(e?.versions.length ?? 0) > 1 ? `${e!.versions.length} versions in the library` : undefined}>{m.version}</span>;
    } },
    ...(extra ? [{ key: "extra", header: extra.header, width: extra.width ?? "minmax(0, 1fr)", render: extra.render }] : []),
    { key: "act", header: <span className="k-sr-only">Actions</span>, width: "32px", align: "right", render: (m) => {
      const vs = lib.data?.find((l) => l.id === m.id)?.versions ?? [];
      return (
        <Menu label={`More for ${libName(lib.data, kind, m.id)}`} icon={<MoreHorizontal />} size="s" items={[
          ...vs.filter((v) => v.version !== m.version).map((v) => ({ label: `Use ${v.version}`, onSelect: () => onVersion(m.id, v.version, v.hash) })),
          ...(actions?.(m) ?? []),
          { label: "Remove from this profile", danger: true, onSelect: () => onRemove(m.id) },
        ]} />
      );
    } },
  ];
  return (
    <>
      <Table aria-label={label ?? `${kind} mods`} columns={columns} rows={mods} rowKey={(m) => m.id} fill={fill}
        empty={<EmptyState title={emptyHint ?? "None in this profile."} action={<Button size="s" onClick={() => setAdding(true)}>Add from the library</Button>} />} />
      {mods.length > 0 && <div><Button size="s" onClick={() => setAdding(true)}>Add</Button></div>}
      <AddModsModal open={adding} onClose={() => setAdding(false)} kind={kind} have={mods.map((m) => m.id)} lib={lib.data ?? []}
        onAdd={(ms) => { onAdd(ms); setAdding(false); }} loading={lib.isFetching && !lib.data?.length} error={lib.error ? errorText(lib.error) : undefined} />
    </>
  );
}

/** Why the picker lists nothing. It shows one kind of mod: "The library is empty" was wrong whenever the Library held
 * mods of other kinds only (a player took it for a broken Library). */
function emptyPick(kind: ModKind, inLibrary: number, inProfile: number, filtered: boolean): { title: string; hint?: string } {
  if (inLibrary === 0)
    return { title: `No ${KIND_SHORT[kind]} mods in the library.`, hint: "This list shows one kind of mod. Mods of another kind are added on that kind's tab of the profile. Import more on the Library page." };
  if (filtered) return { title: "Nothing matches the filter." };
  return { title: "Everything is in this profile already.", hint: inProfile ? undefined : "Import more on the Library page." };
}

export function AddModsModal({ open, onClose, kind, have, lib, onAdd, loading, error }: { open: boolean; onClose: () => void; kind: ModKind; have: string[]; lib: LibraryEntry[]; onAdd: (m: ProfileMod[]) => void; loading?: boolean; error?: string }) {
  const [q, setQ] = useState("");
  const [pick, setPick] = useState<Set<string>>(new Set());
  const avail = useMemo(() => lib.filter((l) => !have.includes(l.id) && (!q || (l.name + l.id).toLowerCase().includes(q.toLowerCase()))), [lib, have, q]);
  const done = () => {
    const ms = lib.filter((l) => pick.has(l.id)).map((l) => ({ id: l.id, version: l.versions[0].version, hash: l.versions[0].hash, enabled: true }));
    setPick(new Set());
    onAdd(ms);
  };
  return (
    <Modal open={open} onClose={onClose} title="Add from the library" width={560}
      footer={<><Link to="/library" className="k-link" style={{ marginRight: "auto", fontSize: 16 }}>Import more</Link><Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={!pick.size} onClick={done}>Add {pick.size || ""}</Button></>}>
      <WellInput label="Filter" data-autofocus value={q} onChange={(e) => setQ(e.target.value)} />
      {loading ? <EmptyState title="Loading the library…" /> : error ? <EmptyState title="Could not read the library." hint={error} /> : avail.length === 0 ? <EmptyState {...emptyPick(kind, lib.length, have.length, !!q)} /> : (
        <div className="k-list" style={{ maxHeight: 340, overflow: "auto" }}>
          {avail.map((l) => (
            <label key={l.id} className="k-list-item clickable">
              <input type="checkbox" className="k-check" checked={pick.has(l.id)} onChange={(e) => { const s = new Set(pick); if (e.target.checked) s.add(l.id); else s.delete(l.id); setPick(s); }} />
              <NameId name={l.name} id={l.id} />
              <span className="k-spacer" />
              <span className="k-mono k-muted">{l.versions[0].version}</span>
              {l.versions[0].side && <Badge>{l.versions[0].side}</Badge>}
            </label>
          ))}
        </div>
      )}
    </Modal>
  );
}
