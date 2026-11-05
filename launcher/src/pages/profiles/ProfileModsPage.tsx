// /profiles/:id/mods: the profile's Kiln mods and the resolver preview (load order, rejections).
import { useQuery } from "@tanstack/react-query";
import { CircleAlert, TriangleAlert } from "lucide-react";
import { useState } from "react";
import * as api from "../../api/commands";
import type { RunSide } from "../../api/commands";
import { qk, useLibrary } from "../../api/hooks";
import { Badge, Card, EmptyState, Panel, Segmented, Skeleton, errorText } from "../../ui";
import { libName, ModList } from "./ModList";
import { useProfileEdit, useProfileId } from "./useProfileEdit";

export default function ProfileModsPage() {
  const id = useProfileId();
  const { profile, toggle, remove, update } = useProfileEdit(id);
  const [side, setSide] = useState<RunSide>("client");
  const preview = useQuery({ queryKey: qk.resolve(id, side), queryFn: () => api.profileResolve(id, side), enabled: !!profile });
  const lib = useLibrary("kiln");
  if (!profile) return null;
  const rejected = new Map((preview.data?.kiln.rejected ?? []).map((r) => [r.id, r]));
  const order = preview.data?.kiln.order ?? [];
  return (
    <div className="k-grid k-grid-main">
      <Card flush title="Kiln mods">
        <ModList kind="kiln" mods={profile.mods.kiln} onToggle={(m, on) => toggle("kiln", m, on)} onRemove={(m) => remove("kiln", m)}
          onAdd={(ms) => update((p) => { p.mods.kiln.push(...ms); })}
          onVersion={(m, version, hash) => update((p) => { p.mods.kiln = p.mods.kiln.map((x) => (x.id === m ? { ...x, version, hash } : x)); })}
          extra={{ header: "Preview", width: "minmax(0, 1.3fr)", render: (m) => {
            if (!m.enabled) return <span className="k-dim">off</span>;
            const r = rejected.get(m.id);
            if (r) return <span title={r.reason}><Badge tone={r.state === "skipped" ? "idle" : "err"}>{r.state}</Badge></span>;
            const at = order.indexOf(m.id);
            return at >= 0 ? <span className="k-muted k-num">#{at + 1}</span> : <span className="k-dim">-</span>;
          } }} />
      </Card>
      <Card title="Resolver" sub="What would load" actions={<Segmented aria-label="Side" value={side} onChange={setSide} options={[{ id: "client", label: "Client" }, { id: "server", label: "Server" }]} />}>
        {preview.isLoading ? <Skeleton /> : preview.error ? <EmptyState title="No preview" hint={errorText(preview.error)} /> : preview.data && (
          <>
            {preview.data.warnings.map((w) => <div key={w} className="k-reject" style={{ background: "#f9c74f0d", borderColor: "#f9c74f33" }}><TriangleAlert size={14} color="var(--k-warn)" /><span>{w}</span></div>)}
            <Panel title={`Load order · ${order.length}`}>
              {order.length === 0 ? <span className="k-hint">Nothing loads.</span> : (
                <ol className="k-order" style={{ margin: 0, padding: 0, listStyle: "none" }}>
                  {order.map((m) => <li key={m} className="k-order-item"><span className="k-item-title" style={{ fontSize: 13 }}>{libName(lib.data, "kiln", m)}</span><span className="k-dim k-mono" style={{ fontSize: 11 }}>{m}</span></li>)}
                </ol>
              )}
            </Panel>
            {preview.data.kiln.rejected.length > 0 && (
              <Panel title={`Not loading · ${preview.data.kiln.rejected.length}`}>
                {preview.data.kiln.rejected.map((r) => (
                  <div key={r.id + r.dir} className="k-reject" style={r.state === "skipped" ? { background: "var(--k-bg-2)", borderColor: "var(--k-line)" } : undefined}>
                    <CircleAlert size={14} style={r.state === "skipped" ? { color: "var(--k-text-3)" } : undefined} />
                    <span className="k-stack" style={{ gap: 1 }}>
                      <span><span className="k-mono">{r.id}</span> <Badge tone={r.state === "skipped" ? "idle" : "err"}>{r.state}</Badge></span>
                      <span className="k-muted" style={{ fontSize: 12.5 }}>{r.reason}</span>
                    </span>
                  </div>
                ))}
              </Panel>
            )}
          </>
        )}
      </Card>
    </div>
  );
}
