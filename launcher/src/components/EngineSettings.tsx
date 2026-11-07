// Settings of another engine's mod in a profile (engine_settings_get/set; docs/kiln/engines.md): ShroudForge's
// `extended.mod.json` settings (JSON Schema with x-kiln-* hints) and Shroudtopia's `shroudtopia.json` (schema derived
// from the values) on the shared SchemaForm, one form per x-kiln-group. Kinds without settings show the backend's note.
// Each change writes one top-level key at once, like the config editor.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import * as api from "../api/commands";
import type { EngineSettings, Json, ModKind, ProfileId, SchemaSnapshot, SnapshotEntry } from "../api/commands";
import { KIND_NAME } from "../api/kinds";
import { Button, EmptyState, Modal, SchemaForm, Skeleton, errorText, getPath, toast } from "../ui";

type Obj = Record<string, Json>;
const isObj = (v: unknown): v is Obj => !!v && typeof v === "object" && !Array.isArray(v);

/** One SchemaForm entry type for a JSON Schema property (`value`: the current value, for untyped properties). */
function entryType(p: Obj, value: Json | undefined): string {
  const t = typeof p.type === "string" ? p.type : value === undefined || value === null ? "string" : Array.isArray(value) ? "array"
    : typeof value === "boolean" ? "boolean" : typeof value === "number" ? (Number.isInteger(value) ? "integer" : "number") : "string";
  if (Array.isArray(p.enum)) return "enum";
  if (t === "boolean") return "bool";
  if (t === "integer") return "int";
  if (t === "number") return "float";
  if (t === "array") {
    const it = isObj(p.items) ? p.items : {};
    return `list<${it.type === "integer" ? "int" : it.type === "number" ? "float" : it.type === "boolean" ? "bool" : "string"}>`;
  }
  return "string";
}

export interface SettingsGroup { title: string | null; schema: SchemaSnapshot }

/** EngineSettings' JSON Schema as SchemaForm snapshots, one per `x-kiln-group` (ungrouped first). Nested objects
 *  (Shroudtopia) become sections with dotted paths. */
export function settingsGroups(s: EngineSettings): SettingsGroup[] {
  const groups = new Map<string | null, { entries: SnapshotEntry[]; sections: SchemaSnapshot["sections"] }>([[null, { entries: [], sections: [] }]]);
  const walk = (schema: Json, prefix: string, inherited: string | null) => {
    const props = isObj(schema) && isObj(schema.properties) ? schema.properties : {};
    for (const [k, raw] of Object.entries(props)) {
      const p = isObj(raw) ? raw : {};
      const path = prefix ? `${prefix}.${k}` : k;
      const group = typeof p["x-kiln-group"] === "string" ? (p["x-kiln-group"] as string) : inherited;
      if (!groups.has(group)) groups.set(group, { entries: [], sections: [] });
      const g = groups.get(group)!;
      const value = getPath(s.values, path);
      if (p.type === "object" || (!p.type && isObj(p.properties))) {
        g.sections.push({ path, comment: typeof p.title === "string" ? p.title : null, synced: false });
        walk(p, path, group);
        continue;
      }
      const words = [typeof p.title === "string" && p.title !== k ? p.title : null, typeof p.description === "string" ? p.description : null].filter(Boolean);
      g.entries.push({
        path, type: entryType(p, value), comment: words.length ? words.join(". ") : null,
        default: p.default !== undefined ? p.default : value !== undefined ? value : null,
        requiresRestart: p["x-kiln-restart"] === true, synced: false,
        min: typeof p.minimum === "number" ? p.minimum : undefined, max: typeof p.maximum === "number" ? p.maximum : undefined,
        options: Array.isArray(p.enum) ? p.enum.map(String) : undefined,
      });
    }
  };
  walk(s.schema, "", null);
  return [...groups.entries()].filter(([, g]) => g.entries.length).map(([title, g]) => ({
    title,
    schema: { format: 1, source: "kiln", file: s.file ?? s.format, valueFormat: "jsonc", id: s.id, mod: s.id, modVersion: null, kind: s.kind, written: null, kiln: null,
      sections: g.sections, entries: g.entries },
  }));
}

/** The top-level key and its new value for a change at a dotted path (set_setting writes top-level keys). */
export function topLevelChange(values: Json, path: string, value: Json): { key: string; value: Json } {
  const [key, ...rest] = path.split(".");
  if (!rest.length) return { key, value };
  const root: Obj = isObj(values) && isObj(values[key]) ? JSON.parse(JSON.stringify(values[key])) : {};
  let cur = root;
  for (const part of rest.slice(0, -1)) cur = (isObj(cur[part]) ? cur[part] : (cur[part] = {})) as Obj;
  cur[rest[rest.length - 1]] = value;
  return { key, value: root };
}

export const engineSettingsKey = (kind: ModKind, id: string, profileId: ProfileId, gameId: string | null) => ["engine_settings", kind, id, profileId, gameId] as const;

export function EngineSettingsForm({ kind, id, profileId, gameId }: { kind: ModKind; id: string; profileId: ProfileId; gameId: string | null }) {
  const qc = useQueryClient();
  const key = engineSettingsKey(kind, id, profileId, gameId);
  const q = useQuery({ queryKey: key, queryFn: () => api.engineSettingsGet(kind, id, profileId, gameId) });
  const set = useMutation({
    mutationFn: ({ k, v }: { k: string; v: Json }) => api.engineSettingsSet(kind, id, profileId, gameId, k, v),
    onSuccess: (d) => qc.setQueryData(key, d),
    onError: (e) => toast.error(e, "Not saved"),
  });
  if (q.isLoading) return <Skeleton lines={4} />;
  if (q.error) return <EmptyState title="The settings are not readable" hint={errorText(q.error)} />;
  const s = q.data!;
  const groups = s.format === "none" ? [] : settingsGroups(s);
  return (
    <div className="k-stack" style={{ gap: 14 }} data-engine-settings={`${kind}/${id}`}>
      {s.file && <span className="k-hint k-mono k-ellipsis" title={s.file}>{s.format}: {s.file}</span>}
      {!groups.length ? <EmptyState title={s.note ? s.note.charAt(0).toUpperCase() + s.note.slice(1) + "." : "No settings."} /> : groups.map((g) => (
        <div key={g.title ?? "_"} className="k-stack" style={{ gap: 6 }}>
          {g.title && <span className="k-label" style={{ fontSize: 16 }}>{g.title}</span>}
          <SchemaForm schema={g.schema} values={s.values} disabled={set.isPending}
            onChange={(path, value) => { const c = topLevelChange(s.values, path, value); set.mutate({ k: c.key, v: c.value }); }} />
        </div>
      ))}
    </div>
  );
}

/** The settings dialog of one profile entry. */
export function EngineSettingsModal({ target, onClose, profileId, gameId }: {
  target: { kind: ModKind; id: string; name: string } | null; onClose: () => void; profileId: ProfileId; gameId: string | null;
}) {
  return (
    <Modal open={!!target} onClose={onClose} title={target ? `${target.name} settings` : "Settings"} width={640} footer={<Button onClick={onClose}>Done</Button>}>
      {target && (
        <>
          <span className="k-hint">{KIND_NAME[target.kind]} mod. Changes are written right away; the game reads them at its next start.</span>
          <EngineSettingsForm kind={target.kind} id={target.id} profileId={profileId} gameId={gameId} />
        </>
      )}
    </Modal>
  );
}
