// Offline config editor for a config folder: a file list, SchemaForm from the `.schema`
// snapshot, and a raw JSONC view. Edits go through config_set (value tokens only; comments stay).
// Reusable: <ConfigEditor target={{ profile: id }} /> | {{ server: id }} | {{ game: id }}.
// A profile's list also says which files belong to mods that are not installed (0.2.0-dev.31.31, `orphaned`): they sit in
// their own group, "Not installed: kept for when you add it back", read-only (their mod is not there to read a change), each
// with a Clear (and a Clear all for the group) that moves the files into the settings backups after a confirmation.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { FileCog, Search, Trash2 } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import * as api from "../api/commands";
import type { ConfigDocument, ConfigFileInfo, ConfigTarget, Json } from "../api/commands";
import { qk } from "../api/hooks";
import { useT } from "../i18n";
import { clearedWords, configName, pickConfig, splitConfigs } from "../shell/configOrphans";
import { settingsNotes } from "../shell/removeMod";
import { Badge, Banner, Button, Card, EmptyState, IconButton, Segmented, SchemaForm, Skeleton, TextArea, TextInput, errorText, fmtAgo, toast } from "../ui";
import { useMarkUnsaved } from "../shell/unsaved";
import { useLeaveGuard } from "../shell/leaveGuard";

/** `initial`: the file, or the mod id whose file, to open first (the main list's Configs). */
export function ConfigEditor({ target, readOnly, initial }: { target: ConfigTarget; readOnly?: boolean; initial?: string }) {
  const t = useT();
  const qc = useQueryClient();
  const files = useQuery({ queryKey: qk.configs(target), queryFn: () => api.configList(target) });
  const [picked, setPicked] = useState<string | null>(null);
  /** The files a Clear would take, while its confirmation is open. */
  const [confirm, setConfirm] = useState<string[] | null>(null);
  const list = files.data ?? [];
  const { installed, orphaned } = splitConfigs(list);
  const sel = pickConfig(list, picked, initial);
  const selected = list.find((f) => f.file === sel);
  const canClear = "profile" in target && !readOnly;
  const clear = useMutation({
    mutationFn: (names: string[]) => api.configClearOrphans(target, names),
    onSuccess: (r) => {
      const words = clearedWords(r, t);
      (r.backup ? toast.ok : toast.info)(words.title, words.detail);
      const notes = settingsNotes(r);
      if (notes) toast.info(t("shell.removeMod.settingsLeft"), notes, { ms: 0 });
      setConfirm(null);
      void qc.invalidateQueries({ queryKey: qk.configs(target) });
    },
    // a refusal (the mod came back, a game started): the list is read again, so the window shows what is true now
    onError: (e) => { toast.error(e); setConfirm(null); void qc.invalidateQueries({ queryKey: qk.configs(target) }); },
  });
  // a confirmation for files the list no longer has (it was read again) closes
  useEffect(() => { if (confirm && !confirm.every((n) => orphaned.some((f) => f.file === n))) setConfirm(null); });
  // the confirmation sits above the list: a bin far down a long list (the window scrolled to it, as on the Deck's screen)
  // brings it into view, else Clear seems to do nothing
  const confirmBox = useRef<HTMLDivElement>(null);
  useEffect(() => { if (confirm) confirmBox.current?.scrollIntoView?.({ block: "nearest" }); }, [confirm]);

  if (files.isLoading) return <Skeleton lines={4} />;
  if (files.error) return <EmptyState icon={<FileCog />} title={t("configEditor.unavailable")} hint={errorText(files.error)} />;
  if (!list.length) return <EmptyState icon={<FileCog />} title={t("configEditor.empty")} hint={t("configEditor.emptyHint")} />;
  const row = (f: ConfigFileInfo) => (
    <div key={f.file} role="option" aria-selected={sel === f.file} tabIndex={0} className={"k-list-item clickable" + (sel === f.file ? " selected" : "") + (f.orphaned ? " k-config-orphan" : "")}
      onClick={() => setPicked(f.file)} onKeyDown={(e) => { if (e.key === "Enter" && e.target === e.currentTarget) setPicked(f.file); }} style={{ padding: "9px 12px" }}>
      <span className="k-stack" style={{ gap: 1, minWidth: 0, flex: 1 }}>
        <span className="k-item-title k-ellipsis">{configName(f)}</span>
        <span className="k-item-sub k-mono k-ellipsis">{f.file}</span>
      </span>
      {f.kind === "emf" && <Badge>emf</Badge>}
      {!f.hasSchema && <Badge title={t("configEditor.rawTip")}>{t("configEditor.raw")}</Badge>}
      {f.orphaned && canClear && (
        <IconButton size="s" label={t("configEditor.orphans.clearFor", { name: configName(f) })} icon={<Trash2 />} disabled={clear.isPending}
          onClick={(e) => { e.stopPropagation(); setConfirm([f.file]); }} onKeyDown={(e) => e.stopPropagation()} />
      )}
    </div>
  );
  return (
    <div className="k-stack">
      {confirm && (
        <div ref={confirmBox}>
          <Banner tone="warn" title={confirm.length === 1 ? t("configEditor.orphans.confirmOne", { name: configName(orphaned.find((f) => f.file === confirm[0]) ?? { mod: null, file: confirm[0] }) }) : t("configEditor.orphans.confirmAll")}
            actions={<>
              <Button size="s" variant="danger-fill" loading={clear.isPending} onClick={() => clear.mutate(confirm)}>{t("configEditor.orphans.confirmAction")}</Button>
              <Button size="s" disabled={clear.isPending} onClick={() => setConfirm(null)}>{t("common.cancel")}</Button>
            </>}>
            {t("configEditor.orphans.confirmBody")}
            <ul className="k-remove-list" aria-label={t("configEditor.files")}>
              {confirm.map((n) => <li key={n}><span className="k-mono">{n}</span></li>)}
            </ul>
          </Banner>
        </div>
      )}
      <div className="k-grid" style={{ gridTemplateColumns: "240px minmax(0, 1fr)" }}>
        <Card flush>
          <div className="k-list" role="listbox" aria-label={t("configEditor.files")}>
            {installed.map(row)}
            {orphaned.length > 0 && (
              <div role="presentation" className="k-config-orphan-head">
                <span className="k-item-sub">{t("configEditor.orphans.group")}</span>
                {canClear && <Button size="s" variant="ghost" disabled={clear.isPending} onClick={() => setConfirm(orphaned.map((f) => f.file))}>{t("configEditor.orphans.clearAll")}</Button>}
              </div>
            )}
            {orphaned.map(row)}
          </div>
        </Card>
        {sel && <ConfigFile key={sel} target={target} file={sel} readOnly={readOnly} orphan={!!selected?.orphaned} name={selected ? configName(selected) : sel}
          onClear={canClear ? () => setConfirm([sel]) : undefined} />}
      </div>
    </div>
  );
}

function ConfigFile({ target, file, readOnly, orphan, name, onClear }: { target: ConfigTarget; file: string; readOnly?: boolean; orphan?: boolean; name: string; onClear?: () => void }) {
  const t = useT();
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
  // the file of a mod that is not installed: read-only, since nothing reads a change until the mod is back
  const locked = readOnly || orphan;

  return (
    <Card title={d?.schema?.mod ?? file} sub={d?.schema ? t("configEditor.schemaSub", { version: d.schema.modVersion ?? "", ago: fmtAgo(d.schema.written) }) : file}
      actions={<>
        {hasSchema && mode === "form" && <div className="k-input-group" style={{ width: 180 }}><Search size={14} className="k-dim" /><TextInput placeholder={t("configEditor.filter")} value={filter} onChange={(e) => setFilter(e.target.value)} style={{ height: 28 }} /></div>}
        {hasSchema && <Segmented aria-label={t("configEditor.view")} value={view} onChange={setView} options={[{ id: "form", label: t("configEditor.form") }, { id: "raw", label: t("configEditor.file") }]} />}
      </>}>
      {orphan && (
        <div className="k-config-orphan-note">
          <Banner tone="info" title={t("configEditor.orphans.badge")} actions={onClear && <Button size="s" onClick={onClear}>{t("configEditor.orphans.clear")}</Button>}>
            {t("configEditor.orphans.hint", { name })}
          </Banner>
        </div>
      )}
      {doc.isLoading ? <Skeleton lines={5} /> : doc.error ? <EmptyState title={t("configEditor.cantRead")} hint={errorText(doc.error)} /> : d && (mode === "form" ? (
        <SchemaForm schema={d.schema!} values={d.values} errors={d.errors} disabled={locked || set.isPending} filter={filter}
          onChange={(path, value) => set.mutate({ path, value })} onReset={(path) => reset.mutate(path)} />
      ) : (
        <RawEditor doc={d} file={file} readOnly={locked} onSave={async (text) => { onDoc(await api.configWrite(target, file, text)); }} />
      ))}
    </Card>
  );
}

function RawEditor({ doc, file, readOnly, onSave }: { doc: ConfigDocument; file: string; readOnly?: boolean; onSave: (text: string) => Promise<void> }) {
  const t = useT();
  const [text, setText] = useState(doc.text);
  const [busy, setBusy] = useState(false);
  useEffect(() => setText(doc.text), [doc.text]);
  const dirty = text !== doc.text;
  useMarkUnsaved("editor", dirty && !readOnly);
  /** True once written (else the toast says why). */
  const save = async (): Promise<boolean> => {
    setBusy(true);
    try { await onSave(text); toast.ok(t("configEditor.saved")); return true; } catch (e) { toast.error(e); return false; } finally { setBusy(false); }
  };
  // leaving the page with the file changed asks: Save, Discard or Stay (shell/leaveGuard.tsx)
  useLeaveGuard(dirty && !readOnly, { what: file, save, discard: () => setText(doc.text) });
  return (
    <div className="k-stack">
      <TextArea aria-label={t("configEditor.file")} rows={18} value={text} onChange={(e) => setText(e.target.value)} readOnly={readOnly} spellCheck={false} />
      {doc.errors.length > 0 && <div className="k-error-text">{doc.errors.map((e) => `${e.path}: ${e.message}`).join(" · ")}</div>}
      {!readOnly && (
        <div className="k-row">
          <Button variant="primary" size="s" disabled={!dirty} loading={busy} onClick={() => void save()}>{t("common.save")}</Button>
          <Button variant="ghost" size="s" disabled={!dirty} onClick={() => setText(doc.text)}>{t("configEditor.revert")}</Button>
        </div>
      )}
    </div>
  );
}
