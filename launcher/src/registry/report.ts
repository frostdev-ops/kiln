// Format 1: schema/state-registry.schema.json, native/services/content/registry/report.cpp.
// Only a bounded, display-only projection crosses into React. Unknown members are ignored.
import published from "../../../schema/registry/ids.lock.json";
import { t } from "../i18n";
export const REPORT_LIMITS = { bytes: 8 * 1024 * 1024, containers: 8 * 1024 * 1024 / 16, rows: 500, writers: 64, text: 2048, depth: 48 } as const;
const kilnFields = new Map(Object.entries(published.registries).map(([name, spec]) => [name, new Set(Object.keys(spec.fields))]));
function isKilnField(registry: string, field: string): boolean {
  const normalized = field.replace(/\[(?:\d+|\*)\]/g, "[]");
  const fields = kilnFields.get(registry);
  return !!fields && (fields.has(normalized) || fields.has(normalized.replace(/\[\]$/, "")));
}
type ObjectRow = Record<string, unknown>;
// certainty is the label in the current language; certain says whether Kiln observed it (the page's tone).
export interface RegistryWriter { element: string; owner: string; kind: string; stage: string; operation: string; value: string; certainty: string; certain: boolean }
export interface RegistryField {
  /** registry|id|field|n (n: earlier rows with the same place): a React key that follows the row across pages and refetches. */
  key: string;
  registry: string; id: string; field: string; applies: string; baseOrigin: string; base: string;
  result: string; winner: string; writers: RegistryWriter[]; writersOmitted: number; formulaInputs: string;
}
export interface RegistryConflict { field: string; kind: string; winner: string; loser: string; certainty: string; certain: boolean; text: string }
export interface RegistryRefusal { owner: string; field: string; stage: string; reason: string }
/** Edits that wait for a restart: refused rows whose reason starts with the stable token
 * "RestartRequired: " (native report.h). total: counts.restartRequired (every row, also those the native cap left out). */
export interface RegistryRestart { total: number; rows: RegistryRefusal[] }
export const RESTART_TOKEN = "RestartRequired: ";
/** One package of registry.json's "replay": `who` "shroudforge:<id>" / "eml:<id>" ("" for the opt-in file itself), `line`
 * the sentence players read, `optIn` what the start read from the opt-in file. */
export interface RegistryReplayRow { who: string; line: string; state: string; optIn: boolean }
export interface RegistryReport {
  written: string; generation: number; phase: string; baseOrigin: string; baseWriter: string; verified: boolean;
  /** Whether the archive writer's packages are named (base.journal, since 0.2.0-dev.31.17); empty: nothing to say. */
  journal: string;
  /** Effect replay (registry.json "replay", next release): one row per ShroudForge or EML package the start's journal lists
   * (and per opted-in one it does not); empty: none. */
  replay: RegistryReplayRow[];
  fields: RegistryField[]; conflicts: RegistryConflict[]; refused: RegistryRefusal[]; restart: RegistryRestart;
  entries: { kind: string; registry: string; id: string; state: string; owner: string; source: string; reason: string }[]; omitted: number; empty: boolean;
  /** Earlier writers left out of field chains: native's truncated.writers plus this view's own 64-writer cut. */
  writersOmitted: number;
}
function object(x: unknown): ObjectRow {
  if (x === null || typeof x !== "object" || Array.isArray(x)) throw new Error(t("registry.error.malformed", { part: "object" }));
  return x as ObjectRow;
}
function array(x: unknown): unknown[] {
  if (!Array.isArray(x)) throw new Error(t("registry.error.malformed", { part: "list" }));
  return x;
}
function text(x: unknown): string {
  if (typeof x !== "string") throw new Error(t("registry.error.malformed", { part: "text" }));
  // Raw addresses are not Kiln identifiers. Also remove terminal/bidi/zero-width controls that could disguise an owner,
  // and never cut a surrogate pair in half.
  let cut = x.slice(0, REPORT_LIMITS.text);
  if (/[\ud800-\udbff]$/.test(cut)) cut = cut.slice(0, -1);
  const s = cut.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f\u061c\u200b-\u200f\u202a-\u202e\u2060-\u2064\u2066-\u2069\ufeff]/g, "")
    .replace(/\b(?:RVA|offset)\s*[:=]?\s*(?:0x[\da-f]+|\d+)\b|\b0x[\da-f]{6,}\b/gi, "[native detail hidden]");
  return s + (x.length > REPORT_LIMITS.text ? "…" : "");
}
function count(x: unknown): number {
  if (!Number.isSafeInteger(x) || (x as number) < 0) throw new Error(t("registry.error.malformed", { part: "count" }));
  return x as number;
}
// A raw member path a mod wrote can sit inside native text attached to a Kiln field (compose.cpp: "... over a's raw path
// ItemDesc.m_stackSize", "items[x].maxStack and raw path X are one place", "raw path \"P\" is inside ...", place_text's
// "items[x].P (raw)", a FieldRef's "items[x].raw:p"). Display text never names one.
const RAW_HIDDEN = "raw path (details hidden)";
export function redactRaw(s: string): string {
  return s
    .replace(/\braw paths? "(?:[^"\\]|\\.)*"(?:\s*(?:and|,)\s*"(?:[^"\\]|\\.)*")*/g, RAW_HIDDEN)
    .replace(/\braw path (?!\(details hidden\))[^\s,;:()"]+/g, RAW_HIDDEN)
    .replace(/[^\s"]*\.raw:[^\s,;:()"]+|[^\s"]+ \(raw\)/g, RAW_HIDDEN);
}
const omitted = (x: unknown): number => x === undefined ? 0 : count(x);
const optional = (x: unknown): string => x === undefined ? "" : text(x);
// A replayed package's stand-in writer (stage.cpp effect replay: mod "replay.sf.<id>" / "replay.eml.<id>", its writer rows
// marked "standIn"); null: not that form. Only ids a standIn row carries are read so: a Kiln mod may have such an id too.
// `packages`: the replay rows' stand-in mod ids -> the package (an id with a capital letter carries a hash in its mod id).
function replayOwner(mod: string, packages?: Map<string, string>): string | null {
  const who = packages?.get(mod);
  const p = who ? /^(shroudforge|eml):(.+)$/.exec(who) : null;
  if (p) return t(p[1] === "eml" ? "registry.replayedEml" : "registry.replayedSf", { id: p[2] });
  const m = /^replay\.(sf|eml)\.(.+)$/.exec(mod);
  return m ? t(m[1] === "eml" ? "registry.replayedEml" : "registry.replayedSf", { id: m[2] }) : null;
}
// An archive-writer package (report.cpp: "shroudforge:<id>", "eml:<id>", "kiln-eml:unattributed",
// "shroudforge-hosted:unattributed") as players read it.
function archiveOwner(who: string): string {
  if (who === "kiln-eml:unattributed") return t("registry.archiveUnattributed");
  if (who === "shroudforge-hosted:unattributed") return t("registry.archiveUnattributedHosted");
  const m = /^(shroudforge|eml):(.+)$/.exec(who);
  return m ? t(m[1] === "eml" ? "registry.emlPackage" : "registry.sfPackage", { id: m[2] }) : who;
}
// An archive-writer row names the entry its package wrote, or (an EML pass's journal, `named: "field"`, next
// release) this field.
function writerKind(kind: string, named?: unknown): string {
  if (kind === "archiveWriter") return t(named === "field" ? "registry.kind.archiveWriterField" : "registry.kind.archiveWriter");
  if (kind === "runtimeWriter") return t("registry.kind.runtimeWriter");  // J3b (sfi R3): a package of kiln mode at run time
  return kind === "drift" ? t("registry.kind.drift") : kind;
}
// base.journal: absent in a report from before 0.2.0-dev.31.17, and nothing to say when the start had no archive writer.
function journalLine(base: ObjectRow): string {
  if (base.journal === undefined) return "";
  const j = object(base.journal), state = text(j.state);
  if (state === "read" && j.writer === "shroudforge")
    return t(j.truncated === true ? "registry.journal.truncatedHosted" : "registry.journal.readHosted", { packages: count(j.packages), entries: count(j.entries) });
  if (state === "read") return t(j.truncated === true ? "registry.journal.truncated" : "registry.journal.read", { packages: count(j.packages), entries: count(j.entries) });
  if (state === "none" && text(base.writer) === "none") return "";
  return t("registry.journal.notNamed", { reason: j.text === undefined ? t(`registry.journal.${state === "stale" || state === "unreadable" ? state : "none"}`) : redactRaw(text(j.text)) });
}
// registry.json "replay": absent before the next release and when the start's journal lists no package.
function replayRows(x: unknown): RegistryReplayRow[] {
  if (x === undefined) return [];
  return array(x).slice(0, REPORT_LIMITS.rows).map(y => {
    const r = object(y), who = text(r.who), state = text(r.state);
    const known = ["recorded", "replayed", "refused", "none"].includes(state) ? state : "none";
    return { who, state: known, optIn: r.optIn === true, line: t("registry.replay.row", {
      who: who ? archiveOwner(who) : t("registry.replay.optIn"),
      state: t(`registry.replay.state.${known}`),
      text: redactRaw(text(r.text)),
    }) };
  });
}
function certainty(x: unknown): string {
  return t(`registry.certainty.${x === "certain" || x === "named" || x === "bestEffort" ? x : "unknown"}`);
}
const ADVANCED_FIELD = () => t("registry.advancedField");
const unknownField = (field: string) => t("registry.unknownField", { field });

// Values may contain lists/records, but never recursively stringify an untrusted tree wholesale.
function value(x: unknown, leftOut: unknown = 0): string {
  let budget = 128;
  function bounded(v: unknown, depth: number): unknown {
    if (--budget < 0 || depth > 4) return "… omitted";
    if (v === null || typeof v === "boolean") return v;
    if (typeof v === "number") return Number.isFinite(v) ? v : "Invalid number";
    if (typeof v === "string") return text(v);
    if (Array.isArray(v)) {
      const rows = v.slice(0, 16).map(a => bounded(a, depth + 1));
      if (v.length > 16) rows.push(`… ${v.length - 16} omitted`);
      return rows;
    }
    const o = object(v);
    const all = Object.keys(o), keys = all.filter(k => !/^(raw|offset|rva|address|function|gameFunction)$/i.test(k));
    const rows: ObjectRow = Object.create(null);
    for (const k of keys.slice(0, 16)) rows[text(k)] = bounded(o[k], depth + 1);
    if (keys.length > 16) rows["…"] = `${keys.length - 16} omitted`;
    if (all.length > keys.length) rows["… hidden"] = `${all.length - keys.length} hidden`;
    return rows;
  }
  const result = text(JSON.stringify(bounded(x, 0)));
  const n = omitted(leftOut);
  return result + (n ? ` (${n} omitted)` : "");
}

// The native writer caps rows, not bytes (report.cpp: 20,000 fields x 64 writers x 64-entry lists can pass 8 MiB): say
// where the whole file is, rather than only that it is too large.
function checkEnvelope(input: string) {
  if (input.length > REPORT_LIMITS.bytes || new TextEncoder().encode(input).length > REPORT_LIMITS.bytes) throw new Error(t("registry.error.tooLarge"));
  // Work is bounded by the byte cap: JSON.parse is linear in it. What costs memory beyond the text are containers (each
  // object/list is an allocation), so those are capped in proportion to the byte cap: one per 16 bytes. A native report
  // spends far more than that per container (about 60 bytes for a field row), so any real report up to 8 MiB passes, while
  // a document of empty lists/objects meant to inflate memory does not. Separators are not counted (at most one per byte).
  let depth = 0, quoted = false, escape = false, containers = 0;
  for (const c of input) {
    if (quoted) {
      if (escape) escape = false;
      else if (c === "\\") escape = true;
      else if (c === '"') quoted = false;
    } else if (c === '"') quoted = true;
    else if (c === "{" || c === "[") {
      if (++depth > REPORT_LIMITS.depth) throw new Error(t("registry.error.nesting"));
      if (++containers > REPORT_LIMITS.containers) throw new Error(t("registry.error.structure"));
    } else if (c === "}" || c === "]") depth--;
  }
}

export function parseRegistryReport(input: string): RegistryReport {
  checkEnvelope(input);
  let parsed: unknown;
  try { parsed = JSON.parse(input); } catch { throw new Error(t("registry.error.notJson")); }
  const r = object(parsed);
  if (r.format !== 1) throw new Error(t("registry.error.format"));
  const written = text(r.written);
  if (!/^\d{4}-\d\d-\d\dT/.test(written) || !Number.isFinite(Date.parse(written))) throw new Error(t("registry.error.malformed", { part: "timestamp" }));
  const base = object(r.base), regmap = object(r.regmap), totals = object(r.counts), truncated = object(r.truncated);
  if (typeof regmap.verified !== "boolean") throw new Error(t("registry.error.malformed", { part: "regmap.verified" }));
  const fields = array(r.fields), conflicts = array(r.conflicts), refused = array(r.refused), added = array(r.added), removed = array(r.removed);
  let left = 0, total = 0;
  for (const [key, rows] of Object.entries({ fields, conflicts, refused, added, removed })) {
    total += Math.max(count(totals[key]), rows.length);
    left += count(truncated[key]) + Math.max(0, rows.length - REPORT_LIMITS.rows);
  }
  // the stand-in writers' mod ids (writer rows with "standIn", effect replay): a winner or a writer of one names the package
  const standIns = new Set<string>();
  for (const x of fields.slice(0, REPORT_LIMITS.rows))
    for (const y of array(object(x).writers)) {
      const w = object(y);
      if (w.standIn === true && typeof w.mod === "string") standIns.add(w.mod);
    }
  // the replay rows name each package's stand-in mod id ("mod"): its label is the package's own id, whatever the mod id holds
  const packages = new Map<string, string>();
  for (const x of r.replay === undefined ? [] : array(r.replay).slice(0, REPORT_LIMITS.rows)) {
    const p = object(x);
    if (typeof p.mod === "string" && typeof p.who === "string") packages.set(p.mod, p.who);
  }
  // a winner or a line's writer that is a package ("shroudforge:<id>": a run-time writer of kiln mode, J3b) is named as one; a
  // Kiln mod's id has no ':'
  const owner = (x: unknown): string => x === null ? t("registry.baseOwner") : (standIns.has(text(x)) && replayOwner(text(x), packages)) ||
    (/^(shroudforge|eml):[^\s]+$|^(kiln-eml|shroudforge-hosted):unattributed$/.test(text(x)) ? archiveOwner(text(x)) : text(x));
  let launcherWriterCut = 0;
  const seen = new Map<string, number>();
  const fieldRows = fields.slice(0, REPORT_LIMITS.rows).map(x => {
    const f = object(x), b = object(f.base), chain = array(f.writers);
    const skip = Math.max(0, chain.length - REPORT_LIMITS.writers);
    launcherWriterCut += skip;
    const raw = f.raw === true, unknown = !raw && !isKilnField(text(f.registry), text(f.field));
    const field = raw ? ADVANCED_FIELD() : unknown ? unknownField(text(f.field)) : text(f.field);
    const place = `${text(f.registry)}|${text(f.id)}|${raw ? "raw" : text(f.field)}`, n = seen.get(place) ?? 0;  // never a raw path
    seen.set(place, n + 1);
    return {
      key: `${place}|${n}`,
      registry: text(f.registry), id: text(f.id), field, applies: text(f.applies),
      baseOrigin: text(b.origin), base: value(b.value, b.entriesLeftOut), result: value(f.result, f.resultEntriesLeftOut), winner: owner(f.winner),
      formulaInputs: f.formulaInputs === undefined ? "" : value(f.formulaInputs),
      writersOmitted: omitted(f.writersLeftOut) + skip,
      writers: chain.slice(skip).map(x => {
        const w = object(x);
        // an element edit names its element ("entries[3].chance", report.cpp writer_json): shown when the lock knows the
        // list and leaf, so a chain says which element an operation changed
        const element = w.element === undefined || raw ? "" : isKilnField(text(f.registry), text(w.element)) ? text(w.element) : t("registry.elementHidden");
        const kind = text(w.kind), who = optional(w.who);
        return { element, owner: (kind === "archiveWriter" || kind === "runtimeWriter") && who ? archiveOwner(who) : (w.standIn === true && replayOwner(text(w.mod), packages)) || who || optional(w.mod) || t("registry.referenceCheck"), kind: writerKind(kind, w.named), stage: optional(w.stage),
          // an archive-writer package: kiln-eml's journal names the entry (or the field) it wrote, not what it did there
          operation: kind === "archiveWriter" ? t(w.named === "field" ? "registry.archiveOpField" : "registry.archiveOp")
            : w.list !== undefined ? value(w.list, w.listEntriesLeftOut) : w.op !== undefined ? value(w.op, w.opEntriesLeftOut) : t("registry.referenceOp"),
          value: value(w.value, w.valueEntriesLeftOut), certainty: certainty(w.certainty), certain: w.certainty === "certain" };
      }),
    };
  });
  const refusedRows = refused.slice(0, REPORT_LIMITS.rows).map(x => {
    // Only a refusal the runtime marks raw (an Advanced-tier raw path, report.cpp) is redacted. A name the lock does not
    // know (a typo, or a field of a newer Kiln) is what the author wrote, and the reason is the one thing they can act on.
    const f = object(x), field = optional(f.field), registry = optional(f.registry), reason = redactRaw(text(f.reason));
    const raw = f.raw === true;
    const unknown = !raw && !!field && !isKilnField(registry, field);
    const place = [registry, optional(f.id), field].filter(Boolean).join(" · ");
    return { owner: text(f.mod), field: raw ? ADVANCED_FIELD() : unknown ? unknownField(place) : place,
      stage: text(f.stage), reason: raw ? `${reason.startsWith("RestartRequired:") ? "RestartRequired: " : reason.startsWith("Unproven:") ? "Unproven: " : ""}${t("registry.advancedReason")}` : reason };
  });
  const restartRows = refusedRows.filter(x => x.reason.startsWith(RESTART_TOKEN));
  return {
    written, generation: count(r.generation), phase: text(r.phase), baseOrigin: text(base.origin), baseWriter: text(base.writer), verified: regmap.verified,
    restart: { total: Math.max(totals.restartRequired === undefined ? 0 : count(totals.restartRequired), restartRows.length), rows: restartRows },
    journal: journalLine(base),
    replay: replayRows(r.replay),
    fields: fieldRows, writersOmitted: omitted(truncated.writers) + launcherWriterCut,
    conflicts: conflicts.slice(0, REPORT_LIMITS.rows).map(x => {
      const c = object(x), field = text(c.field);
      const registry = text(c.registry), prefix = `${registry}[${text(c.id)}].`;
      const raw = c.raw === true || !field.startsWith(prefix), unknown = !raw && !isKilnField(registry, field.slice(prefix.length));
      return { field: raw ? ADVANCED_FIELD() : unknown ? unknownField(field) : field, kind: text(c.kind), winner: owner(c.winner), loser: c.loser === null ? t("registry.noOwner") : owner(c.loser),
        certainty: certainty(c.certainty), certain: c.certainty === "certain", text: raw ? t("registry.advancedConflict") : redactRaw(text(c.text)) };
    }),
    refused: refusedRows,
    entries: [added, removed].flatMap((rows, index) => rows.slice(0, REPORT_LIMITS.rows).map(x => {
      const e = object(x);
      const state = typeof e.state === "string" && ["pendingWorld", "registered", "hidden", "disabled"].includes(e.state) ? e.state : "";
      return { kind: index === 0 ? t("registry.added") : t("registry.removed"), registry: text(e.registry), id: text(e.id), state,
        owner: optional(e.mod), source: optional(e.source), reason: redactRaw(optional(e.reason)) };
    })),
    omitted: left, empty: total === 0,
  };
}

/** No PID equality: the native report has a Windows PID even under Proton. Never call a snapshot live telemetry. */
export function reportFreshness(written: string, status?: { running: boolean; started?: string | null }, now = Date.now()): string | null {
  const at = Date.parse(written);
  if (!Number.isFinite(at) || at > now + 60_000) return t("registry.fresh.clock");
  if (!status) return t("registry.fresh.process");
  if (!status.running) return t("registry.fresh.stopped");
  if (status.started && at < Date.parse(status.started)) return t("registry.fresh.previousStart");
  if (now - at > 86_400_000) return t("registry.fresh.old");
  if (!status.started || !Number.isFinite(Date.parse(status.started))) return t("registry.fresh.start");
  return null;
}
