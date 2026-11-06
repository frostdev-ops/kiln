// /servers/:id/admin/configs: the Kiln mods' configs on the running server: config_list, then
// SchemaForm on the live schema (config_get) with every change through Kiln's own validation (config_set), applied at
// once except entries marked restart; config_reload re-reads the file.
import { useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useAdminCall, useAdminQuery, useHasOp } from "../../api/admin";
import type { Json, SchemaSnapshot, ValidationError } from "../../api/commands";
import { NeedsOp, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import type { ConfigFile, KilnMod } from "../types";
import { Banner, Button, Card, EmptyState, Page, PageHeader, SchemaForm, Tabs, TextInput, checkValue, fmtAgo, toastOk } from "../ui";

const CONFIG_PATH = /^[A-Za-z0-9_]{1,64}(\.[A-Za-z0-9_]{1,64}){0,8}$/;

/** The live schema (Config.Schema()) as the SchemaForm's snapshot type. */
function toSnapshot(c: ConfigFile): SchemaSnapshot {
  return {
    format: 1, source: "kiln", file: c.file, valueFormat: "jsonc", id: c.id, mod: c.mod, modVersion: null, kind: c.kind, written: c.loadedAt ?? null, kiln: null,
    sections: c.sections.map((s) => ({ path: s.path, comment: s.comment ?? null, synced: !!s.synced })),
    entries: c.entries.map((e) => ({ path: e.path, type: e.type, comment: e.comment ?? null, default: e.default as Json, requiresRestart: !!e.requiresRestart, synced: !!e.synced, min: e.min, max: e.max, options: e.options })),
  };
}

/** Nested values by path: what the file holds (fileValue while a restart is pending), else the running value. */
function toValues(c: ConfigFile): Json {
  const root: Record<string, Json> = {};
  for (const e of c.entries) {
    const parts = e.path.split(".");
    let cur = root;
    for (const p of parts.slice(0, -1)) cur = (cur[p] ??= {}) as Record<string, Json>;
    cur[parts[parts.length - 1]] = (e.fileValue !== undefined ? e.fileValue : e.value) as Json;
  }
  return root;
}

/** A config's tab label: its mod's name when the config is the mod's own (short enough for a tab), else its id. */
function configLabel(c: ConfigFile, mods: KilnMod[] | undefined): string {
  const name = c.id === c.mod ? mods?.find((m) => m.id === c.mod)?.name : undefined;
  return name && name.length <= 24 ? name : c.id.charAt(0).toUpperCase() + c.id.slice(1);
}

const fmt = (v: unknown) => (Array.isArray(v) ? v.join(", ") || "(empty)" : typeof v === "boolean" ? (v ? "on" : "off") : String(v));

function ConfigsPage() {
  const id = useServerId();
  const qc = useQueryClient();
  const list = useAdminQuery<ConfigFile[]>("config_list", {}, { refetchInterval: 10_000 });
  const hasMods = useHasOp("mods");
  const mods = useAdminQuery<KilnMod[]>("mods", {}, { enabled: hasMods, staleTime: 30_000 });
  const [sel, setSel] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  const [errors, setErrors] = useState<ValidationError[]>([]);
  const cur = list.data?.find((c) => c.id === (sel ?? list.data?.[0]?.id)) ?? null;
  const put = (c: ConfigFile) => qc.setQueriesData<ConfigFile[]>({ queryKey: ["admin", id, "config_list"] }, (old) => old?.map((x) => (x.id === c.id ? c : x)));
  const set = useAdminCall<ConfigFile>("config_set");
  const reload = useAdminCall<ConfigFile>("config_reload", { onSuccess: (c) => { put(c); toastOk(`Re-read ${c.id}`); } });
  const snapshot = useMemo(() => (cur ? toSnapshot(cur) : null), [cur]);
  const values = useMemo(() => (cur ? toValues(cur) : null), [cur]);
  const pending = cur?.entries.filter((e) => e.fileValue !== undefined && JSON.stringify(e.fileValue) !== JSON.stringify(e.value)).map((e) => e.path) ?? [];
  const change = (path: string, value: Json) => {
    if (!cur || !snapshot) return;
    const entry = snapshot.entries.find((e) => e.path === path);
    const bad = !CONFIG_PATH.test(path) ? "Unexpected characters in the setting's path." : entry ? checkValue(entry, value) : null;
    const tooLong = Array.isArray(value) && value.length > 500 ? "Lists hold up to 500 values." : typeof value === "string" && value.length > 2000 ? "Text up to 2000 characters." : null;
    const err = bad ?? tooLong;
    setErrors((e) => [...e.filter((x) => x.path !== path), ...(err ? [{ path, message: err }] : [])]);
    if (err) return;
    set.mutate({ id: cur.id, path, value }, {
      onSuccess: (c) => {
        put(c);
        const e = c.entries.find((x) => x.path === path);
        toastOk(`${c.id}: ${path} = ${fmt(e?.fileValue ?? e?.value ?? value)}`, e?.requiresRestart && e.fileValue !== undefined ? "applies when the mod loads next" : undefined);
      },
      onError: (e) => setErrors((x) => [...x.filter((y) => y.path !== path), { path, message: e.message }]),
    });
  };
  const file = String(cur?.file ?? "").replace(/^.*[\\/]kiln[\\/]/, "kiln/").replace(/\\/g, "/");
  return (
    <Page>
      <PageHeader title="Mod configs" sub="Settings of the Kiln mods on the server, from their config files. Each change is checked by Kiln and applied while the server runs." />
      <OfflineNote />
      <NeedsOp ops={["config_list", "config_set"]} what="Mod configs">
        <div className="k-stack" style={{ gap: 18 }}>
          {list.data?.length ? (
            <Tabs aria-label="Configs" value={cur?.id ?? ""} onChange={(v) => { setSel(v); setErrors([]); }}
              items={list.data.map((c) => ({
                id: c.id,
                count: c.entries.length,
                label: <>{configLabel(c, mods.data)}{c.error ? <span className="k-tone-err"> error</span> : c.warnings?.length ? <span className="k-tone-warn" title={c.warnings.join(" · ")}> corrected</span> : null}</>,
              }))} />
          ) : null}
          {list.data && !list.data.length ? <EmptyState title="No mod configs" hint="Mods that register a config show up here." /> : null}
          {list.error ? <div className="k-error-text">{list.error.message}</div> : null}
          {cur && snapshot && values ? (
            <Card title={configLabel(cur, mods.data)} sub={<>config <span className="k-mono">{cur.id}</span> of mod <span className="k-mono">{cur.mod}</span> · {cur.kind} · <span className="k-mono">{file}</span>{cur.loadedAt ? ` · read ${fmtAgo(cur.loadedAt)}` : ""}</>}
              actions={<>
                <TextInput aria-label="Filter settings" placeholder="Filter…" value={filter} onChange={(e) => setFilter(e.target.value)} style={{ width: 160 }} />
                <Button size="s" variant="ghost" loading={reload.isPending} onClick={() => reload.mutate({ id: cur.id })} title="Read the file again (Kiln also does this by itself when it changes)">Re-read file</Button>
              </>}>
              <div className="k-stack" style={{ gap: 12 }}>
                {cur.error ? <Banner tone="err" title="The file has an error, so Kiln keeps the previous values">{cur.error} Fix the file by hand, or set a value here: Kiln then writes the file again in full.</Banner> : null}
                {cur.warnings?.length ? <Banner tone="warn" title="Kiln corrected the file when it read it">{cur.warnings.join(" · ")}</Banner> : null}
                <SchemaForm schema={snapshot} values={values} onChange={change} onReset={(path) => { const e = snapshot.entries.find((x) => x.path === path); if (e) change(path, e.default); }} errors={errors} pending={pending} disabled={set.isPending} filter={filter} />
                {pending.length ? <Banner tone="ember">Saved values marked “pending” keep their running value until the mod loads again (reload it on the Mods page).</Banner> : null}
              </div>
            </Card>
          ) : list.data?.length || !list.data ? (
            <EmptyState title={list.data ? "Pick a config" : "Loading…"} hint="Changes are checked by Kiln (types, ranges, options), written to the file with its comments and applied at once." />
          ) : null}
        </div>
      </NeedsOp>
    </Page>
  );
}

export default withAdmin(ConfigsPage);
