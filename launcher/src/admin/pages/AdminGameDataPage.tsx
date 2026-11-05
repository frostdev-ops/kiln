// /servers/:id/admin/gamedata: kiln.gamedata through
// Kiln.Admin's gamedata_* ops: search a section, view an entry (its fields for templates, its JSON otherwise), set a
// path to a pack value, reset it. Changes are Kiln.Admin's layers: they end when it unloads.
import { useState } from "react";
import { useAdminCall, useAdminQuery, useHasOp } from "../../api/admin";
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
  const status = useAdminQuery<Status>("gamedata_status", {}, { refetchInterval: 15000 });
  const [section, setSection] = useState("templates");
  const [q, setQ] = useState("");
  const [query, setQuery] = useState<{ section: string; q: string } | null>(null);
  const [entry, setEntry] = useState<Hit | null>(null);
  const hits = useAdminQuery<Hit[]>("gamedata_search", query ? { ...query, limit: 200 } : {}, { enabled: !!query });
  const sec = status.data?.sections;
  return (
    <Page>
      <PageHeader title="Game data" sub={sec ? SECTIONS.filter((s) => sec[s]).map((s) => `${s} ${sec[s].count}${sec[s].changed ? ` (${sec[s].changed} changed)` : ""}`).join(" · ") : "Templates, arsenals, sequences, buffs, perks, skills and item data"} />
      <OfflineNote />
      <NeedsOp ops={["gamedata_status", "gamedata_search", "gamedata_get"]} what="Game data">
        <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
          <Card title="Search" sub="By name, 0x id or GUID">
            <div className="k-stack">
              <div className="ka-inline">
                <Select aria-label="Section" value={section} onChange={(s) => { setSection(s); setEntry(null); }} options={SECTIONS} />
                <TextInput aria-label="Search" placeholder="name contains…" value={q} onChange={(e) => setQ(e.target.value)} onKeyDown={(e) => { if (e.key === "Enter") setQuery({ section, q: q.trim() }); }} style={{ width: 220 }} />
                <Button size="s" variant="primary" loading={hits.isFetching && !hits.data} onClick={() => setQuery({ section, q: q.trim() })}>Search</Button>
              </div>
              {hits.error ? <div className="k-error-text">{hits.error.message}</div> : null}
              {query ? (
                <Table aria-label="Entries" rows={hits.data ?? []} rowKey={(h) => h.id} maxHeight={420} selectedKey={entry?.id ?? null} onRowClick={setEntry}
                  empty={<EmptyState title="Nothing found" hint="Try a shorter name." />}
                  columns={[
                    { key: "n", header: "Entry", render: (h) => <span>{h.name} {h.modified ? <Badge tone="ember">changed</Badge> : null} {h.clone ? <Badge tone="info">clone</Badge> : null}</span> },
                    { key: "i", header: "Id", width: "110px", render: (h) => <span className="k-mono">{h.id}</span> },
                  ]} />
              ) : null}
            </div>
          </Card>
          {entry ? <EntryCard key={entry.section + entry.id} hit={entry} /> : <Card title="Entry"><EmptyState title="No entry picked" hint="Search a section and click an entry." /></Card>}
        </div>
      </NeedsOp>
      <div className="k-hint">Values are pack JSON: numbers, names, flag lists, objects, {"{\"mul\": 1.5}"}. They are Kiln.Admin's: they go back when Kiln.Admin reloads or the server restarts.</div>
    </Page>
  );
}

function EntryCard({ hit }: { hit: Hit }) {
  const isTemplate = hit.section === "templates";
  const hasFields = useHasOp("template_fields");
  const fields = useAdminQuery<Field[]>("template_fields", { entry: hit.name }, { enabled: isTemplate && hasFields });
  const whole = useAdminQuery<unknown>("gamedata_get", { section: hit.section, entry: hit.name, path: "" }, { enabled: !isTemplate || !hasFields });
  const [path, setPath] = useState("");
  const [value, setValue] = useState("");
  const [filter, setFilter] = useState("");
  const cur = useAdminQuery<Field>("gamedata_get", { section: hit.section, entry: hit.name, path }, { enabled: path.trim().length > 0 });
  const refresh = () => { cur.refetch(); fields.refetch(); whole.refetch(); };
  const set = useAdminCall<{ applied: number }>("gamedata_set", { onSuccess: (r) => { toastOk(`Set: ${r.applied} value(s) changed`); refresh(); }, onError: (e) => toastError(e) });
  const reset = useAdminCall<{ reset: boolean }>("gamedata_reset", { onSuccess: (r) => { toastOk(r.reset ? "Reset" : "Nothing of Kiln.Admin's to reset"); refresh(); }, onError: (e) => toastError(e) });
  const list = (fields.data ?? []).filter((f) => !filter || f.path.toLowerCase().includes(filter.toLowerCase()));
  return (
    <Card title={hit.name} sub={`${hit.section} · ${hit.id}${hit.guid ? ` · ${hit.guid}` : ""}`}>
      <div className="k-stack">
        <div className="ka-inline">
          <TextInput aria-label="Path" mono placeholder="path (e.g. Health.max)" value={path} onChange={(e) => setPath(e.target.value)} style={{ width: 260 }} />
          <TextInput aria-label="Value" mono placeholder='value: 120, {"mul": 1.5}' value={value} onChange={(e) => setValue(e.target.value)} style={{ width: 170 }} />
          <Button size="s" variant="primary" disabled={!path.trim() || !value.trim()} loading={set.isPending}
            onClick={() => set.mutate({ section: hit.section, entry: hit.name, path: path.trim(), value: packValue(value.trim()) as never })}>Set</Button>
          <Button size="s" variant="ghost" disabled={!path.trim()} loading={reset.isPending} onClick={() => reset.mutate({ section: hit.section, entry: hit.name, path: path.trim() })}>Reset</Button>
        </div>
        {cur.data && path.trim() ? (
          <div className="k-hint k-mono">{cur.data.type}: {show(cur.data.value)} (game: {show(cur.data.original)}){cur.data.writable ? "" : " · read-only"}</div>
        ) : cur.error ? <div className="k-error-text">{cur.error.message}</div> : null}
        {isTemplate && hasFields ? (
          <>
            <TextInput aria-label="Filter fields" placeholder="filter fields…" value={filter} onChange={(e) => setFilter(e.target.value)} />
            <Table aria-label="Fields" rows={list} rowKey={(f) => f.path} maxHeight={360} onRowClick={(f) => { setPath(f.path); setValue(show(f.value)); }}
              columns={[
                { key: "p", header: "Path", render: (f) => <span className="k-mono">{f.path}</span> },
                { key: "v", header: "Value", width: "150px", render: (f) => <span className="k-mono">{show(f.value)}</span> },
                { key: "t", header: "Type", width: "150px", render: (f) => <span className="k-hint">{f.type.replace(/^keen::/, "")}</span> },
              ]} />
          </>
        ) : whole.data !== undefined ? <JsonView value={whole.data} /> : whole.error ? <div className="k-error-text">{whole.error.message}</div> : null}
      </div>
    </Card>
  );
}

export default withAdmin(GameDataPage);
