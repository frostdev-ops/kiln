// /servers/:id/admin/content: kiln.content as Kiln.Admin serves it: registry status, the
// data-pack report and re-apply, mods' new definitions, and a lookup to read, change and reset one field of an item,
// recipe, loot entry or drop table (content_search/content_entry/content_set/content_reset; content_get as a fallback
// on an older Kiln.Admin). Changes made here are Kiln.Admin's layer: temporary.
import { useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useAdminCall, useAdminQuery, useHasOp } from "../../api/admin";
import type { Json } from "../../api/commands";
import { rich, t as tr, useT } from "../../i18n";
import { FIELD, NeedsOp, definitionName, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import type { ContentEntry, ContentField, ContentHit, ContentKind, ContentStatus, ContentValue, PackReport } from "../types";
import {
  Badge, Banner, Button, Card, EmptyState, JsonView, NameId, Page, PageHeader, Segmented, Select, Stat, Stats, SuggestInput, Table, Tabs, TextInput, Toggle,
  toastError, toastOk, useDebounced, type Suggestion,
} from "../ui";

const kinds = (): { id: ContentKind; label: string }[] => [{ id: "items", label: tr("admin.content.kindItems") }, { id: "recipes", label: tr("admin.content.kindRecipes") }, { id: "loot", label: tr("admin.content.kindLoot") }, { id: "drops", label: tr("admin.content.kindDrops") }];
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
  const t = useT();
  const status = useAdminQuery<ContentStatus>("content_status", {}, { refetchInterval: 15_000 });
  const [tab, setTab] = useState<"lookup" | "packs" | "definitions">("lookup");
  const s = status.data;
  return (
    <Page>
      <PageHeader title={t("admin.content.title")} sub={t("admin.content.sub")} />
      <OfflineNote />
      <NeedsOp ops="content_status" what={t("admin.content.what")}>
        <Banner tone="ember" title={t("admin.content.tempTitle")}>
          {rich("admin.content.tempBody", { path: <span className="k-mono">content/*.json</span> })}
        </Banner>
        {s ? (
          <Stats>
            <Stat k={t("admin.content.statRegistries")} v={s.ready ? t("admin.content.ready") : t("admin.content.loadingState")} s={s.detail} />
            <Stat k={t("admin.content.kindItems")} v={s.items} s={t("admin.content.statLoot", { n: s.loot })} />
            <Stat k={t("admin.content.kindRecipes")} v={s.recipes} s={t("admin.content.statDrops", { n: s.drops })} />
            <Stat k={t("admin.content.statChanged")} v={s.changed} s={t("admin.content.statChangedSub")} />
            <Stat k={t("admin.content.dataPacks")} v={s.packs} s={s.stackGuard ? t("admin.content.statPacksGuard", { errors: s.errors, conflicts: s.conflicts }) : t("admin.content.statPacks", { errors: s.errors, conflicts: s.conflicts })} />
          </Stats>
        ) : status.error ? <Banner tone="warn">{status.error.message}</Banner> : null}
        <Tabs value={tab} onChange={setTab} items={[{ id: "lookup", label: t("admin.content.lookUp") }, { id: "packs", label: t("admin.content.dataPacks"), count: s?.packs }, { id: "definitions", label: t("admin.content.newContent") }]} />
        {tab === "lookup" ? <Lookup /> : tab === "packs" ? <Packs /> : <Definitions />}
      </NeedsOp>
    </Page>
  );
}

function Lookup() {
  const t = useT();
  const [kind, setKind] = useState<ContentKind>("items");
  const [text, setText] = useState("");
  const [entry, setEntry] = useState<string | null>(null);
  const dq = useDebounced(text.trim(), 200);
  const hasEntry = useHasOp("content_entry");
  const search = useAdminQuery<ContentHit[]>("content_search", { kind, q: dq, limit: 30 }, { enabled: dq.length >= 2 && dq !== entry, staleTime: 30_000 });
  const sugg = useMemo<Suggestion<string>[]>(() => (search.data ?? []).map((h) => ({ key: h.id, label: h.name, sub: <>{h.modified ? <Badge tone="ember">{t("admin.gameData.changed")}</Badge> : null} <span className="k-mono">{h.id}</span></>, value: h.name })), [search.data, t]);
  return (
    <>
      <Card title={t("admin.content.lookUp")} actions={<Segmented value={kind} onChange={(k) => { setKind(k); setEntry(null); setText(""); }} options={kinds()} aria-label={t("admin.content.registry")} />}>
        <SuggestInput value={text} onChange={setText} suggestions={dq === entry ? [] : sugg} placeholder={t("admin.content.searchPlaceholder")} mono
          onPick={(s) => { setText(s.value); setEntry(s.value); }} onEnter={() => text.trim() && setEntry(text.trim())} />
        <div className="k-hint" style={{ marginTop: 6 }}>{t("admin.content.debugHint")} {search.error ? <span className="k-error-text">{search.error.message}</span> : null}</div>
      </Card>
      {entry ? (hasEntry ? <EntryView kind={kind} name={entry} /> : <SingleField kind={kind} entry={entry} />) : <Card><EmptyState title={t("admin.content.lookEmpty")} hint={t("admin.content.lookEmptyHint")} /></Card>}
    </>
  );
}

function EntryView({ kind, name }: { kind: ContentKind; name: string }) {
  const t = useT();
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
  if (!x) return <div className="k-muted">{t("admin.content.readingName", { name })}</div>;
  const f0 = filter.toLowerCase();
  const list = x.fields.filter((f) => (!editable || f.writable) && (!changedOnly || f.changed) && (!f0 || f.path.toLowerCase().includes(f0))).sort((a, b) => Number(!!b.changed) - Number(!!a.changed));
  const apply = (f: ContentField, value: Json) => {
    set.mutate({ kind, entry: x.name, field: f.path, value }, {
      onSuccess: (r) => { patchField(f.path, r); setEditing(null); setErrors(({ [f.path]: _d, ...rest }) => rest); toastOk(`${x.name}.${f.path} = ${r.value}`, t("admin.content.untilReload")); },
      onError: (e) => setErrors((o) => ({ ...o, [f.path]: e.message })),
    });
  };
  const stacks = (title: string, s?: { item: string; name?: string | null; count: number }[]) =>
    s?.length ? <div className="ka-inline"><b style={{ fontSize: 12.5 }}>{title}</b><span className="ka-chips">{s.map((i, n) => <span key={n} className="ka-chip plain">{i.count} × {i.name ?? i.item}</span>)}</span></div> : null;
  return (
    <Card title={x.name} sub={<><span className="k-mono">{x.id}</span> · {x.kind} · <span className="k-mono">{x.guid}</span></>} actions={x.modified ? <Badge tone="ember" title={t("admin.content.changedTip")}>{t("admin.gameData.changed")}</Badge> : null}>
      <div className="k-stack" style={{ gap: 12 }}>
        {stacks(t("admin.content.needs"), x.inputs)}
        {stacks(t("admin.content.makes"), x.outputs)}
        {x.stacks?.length ? (
          <table className="ka-fields"><thead><tr><th>#</th><th>{t("admin.items.colItem")}</th><th className="num">{t("admin.creatures.count")}</th><th className="num">{t("admin.content.chance")}</th><th className="num">{t("admin.content.weight")}</th></tr></thead>
            <tbody>{x.stacks.map((s) => <tr key={s.index}><td>{s.index}</td><td>{s.name ?? s.item}</td><td className="num">{s.countMin}–{s.countMax}</td><td className="num">{s.chance}</td><td className="num">{s.weight}</td></tr>)}</tbody></table>
        ) : null}
        <div className="ka-inline">
          <TextInput aria-label={t("admin.gameData.filterFields")} placeholder={t("admin.content.filterFieldsPlaceholder")} value={filter} onChange={(e) => setFilter(e.target.value)} style={{ width: 220 }} />
          <Toggle checked={editable} onChange={setEditable} label={t("admin.content.changeableOnly")} />
          <Toggle checked={changedOnly} onChange={setChangedOnly} label={t("admin.content.changedOnly")} />
          <span className="k-spacer" />
          <span className="k-muted">{t("admin.content.fieldsCount", { n: list.length, total: x.fields.length })}</span>
        </div>
        {list.length ? (
          <table className="ka-fields">
            <thead><tr><th>{t("admin.creatures.field")}</th><th>{t("admin.content.now")}</th><th>{t("admin.content.gameValue")}</th><th /></tr></thead>
            <tbody>
              {list.slice(0, 200).map((f) => (
                <tr key={f.path} className={(f.changed ? "changed " : "") + (f.writable ? "" : "ro")}>
                  <td className="path">{f.path}<div className="k-hint">{f.writable ? f.type : t("admin.content.typeReadOnly", { type: f.type })}</div></td>
                  {editing === f.path ? (
                    <td colSpan={2}>
                      <FieldEditor f={f} busy={set.isPending} onCancel={() => setEditing(null)} onSet={(v) => apply(f, v)} />
                      {errors[f.path] ? <div className="k-error-text">{errors[f.path]}</div> : null}
                    </td>
                  ) : (
                    <>
                      <td className="val">{f.error ? <span className="k-muted">{f.error}</span> : f.value === "" ? t("admin.content.none") : f.value}</td>
                      <td className="val k-muted">{f.changed ? (f.original === "" ? t("admin.content.none") : f.original) : ""}</td>
                    </>
                  )}
                  <td style={{ whiteSpace: "nowrap", textAlign: "right" }}>
                    {f.writable && !f.error && editing !== f.path ? <Button size="s" onClick={() => setEditing(f.path)}>{t("admin.zones.change")}</Button> : null}{" "}
                    {f.changed && f.writable ? (
                      <Button size="s" variant="ghost" title={t("admin.content.resetTip")} loading={reset.isPending && reset.variables?.field === f.path}
                        onClick={() => reset.mutate({ kind, entry: x.name, field: f.path }, { onSuccess: (r) => { patchField(f.path, r); toastOk(t("admin.content.backTo", { name: x.name, path: f.path, value: r.value })); }, onError: (e) => toastError(e) })}>{t("common.reset")}</Button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : <EmptyState title={t("admin.content.noFieldMatches")} />}
        {list.length > 200 ? <div className="k-hint">{t("admin.content.first200", { n: list.length })}</div> : null}
      </div>
    </Card>
  );
}

/** The editor for one field: enum select, flags checkboxes, bool, number or text (Kiln.Admin's content_set types). */
function FieldEditor({ f, busy, onSet, onCancel }: { f: ContentField; busy: boolean; onSet: (v: Json) => void; onCancel: () => void }) {
  const t = useT();
  const num = ["int", "float", "time"].includes(f.valueType) && f.enumKind === "plain";
  const [text, setText] = useState(num ? String(f.value ?? "").replace(/s$/, "") : String(f.value ?? ""));
  const [flags, setFlags] = useState(new Set(String(f.value ?? "").split(",").map((s) => s.trim()).filter(Boolean)));
  const value = (): Json | Error => {
    if (f.enumKind === "flags") return [...flags].join(",");
    if (f.enumKind === "enum") return text;
    if (f.valueType === "bool") return text === "true";
    if (num) {
      const n = Number(text);
      if (text.trim() === "" || !Number.isFinite(n)) return new Error(t("admin.content.needNumber"));
      if (f.valueType === "int" && !Number.isInteger(n)) return new Error(t("admin.content.needWhole"));
      return n;
    }
    if (text.length > 2000) return new Error(t("admin.content.textMax"));
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
      {f.valueType === "time" ? <span className="k-hint">{t("admin.content.seconds")}</span> : null}
      <Button size="s" loading={busy} onClick={go}>{t("admin.gameData.set")}</Button>
      <Button size="s" variant="ghost" onClick={onCancel}>{t("common.cancel")}</Button>
    </div>
  );
}

/** Older Kiln.Admin without content_entry: one field at a time (content_get/content_set/content_reset). */
function SingleField({ kind, entry }: { kind: ContentKind; entry: string }) {
  const t = useT();
  const [field, setField] = useState("");
  const [cur, setCur] = useState<ContentValue | null>(null);
  const [val, setVal] = useState("");
  const get = useAdminCall<ContentValue>("content_get", { onSuccess: setCur, onError: (e) => toastError(e) });
  const set = useAdminCall<ContentValue>("content_set", { onSuccess: (r) => { setCur(r); toastOk(t("admin.content.setTemporary", { entry, field, value: r.value })); }, onError: (e) => toastError(e) });
  const reset = useAdminCall<ContentValue>("content_reset", { onSuccess: setCur, onError: (e) => toastError(e) });
  const ok = () => (FIELD.test(field.trim()) ? true : (toastError({ message: t("admin.content.fieldRule") }), false));
  const parsed = (): Json => (val === "true" ? true : val === "false" ? false : val.trim() !== "" && Number.isFinite(Number(val)) ? Number(val) : val);
  return (
    <Card title={entry} sub={t("admin.content.singleSub")}>
      <div className="k-stack">
        <div className="ka-inline">
          <TextInput aria-label={t("admin.creatures.field")} placeholder={t("admin.content.fieldPlaceholder")} value={field} onChange={(e) => setField(e.target.value)} mono style={{ width: 260 }} />
          <Button onClick={() => ok() && get.mutate({ kind, entry, field: field.trim() })}>{t("admin.content.read")}</Button>
        </div>
        {cur ? (
          <div className="ka-inline">
            <span>{rich("admin.content.nowGame", { value: <b className="k-mono">{cur.value}</b>, original: <span className="k-mono">{cur.original}</span> })}</span>
            <TextInput aria-label={t("admin.content.newValue")} placeholder={t("admin.content.newValuePlaceholder")} value={val} onChange={(e) => setVal(e.target.value)} style={{ width: 160 }} />
            <Button size="s" onClick={() => ok() && set.mutate({ kind, entry, field: field.trim(), value: parsed() })}>{t("admin.gameData.set")}</Button>
            <Button size="s" variant="ghost" onClick={() => ok() && reset.mutate({ kind, entry, field: field.trim() })}>{t("common.reset")}</Button>
          </div>
        ) : null}
      </div>
    </Card>
  );
}

function Packs() {
  const t = useT();
  const id = useServerId();
  const qc = useQueryClient();
  const has = useHasOp("content_report");
  const rep = useAdminQuery<{ report: string }>("content_report", {}, { enabled: has, refetchInterval: 30_000 });
  const reload = useAdminCall<{ report: string }>("content_reload", { onSuccess: (r) => { qc.setQueriesData({ queryKey: ["admin", id, "content_report"] }, r); toastOk(t("admin.content.packsApplied")); }, onError: (e) => toastError(e), invalidates: ["content_status"] });
  const r = useMemo<PackReport>(() => { try { return JSON.parse(rep.data?.report ?? "{}") as PackReport; } catch { return {}; } }, [rep.data]);
  return (
    <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
      <Card title={t("admin.content.dataPacks")} sub={t("admin.content.packsSub")} actions={<Button size="s" loading={reload.isPending} onClick={() => reload.mutate({})} title={t("admin.content.reapplyTip")}>{t("admin.content.reapply")}</Button>}>
        <div className="ka-list">
          {(r.packs ?? []).length ? r.packs!.map((p) => (
            <div key={p.file} className="k-stack" style={{ gap: 4 }}>
              <div className="ka-row-item"><b className="k-mono">{p.file}</b><span className="k-spacer" /><Badge tone="ok">{t("admin.content.applied", { n: p.applied })}</Badge>{p.errors?.length ? <Badge tone="err">{t("admin.content.errors", { n: p.errors.length })}</Badge> : null}{p.warnings?.length ? <Badge tone="warn">{t("admin.content.warnings", { n: p.warnings.length })}</Badge> : null}</div>
              {(p.errors ?? []).map((e, i) => <div key={i} className="k-error-text">{e}</div>)}
              {(p.warnings ?? []).map((w, i) => <div key={i} className="k-hint" style={{ color: "var(--k-warn)" }}>{w}</div>)}
            </div>
          )) : <EmptyState title={t("admin.content.noPacks")} hint={t("admin.content.noPacksHint")} />}
          {(r.skipped ?? []).length ? <div className="k-hint">{t("admin.content.skipped", { list: r.skipped!.join(", ") })}</div> : null}
        </div>
      </Card>
      <Card title={t("admin.content.conflicts")} sub={t("admin.content.conflictsSub")}>
        {(r.conflicts ?? []).length ? (
          <div className="ka-list">{r.conflicts!.map((c, i) => <div key={i}><div className="k-mono">{c.field}</div><div className="k-hint">{rich("admin.content.conflict", { winnerValue: <b>{c.winnerValue}</b>, winner: c.winner, loserValue: c.loserValue, loser: c.loser })}</div></div>)}</div>
        ) : <EmptyState title={t("admin.content.noConflicts")} />}
      </Card>
    </div>
  );
}

function Definitions() {
  const t = useT();
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
        <Banner tone="info" title={t("admin.content.syncTitle")}>
          {sync.data.content ? t("admin.content.syncServing", { state: sync.data.content.state, fields: sync.data.content.fields, bytes: sync.data.content.bytes, parts: sync.data.content.parts }) : t("admin.content.syncNothing")}
        </Banner>
      ) : null}
      <Card title={t("admin.content.defsTitle")} sub={t("admin.content.defsSub")} flush>
        <Table
          aria-label={t("admin.content.defsTable")}
          rows={defs.data ?? []}
          rowKey={(d) => d.guid}
          selectedKey={open}
          onRowClick={(d) => setOpen(open === d.guid ? null : d.guid)}
          columns={[
            { key: "n", header: t("admin.spawn.colName"), sort: (a, b) => (shown.get(a.guid) ?? a.name).localeCompare(shown.get(b.guid) ?? b.name), render: (d) => <NameId name={shown.get(d.guid) ?? d.name} id={d.name} /> },
            { key: "k", header: t("admin.items.colKind"), width: "80px", render: (d) => d.kind },
            { key: "b", header: t("admin.content.basedOn"), render: (d) => <span className="k-mono">{d.base ?? "-"}</span> },
            { key: "m", header: t("admin.content.mod"), width: "120px", render: (d) => d.mod ?? d.owner ?? "" },
            { key: "s", header: t("admin.players.colState"), width: "110px", render: (d) => <Badge tone={d.state === "registered" ? "ok" : "warn"}>{d.state ?? "?"}</Badge> },
          ]}
          empty={<EmptyState title={has ? t("admin.content.noDefs") : t("admin.content.noDefsOp")} />}
        />
      </Card>
      {sel ? <Card title={shown.get(sel.guid) ?? sel.name} sub={<span className="k-mono">{sel.name} · {sel.guid} · {sel.id}</span>}><JsonView value={sel.definition ?? null} open={2} /></Card> : null}
    </div>
  );
}

export default withAdmin(ContentPage);
