// SchemaForm: edits a config from its `.schema` snapshot (contracts.md 5.5).
// Controls by entry type: bool → Toggle; int/float → number (slider when min and max are set); enum → Select;
// duration ("5m", "1h30m") → text with a check; list<T> → chips; string → text. Badges: requires restart, synced.
// The form never writes: it calls onChange(path, value) / onReset(path) and the page saves (config_set).
import { Plus, RotateCcw, X } from "lucide-react";
import { useMemo, useState, type ReactNode } from "react";
import type { Json, SchemaSnapshot, SnapshotEntry, ValidationError } from "../api/commands";
import { IconButton } from "./Button";
import { NumberInput, Select, Slider, TextInput, Toggle } from "./Form";
import { Badge } from "./StatusPill";

export interface SchemaFormProps {
  schema: SchemaSnapshot;
  /** The current values (JSON, nested by path). Missing entries show their default. */
  values: Json;
  onChange: (path: string, value: Json) => void;
  /** Back to the default (removes the key). */
  onReset?: (path: string) => void;
  errors?: ValidationError[];
  /** Paths whose new value waits for a restart (running servers). */
  pending?: string[];
  disabled?: boolean;
  /** Substring filter over path and comment. */
  filter?: string;
}

const DURATION = /^(\d+(\.\d+)?(ms|s|m|h|d))+$/;
const UNITS: Record<string, number> = { ms: 0.001, s: 1, m: 60, h: 3600, d: 86400 };

/** Seconds in a Kiln duration ("90s", "1h30m"), or null. */
export function parseDuration(text: string): number | null {
  const t = text.trim();
  if (!DURATION.test(t)) return null;
  let s = 0;
  for (const m of t.matchAll(/(\d+(?:\.\d+)?)(ms|s|m|h|d)/g)) s += Number(m[1]) * UNITS[m[2]];
  return s;
}

/** Value at a dotted path (`a.b`, `list[2]`). */
export function getPath(values: Json, path: string): Json | undefined {
  let cur: Json | undefined = values;
  for (const part of path.split(".")) {
    const [key, ...idx] = part.split("[");
    if (key) cur = cur && typeof cur === "object" && !Array.isArray(cur) ? (cur as Record<string, Json>)[key] : undefined;
    for (const i of idx) cur = Array.isArray(cur) ? cur[Number(i.replace("]", ""))] : undefined;
    if (cur === undefined) return undefined;
  }
  return cur;
}

/** Client-side check mirroring Kiln.Api's rules (the backend validates again). */
export function checkValue(e: SnapshotEntry, v: Json): string | null {
  const t = e.type;
  if (t === "bool") return typeof v === "boolean" ? null : "Not on/off";
  if (t === "int" || t === "float") {
    if (typeof v !== "number" || Number.isNaN(v)) return "Not a number";
    if (t === "int" && !Number.isInteger(v)) return "Whole numbers only";
    if (e.min !== undefined && v < e.min) return `Min ${e.min}`;
    if (e.max !== undefined && v > e.max) return `Max ${e.max}`;
    return null;
  }
  if (t === "enum") return typeof v === "string" && (!e.options || e.options.includes(v)) ? null : "Not an option";
  if (t === "duration") return typeof v === "string" && parseDuration(v) !== null ? null : 'Like "30s", "5m", "1h30m"';
  if (t.startsWith("list<")) return Array.isArray(v) ? null : "Not a list";
  return typeof v === "string" ? null : "Not text";
}

const same = (a: Json | undefined, b: Json | undefined) => JSON.stringify(a) === JSON.stringify(b);
const label = (path: string) => {
  const last = path.split(".").pop() ?? path;
  const words = last.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[_-]+/g, " ").trim();
  return words.charAt(0).toUpperCase() + words.slice(1);
};

export function SchemaForm({ schema, values, onChange, onReset, errors = [], pending = [], disabled, filter }: SchemaFormProps) {
  const groups = useMemo(() => {
    const secs = [...(schema.sections ?? [])].sort((a, b) => b.path.length - a.path.length);
    const f = filter?.trim().toLowerCase();
    const entries = schema.entries.filter((e) => !f || e.path.toLowerCase().includes(f) || (e.comment ?? "").toLowerCase().includes(f));
    const map = new Map<string, SnapshotEntry[]>();
    for (const e of entries) {
      const sec = secs.find((s) => e.path.startsWith(s.path + "."))?.path ?? "";
      map.set(sec, [...(map.get(sec) ?? []), e]);
    }
    const order = ["", ...(schema.sections ?? []).map((s) => s.path)];
    return order.filter((s, i) => map.has(s) && order.indexOf(s) === i).map((s) => ({ section: (schema.sections ?? []).find((x) => x.path === s) ?? null, entries: map.get(s)! }));
  }, [schema, filter]);

  if (groups.length === 0) return <div className="k-muted" style={{ padding: 12 }}>{filter ? "No matching settings." : "No settings."}</div>;
  return (
    <div className="k-sform">
      {groups.map((g) => (
        <div key={g.section?.path ?? "_"} className="k-sform-section">
          {g.section && (
            <div className="k-sform-section-head">
              <span className="k-sform-section-title">{label(g.section.path)}</span>
              {g.section.synced && <Badge tone="info" title="Sent to Kiln clients">synced</Badge>}
              {g.section.comment && <span className="k-hint">{g.section.comment}</span>}
            </div>
          )}
          {g.entries.map((e) => (
            <EntryRow key={e.path} entry={e} value={getPath(values, e.path)} onChange={onChange} onReset={onReset} disabled={disabled}
              error={errors.find((x) => x.path === e.path)?.message} pending={pending.includes(e.path)} />
          ))}
        </div>
      ))}
    </div>
  );
}

interface RowProps {
  entry: SnapshotEntry;
  value: Json | undefined;
  onChange: (path: string, value: Json) => void;
  onReset?: (path: string) => void;
  error?: string;
  pending?: boolean;
  disabled?: boolean;
}

function EntryRow({ entry: e, value, onChange, onReset, error, pending, disabled }: RowProps) {
  const v = value === undefined ? e.default : value;
  const changed = value !== undefined && !same(value, e.default);
  const local = checkValue(e, v);
  const shownError = error ?? (changed ? local : null);
  const id = `sf-${e.path.replace(/[^a-z0-9]/gi, "-")}`;
  const set = (nv: Json) => { if (!same(nv, v)) onChange(e.path, nv); };
  return (
    <div className={"k-sform-row" + (changed ? " changed" : "") + (shownError ? " invalid" : "")}>
      <div className="k-sform-meta">
        <div className="k-field-head">
          <label className="k-label" htmlFor={id}>{label(e.path)}</label>
          {e.requiresRestart && <Badge tone="warn" title="Applies after a restart">restart</Badge>}
          {e.synced && <Badge tone="info" title="Sent to Kiln clients">synced</Badge>}
          {pending && <Badge tone="ember" title="Saved; waits for a restart">pending</Badge>}
        </div>
        {e.comment && <span className="k-hint">{e.comment}</span>}
        <span className="k-sform-key">{e.path}{changed ? ` · default ${fmt(e.default)}` : ""}</span>
      </div>
      <div className="k-sform-control">
        <div className="k-input-group">
          <div style={{ flex: 1, minWidth: 0 }}><Control id={id} entry={e} value={v} set={set} disabled={disabled} invalid={!!shownError} /></div>
          {onReset && changed && <IconButton label="Reset to default" size="s" icon={<RotateCcw />} onClick={() => onReset(e.path)} disabled={disabled} />}
        </div>
        {shownError && <span className="k-error-text">{shownError}</span>}
      </div>
    </div>
  );
}

function fmt(v: Json): string {
  if (typeof v === "string") return `"${v}"`;
  return JSON.stringify(v);
}

function Control({ id, entry: e, value, set, disabled, invalid }: { id: string; entry: SnapshotEntry; value: Json; set: (v: Json) => void; disabled?: boolean; invalid?: boolean }): ReactNode {
  const t = e.type;
  if (t === "bool") return <Toggle id={id} checked={value === true} onChange={set} disabled={disabled} aria-label={e.path} />;
  if (t === "int" || t === "float") {
    const n = typeof value === "number" ? value : Number(value);
    if (e.min !== undefined && e.max !== undefined && e.max > e.min && e.max - e.min <= 100000)
      return <Slider id={id} value={n} min={e.min} max={e.max} integer={t === "int"} onCommit={set} disabled={disabled} aria-label={e.path} />;
    return <NumberInput id={id} value={Number.isNaN(n) ? null : n} integer={t === "int"} min={e.min} max={e.max} onCommit={set} disabled={disabled} invalid={invalid} />;
  }
  if (t === "enum") {
    const opts = e.options ?? [];
    const cur = typeof value === "string" ? value : "";
    return <Select id={id} value={cur} options={opts.includes(cur) ? opts : [cur, ...opts]} onChange={set} disabled={disabled} invalid={invalid} />;
  }
  if (t === "duration") return <CommitText id={id} value={typeof value === "string" ? value : String(value ?? "")} onCommit={set} disabled={disabled} invalid={invalid} placeholder="5m" mono />;
  if (t.startsWith("list<")) return <ListEdit id={id} inner={t.slice(5, -1)} value={Array.isArray(value) ? value : []} onCommit={set} disabled={disabled} />;
  return <CommitText id={id} value={typeof value === "string" ? value : value === null ? "" : String(value)} onCommit={set} disabled={disabled} invalid={invalid} />;
}

function CommitText({ value, onCommit, ...rest }: { id: string; value: string; onCommit: (v: string) => void; disabled?: boolean; invalid?: boolean; placeholder?: string; mono?: boolean }) {
  const [text, setText] = useState(value);
  const [base, setBase] = useState(value);
  if (base !== value) { setBase(value); setText(value); }
  return <TextInput {...rest} value={text} onChange={(ev) => setText(ev.target.value)} onBlur={() => text !== value && onCommit(text)}
    onKeyDown={(ev) => { if (ev.key === "Enter") onCommit(text); if (ev.key === "Escape") setText(value); }} spellCheck={false} />;
}

function parseItem(inner: string, text: string): Json | undefined {
  const s = text.trim();
  if (!s) return undefined;
  if (inner === "int") return /^-?\d+$/.test(s) ? Number(s) : undefined;
  if (inner === "float") return Number.isNaN(Number(s)) ? undefined : Number(s);
  if (inner === "bool") return s === "true" ? true : s === "false" ? false : undefined;
  if (inner === "duration") return parseDuration(s) !== null ? s : undefined;
  return s;
}

function ListEdit({ id, inner, value, onCommit, disabled }: { id: string; inner: string; value: Json[]; onCommit: (v: Json[]) => void; disabled?: boolean }) {
  const [text, setText] = useState("");
  const item = parseItem(inner, text);
  const add = () => { if (item !== undefined) { onCommit([...value, item]); setText(""); } };
  return (
    <div className="k-list-edit">
      {value.length > 0 && (
        <div className="k-chip-row">
          {value.map((x, i) => (
            <span key={i} className="k-chip">{String(x)}
              <button type="button" aria-label={`Remove ${String(x)}`} disabled={disabled} onClick={() => onCommit(value.filter((_, j) => j !== i))}><X size={12} /></button>
            </span>
          ))}
        </div>
      )}
      <div className="k-input-group">
        <TextInput id={id} value={text} onChange={(e) => setText(e.target.value)} placeholder={`Add ${inner}`} disabled={disabled} mono
          invalid={text.trim() !== "" && item === undefined} onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); add(); } }} />
        <IconButton label="Add" icon={<Plus />} variant="secondary" onClick={add} disabled={disabled || item === undefined} />
      </div>
    </div>
  );
}
