// /servers/:id/admin/configs: the Kiln mods' configs on the running server: config_list, then
// SchemaForm on the live schema (config_get) with every change through Kiln's own validation (config_set), applied at
// once except entries marked restart; config_reload re-reads the file.
import { useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { useAdminCall, useAdminQuery, useHasOp } from "../../api/admin";
import type { Json, SchemaSnapshot, ValidationError } from "../../api/shared";
import { rich, t as tr, useT } from "../../i18n";
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
    entries: c.entries.map((e) => ({ path: e.path, type: e.type, comment: e.comment ?? null, default: e.default as Json, requiresRestart: !!e.requiresRestart, synced: !!e.synced, min: e.min, max: e.max, step: e.step, softMin: e.softMin, softMax: e.softMax, options: e.options })),
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

const fmt = (v: unknown) => (Array.isArray(v) ? v.join(", ") || tr("admin.configs.empty") : typeof v === "boolean" ? (v ? tr("admin.configs.on") : tr("admin.configs.off")) : String(v));

function ConfigsPage() {
  const t = useT();
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
  const reload = useAdminCall<ConfigFile>("config_reload", { onSuccess: (c) => { put(c); toastOk(t("admin.configs.reread", { id: c.id })); } });
  const snapshot = useMemo(() => (cur ? toSnapshot(cur) : null), [cur]);
  const values = useMemo(() => (cur ? toValues(cur) : null), [cur]);
  const pending = cur?.entries.filter((e) => e.fileValue !== undefined && JSON.stringify(e.fileValue) !== JSON.stringify(e.value)).map((e) => e.path) ?? [];
  const change = (path: string, value: Json) => {
    if (!cur || !snapshot) return;
    const entry = snapshot.entries.find((e) => e.path === path);
    const bad = !CONFIG_PATH.test(path) ? t("admin.configs.badPath") : entry ? checkValue(entry, value) : null;
    const tooLong = Array.isArray(value) && value.length > 500 ? t("admin.configs.listMax") : typeof value === "string" && value.length > 2000 ? t("admin.configs.textMax") : null;
    const err = bad ?? tooLong;
    setErrors((e) => [...e.filter((x) => x.path !== path), ...(err ? [{ path, message: err }] : [])]);
    if (err) return;
    set.mutate({ id: cur.id, path, value }, {
      onSuccess: (c) => {
        put(c);
        const e = c.entries.find((x) => x.path === path);
        toastOk(`${c.id}: ${path} = ${fmt(e?.fileValue ?? e?.value ?? value)}`, e?.requiresRestart && e.fileValue !== undefined ? t("admin.configs.appliesNext") : undefined);
      },
      onError: (e) => setErrors((x) => [...x.filter((y) => y.path !== path), { path, message: e.message }]),
    });
  };
  const file = String(cur?.file ?? "").replace(/^.*[\\/]kiln[\\/]/, "kiln/").replace(/\\/g, "/");
  return (
    <Page>
      <PageHeader title={t("admin.configs.title")} sub={t("admin.configs.sub")} />
      <OfflineNote />
      <NeedsOp ops={["config_list", "config_set"]} what={t("admin.configs.title")}>
        <div className="k-stack" style={{ gap: 18 }}>
          {list.data?.length ? (
            <Tabs aria-label={t("admin.configs.tabs")} value={cur?.id ?? ""} onChange={(v) => { setSel(v); setErrors([]); }}
              items={list.data.map((c) => ({
                id: c.id,
                count: c.entries.length,
                label: <>{configLabel(c, mods.data)}{c.error ? <span className="k-tone-err">{t("admin.configs.tabError")}</span> : c.warnings?.length ? <span className="k-tone-warn" title={c.warnings.join(" · ")}>{t("admin.configs.tabCorrected")}</span> : null}</>,
              }))} />
          ) : null}
          {list.data && !list.data.length ? <EmptyState title={t("admin.configs.none")} hint={t("admin.configs.noneHint")} /> : null}
          {list.error ? <div className="k-error-text">{list.error.message}</div> : null}
          {cur && snapshot && values ? (
            <Card title={configLabel(cur, mods.data)} sub={<>{cur.loadedAt
                ? rich("admin.configs.cardSubRead", { id: <span className="k-mono">{cur.id}</span>, mod: <span className="k-mono">{cur.mod}</span>, kind: cur.kind, file: <span className="k-mono">{file}</span>, ago: fmtAgo(cur.loadedAt) })
                : rich("admin.configs.cardSub", { id: <span className="k-mono">{cur.id}</span>, mod: <span className="k-mono">{cur.mod}</span>, kind: cur.kind, file: <span className="k-mono">{file}</span> })}</>}
              actions={<>
                <TextInput aria-label={t("admin.configs.filterLabel")} placeholder={t("admin.configs.filterPlaceholder")} value={filter} onChange={(e) => setFilter(e.target.value)} style={{ width: 160 }} />
                <Button size="s" variant="ghost" loading={reload.isPending} onClick={() => reload.mutate({ id: cur.id })} title={t("admin.configs.rereadTip")}>{t("admin.configs.rereadFile")}</Button>
              </>}>
              <div className="k-stack" style={{ gap: 12 }}>
                {cur.error ? <Banner tone="err" title={t("admin.configs.errorTitle")}>{t("admin.configs.errorBody", { error: cur.error })}</Banner> : null}
                {cur.warnings?.length ? <Banner tone="warn" title={t("admin.configs.correctedTitle")}>{cur.warnings.join(" · ")}</Banner> : null}
                <SchemaForm schema={snapshot} values={values} onChange={change} onReset={(path) => { const e = snapshot.entries.find((x) => x.path === path); if (e) change(path, e.default); }} errors={errors} pending={pending} disabled={set.isPending} filter={filter} />
                {pending.length ? <Banner tone="ember">{t("admin.configs.pendingNote")}</Banner> : null}
              </div>
            </Card>
          ) : list.data?.length || !list.data ? (
            <EmptyState title={list.data ? t("admin.configs.pick") : t("admin.map.loading")} hint={t("admin.configs.pickHint")} />
          ) : null}
        </div>
      </NeedsOp>
    </Page>
  );
}

export default withAdmin(ConfigsPage);
