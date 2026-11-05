// A profile's mods of one kind: toggles, version pick, remove, and the add-from-library modal.
import { Library, Plus, Search, Trash2 } from "lucide-react";
import { useMemo, useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import type { LibraryEntry, ModKind, ProfileMod } from "../../api/commands";
import { useLibrary } from "../../api/hooks";
import { Badge, Button, EmptyState, IconButton, Modal, Select, Table, TextInput, Toggle, type Column } from "../../ui";

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
}

export function ModList({ kind, mods, onToggle, onRemove, onAdd, onVersion, extra, emptyHint }: ModListProps) {
  const lib = useLibrary(kind);
  const [adding, setAdding] = useState(false);
  const columns: Column<ProfileMod>[] = [
    { key: "on", header: "", width: "48px", render: (m) => <Toggle checked={m.enabled} onChange={(on) => onToggle(m.id, on)} aria-label={`Enable ${m.id}`} /> },
    { key: "name", header: "Mod", width: "minmax(0, 2fr)", sort: (a, b) => a.id.localeCompare(b.id), render: (m) => {
      const e = lib.data?.find((l) => l.id === m.id);
      return (
        <span className="k-stack" style={{ gap: 0 }}>
          <span className="k-item-title" style={{ opacity: m.enabled ? 1 : 0.55 }}>{e?.name ?? m.id}</span>
          <span className="k-dim k-mono" style={{ fontSize: 11 }}>{m.id}</span>
        </span>
      );
    } },
    { key: "version", header: "Version", width: "150px", render: (m) => {
      const e = lib.data?.find((l) => l.id === m.id);
      const vs = e?.versions ?? [];
      if (!e) return <Badge tone="err" title="Not in the library">missing</Badge>;
      if (vs.length < 2) return <span className="k-mono">{m.version}</span>;
      return <Select aria-label={`Version of ${m.id}`} value={m.version} onChange={(v) => onVersion(m.id, v, vs.find((x) => x.version === v)!.hash)}
        options={vs.map((v) => v.version)} style={{ height: 28 }} />;
    } },
    ...(extra ? [{ key: "extra", header: extra.header, width: extra.width ?? "minmax(0, 1.5fr)", render: extra.render }] : []),
    { key: "rm", header: "", width: "40px", align: "right", render: (m) => <IconButton label={`Remove ${m.id}`} size="s" icon={<Trash2 />} onClick={() => onRemove(m.id)} /> },
  ];
  return (
    <>
      <Table aria-label={`${kind} mods`} columns={columns} rows={mods} rowKey={(m) => m.id} rowHeight={52} maxHeight={560}
        empty={<EmptyState icon={<Library />} title="No mods" hint={emptyHint ?? "Add some from the library."} action={<Button icon={<Plus />} onClick={() => setAdding(true)}>Add</Button>} />} />
      {mods.length > 0 && <div style={{ padding: "10px 14px" }}><Button size="s" icon={<Plus />} onClick={() => setAdding(true)}>Add</Button></div>}
      <AddModsModal open={adding} onClose={() => setAdding(false)} kind={kind} have={mods.map((m) => m.id)} lib={lib.data ?? []}
        onAdd={(ms) => { onAdd(ms); setAdding(false); }} />
    </>
  );
}

function AddModsModal({ open, onClose, have, lib, onAdd }: { open: boolean; onClose: () => void; kind: ModKind; have: string[]; lib: LibraryEntry[]; onAdd: (m: ProfileMod[]) => void }) {
  const [q, setQ] = useState("");
  const [pick, setPick] = useState<Set<string>>(new Set());
  const avail = useMemo(() => lib.filter((l) => !have.includes(l.id) && (!q || (l.name + l.id).toLowerCase().includes(q.toLowerCase()))), [lib, have, q]);
  const done = () => {
    const ms = lib.filter((l) => pick.has(l.id)).map((l) => ({ id: l.id, version: l.versions[0].version, hash: l.versions[0].hash, enabled: true }));
    setPick(new Set());
    onAdd(ms);
  };
  return (
    <Modal open={open} onClose={onClose} title="Add from library" width={560}
      footer={<><Link to="/library" className="k-link" style={{ marginRight: "auto", fontSize: 13 }}>Import more</Link><Button variant="ghost" onClick={onClose}>Cancel</Button>
        <Button variant="primary" disabled={!pick.size} onClick={done}>Add {pick.size || ""}</Button></>}>
      <div className="k-input-group"><Search size={15} className="k-dim" /><TextInput data-autofocus placeholder="Filter" value={q} onChange={(e) => setQ(e.target.value)} /></div>
      {avail.length === 0 ? <EmptyState title={lib.length ? "Everything is added" : "Library is empty"} hint={lib.length ? undefined : "Import mods on the Library page."} /> : (
        <div className="k-list" style={{ maxHeight: 360, overflow: "auto", margin: "0 -4px" }}>
          {avail.map((l) => (
            <label key={l.id} className="k-list-item clickable" style={{ padding: "8px 6px" }}>
              <input type="checkbox" checked={pick.has(l.id)} onChange={(e) => { const s = new Set(pick); if (e.target.checked) s.add(l.id); else s.delete(l.id); setPick(s); }}
                style={{ accentColor: "var(--k-ember-500)", width: 16, height: 16 }} />
              <span className="k-stack" style={{ gap: 0, flex: 1 }}>
                <span className="k-item-title">{l.name}</span>
                <span className="k-item-sub k-mono">{l.id} · {l.versions[0].version}</span>
              </span>
              {l.versions[0].side && <Badge>{l.versions[0].side}</Badge>}
            </label>
          ))}
        </div>
      )}
    </Modal>
  );
}
