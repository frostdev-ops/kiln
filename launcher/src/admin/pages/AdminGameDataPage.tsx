// /servers/:id/admin/gamedata: kiln.gamedata through
// Kiln.Admin's gamedata_* ops: search a section, view an entry (its fields for templates, its JSON otherwise), set a
// path to a pack value, reset it. Changes are Kiln.Admin's layers: they end when it unloads.
import { useState } from "react";
import { useAdminCall, useAdminQuery, useHasOp } from "../../api/admin";
import { useT } from "../../i18n";
import { NeedsOp, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { Badge, Button, Card, EmptyState, JsonView, Page, PageHeader, Select, Table, TextInput, toastError, toastOk } from "../ui";

interface Hit { section: string; name: string; id: string; guid: string | null; modified: boolean; clone: boolean }
interface Field { path: string; type: string; value: unknown; writable: boolean; original: unknown }
interface Status { ready: boolean; sections?: Record<string, { count: number; changed: number }> }

const SECTIONS = ["templates", "arsenals", "sequences", "buffs", "perks", "skills", "items"];

/** A value text as pack JSON: JSON when it parses (numbers, true, {"mul": 1.5}, ["Move"]), else a plain string. */
function packValue(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

const show = (v: unknown) => (typeof v === "string" ? v : JSON.stringify(v));

function GameDataPage() {
  const t = useT();
  const status = useAdminQuery<Status>("gamedata_status", {}, { refetchInterval: 15000 });
  const [section, setSection] = useState("templates");
  const [q, setQ] = useState("");
  const [query, setQuery] = useState<{ section: string; q: string } | null>(null);
  const [entry, setEntry] = useState<Hit | null>(null);
  const hits = useAdminQuery<Hit[]>("gamedata_search", query ? { ...query, limit: 200 } : {}, { enabled: !!query });
  const sec = status.data?.sections;
  return (
    <Page>
      <PageHeader title={t("admin.gameData.title")} sub={sec ? SECTIONS.filter((s) => sec[s]).map((s) => (sec[s].changed ? t("admin.gameData.sectionChanged", { section: s, count: sec[s].count, changed: sec[s].changed }) : t("admin.gameData.section", { section: s, count: sec[s].count }))).join(" · ") : t("admin.gameData.sub")} />
      <OfflineNote />
      <NeedsOp ops={["gamedata_status", "gamedata_search", "gamedata_get"]} what={t("admin.gameData.title")}>
        <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
          <Card title={t("admin.gameData.search")} sub={t("admin.gameData.searchSub")}>
            <div className="k-stack">
              <div className="ka-inline">
                <Select aria-label={t("admin.gameData.sectionLabel")} value={section} onChange={(s) => { setSection(s); setEntry(null); }} options={SECTIONS} />
                <TextInput aria-label={t("admin.gameData.search")} placeholder={t("admin.gameData.searchPlaceholder")} value={q} onChange={(e) => setQ(e.target.value)} onKeyDown={(e) => { if (e.key === "Enter") setQuery({ section, q: q.trim() }); }} style={{ width: 220 }} />
                <Button size="s" loading={hits.isFetching && !hits.data} onClick={() => setQuery({ section, q: q.trim() })}>{t("admin.gameData.search")}</Button>
              </div>
              {hits.error ? <div className="k-error-text">{hits.error.message}</div> : null}
              {query ? (
                <Table aria-label={t("admin.gameData.entries")} rows={hits.data ?? []} rowKey={(h) => h.id} maxHeight={420} selectedKey={entry?.id ?? null} onRowClick={setEntry}
                  empty={<EmptyState title={t("admin.gameData.nothingFound")} hint={t("admin.gameData.nothingFoundHint")} />}
                  columns={[
                    { key: "n", header: t("admin.gameData.entry"), render: (h) => <span>{h.name} {h.modified ? <Badge tone="ember">{t("admin.gameData.changed")}</Badge> : null} {h.clone ? <Badge tone="info">{t("admin.gameData.clone")}</Badge> : null}</span> },
                    { key: "i", header: t("admin.items.colId"), width: "110px", render: (h) => <span className="k-mono">{h.id}</span> },
                  ]} />
              ) : null}
            </div>
          </Card>
          {entry ? <EntryCard key={entry.section + entry.id} hit={entry} /> : <Card title={t("admin.gameData.entry")}><EmptyState title={t("admin.gameData.noEntry")} hint={t("admin.gameData.noEntryHint")} /></Card>}
        </div>
      </NeedsOp>
      <div className="k-hint">{t("admin.gameData.valuesHint")}</div>
    </Page>
  );
}

function EntryCard({ hit }: { hit: Hit }) {
  const t = useT();
  const isTemplate = hit.section === "templates";
  const hasFields = useHasOp("template_fields");
  const fields = useAdminQuery<Field[]>("template_fields", { entry: hit.name }, { enabled: isTemplate && hasFields });
  const whole = useAdminQuery<unknown>("gamedata_get", { section: hit.section, entry: hit.name, path: "" }, { enabled: !isTemplate || !hasFields });
  const [path, setPath] = useState("");
  const [value, setValue] = useState("");
  const [filter, setFilter] = useState("");
  const cur = useAdminQuery<Field>("gamedata_get", { section: hit.section, entry: hit.name, path }, { enabled: path.trim().length > 0 });
  const refresh = () => { cur.refetch(); fields.refetch(); whole.refetch(); };
  const set = useAdminCall<{ applied: number }>("gamedata_set", { onSuccess: (r) => { toastOk(t("admin.gameData.setDone", { n: r.applied })); refresh(); }, onError: (e) => toastError(e) });
  const reset = useAdminCall<{ reset: boolean }>("gamedata_reset", { onSuccess: (r) => { toastOk(r.reset ? t("common.reset") : t("admin.gameData.nothingToReset")); refresh(); }, onError: (e) => toastError(e) });
  const list = (fields.data ?? []).filter((f) => !filter || f.path.toLowerCase().includes(filter.toLowerCase()));
  return (
    <Card title={hit.name} sub={`${hit.section} · ${hit.id}${hit.guid ? ` · ${hit.guid}` : ""}`}>
      <div className="k-stack">
        <div className="ka-inline">
          <TextInput aria-label={t("admin.gameData.path")} mono placeholder={t("admin.gameData.pathPlaceholder")} value={path} onChange={(e) => setPath(e.target.value)} style={{ width: 260 }} />
          <TextInput aria-label={t("admin.gameData.value")} mono placeholder={t("admin.gameData.valuePlaceholder")} value={value} onChange={(e) => setValue(e.target.value)} style={{ width: 170 }} />
          <Button size="s" variant="primary" disabled={!path.trim() || !value.trim()} loading={set.isPending}
            onClick={() => set.mutate({ section: hit.section, entry: hit.name, path: path.trim(), value: packValue(value.trim()) as never })}>{t("admin.gameData.set")}</Button>
          <Button size="s" variant="ghost" disabled={!path.trim()} loading={reset.isPending} onClick={() => reset.mutate({ section: hit.section, entry: hit.name, path: path.trim() })}>{t("common.reset")}</Button>
        </div>
        {cur.data && path.trim() ? (
          <div className="k-hint k-mono">{cur.data.writable ? t("admin.gameData.fieldInfo", { type: cur.data.type, value: show(cur.data.value), original: show(cur.data.original) }) : t("admin.gameData.fieldInfoRo", { type: cur.data.type, value: show(cur.data.value), original: show(cur.data.original) })}</div>
        ) : cur.error ? <div className="k-error-text">{cur.error.message}</div> : null}
        {isTemplate && hasFields ? (
          <>
            <TextInput aria-label={t("admin.gameData.filterFields")} placeholder={t("admin.gameData.filterFieldsPlaceholder")} value={filter} onChange={(e) => setFilter(e.target.value)} />
            <Table aria-label={t("admin.gameData.fields")} rows={list} rowKey={(f) => f.path} maxHeight={360} onRowClick={(f) => { setPath(f.path); setValue(show(f.value)); }}
              columns={[
                { key: "p", header: t("admin.gameData.path"), render: (f) => <span className="k-mono">{f.path}</span> },
                { key: "v", header: t("admin.gameData.value"), width: "150px", render: (f) => <span className="k-mono">{show(f.value)}</span> },
                { key: "t", header: t("admin.gameData.type"), width: "150px", render: (f) => <span className="k-hint">{f.type.replace(/^keen::/, "")}</span> },
              ]} />
          </>
        ) : whole.data !== undefined ? <JsonView value={whole.data} /> : whole.error ? <div className="k-error-text">{whole.error.message}</div> : null}
      </div>
    </Card>
  );
}

export default withAdmin(GameDataPage);
