// Settings of another engine's mod in a profile (engine_settings_get/set; docs/kiln/engines.md): ShroudForge's
// `extended.mod.json` settings (JSON Schema with x-kiln-* hints) and Shroudtopia's `shroudtopia.json` (schema derived
// from the values) on the shared SchemaForm, one form per x-kiln-group. Kinds without settings show the backend's note.
// Each change writes one top-level key at once, like the config editor.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import * as api from "../api/commands";
import type { EngineSettings, Json, ModKind, ProfileId, SchemaSnapshot, SnapshotEntry } from "../api/commands";
import { KIND_NAME } from "../api/kinds";
import { useActiveProfile, useGameStatus } from "../api/hooks";
import { Banner, Button, EmptyState, Field, Modal, SchemaForm, Skeleton, TextArea, TextInput, Toggle, errorText, toast } from "../ui";

type Obj = Record<string, Json>;
const isObj = (v: unknown): v is Obj => !!v && typeof v === "object" && !Array.isArray(v);

/** One SchemaForm entry type for a JSON Schema property (`value`: the current value, for untyped properties). */
function entryType(p: Obj, value: Json | undefined): string {
  const t = typeof p.type === "string" ? p.type : value === undefined || value === null ? "string" : Array.isArray(value) ? "array"
    : typeof value === "boolean" ? "boolean" : typeof value === "number" ? (Number.isInteger(value) ? "integer" : "number") : "string";
  // SchemaForm's enum control emits strings. Numeric SF options must stay numbers for the engine's validator.
  if (Array.isArray(p.enum) && t !== "integer" && t !== "number") return "enum";
  if (t === "boolean") return "bool";
  if (t === "integer") return "int";
  if (t === "number") return "float";
  if (t === "array") {
    const it = isObj(p.items) ? p.items : {};
    return `list<${it.type === "integer" ? "int" : it.type === "number" ? "float" : it.type === "boolean" ? "bool" : "string"}>`;
  }
  return "string";
}

export interface SettingsGroup { title: string | null; schema: SchemaSnapshot; paths: string[][] }

/** EngineSettings' JSON Schema as SchemaForm snapshots, one per `x-kiln-group` (ungrouped first). Nested objects
 *  (Shroudtopia) become sections with dotted paths. */
export function settingsGroups(s: EngineSettings): SettingsGroup[] {
  const groups = new Map<string | null, { entries: SnapshotEntry[]; sections: SchemaSnapshot["sections"]; paths: string[][] }>([[null, { entries: [], sections: [], paths: [] }]]);
  const walk = (schema: Json, prefix: string, inherited: string | null, parent: string[]) => {
    const props = isObj(schema) && isObj(schema.properties) ? schema.properties : {};
    for (const [k, raw] of Object.entries(props)) {
      const p = isObj(raw) ? raw : {};
      const path = prefix ? `${prefix}.${k}` : k;
      const parts = [...parent, k];
      const group = typeof p["x-kiln-group"] === "string" ? (p["x-kiln-group"] as string) : inherited;
      if (!groups.has(group)) groups.set(group, { entries: [], sections: [], paths: [] });
      const g = groups.get(group)!;
      const value = parts.reduce<Json | undefined>((v, key) => isObj(v) && Object.hasOwn(v, key) ? v[key] : undefined, s.values);
      if (p.type === "object" || (!p.type && isObj(p.properties))) {
        g.sections.push({ path, comment: typeof p.title === "string" ? p.title : null, synced: false });
        walk(p, path, group, parts);
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
      g.paths.push(parts);
    }
  };
  walk(s.schema, "", null, []);
  return [...groups.entries()].filter(([, g]) => g.entries.length).map(([title, g]) => ({
    title,
    paths: g.paths,
    schema: { format: 1, source: "kiln", file: s.file ?? s.format, valueFormat: "jsonc", id: s.id, mod: s.id, modVersion: null, kind: s.kind, written: null, kiln: null,
      sections: g.sections, entries: g.entries },
  }));
}

/** The top-level key and its new value for a change at a dotted path (set_setting writes top-level keys). */
export function topLevelChange(values: Json, path: string | string[], value: Json): { key: string; value: Json } {
  const [key, ...rest] = Array.isArray(path) ? path : path.split(".");
  if (!rest.length) return { key, value };
  const root: Obj = isObj(values) && Object.hasOwn(values, key) && isObj(values[key]) ? JSON.parse(JSON.stringify(values[key])) : {};
  let cur = root;
  for (const part of rest.slice(0, -1)) {
    if (!Object.hasOwn(cur, part) || !isObj(cur[part])) Object.defineProperty(cur, part, { value: {}, enumerable: true, writable: true, configurable: true });
    cur = cur[part] as Obj;
  }
  Object.defineProperty(cur, rest[rest.length - 1], { value, enumerable: true, writable: true, configurable: true });
  return { key, value: root };
}

/** SchemaForm uses dotted paths. Literal punctuation in a JSON key needs an exact-key editor instead. */
function LiteralSetting({ entry, parts, value, disabled, onChange }: {
  entry: SnapshotEntry; parts: string[]; value: Json; disabled: boolean; onChange: (value: Json) => void;
}) {
  const string = entry.type === "string" || entry.type === "enum";
  const format = (v: Json) => string && typeof v === "string" ? v : JSON.stringify(v);
  const [text, setText] = useState(format(value));
  const [base, setBase] = useState(format(value));
  if (base !== format(value)) { setBase(format(value)); setText(format(value)); }
  const commit = () => {
    try {
      const next: Json = string ? text : JSON.parse(text);
      if (JSON.stringify(next) !== JSON.stringify(value)) onChange(next);
    } catch { toast.error("Enter a valid JSON value.", "Not saved"); }
  };
  const label = parts.map((p) => JSON.stringify(p)).join(" / ");
  return <Field label={label} hint={entry.comment ?? undefined}>
    {entry.type === "bool" ? <Toggle checked={value === true} disabled={disabled} onChange={onChange} aria-label={label} />
      : entry.type.startsWith("list<") ? <TextArea value={text} disabled={disabled} onChange={(e) => setText(e.target.value)} onBlur={commit} aria-label={label} />
        : <TextInput value={text} disabled={disabled} onChange={(e) => setText(e.target.value)} onBlur={commit}
            onKeyDown={(e) => { if (e.key === "Enter") commit(); }} aria-label={label} />}
  </Field>;
}

export const engineSettingsKey = (kind: ModKind, id: string, profileId: ProfileId, gameId: string | null) => ["engine_settings", kind, id, profileId, gameId] as const;

export function EngineSettingsForm({ kind, id, profileId, gameId }: { kind: ModKind; id: string; profileId: ProfileId; gameId: string | null }) {
  const qc = useQueryClient();
  const status = useGameStatus(gameId);
  const active = useActiveProfile(gameId);
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
  const blocked = gameId && (!status.data || status.data.running) ? "Close the game before editing engine settings."
    : kind === "shroudforge" && (!gameId || active.data?.profileId !== profileId) ? "Activate this profile in the selected game to edit ShroudForge settings." : null;
  return (
    <div className="k-stack" style={{ gap: 14 }} data-engine-settings={`${kind}/${id}`}>
      {blocked && groups.length > 0 && <Banner tone="info" title={blocked} />}
      {s.ownFolder && <span className="k-hint" data-own-folder="">This is your own folder, not a copy Kiln placed: Kiln changes only the setting you change here.</span>}
      {s.file && <span className="k-hint k-mono k-ellipsis" title={s.file}>{s.format}: {s.file}</span>}
      {!groups.length ? <EmptyState title={s.note ? s.note.charAt(0).toUpperCase() + s.note.slice(1) + "." : "No settings."} /> : groups.map((g) => (
        <div key={g.title ?? "_"} className="k-stack" style={{ gap: 6 }}>
          {g.title && <span className="k-label" style={{ fontSize: 16 }}>{g.title}</span>}
          {g.paths.some((parts) => parts.some((p) => /[.\[\]]/.test(p))) ? g.schema.entries.map((entry, index) => {
            const parts = g.paths[index];
            const value = parts.reduce<Json | undefined>((v, k) => isObj(v) && Object.hasOwn(v, k) ? v[k] : undefined, s.values) ?? entry.default;
            const change = (value: Json) => { const c = topLevelChange(s.values, parts, value); set.mutate({ k: c.key, v: c.value }); };
            return <LiteralSetting key={JSON.stringify(parts)} entry={entry} parts={parts} value={value} disabled={set.isPending || !!blocked} onChange={change} />;
          }) : <SchemaForm schema={g.schema} values={s.values} disabled={set.isPending || !!blocked}
            onChange={(path, value) => { const index = g.schema.entries.findIndex((e) => e.path === path); const c = topLevelChange(s.values, g.paths[index], value); set.mutate({ k: c.key, v: c.value }); }} />}
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
