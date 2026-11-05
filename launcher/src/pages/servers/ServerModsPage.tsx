// /servers/:id/mods: the server's mod folders (kiln/mods, kiln/native) and the library mods synced into them
//. Synced folders carry .kiln-source.json; folders without it (hand-placed, deploy-server.sh)
// are listed but never replaced or deleted. A running server reloads changed mods through the admin ops.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { RefreshCw } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useParams } from "react-router-dom";
import * as api from "../../api/commands";
import type { LibraryEntry, LibraryRef } from "../../api/commands";
import { useLibrary } from "../../api/hooks";
import { Badge, Button, Card, Select, Table, Toggle, runTask, shortHash, toast, type Column } from "../../ui";
import { sk, type ServerModX } from "./serverApi";

const refKey = (r: LibraryRef) => `${r.kind}:${r.id}`;

export default function ServerModsPage() {
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const mods = useQuery({ queryKey: sk.mods(id), queryFn: () => api.serverMods(id) as Promise<ServerModX[]> });
  const kiln = useLibrary("kiln");
  const native = useLibrary("native");
  const library: LibraryEntry[] = useMemo(() => [...(kiln.data ?? []), ...(native.data ?? [])], [kiln.data, native.data]);
  // the wanted set: what is synced now, edited here
  const [want, setWant] = useState<Map<string, LibraryRef>>(new Map());
  useEffect(() => {
    const m = new Map<string, LibraryRef>();
    for (const s of mods.data ?? []) if (s.source) m.set(refKey(s.source), s.source);
    setWant(m);
  }, [mods.data]);
  const [syncing, setSyncing] = useState(false);
  const synced = new Set((mods.data ?? []).filter((m) => m.source).map((m) => refKey(m.source!)));
  const dirty = want.size !== synced.size || [...want.keys()].some((k) => !synced.has(k)) ||
    (mods.data ?? []).some((m) => m.source && want.get(refKey(m.source))?.hash !== m.source.hash);
  const sync = async () => {
    setSyncing(true);
    try {
      await runTask(() => api.serverModsSync(id, [...want.values()]), { done: "Mods synced" });
    } catch { /* toasted */ } finally {
      setSyncing(false);
      qc.invalidateQueries({ queryKey: sk.mods(id) });
    }
  };
  const toggle = async (m: ServerModX, enabled: boolean) => {
    try {
      await api.serverModEnable(id, m.folder, enabled);
      qc.invalidateQueries({ queryKey: sk.mods(id) });
    } catch (e) {
      toast.error(e);
    }
  };
  const cols: Column<ServerModX>[] = [
    { key: "id", header: "Mod", render: (m) => <span><b>{m.id}</b> <span className="k-hint">{m.version ?? ""}</span></span>, sort: (a, b) => a.id.localeCompare(b.id) },
    { key: "kind", header: "Kind", width: "90px", render: (m) => m.kind ?? "kiln" },
    { key: "clients", header: "Players need", width: "120px", render: (m) => (m.clients === "mod" ? <Badge tone="ember">the mod</Badge> : m.clients === "kiln" ? <Badge>Kiln</Badge> : <span className="k-hint">nothing</span>) },
    { key: "source", header: "Source", width: "180px", render: (m) => (m.source ? <span className="k-mono" title={m.source.hash}>library {shortHash(m.source.hash)}</span> : <span className="k-hint">by hand</span>) },
    { key: "state", header: "State", width: "110px", render: (m) => m.state ?? "-" },
    { key: "on", header: "On", width: "70px", render: (m) => <Toggle checked={m.enabled} onChange={(v) => toggle(m, v)} aria-label={`enable ${m.id}`} /> },
  ];
  return (
    <div className="k-stack" style={{ gap: 16 }}>
      <Card title="Mods on the server" sub="A .disabled file switches a mod off at the next start." flush>
        <Table columns={cols} rows={mods.data ?? []} rowKey={(m) => `${m.kind}:${m.folder}`} empty="No mods in kiln/mods" aria-label="Server mods" />
      </Card>
      <Card title="Sync from the library" sub="The server gets exactly the checked versions; mods put there by hand stay."
        actions={<Button icon={<RefreshCw size={16} />} variant="primary" disabled={!dirty} loading={syncing} onClick={sync}>Sync</Button>}>
        <div className="k-stack">
          {library.length === 0 && <div className="k-hint">The library is empty: import mods on the Library page.</div>}
          {library.map((e) => {
            const k = `${e.kind}:${e.id}`;
            const cur = want.get(k);
            const handPlaced = (mods.data ?? []).some((m) => m.id === e.id && !m.source);
            return (
              <div key={k} className="k-row" style={{ gap: 12 }}>
                <Toggle checked={!!cur} disabled={handPlaced} label={`${e.name} (${e.kind})`} hint={handPlaced ? "a folder put there by hand has this id" : undefined}
                  onChange={(on) => {
                    const m = new Map(want);
                    const v = e.versions[0];
                    if (on && v) m.set(k, { kind: e.kind, id: e.id, version: v.version, hash: v.hash });
                    else m.delete(k);
                    setWant(m);
                  }} />
                {cur && e.versions.length > 1 && (
                  <Select value={cur.hash} onChange={(hash) => {
                    const v = e.versions.find((x) => x.hash === hash)!;
                    setWant(new Map(want).set(k, { ...cur, version: v.version, hash }));
                  }} options={e.versions.map((v) => ({ value: v.hash, label: `${v.version} (${shortHash(v.hash)})` }))} />
                )}
              </div>
            );
          })}
        </div>
      </Card>
    </div>
  );
}
