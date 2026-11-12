// A profile's mods of one kind as a table (the plugins on Native and DLLs): flame toggle, name and id, version, and the
// row's other Library versions. Adding and removing are the main list's (pages/mods/MainList.tsx).
import { MoreHorizontal } from "lucide-react";
import type { ReactNode } from "react";
import type { LibraryEntry, ModKind, ProfileMod } from "../../api/commands";
import { useLibrary } from "../../api/hooks";
import { Badge, EmptyState, FlameToggle, Menu, NameId, Table, type Column, type MenuItem } from "../../ui";

export function libName(lib: LibraryEntry[] | undefined, kind: ModKind, id: string) {
  return lib?.find((l) => l.kind === kind && l.id === id)?.name ?? id;
}

export interface ModListProps {
  kind: ModKind;
  mods: ProfileMod[];
  onToggle: (id: string, on: boolean) => void;
  /** Version change: same id, another library version. */
  onVersion: (id: string, version: string, hash: string) => void;
  /** Extra cell per row (e.g. resolver state). */
  extra?: { header: ReactNode; width?: string; render: (m: ProfileMod) => ReactNode };
  emptyHint?: string;
  /** Fill the parent's height (the list scrolls inside). */
  fill?: boolean;
  /** More row menu items, e.g. Settings. */
  actions?: (m: ProfileMod) => MenuItem[];
  /** The table's accessible name (default "<kind> mods"). */
  label?: string;
}

export function ModList({ kind, mods, onToggle, onVersion, extra, emptyHint, fill, actions, label }: ModListProps) {
  const lib = useLibrary(kind);
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
      const items = [
        ...vs.filter((v) => v.version !== m.version).map((v) => ({ label: `Use ${v.version}`, onSelect: () => onVersion(m.id, v.version, v.hash) })),
        ...(actions?.(m) ?? []),
      ];
      return items.length ? <Menu label={`More for ${libName(lib.data, kind, m.id)}`} icon={<MoreHorizontal />} size="s" items={items} /> : null;
    } },
  ];
  return <Table aria-label={label ?? `${kind} mods`} columns={columns} rows={mods} rowKey={(m) => m.id} fill={fill} empty={<EmptyState title={emptyHint ?? "None on your list."} />} />;
}
