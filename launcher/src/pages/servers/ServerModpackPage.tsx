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

export default function ServerModpackPage() {
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
      const b = await runTask<BuiltModpack>(() => api.serverModpackBuild(id, name.trim(), version.trim()), { done: "Modpack built" });
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
      toast.ok("Published: players who join now get the offer");
      qc.invalidateQueries({ queryKey: sk.modpack(id) });
    } catch (e) {
      toast.error(e, "Not published");
    } finally {
      setBusy(null);
    }
  };
  const cols: Column<RequiredMod>[] = [
    { key: "id", header: "Mod", render: (m) => <span><b>{m.name ?? m.id}</b> <span className="k-hint">{m.id} {m.version}</span></span> },
    { key: "kind", header: "Kind", width: "90px", render: (m) => m.kind ?? "kiln" },
    { key: "hash", header: "kdh1", width: "170px", render: (m) => <span className="k-mono" title={m.hash}>{shortHash(m.hash)}</span> },
  ];
  const stale = built && cfg && cfg.sha256 !== built.sha256;
  return (
    <Page>
      <Card title="What players need" sub="Mods whose manifest says clients: &quot;mod&quot;; the server checks these hashes when a player joins." flush>
        <Table columns={cols} rows={q.data?.required ?? []} rowKey={(m) => `${m.kind}:${m.id}`} empty="Nothing: vanilla players and any Kiln client can join" aria-label="Required mods" />
      </Card>
      <Card title="Build" sub="A deterministic zip: the same mods give the same file and sha256.">
        <div className="k-row" style={{ gap: 12, alignItems: "flex-end", flexWrap: "wrap" }}>
          <Field label="Name"><TextInput value={name} placeholder={built?.manifest.name ?? "Ember Isles"} onChange={(e) => setName(e.target.value)} /></Field>
          <Field label="Version"><TextInput value={version} onChange={(e) => setVersion(e.target.value)} /></Field>
          <Button disabled={!name.trim() || !version.trim() || !q.data?.required.length} loading={busy === "build"} onClick={build}>Build</Button>
        </div>
        {built && (
          <div style={{ marginTop: 12 }}>
            <KeyValue rows={[
              ["File", <span className="k-mono">{built.file}</span>],
              ["sha256", <span className="k-mono">{built.sha256}</span>],
              ["Size", fmtBytes(built.size)],
              ["Mods", built.manifest.mods.map((m) => `${m.id} ${m.version}`).join(", ")],
            ]} />
          </div>
        )}
      </Card>
      <Card title="Publish" sub="Upload the zip somewhere players can download it (https), then enter its URL.">
        {stale && <Banner tone="warn" title="The published modpack is not the last build">Publish again after uploading the new zip.</Banner>}
        {cfg && <KeyValue rows={[["Published URL", <span className="k-mono">{cfg.url}</span>], ["sha256", <span className="k-mono">{cfg.sha256}</span>], cfg.version && ["Version", cfg.version]]} />}
        <div className="k-row" style={{ gap: 12, alignItems: "flex-end", marginTop: 12 }}>
          <Field label="Download URL"><TextInput mono value={url} placeholder="https://example.org/ember-isles-3.zip" onChange={(e) => setUrl(e.target.value)} style={{ minWidth: 420 }} /></Field>
          <Button icon={<Send size={16} />} variant="primary" disabled={!built || !/^https?:\/\//i.test(url.trim())} loading={busy === "publish"} onClick={publish}>Publish</Button>
        </div>
        {/^http:\/\//i.test(url.trim()) && <div className="k-hint">Players' launchers refuse http:// unless they allow it in Settings.</div>}
      </Card>
    </Page>
  );
}
