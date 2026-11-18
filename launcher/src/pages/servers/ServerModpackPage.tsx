// /servers/:id/modpack: the server side of modpacks. What players need (mods with
// clients: "mod", by kdh1), a deterministic zip of them, and the URL players download it from (written into
// network.json; a running server re-reads it at once). Hosting the zip is up to the admin.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Send } from "lucide-react";
import { useState } from "react";
import { useParams } from "react-router-dom";
import * as api from "../../api/commands";
import type { BuiltModpack, RequiredMod } from "../../api/commands";
import { Banner, Button, Card, Field, KeyValue, Page, Table, TextInput, fmtBytes, runTask, shortHash, toast, type Column } from "../../ui";
import { sk } from "./serverApi";
import { useT } from "../../i18n";

export default function ServerModpackPage() {
  const t = useT();
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: sk.modpack(id), queryFn: () => api.serverModpackStatus(id) });
  const [name, setName] = useState("");
  const [version, setVersion] = useState("1");
  const [url, setUrl] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const built = q.data?.built;
  const cfg = q.data?.config;
  const build = async () => {
    setBusy("build");
    try {
      const b = await runTask<BuiltModpack>(() => api.serverModpackBuild(id, name.trim(), version.trim()), { done: t("servers.modpack.built") });
      setName(b.manifest.name);
    } catch { /* toasted */ } finally {
      setBusy(null);
      qc.invalidateQueries({ queryKey: sk.modpack(id) });
    }
  };
  const publish = async () => {
    setBusy("publish");
    try {
      await api.serverModpackPublish(id, url.trim());
      toast.ok(t("servers.modpack.published"));
      qc.invalidateQueries({ queryKey: sk.modpack(id) });
    } catch (e) {
      toast.error(e, t("servers.modpack.notPublished"));
    } finally {
      setBusy(null);
    }
  };
  const cols: Column<RequiredMod>[] = [
    { key: "id", header: t("servers.modpack.mod"), render: (m) => <span><b>{m.name ?? m.id}</b> <span className="k-hint">{m.id} {m.version}</span></span> },
    { key: "kind", header: t("servers.modpack.kind"), width: "90px", render: (m) => m.kind ?? "kiln" },
    { key: "hash", header: "kdh1", width: "170px", render: (m) => <span className="k-mono" title={m.hash}>{shortHash(m.hash)}</span> },
  ];
  const stale = built && cfg && cfg.sha256 !== built.sha256;
  return (
    <Page>
      <Card title={t("servers.modpack.needTitle")} sub={t("servers.modpack.needSub")} flush>
        <Table columns={cols} rows={q.data?.required ?? []} rowKey={(m) => `${m.kind}:${m.id}`} empty={t("servers.modpack.needEmpty")} aria-label={t("servers.modpack.required")} />
      </Card>
      <Card title={t("servers.modpack.buildTitle")} sub={t("servers.modpack.buildSub")}>
        <div className="k-row" style={{ gap: 12, alignItems: "flex-end", flexWrap: "wrap" }}>
          <Field label={t("servers.modpack.name")}><TextInput value={name} placeholder={built?.manifest.name ?? t("servers.list.namePlaceholder")} onChange={(e) => setName(e.target.value)} /></Field>
          <Field label={t("servers.modpack.version")}><TextInput value={version} onChange={(e) => setVersion(e.target.value)} /></Field>
          <Button disabled={!name.trim() || !version.trim() || !q.data?.required.length} loading={busy === "build"} onClick={build}>{t("servers.modpack.buildTitle")}</Button>
        </div>
        {built && (
          <div style={{ marginTop: 12 }}>
            <KeyValue rows={[
              [t("servers.modpack.file"), <span className="k-mono">{built.file}</span>],
              ["sha256", <span className="k-mono">{built.sha256}</span>],
              [t("servers.modpack.size"), fmtBytes(built.size)],
              [t("servers.modpack.mods"), built.manifest.mods.map((m) => `${m.id} ${m.version}`).join(", ")],
            ]} />
          </div>
        )}
      </Card>
      <Card title={t("servers.modpack.publish")} sub={t("servers.modpack.publishSub")}>
        {stale && <Banner tone="warn" title={t("servers.modpack.stale")}>{t("servers.modpack.staleText")}</Banner>}
        {cfg && <KeyValue rows={[[t("servers.modpack.publishedUrl"), <span className="k-mono">{cfg.url}</span>], ["sha256", <span className="k-mono">{cfg.sha256}</span>], cfg.version && [t("servers.modpack.version"), cfg.version]]} />}
        <div className="k-row" style={{ gap: 12, alignItems: "flex-end", marginTop: 12 }}>
          <Field label={t("servers.modpack.downloadUrl")}><TextInput mono value={url} placeholder="https://example.org/ember-isles-3.zip" onChange={(e) => setUrl(e.target.value)} style={{ minWidth: 420 }} /></Field>
          <Button icon={<Send size={16} />} variant="primary" disabled={!built || !/^https?:\/\//i.test(url.trim())} loading={busy === "publish"} onClick={publish}>{t("servers.modpack.publish")}</Button>
        </div>
        {/^http:\/\//i.test(url.trim()) && <div className="k-hint">{t("servers.modpack.httpHint")}</div>}
      </Card>
    </Page>
  );
}
