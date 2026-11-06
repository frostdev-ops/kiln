// /servers/:id/admin/content: kiln.content as Kiln.Admin serves it: registry status, the
// data-pack report and re-apply, mods' new definitions, and a lookup to read, change and reset one field of an item,
// recipe, loot entry or drop table (content_search/content_entry/content_set/content_reset; content_get as a fallback
// on an older Kiln.Admin). Changes made here are Kiln.Admin's layer: temporary.
import { useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useAdminCall, useAdminQuery, useHasOp } from "../../api/admin";
import type { Json } from "../../api/commands";
import { FIELD, NeedsOp, definitionName, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import type { ContentEntry, ContentField, ContentHit, ContentKind, ContentStatus, ContentValue, PackReport } from "../types";
import {
  Badge, Banner, Button, Card, EmptyState, JsonView, NameId, Page, PageHeader, Segmented, Select, Stat, Stats, SuggestInput, Table, Tabs, TextInput, Toggle,
  toastError, toastOk, useDebounced, type Suggestion,
} from "../ui";

const KINDS: { id: ContentKind; label: string }[] = [{ id: "items", label: "Items" }, { id: "recipes", label: "Recipes" }, { id: "loot", label: "Loot" }, { id: "drops", label: "Drop tables" }];
interface Definition {
  kind: string;
  guid: string;
  id: string;
  name: string;
  base?: string;
  owner?: string;
  state?: string;
  mod?: string;
  displayName?: string;
  languages?: string[];
  definition?: Json;
}

function ContentPage() {
  const status = useAdminQuery<ContentStatus>("content_status", {}, { refetchInterval: 15_000 });
  const [tab, setTab] = useState<"lookup" | "packs" | "definitions">("lookup");
  const s = status.data;
  return (
    <Page>
      <PageHeader title="Content" sub="The game's items, recipes, loot and drop tables as Kiln sees them: what mods and data packs changed, and single values to try out." />
      <OfflineNote />
      <NeedsOp ops="content_status" what="Content tools">
        <Banner tone="ember" title="Changes made here are temporary">
          They belong to Kiln.Admin and come off when it reloads or the server restarts. Some (stack sizes, recipes, item stats) also look different on players'
          unmodded games. For a lasting change, put it in a mod's <span className="k-mono">content/*.json</span> data pack.
        </Banner>
        {s ? (
          <Stats>
            <Stat k="Registries" v={s.ready ? "ready" : "loading"} s={s.detail} />
            <Stat k="Items" v={s.items} s={`${s.loot} with random loot`} />
            <Stat k="Recipes" v={s.recipes} s={`${s.drops} drop tables loaded`} />
            <Stat k="Changed fields" v={s.changed} s="by mods, packs and here" />
            <Stat k="Data packs" v={s.packs} s={`${s.errors} errors · ${s.conflicts} conflicts${s.stackGuard ? " · stack guard on" : ""}`} />
          </Stats>
        ) : status.error ? <Banner tone="warn">{status.error.message}</Banner> : null}
        <Tabs value={tab} onChange={setTab} items={[{ id: "lookup", label: "Look up" }, { id: "packs", label: "Data packs", count: s?.packs }, { id: "definitions", label: "New content" }]} />
        {tab === "lookup" ? <Lookup /> : tab === "packs" ? <Packs /> : <Definitions />}
      </NeedsOp>
    </Page>
  );
}

function Lookup() {
  const [kind, setKind] = useState<ContentKind>("items");
  const [text, setText] = useState("");
  const [entry, setEntry] = useState<string | null>(null);
  const dq = useDebounced(text.trim(), 200);
  const hasEntry = useHasOp("content_entry");
  const search = useAdminQuery<ContentHit[]>("content_search", { kind, q: dq, limit: 30 }, { enabled: dq.length >= 2 && dq !== entry, staleTime: 30_000 });
  const sugg = useMemo<Suggestion<string>[]>(() => (search.data ?? []).map((h) => ({ key: h.id, label: h.name, sub: <>{h.modified ? <Badge tone="ember">changed</Badge> : null} <span className="k-mono">{h.id}</span></>, value: h.name })), [search.data]);
  return (
    <>
      <Card title="Look up" actions={<Segmented value={kind} onChange={(k) => { setKind(k); setEntry(null); setText(""); }} options={KINDS} aria-label="Registry" />}>
        <SuggestInput value={text} onChange={setText} suggestions={dq === entry ? [] : sugg} placeholder="Search by name or id (Material_T1_Wood_Logs, 0x2b0cf1d5)…" mono
          onPick={(s) => { setText(s.value); setEntry(s.value); }} onEnter={() => text.trim() && setEntry(text.trim())} />
        <div className="k-hint" style={{ marginTop: 6 }}>The game's debug names; ids work too. {search.error ? <span className="k-error-text">{search.error.message}</span> : null}</div>
      </Card>
      {entry ? (hasEntry ? <EntryView kind={kind} name={entry} /> : <SingleField kind={kind} entry={entry} />) : <Card><EmptyState title="Look something up" hint="Search above, then read and change its fields." /></Card>}
    </>
  );
}

function EntryView({ kind, name }: { kind: ContentKind; name: string }) {
  const id = useServerId();
  const qc = useQueryClient();
  const q = useAdminQuery<ContentEntry>("content_entry", { kind, entry: name }, { staleTime: 5_000 });
  const [filter, setFilter] = useState("");
  const [editable, setEditable] = useState(true);
  const [changedOnly, setChangedOnly] = useState(false);
  const [editing, setEditing] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const set = useAdminCall<ContentValue>("content_set");
  const reset = useAdminCall<ContentValue>("content_reset");
  const patchField = (path: string, r: ContentValue) =>
    qc.setQueriesData<ContentEntry>({ queryKey: ["admin", id, "content_entry", { kind, entry: name }] }, (old) => old && {
      ...old,
      fields: old.fields.map((f) => (f.path === path ? { ...f, value: r.value, original: r.original ?? f.original, changed: r.value !== (r.original ?? f.original) } : f)),
    });
  if (q.error) return <Banner tone="warn">{q.error.message}</Banner>;
  const x = q.data;
  if (!x) return <div className="k-muted">Reading {name}…</div>;
  const f0 = filter.toLowerCase();
  const list = x.fields.filter((f) => (!editable || f.writable) && (!changedOnly || f.changed) && (!f0 || f.path.toLowerCase().includes(f0))).sort((a, b) => Number(!!b.changed) - Number(!!a.changed));
  const apply = (f: ContentField, value: Json) => {
    set.mutate({ kind, entry: x.name, field: f.path, value }, {
      onSuccess: (r) => { patchField(f.path, r); setEditing(null); setErrors(({ [f.path]: _d, ...rest }) => rest); toastOk(`${x.name}.${f.path} = ${r.value}`, "until Kiln.Admin reloads or the server restarts"); },
      onError: (e) => setErrors((o) => ({ ...o, [f.path]: e.message })),
    });
  };
  const stacks = (title: string, s?: { item: string; name?: string | null; count: number }[]) =>
    s?.length ? <div className="ka-inline"><b style={{ fontSize: 12.5 }}>{title}</b><span className="ka-chips">{s.map((i, n) => <span key={n} className="ka-chip plain">{i.count} × {i.name ?? i.item}</span>)}</span></div> : null;
  return (
    <Card title={x.name} sub={<><span className="k-mono">{x.id}</span> · {x.kind} · <span className="k-mono">{x.guid}</span></>} actions={x.modified ? <Badge tone="ember" title="At least one field differs from the game's value">changed</Badge> : null}>
      <div className="k-stack" style={{ gap: 12 }}>
        {stacks("Needs", x.inputs)}
        {stacks("Makes", x.outputs)}
        {x.stacks?.length ? (
          <table className="ka-fields"><thead><tr><th>#</th><th>Item</th><th className="num">Count</th><th className="num">Chance</th><th className="num">Weight</th></tr></thead>
            <tbody>{x.stacks.map((s) => <tr key={s.index}><td>{s.index}</td><td>{s.name ?? s.item}</td><td className="num">{s.countMin}–{s.countMax}</td><td className="num">{s.chance}</td><td className="num">{s.weight}</td></tr>)}</tbody></table>
        ) : null}
        <div className="ka-inline">
          <TextInput aria-label="Filter fields" placeholder="Filter fields…" value={filter} onChange={(e) => setFilter(e.target.value)} style={{ width: 220 }} />
          <Toggle checked={editable} onChange={setEditable} label="Changeable only" />
          <Toggle checked={changedOnly} onChange={setChangedOnly} label="Changed only" />
          <span className="k-spacer" />
          <span className="k-muted">{list.length} of {x.fields.length} fields</span>
        </div>
        {list.length ? (
          <table className="ka-fields">
            <thead><tr><th>Field</th><th>Now</th><th>Game's value</th><th /></tr></thead>
            <tbody>
              {list.slice(0, 200).map((f) => (
                <tr key={f.path} className={(f.changed ? "changed " : "") + (f.writable ? "" : "ro")}>
                  <td className="path">{f.path}<div className="k-hint">{f.type}{f.writable ? "" : " · read-only"}</div></td>
                  {editing === f.path ? (
                    <td colSpan={2}>
                      <FieldEditor f={f} busy={set.isPending} onCancel={() => setEditing(null)} onSet={(v) => apply(f, v)} />
                      {errors[f.path] ? <div className="k-error-text">{errors[f.path]}</div> : null}
                    </td>
                  ) : (
                    <>
                      <td className="val">{f.error ? <span className="k-muted">{f.error}</span> : f.value === "" ? "(none)" : f.value}</td>
                      <td className="val k-muted">{f.changed ? (f.original === "" ? "(none)" : f.original) : ""}</td>
                    </>
                  )}
                  <td style={{ whiteSpace: "nowrap", textAlign: "right" }}>
                    {f.writable && !f.error && editing !== f.path ? <Button size="s" onClick={() => setEditing(f.path)}>Change</Button> : null}{" "}
                    {f.changed && f.writable ? (
                      <Button size="s" variant="ghost" title="Take Kiln.Admin's change off (changes by other mods stay)" loading={reset.isPending && reset.variables?.field === f.path}
                        onClick={() => reset.mutate({ kind, entry: x.name, field: f.path }, { onSuccess: (r) => { patchField(f.path, r); toastOk(`${x.name}.${f.path} back to ${r.value}`); }, onError: (e) => toastError(e) })}>Reset</Button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title="No field matches" />}
        {list.length > 200 ? <div className="k-hint">The first 200 of {list.length}; filter to narrow it down.</div> : null}
      </div>
    </Card>
  );
}

/** The editor for one field: enum select, flags checkboxes, bool, number or text (Kiln.Admin's content_set types). */
function FieldEditor({ f, busy, onSet, onCancel }: { f: ContentField; busy: boolean; onSet: (v: Json) => void; onCancel: () => void }) {
  const num = ["int", "float", "time"].includes(f.valueType) && f.enumKind === "plain";
  const [text, setText] = useState(num ? String(f.value ?? "").replace(/s$/, "") : String(f.value ?? ""));
  const [flags, setFlags] = useState(new Set(String(f.value ?? "").split(",").map((s) => s.trim()).filter(Boolean)));
  const value = (): Json | Error => {
    if (f.enumKind === "flags") return [...flags].join(",");
    if (f.enumKind === "enum") return text;
    if (f.valueType === "bool") return text === "true";
    if (num) {
      const n = Number(text);
      if (text.trim() === "" || !Number.isFinite(n)) return new Error("A number, please");
      if (f.valueType === "int" && !Number.isInteger(n)) return new Error("A whole number, please");
      return n;
    }
    if (text.length > 2000) return new Error("Text up to 2000 characters");
    return text;
  };
  const go = () => {
    const v = value();
    if (v instanceof Error) return toastError({ message: v.message });
    onSet(v);
  };
  let ctl;
  if (f.enumKind === "flags")
    ctl = <div className="ka-flags">{(f.options ?? []).map((o) => <label key={o}><input type="checkbox" className="k-check" checked={flags.has(o)} onChange={(e) => { const n = new Set(flags); if (e.target.checked) n.add(o); else n.delete(o); setFlags(n); }} /> {o}</label>)}</div>;
  else if (f.enumKind === "enum") ctl = <Select aria-label={f.path} value={text} onChange={setText} options={(f.options ?? []).includes(text) ? f.options! : [text, ...(f.options ?? [])]} />;
  else if (f.valueType === "bool") ctl = <Select aria-label={f.path} value={text === "true" ? "true" : "false"} onChange={setText} options={["true", "false"]} />;
  else ctl = <TextInput aria-label={f.path} autoFocus inputMode={num ? "decimal" : undefined} value={text} onChange={(e) => setText(e.target.value)} onKeyDown={(e) => { if (e.key === "Enter") go(); if (e.key === "Escape") onCancel(); }} style={{ width: 180 }} mono />;
  return (
    <div className="ka-inline">
      {ctl}
      {f.valueType === "time" ? <span className="k-hint">seconds</span> : null}
      <Button size="s" loading={busy} onClick={go}>Set</Button>
      <Button size="s" variant="ghost" onClick={onCancel}>Cancel</Button>
    </div>
  );
}

/** Older Kiln.Admin without content_entry: one field at a time (content_get/content_set/content_reset). */
function SingleField({ kind, entry }: { kind: ContentKind; entry: string }) {
  const [field, setField] = useState("");
  const [cur, setCur] = useState<ContentValue | null>(null);
  const [val, setVal] = useState("");
  const get = useAdminCall<ContentValue>("content_get", { onSuccess: setCur, onError: (e) => toastError(e) });
  const set = useAdminCall<ContentValue>("content_set", { onSuccess: (r) => { setCur(r); toastOk(`${entry}.${field} = ${r.value} (temporary)`); }, onError: (e) => toastError(e) });
  const reset = useAdminCall<ContentValue>("content_reset", { onSuccess: setCur, onError: (e) => toastError(e) });
  const ok = () => (FIELD.test(field.trim()) ? true : (toastError({ message: "Field: a path like maxStackSize or equipment.slot" }), false));
  const parsed = (): Json => (val === "true" ? true : val === "false" ? false : val.trim() !== "" && Number.isFinite(Number(val)) ? Number(val) : val);
  return (
    <Card title={entry} sub="This server's Kiln.Admin can't list fields yet: one field at a time">
      <div className="k-stack">
        <div className="ka-inline">
          <TextInput aria-label="Field" placeholder="field (maxStackSize)" value={field} onChange={(e) => setField(e.target.value)} mono style={{ width: 260 }} />
          <Button onClick={() => ok() && get.mutate({ kind, entry, field: field.trim() })}>Read</Button>
        </div>
        {cur ? (
          <div className="ka-inline">
            <span>Now <b className="k-mono">{cur.value}</b>, game's <span className="k-mono">{cur.original}</span></span>
            <TextInput aria-label="New value" placeholder="new value" value={val} onChange={(e) => setVal(e.target.value)} style={{ width: 160 }} />
            <Button size="s" onClick={() => ok() && set.mutate({ kind, entry, field: field.trim(), value: parsed() })}>Set</Button>
            <Button size="s" variant="ghost" onClick={() => ok() && reset.mutate({ kind, entry, field: field.trim() })}>Reset</Button>
          </div>
        ) : null}
      </div>
    </Card>
  );
}

function Packs() {
  const id = useServerId();
  const qc = useQueryClient();
  const has = useHasOp("content_report");
  const rep = useAdminQuery<{ report: string }>("content_report", {}, { enabled: has, refetchInterval: 30_000 });
  const reload = useAdminCall<{ report: string }>("content_reload", { onSuccess: (r) => { qc.setQueriesData({ queryKey: ["admin", id, "content_report"] }, r); toastOk("Data packs applied again"); }, onError: (e) => toastError(e), invalidates: ["content_status"] });
  const r = useMemo<PackReport>(() => { try { return JSON.parse(rep.data?.report ?? "{}") as PackReport; } catch { return {}; } }, [rep.data]);
  return (
    <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
      <Card title="Data packs" sub="Every mod's content/*.json" actions={<Button size="s" loading={reload.isPending} onClick={() => reload.mutate({})} title="Read every mod's content/*.json again and re-apply">Re-apply packs</Button>}>
        <div className="ka-list">
          {(r.packs ?? []).length ? r.packs!.map((p) => (
            <div key={p.file} className="k-stack" style={{ gap: 4 }}>
              <div className="ka-row-item"><b className="k-mono">{p.file}</b><span className="k-spacer" /><Badge tone="ok">{p.applied} applied</Badge>{p.errors?.length ? <Badge tone="err">{p.errors.length} errors</Badge> : null}{p.warnings?.length ? <Badge tone="warn">{p.warnings.length} warnings</Badge> : null}</div>
              {(p.errors ?? []).map((e, i) => <div key={i} className="k-error-text">{e}</div>)}
              {(p.warnings ?? []).map((w, i) => <div key={i} className="k-hint" style={{ color: "var(--k-warn)" }}>{w}</div>)}
            </div>
          )) : <EmptyState title="No data packs" hint="No mod ships content/*.json." />}
          {(r.skipped ?? []).length ? <div className="k-hint">Skipped: {r.skipped!.join(", ")}</div> : null}
        </div>
      </Card>
      <Card title="Conflicts" sub="Two owners set one field; the later one wins">
        {(r.conflicts ?? []).length ? (
          <div className="ka-list">{r.conflicts!.map((c, i) => <div key={i}><div className="k-mono">{c.field}</div><div className="k-hint"><b>{c.winnerValue}</b> from {c.winner} · beats {c.loserValue} from {c.loser}</div></div>)}</div>
        ) : <EmptyState title="No conflicts" />}
      </Card>
    </div>
  );
}

function Definitions() {
  const has = useHasOp("content_definitions");
  const defs = useAdminQuery<Definition[]>("content_definitions", {}, { enabled: has, staleTime: 30_000 });
  const sync = useAdminQuery<{ content: { state: string; fields: number; bytes: number; parts: number } | null }>("sync_status", {}, { refetchInterval: 15_000 });
  const [open, setOpen] = useState<string | null>(null);
  // what players see: the mod's name for it (a recipe without one: what it makes), else the debug name
  const shown = useMemo(() => {
    const items = new Map<string, string>();
    for (const d of defs.data ?? []) if (d.kind === "item") { const n = definitionName(d); if (n) items.set(d.name, n); }
    return new Map((defs.data ?? []).map((d) => [d.guid, definitionName(d, items) ?? d.name]));
  }, [defs.data]);
  const sel = defs.data?.find((d) => d.guid === open) ?? null;
  return (
    <div className="k-stack" style={{ gap: 16 }}>
      {sync.data ? (
        <Banner tone="info" title="Content sync to Kiln clients">
          {sync.data.content ? `Serving state ${sync.data.content.state}: ${sync.data.content.fields} fields, ${sync.data.content.bytes} bytes in ${sync.data.content.parts} parts.` : "Nothing to send: no content changes need syncing right now."}
        </Banner>
      ) : null}
      <Card title="New content from mods" sub="Items and recipes mods define (kiln.content definitions)" flush>
        <Table
          aria-label="Definitions"
          rows={defs.data ?? []}
          rowKey={(d) => d.guid}
          selectedKey={open}
          onRowClick={(d) => setOpen(open === d.guid ? null : d.guid)}
          columns={[
            { key: "n", header: "Name", sort: (a, b) => (shown.get(a.guid) ?? a.name).localeCompare(shown.get(b.guid) ?? b.name), render: (d) => <NameId name={shown.get(d.guid) ?? d.name} id={d.name} /> },
            { key: "k", header: "Kind", width: "80px", render: (d) => d.kind },
            { key: "b", header: "Based on", render: (d) => <span className="k-mono">{d.base ?? "-"}</span> },
            { key: "m", header: "Mod", width: "120px", render: (d) => d.mod ?? d.owner ?? "" },
            { key: "s", header: "State", width: "110px", render: (d) => <Badge tone={d.state === "registered" ? "ok" : "warn"}>{d.state ?? "?"}</Badge> },
          ]}
          empty={<EmptyState title={has ? "No mod defines new content" : "content_definitions is not available"} />}
        />
      </Card>
      {sel ? <Card title={shown.get(sel.guid) ?? sel.name} sub={<span className="k-mono">{sel.name} · {sel.guid} · {sel.id}</span>}><JsonView value={sel.definition ?? null} open={2} /></Card> : null}
    </div>
  );
}

export default withAdmin(ContentPage);
