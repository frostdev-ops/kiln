// SchemaForm: edits a config from its `.schema` snapshot (contracts.md 5.5).
// Controls by entry type: bool → Toggle; int/float → number (slider when min and max are set); enum → Select;
// duration ("5m", "1h30m") → text with a check; list<T> → chips; string → text. Words, not badges: Restart, Synced,
// Pending. A changed row has a gold diamond before its label and shows the default with Reset; an invalid row shows an
// iron-red message under the field. One column, max 720 wide; section heads in small caps. A row's label is its key in
// words ("chatHistory": "Chat History") unless the card gives its own (`labels`).
// The form never writes: it calls onChange(path, value) / onReset(path) and the page saves (config_set).
import { Plus, X } from "lucide-react";
import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import type { Json, SchemaSnapshot, SnapshotEntry, ValidationError } from "../api/shared";
import { rich, t as tr, useT } from "../i18n";
import { IconButton } from "./Button";
import { NumberInput, Select, Slider, TextInput, Toggle } from "./Form";

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
  /** Field labels by path, for a card whose key names do not read as words ("graphics.fpsCap": "Frame cap"); an entry
   *  without one is named from its key. */
  labels?: Record<string, string>;
  /** A long comment shows its first clause, with More for the rest (Settings: one line of help per row). */
  clamp?: boolean;
}

const DURATION = /^(\d+(\.\d+)?(ms|s|m|h|d))+$/;
const UNITS: Record<string, number> = { ms: 0.001, s: 1, m: 60, h: 3600, d: 86400 };

/** Seconds in a Kiln duration ("90s", "1h30m"), or null. */
export function parseDuration(text: string): number | null {
  const str = text.trim();
  if (!DURATION.test(str)) return null;
  let s = 0;
  for (const m of str.matchAll(/(\d+(?:\.\d+)?)(ms|s|m|h|d)/g)) s += Number(m[1]) * UNITS[m[2]];
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

/** The entry's slider step ([Range(min, max, step)]) when it suits the entry, else undefined (the slider keeps its
 * own increment). Mirrors Kiln.Api ConfigSchema.SliderStep: int/float with both bounds, a positive finite step no
 * larger than max - min that changes a value at either end, whole step and min for int. The range input steps from
 * min, so the slider gives min + n * step; the number box and checkValue never round to it. */
export function sliderStep(e: SnapshotEntry): number | undefined {
  const s: unknown = e.step;
  const { min, max } = e;
  if ((e.type !== "int" && e.type !== "float") || typeof s !== "number" || typeof min !== "number" || typeof max !== "number") return undefined;
  if (!Number.isFinite(s) || !Number.isFinite(min) || !Number.isFinite(max) || s <= 0 || max <= min || s > max - min) return undefined;
  if (min + s === min || max - s === max) return undefined;
  if (e.type === "int" && (!Number.isInteger(s) || !Number.isInteger(min))) return undefined;
  return s;
}

/** The bounds a value is checked against: min and max unless that side is soft ([Range(..., SoftMin/SoftMax = true)]:
 * the side only ends the slider). Mirrors Kiln.Api ConfigNode.HardMin / HardMax. */
export function hardRange(e: SnapshotEntry): { min?: number; max?: number } {
  return { min: e.softMin === true ? undefined : e.min, max: e.softMax === true ? undefined : e.max };
}

/** Client-side check mirroring Kiln.Api's rules (the backend validates again). */
export function checkValue(e: SnapshotEntry, v: Json): string | null {
  const ty = e.type;
  if (ty === "bool") return typeof v === "boolean" ? null : tr("ui.schemaForm.notOnOff");
  if (ty === "int" || ty === "float") {
    if (typeof v !== "number" || Number.isNaN(v)) return tr("ui.schemaForm.notNumber");
    if (ty === "int" && !Number.isInteger(v)) return tr("ui.schemaForm.wholeOnly");
    const { min, max } = hardRange(e);
    if (min !== undefined && v < min) return tr("ui.schemaForm.min", { min });
    if (max !== undefined && v > max) return tr("ui.schemaForm.max", { max });
    return null;
  }
  if (ty === "enum") return typeof v === "string" && (!e.options || e.options.includes(v)) ? null : tr("ui.schemaForm.notOption");
  if (ty === "duration") return typeof v === "string" && parseDuration(v) !== null ? null : tr("ui.schemaForm.durationLike");
  if (ty.startsWith("list<")) return Array.isArray(v) ? null : tr("ui.schemaForm.notList");
  return typeof v === "string" ? null : tr("ui.schemaForm.notText");
}

const same = (a: Json | undefined, b: Json | undefined) => JSON.stringify(a) === JSON.stringify(b);
const label = (path: string) => {
  const last = path.split(".").pop() ?? path;
  const words = last.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[_-]+/g, " ").trim();
  return words.charAt(0).toUpperCase() + words.slice(1);
};

export function SchemaForm({ schema, values, onChange, onReset, errors = [], pending = [], disabled, filter, labels, clamp }: SchemaFormProps) {
  const t = useT();
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

  if (groups.length === 0) return <div className="k-empty-title">{filter ? t("ui.schemaForm.noMatch") : t("ui.schemaForm.noSettings")}</div>;
  return (
    <div className="k-sform">
      {groups.map((g) => (
        <div key={g.section?.path ?? "_"} className="k-sform-section">
          {g.section && (
            <div className="k-sform-section-head">
              <span className="k-sform-section-title">{label(g.section.path)}</span>
              {g.section.synced && <span className="k-word-sm k-tone-info" title={t("ui.schemaForm.syncedTitle")}>{t("ui.schemaForm.synced")}</span>}
              {g.section.comment && <span className="k-hint">{g.section.comment}</span>}
            </div>
          )}
          {g.entries.map((e) => (
            <EntryRow key={e.path} entry={e} value={getPath(values, e.path)} onChange={onChange} onReset={onReset} disabled={disabled}
              error={errors.find((x) => x.path === e.path)?.message} pending={pending.includes(e.path)} name={labels?.[e.path]} clamp={clamp} />
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
  /** The field's label (SchemaFormProps.labels); else from its key. */
  name?: string;
  clamp?: boolean;
}

function EntryRow({ entry: e, value, onChange, onReset, error, pending, disabled, name: given, clamp }: RowProps) {
  const t = useT();
  const name = given ?? label(e.path);
  const v = value === undefined ? e.default : value;
  const changed = value !== undefined && !same(value, e.default);
  const local = checkValue(e, v);
  const shownError = error ?? (changed ? local : null);
  const id = `sf-${e.path.replace(/[^a-z0-9]/gi, "-")}`;
  const set = (nv: Json) => { if (!same(nv, v)) onChange(e.path, nv); };
  // the control is described by the entry's comment (a screen reader reads it with the control); the controls are of
  // many kinds, so the attribute goes on the element itself
  const row = useRef<HTMLDivElement>(null);
  const help = e.comment ? `${id}-help` : null;
  useEffect(() => {
    const el = row.current?.querySelector<HTMLElement>(`#${CSS.escape(id)}`);
    if (!el) return;
    if (help) el.setAttribute("aria-describedby", help);
    else if (el.getAttribute("aria-describedby")?.endsWith("-help")) el.removeAttribute("aria-describedby");
  });
  return (
    <div className={"k-sform-row" + (changed ? " changed" : "") + (shownError ? " invalid" : "")} ref={row}>
      <div className="k-sform-meta">
        <div className="k-sform-label">
          <label htmlFor={id}>{name}</label>
          {e.requiresRestart && <span className="k-word-sm k-tone-warn" title={t("ui.schemaForm.restartTitle")}>{t("ui.schemaForm.restart")}</span>}
          {e.synced && <span className="k-word-sm k-tone-info" title={t("ui.schemaForm.syncedTitle")}>{t("ui.schemaForm.synced")}</span>}
          {pending && <span className="k-word-sm k-tone-ember" title={t("ui.schemaForm.pendingTitle")}>{t("ui.schemaForm.pending")}</span>}
        </div>
        {e.comment && <Comment id={`${id}-help`} text={e.comment} clamp={clamp} label={name} />}
        <span className="k-sform-key">{e.path}</span>
      </div>
      <div className="k-sform-control">
        <Control id={id} entry={e} value={v} set={set} disabled={disabled} invalid={!!shownError} />
        {shownError && <span className="k-error-text">{shownError}</span>}
        {changed && (
          <span className="k-sform-default">
            {rich("ui.schemaForm.default", { value: <span className="k-mono">{fmt(e.default)}</span> })}
            {onReset && <> · <button type="button" className="k-link" aria-label={t("ui.schemaForm.resetLabel", { name })} onClick={() => onReset(e.path)} disabled={disabled}>{t("common.reset")}</button></>}
          </span>
        )}
      </div>
    </div>
  );
}

/** The longest line of help a row shows whole. */
const LINE = 90;
const foldWords = (s: string) => s.normalize("NFD").replace(/\p{M}+/gu, "").toLocaleLowerCase().replace(/[^\p{L}\p{N}]+/gu, " ").trim();
const upperFirst = (s: string) => s.charAt(0).toLocaleUpperCase() + s.slice(1);

/** Where `sep` first stands outside parentheses in `text`, from `from` on (-1: nowhere). */
function outside(text: string, sep: string, from = 0): number {
  let depth = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === "(") depth++;
    else if (c === ")") depth = Math.max(0, depth - 1);
    if (depth === 0 && i >= from && text.startsWith(sep, i)) return i;
  }
  return -1;
}

/** `text` without its parentheses ("(0: off; 1 to 19 count as 20)"). */
const unbracketed = (text: string) => {
  let out = "", depth = 0;
  for (const c of text) {
    if (c === "(") depth++;
    else if (c === ")") depth = Math.max(0, depth - 1);
    else if (depth === 0) out += c;
  }
  return out.replace(/\s+([,.;:])/g, "$1").replace(/\s{2,}/g, " ").trim();
};

/** The part before a comment's colon only names its subject: it says when the entry applies ('With the look "theme"'),
 *  repeats the row's name ("Kiln's frame cap" for Frame cap), or is a few words ("Chat history"). */
function subjectOnly(subject: string, label?: string): boolean {
  const s = foldWords(subject);
  if (/^with\b/.test(s) || subject.trim().split(/\s+/).length <= 3) return true;
  const l = label ? foldWords(label) : "";
  return !!l && (s === l || s.endsWith(` ${l}`));
}

/** A clause of `text` short enough for one line: the text as it is, without its parentheses, or its first clause. */
function oneLine(text: string, label?: string): string {
  if (text.length <= LINE) return text;
  const plain = unbracketed(text);
  // the plain text, unless all it says is the row's name ("The theme" for Theme)
  const bare = (s: string) => foldWords(s).replace(/^the /, "");
  if (plain.length <= LINE && (!label || bare(plain) !== bare(label))) return plain;
  let at = -1;
  for (const sep of [": ", ". ", "; "]) {
    const i = outside(plain, sep, 12);
    if (i >= 12 && i <= LINE && (at < 0 || i < at)) at = i;
  }
  if (at > 0) return plain.slice(0, at) + (plain[at] === "." ? "." : "");
  const cut = plain.lastIndexOf(" ", 80);
  return `${plain.slice(0, cut > 20 ? cut : 80)}…`;
}

/** A long comment's line of help (null: the comment is short enough to show whole). The part before the colon is kept
 *  when it says what the entry is ("A small readout in a corner of the game: …"); when it only names the subject
 *  ('With the DLSS render size custom: render width…', "Kiln's frame cap: the game renders…", "Chat history: Up/Down…"),
 *  the line is the clause after it. Either way the line is cut at its first clause, parentheses left out. */
export function commentHead(text: string, label?: string): string | null {
  if (text.length <= LINE) return null;
  const colon = outside(text, ": ");
  const body = colon > 0 && subjectOnly(text.slice(0, colon), label) ? upperFirst(text.slice(colon + 2).trim()) : text;
  const head = oneLine(body, label);
  return head === text ? null : head;
}

/** An entry's comment; with `clamp`, a long one shows one line (commentHead) and More. */
function Comment({ id, text, clamp, label }: { id: string; text: string; clamp?: boolean; label?: string }) {
  const t = useT();
  const [open, setOpen] = useState(false);
  const head = clamp ? commentHead(text, label) : null;
  if (!head) return <span className="k-hint" id={id}>{text}</span>;
  return (
    <span className="k-hint">
      <span id={id}>{open ? text : head}</span>
      <button type="button" className="k-link k-sform-more" aria-expanded={open} onClick={() => setOpen(!open)}>{open ? t("ui.schemaForm.less") : t("ui.schemaForm.more")}</button>
    </span>
  );
}

function fmt(v: Json): string {
  if (typeof v === "string") return `"${v}"`;
  return JSON.stringify(v);
}

function Control({ id, entry: e, value, set, disabled, invalid }: { id: string; entry: SnapshotEntry; value: Json; set: (v: Json) => void; disabled?: boolean; invalid?: boolean }): ReactNode {
  const ty = e.type;
  if (ty === "bool") return <Toggle id={id} checked={value === true} onChange={set} disabled={disabled} aria-label={e.path} />;
  if (ty === "int" || ty === "float") {
    const n = typeof value === "number" ? value : Number(value);
    if (e.min !== undefined && e.max !== undefined && e.max > e.min && e.max - e.min <= 100000)
      return <Slider id={id} value={n} min={e.min} max={e.max} step={sliderStep(e)} softMin={e.softMin === true} softMax={e.softMax === true} integer={ty === "int"} onCommit={set} disabled={disabled} aria-label={e.path} />;
    const hard = hardRange(e);
    return <NumberInput id={id} value={Number.isNaN(n) ? null : n} integer={ty === "int"} min={hard.min} max={hard.max} onCommit={set} disabled={disabled} invalid={invalid} />;
  }
  if (ty === "enum") {
    const opts = e.options ?? [];
    const cur = typeof value === "string" ? value : "";
    return <Select id={id} value={cur} options={opts.includes(cur) ? opts : [cur, ...opts]} onChange={set} disabled={disabled} invalid={invalid} />;
  }
  if (ty === "duration") return <CommitText id={id} value={typeof value === "string" ? value : String(value ?? "")} onCommit={set} disabled={disabled} invalid={invalid} placeholder="5m" mono />;
  if (ty.startsWith("list<")) return <ListEdit id={id} inner={ty.slice(5, -1)} value={Array.isArray(value) ? value : []} onCommit={set} disabled={disabled} />;
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
  const t = useT();
  const [text, setText] = useState("");
  const item = parseItem(inner, text);
  const add = () => { if (item !== undefined) { onCommit([...value, item]); setText(""); } };
  return (
    <div className="k-list-edit">
      {value.length > 0 && (
        <div className="k-chip-row">
          {value.map((x, i) => (
            <span key={i} className="k-chip">{String(x)}
              <button type="button" aria-label={t("ui.schemaForm.removeItem", { item: String(x) })} disabled={disabled} onClick={() => onCommit(value.filter((_, j) => j !== i))}><X size={12} /></button>
            </span>
          ))}
        </div>
      )}
      <div className="k-input-group">
        <TextInput id={id} value={text} onChange={(e) => setText(e.target.value)} placeholder={t("ui.schemaForm.addItem", { type: inner })} disabled={disabled} mono
          invalid={text.trim() !== "" && item === undefined} onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); add(); } }} />
        <IconButton label={t("common.add")} icon={<Plus />} variant="secondary" onClick={add} disabled={disabled || item === undefined} />
      </div>
    </div>
  );
}
