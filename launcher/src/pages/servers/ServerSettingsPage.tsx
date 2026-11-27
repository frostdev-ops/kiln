// /servers/:id/settings: enshrouded_server.json: general settings, difficulty (the settings
// spec from the backend: factors, switches, choices, minutes stored as nanoseconds), user groups and
// bans. Edits go to server_settings_set, which validates them and refuses while the server runs.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus, Trash2 } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useParams } from "react-router-dom";
import * as api from "../../api/commands";
import { Banner, Button, Card, Field, IconButton, NumberInput, Page, Select, Skeleton, Slider, TextArea, TextInput, Toggle, errorText, toast } from "../../ui";
import { sk, type GameSettingSpec, type ServerSettingsX } from "./serverApi";
import { useT } from "../../i18n";
import { useMarkUnsaved } from "../../shell/unsaved";
import { useLeaveGuard } from "../../shell/leaveGuard";

type Json = Record<string, unknown>;
interface Group { name: string; originalName?: string; password: string; reservedSlots: number; [perm: string]: unknown }

const NS_PER_MIN = 60e9;

function Setting({ spec, value, onChange, disabled }: { spec: GameSettingSpec; value: unknown; onChange: (v: unknown) => void; disabled: boolean }) {
  const t = useT();
  const label = spec.label + (spec.hint ? ` (${spec.hint})` : "");
  switch (spec.kind) {
    case "bool":
      return <Toggle checked={!!value} onChange={onChange} label={spec.label} hint={spec.hint || undefined} disabled={disabled} />;
    case "enum":
      return <Field label={label}><Select value={String(value ?? "")} onChange={onChange} options={spec.options ?? []} disabled={disabled} /></Field>;
    case "minutes":
      return (
        <Field label={label}>
          <NumberInput integer min={spec.min} max={spec.max} value={typeof value === "number" ? Math.round(value / NS_PER_MIN) : null}
            onCommit={(m) => onChange(Math.round(m * NS_PER_MIN))} disabled={disabled} />
        </Field>
      );
    case "int":
      return <Field label={label}><NumberInput integer min={spec.min} max={spec.max} value={typeof value === "number" ? value : null} onCommit={onChange} disabled={disabled} /></Field>;
    default:
      return (
        <Field label={`${spec.label}: ${typeof value === "number" ? value.toFixed(2) : "-"}`} hint={spec.hint || t("servers.settings.range", { min: spec.min, max: spec.max })}>
          <Slider value={typeof value === "number" ? value : 1} min={spec.min ?? 0} max={spec.max ?? 1} step={0.05} onCommit={onChange} disabled={disabled} aria-label={spec.label} />
        </Field>
      );
  }
}

export default function ServerSettingsPage() {
  const t = useT();
  const { id = "" } = useParams();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: sk.settings(id), queryFn: () => api.serverSettingsGet(id) as Promise<ServerSettingsX> });
  const [edit, setEdit] = useState<Json>({});
  const [saving, setSaving] = useState(false);
  useEffect(() => setEdit({}), [q.data]);
  const values = (q.data?.values ?? {}) as Json;
  const cur: Json = useMemo(() => ({ ...values, ...edit, gameSettings: { ...((values.gameSettings as Json) ?? {}), ...((edit.gameSettings as Json) ?? {}) } }), [values, edit]);
  const running = !!q.data?.running;
  const schema = q.data?.schema;
  const groups = (cur.userGroups as Group[] | undefined) ?? [];
  const dirty = Object.keys(edit).length > 0;
  useMarkUnsaved("editor", dirty);
  const set = (k: string, v: unknown) => setEdit((e) => ({ ...e, [k]: v }));
  const setGame = (k: string, v: unknown) => setEdit((e) => ({ ...e, gameSettings: { ...((e.gameSettings as Json) ?? {}), [k]: v }, gameSettingsPreset: "Custom" }));
  const setGroups = (g: Group[]) => set("userGroups", g.map((x) => ({ ...x, originalName: x.originalName ?? x.name })));
  /** True once saved (else the toast says why). */
  const save = async (): Promise<boolean> => {
    setSaving(true);
    try {
      const next = await api.serverSettingsSet(id, edit as api.Json);
      toast.ok(t("servers.settings.saved"));
      // the saved file at once (its new values clear the edits, and with them the leave guard), then read again
      qc.setQueryData(sk.settings(id), next);
      qc.invalidateQueries({ queryKey: sk.settings(id) });
      return true;
    } catch (e) {
      toast.error(e, t("servers.settings.notSaved"));
      return false;
    } finally {
      setSaving(false);
    }
  };
  // leaving with changes asks: Save (not while the server runs: it writes the file back), Discard or Stay
  useLeaveGuard(dirty, { what: t("servers.settings.leaveWhat"), save: running ? undefined : save, discard: () => setEdit({}) });
  if (q.isLoading) return <Page><Skeleton lines={8} /></Page>;
  if (q.error) return <Page><Banner tone="err" title="enshrouded_server.json">{errorText(q.error)}</Banner></Page>;
  const byGroup = new Map<string, GameSettingSpec[]>();
  for (const s of schema?.gameSettings ?? []) byGroup.set(s.group, [...(byGroup.get(s.group) ?? []), s]);
  return (
    <Page>
      {running && <Banner tone="warn" title={t("servers.settings.running")}>{t("servers.settings.runningText")}</Banner>}
      <div className="k-row" style={{ justifyContent: "space-between" }}>
        <span className="k-hint k-mono">{q.data?.path}</span>
        <div className="k-row" style={{ gap: 8 }}>
          <Button variant="ghost" disabled={!dirty} onClick={() => setEdit({})}>{t("servers.settings.discard")}</Button>
          <Button variant={dirty ? "primary" : "secondary"} disabled={!dirty || running} loading={saving} onClick={() => void save()}>{t("common.save")}</Button>
        </div>
      </div>
      <Card title={t("servers.settings.general")}>
        <div className="k-grid" style={{ gridTemplateColumns: "repeat(auto-fit, minmax(260px, 1fr))", gap: 12 }}>
          <Field label={t("servers.settings.serverName")}><TextInput value={String(cur.name ?? "")} onChange={(e) => set("name", e.target.value)} disabled={running} /></Field>
          <Field label={t("servers.settings.playerSlots")}><NumberInput integer min={1} max={16} value={(cur.slotCount as number) ?? null} onCommit={(v) => set("slotCount", v)} disabled={running} /></Field>
          <Field label={t("servers.settings.queryPort")}><NumberInput integer min={1024} max={65535} value={(cur.queryPort as number) ?? null} onCommit={(v) => set("queryPort", v)} disabled={running} /></Field>
          <Field label={t("servers.settings.preset")}><Select value={String(cur.gameSettingsPreset ?? "Default")} onChange={(v) => set("gameSettingsPreset", v)} options={schema?.presets ?? []} disabled={running} /></Field>
          <Field label={t("servers.settings.voiceMode")}><Select value={String(cur.voiceChatMode ?? "Proximity")} onChange={(v) => set("voiceChatMode", v)} options={[{ value: "Proximity", label: t("servers.settings.proximity") }, { value: "Global", label: t("servers.settings.global") }]} disabled={running} /></Field>
          <Field label={t("servers.settings.chat")}>
            <div className="k-row" style={{ gap: 20, minHeight: 34 }}>
              <Toggle checked={!!cur.enableVoiceChat} onChange={(v) => set("enableVoiceChat", v)} label={t("servers.settings.voice")} disabled={running} />
              <Toggle checked={!!cur.enableTextChat} onChange={(v) => set("enableTextChat", v)} label={t("servers.settings.text")} disabled={running} />
            </div>
          </Field>
        </div>
      </Card>
      {[...byGroup.entries()].map(([group, specs]) => (
        <Card key={group} title={group} sub={group === "Player" ? t("servers.settings.playerSub") : undefined}>
          <div className="k-grid" style={{ gridTemplateColumns: "repeat(auto-fit, minmax(260px, 1fr))", gap: 12 }}>
            {specs.map((s) => <Setting key={s.key} spec={s} value={(cur.gameSettings as Json)[s.key]} onChange={(v) => setGame(s.key, v)} disabled={running} />)}
          </div>
        </Card>
      ))}
      <Card title={t("servers.settings.groups")} sub={t("servers.settings.groupsSub")}
        actions={<Button size="s" icon={<Plus size={14} />} disabled={running || groups.length >= 16}
          onClick={() => setGroups([...groups, { name: `Group ${groups.length + 1}`, password: "", reservedSlots: 0 }])}>{t("servers.settings.addGroup")}</Button>}>
        <div className="k-stack">
          {groups.map((g, i) => (
            <div key={i} className="k-row" style={{ gap: 8, flexWrap: "wrap", alignItems: "flex-end" }}>
              <Field label={t("servers.settings.name")}><TextInput value={g.name} onChange={(e) => setGroups(groups.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} disabled={running} /></Field>
              <Field label={t("servers.settings.password")}><TextInput mono value={g.password ?? ""} onChange={(e) => setGroups(groups.map((x, j) => (j === i ? { ...x, password: e.target.value } : x)))} disabled={running} /></Field>
              <Field label={t("servers.settings.reserved")}><NumberInput integer min={0} max={16} value={g.reservedSlots ?? 0} onCommit={(v) => setGroups(groups.map((x, j) => (j === i ? { ...x, reservedSlots: v } : x)))} disabled={running} /></Field>
              {(schema?.permissions ?? []).map((p) => (
                <Toggle key={p.key} label={p.label} checked={!!(g[p.field] ?? g[p.key])} disabled={running}
                  onChange={(v) => setGroups(groups.map((x, j) => (j === i ? { ...x, [p.key]: v, [p.field]: v } : x)))} />
              ))}
              <IconButton icon={<Trash2 size={16} />} label={t("servers.settings.removeGroup")} disabled={running || groups.length <= 1} onClick={() => setGroups(groups.filter((_, j) => j !== i))} />
            </div>
          ))}
        </div>
      </Card>
      <Card title={t("servers.settings.banned")} sub={t("servers.settings.bannedSub")}>
        <TextArea rows={4} value={((cur.bannedAccounts as string[]) ?? []).join("\n")} disabled={running}
          onChange={(e) => set("bannedAccounts", e.target.value.split(/\s+/).map((x) => x.trim()).filter(Boolean))} />
      </Card>
    </Page>
  );
}
