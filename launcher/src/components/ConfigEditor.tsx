// Offline config editor for a config folder: a file list, SchemaForm from the `.schema`
// snapshot, and a raw JSONC view. Edits go through config_set (value tokens only; comments stay).
// Reusable: <ConfigEditor target={{ profile: id }} /> | {{ server: id }} | {{ game: id }}.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { FileCog, Search } from "lucide-react";
import { useEffect, useState } from "react";
import * as api from "../api/commands";
import type { ConfigDocument, ConfigTarget, Json } from "../api/commands";
import { qk } from "../api/hooks";
import { Badge, Button, Card, EmptyState, Segmented, SchemaForm, Skeleton, TextArea, TextInput, errorText, fmtAgo, toast } from "../ui";

/** `initial`: the file, or the mod id whose file, to open first (the main list's Configs). */
export function ConfigEditor({ target, readOnly, initial }: { target: ConfigTarget; readOnly?: boolean; initial?: string }) {
  const files = useQuery({ queryKey: qk.configs(target), queryFn: () => api.configList(target) });
  const [sel, setSel] = useState<string | null>(null);
  const list = files.data ?? [];
  useEffect(() => {
    if (!sel && list.length) setSel(list.find((f) => initial && (f.file === initial || f.mod === initial))?.file ?? list.find((f) => f.hasSchema)?.file ?? list[0].file);
  }, [list, sel, initial]);

  if (files.isLoading) return <Skeleton lines={4} />;
  if (files.error) return <EmptyState icon={<FileCog />} title="Configs unavailable" hint={errorText(files.error)} />;
  if (!list.length) return <EmptyState icon={<FileCog />} title="No configs yet" hint="Mods write their config and schema the first time they load." />;
  return (
    <div className="k-grid" style={{ gridTemplateColumns: "240px minmax(0, 1fr)" }}>
      <Card flush>
        <div className="k-list" role="listbox" aria-label="Config files">
          {list.map((f) => (
            <div key={f.file} role="option" aria-selected={sel === f.file} tabIndex={0} className={"k-list-item clickable" + (sel === f.file ? " selected" : "")}
              onClick={() => setSel(f.file)} onKeyDown={(e) => { if (e.key === "Enter") setSel(f.file); }} style={{ padding: "9px 12px" }}>
              <span className="k-stack" style={{ gap: 1, minWidth: 0, flex: 1 }}>
                <span className="k-item-title k-ellipsis">{f.mod ?? f.file}</span>
                <span className="k-item-sub k-mono k-ellipsis">{f.file}</span>
              </span>
              {f.kind === "emf" && <Badge>emf</Badge>}
              {!f.hasSchema && <Badge title="No schema snapshot: raw edit only">raw</Badge>}
            </div>
          ))}
        </div>
      </Card>
      {sel && <ConfigFile key={sel} target={target} file={sel} readOnly={readOnly} />}
    </div>
  );
}

function ConfigFile({ target, file, readOnly }: { target: ConfigTarget; file: string; readOnly?: boolean }) {
  const qc = useQueryClient();
  const key = qk.config(target, file);
  const doc = useQuery({ queryKey: key, queryFn: () => api.configRead(target, file) });
  const [view, setView] = useState<"form" | "raw">("form");
  const [filter, setFilter] = useState("");
  const onDoc = (d: ConfigDocument) => qc.setQueryData(key, d);
  const set = useMutation({ mutationFn: (v: { path: string; value: Json }) => api.configSet(target, file, v.path, v.value), onSuccess: onDoc, onError: (e) => toast.error(e) });
  const reset = useMutation({ mutationFn: (path: string | null) => api.configReset(target, file, path), onSuccess: onDoc, onError: (e) => toast.error(e) });
  const d = doc.data;
  const hasSchema = !!d?.schema;
  const mode = hasSchema ? view : "raw";

  return (
    <Card title={d?.schema?.mod ?? file} sub={d?.schema ? `${d.schema.modVersion ?? ""} · schema ${fmtAgo(d.schema.written)}` : file}
      actions={<>
        {hasSchema && mode === "form" && <div className="k-input-group" style={{ width: 180 }}><Search size={14} className="k-dim" /><TextInput placeholder="Filter" value={filter} onChange={(e) => setFilter(e.target.value)} style={{ height: 28 }} /></div>}
        {hasSchema && <Segmented aria-label="View" value={view} onChange={setView} options={[{ id: "form", label: "Form" }, { id: "raw", label: "File" }]} />}
      </>}>
      {doc.isLoading ? <Skeleton lines={5} /> : doc.error ? <EmptyState title="Can't read it" hint={errorText(doc.error)} /> : d && (mode === "form" ? (
        <SchemaForm schema={d.schema!} values={d.values} errors={d.errors} disabled={readOnly || set.isPending} filter={filter}
          onChange={(path, value) => set.mutate({ path, value })} onReset={(path) => reset.mutate(path)} />
      ) : (
        <RawEditor doc={d} readOnly={readOnly} onSave={async (text) => { onDoc(await api.configWrite(target, file, text)); }} />
      ))}
    </Card>
  );
}

function RawEditor({ doc, readOnly, onSave }: { doc: ConfigDocument; readOnly?: boolean; onSave: (text: string) => Promise<void> }) {
  const [text, setText] = useState(doc.text);
  const [busy, setBusy] = useState(false);
  useEffect(() => setText(doc.text), [doc.text]);
  const dirty = text !== doc.text;
  return (
    <div className="k-stack">
      <TextArea aria-label="File" rows={18} value={text} onChange={(e) => setText(e.target.value)} readOnly={readOnly} spellCheck={false} />
      {doc.errors.length > 0 && <div className="k-error-text">{doc.errors.map((e) => `${e.path}: ${e.message}`).join(" · ")}</div>}
      {!readOnly && (
        <div className="k-row">
          <Button variant="primary" size="s" disabled={!dirty} loading={busy} onClick={async () => { setBusy(true); try { await onSave(text); toast.ok("Saved"); } catch (e) { toast.error(e); } finally { setBusy(false); } }}>Save</Button>
          <Button variant="ghost" size="s" disabled={!dirty} onClick={() => setText(doc.text)}>Revert</Button>
        </div>
      )}
    </div>
  );
}
