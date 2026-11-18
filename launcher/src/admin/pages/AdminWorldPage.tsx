// /servers/:id/admin/world: time of day (time_get/time_set), weather (weather_reset), a
// broadcast to everyone, and the live world settings (settings_get/settings_set with the game's ranges; settings_save
// makes them permanent).
import { useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { invalidateAdmin, useAdminCall, useAdminQuery, useHasOp } from "../../api/admin";
import { useT } from "../../i18n";
import { NeedsOp, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { GAME_SETTINGS, NS_PER_MIN, checkSetting, groupLabel, optionLabel, shownSetting, type SettingSpec } from "../gameSettings";
import type { SettingsGet, SettingsSet, TimeState } from "../types";
import { Badge, Banner, Button, Card, Page, PageHeader, Select, Slider, TextInput, Toggle, confirm, toastError, toastOk } from "../ui";

function WorldPage() {
  const t = useT();
  return (
    <Page>
      <PageHeader title={t("admin.world.title")} sub={t("admin.world.sub")} />
      <OfflineNote />
      <div className="k-grid k-grid-2">
        <NeedsOp ops={["time_get", "time_set"]} what={t("admin.world.timeOfDay")}>
          <TimeCard />
        </NeedsOp>
        <NeedsOp ops="broadcast" what={t("admin.world.broadcast")}>
          <BroadcastCard />
        </NeedsOp>
      </div>
      <NeedsOp ops={["settings_get", "settings_set"]} what={t("admin.world.liveSettings")}>
        <SettingsCard />
      </NeedsOp>
    </Page>
  );
}

/** Hours of the time presets. */
const PRESETS = [6, 9, 12, 19, 22, 0];
function presetLabel(h: number, t: (key: string) => string): string {
  switch (h) {
    case 6: return t("admin.world.dawn");
    case 9: return t("admin.world.morning");
    case 12: return t("admin.world.noon");
    case 19: return t("admin.world.dusk");
    case 22: return t("admin.world.night");
    default: return t("admin.world.midnight");
  }
}

function TimeCard() {
  const t = useT();
  const id = useServerId();
  const qc = useQueryClient();
  const time = useAdminQuery<TimeState>("time_get", {}, { refetchInterval: 4000 });
  const set = useAdminCall<TimeState>("time_set", { onSuccess: (r) => { qc.setQueriesData({ queryKey: ["admin", id, "time_get"] }, r); toastOk(t("admin.world.timeNow", { clock: r.clock })); }, onError: (e) => toastError(e) });
  const weather = useAdminCall("weather_reset", { onSuccess: () => toastOk(t("admin.world.weatherReset")), onError: (e) => toastError(e) });
  const hasWeather = useHasOp("weather_reset");
  const [clock, setClock] = useState("");
  const [game, setGame] = useState(false);
  const mode: Record<string, string> = game ? { mode: "game" } : {};
  const setClockText = () => {
    const m = /^(\d{1,2}):(\d{2})$/.exec(clock.trim());
    if (!m || +m[1] > 23 || +m[2] > 59) return toastError({ message: t("admin.world.timeFormat") });
    set.mutate({ clock: clock.trim(), ...mode });
  };
  const s = time.data;
  return (
    <Card title={t("admin.world.timeOfDay")} sub={s ? t("admin.world.dayNight", { day: s.dayMinutes, night: s.nightMinutes }) : undefined}
      actions={s ? <Badge tone={s.day ? "warn" : "info"}>{s.day ? t("admin.world.badgeDay") : t("admin.world.badgeNight")}</Badge> : null}>
      <div className="k-stack" style={{ gap: 14 }}>
        <div className="ka-time">
          <span className="ka-clock">{s?.clock ?? "--:--"}</span>
          <div className="k-row">
            {PRESETS.map((h) => (
              <Button key={h} size="s" variant="secondary" disabled={set.isPending} onClick={() => set.mutate({ hours: h, ...mode })}>{presetLabel(h, t)}</Button>
            ))}
          </div>
        </div>
        {s ? <Slider aria-label={t("admin.world.hour")} value={Math.round(s.hours * 4) / 4} min={0} max={23.75} step={0.25} onCommit={(h) => set.mutate({ hours: h, ...mode })} /> : null}
        <div className="ka-inline">
          <TextInput aria-label={t("admin.world.clock")} placeholder="19:45" value={clock} onChange={(e) => setClock(e.target.value)} onKeyDown={(e) => e.key === "Enter" && setClockText()} style={{ width: 100 }} mono />
          <Button size="s" loading={set.isPending} onClick={setClockText}>{t("admin.world.setTime")}</Button>
          <Toggle checked={game} onChange={setGame} label={t("admin.world.resetClock")} hint={t("admin.world.resetClockHint")} />
        </div>
        {s?.weather && hasWeather ? (
          <div className="ka-inline">
            <Button size="s" loading={weather.isPending} onClick={() => weather.mutate({})}>{t("admin.world.resetWeather")}</Button>
            <span className="k-hint">{t("admin.world.resetWeatherHint")}</span>
          </div>
        ) : null}
      </div>
    </Card>
  );
}

function BroadcastCard() {
  const t = useT();
  const [text, setText] = useState("");
  const send = useAdminCall<{ delivered: number }>("broadcast", { onSuccess: (r) => { toastOk(t("admin.world.sentTo", { count: r.delivered })); setText(""); }, onError: (e) => toastError(e) });
  const go = () => {
    const msg = text.trim();
    if (!msg || msg.length > 500) return toastError({ message: t("admin.world.messageRange") });
    send.mutate({ text: msg });
  };
  return (
    <Card title={t("admin.world.broadcast")} sub={t("admin.world.broadcastSub")}>
      <div className="k-stack">
        <TextInput aria-label={t("admin.world.broadcastText")} value={text} maxLength={500} placeholder={t("admin.world.broadcastPlaceholder")} onChange={(e) => setText(e.target.value)} onKeyDown={(e) => e.key === "Enter" && go()} />
        <div className="k-row"><span className="k-hint">{text.length} / 500</span><span className="k-spacer" /><Button size="s" loading={send.isPending} onClick={go}>{t("admin.world.sendEveryone")}</Button></div>
      </div>
    </Card>
  );
}

function SettingsCard() {
  const t = useT();
  const id = useServerId();
  const qc = useQueryClient();
  const q = useAdminQuery<SettingsGet>("settings_get", {}, { staleTime: 10_000 });
  const [busy, setBusy] = useState<Set<string>>(new Set());
  const [errors, setErrors] = useState<Record<string, string>>({});
  const set = useAdminCall<SettingsSet>("settings_set");
  const save = useAdminCall("settings_save", { onSuccess: () => toastOk(t("admin.world.savedConfig")), onError: (e) => toastError(e) });
  const hasSave = useHasOp("settings_save");
  const groups = useMemo(() => {
    const g = new Map<string, SettingSpec[]>();
    for (const s of GAME_SETTINGS) g.set(s.group, [...(g.get(s.group) ?? []), s]);
    return [...g.entries()];
  }, []);
  const values = q.data?.values ?? {};
  const change = (spec: SettingSpec, raw: unknown) => {
    const v = spec.kind === "minutes" && typeof raw === "number" ? Math.round(raw) * NS_PER_MIN : spec.kind === "int" && typeof raw === "number" ? Math.round(raw) : raw;
    const r = checkSetting(spec, v, values[spec.key]);
    if (r.error) return setErrors((e) => ({ ...e, [spec.key]: r.error! }));
    setErrors(({ [spec.key]: _drop, ...rest }) => rest);
    setBusy((b) => new Set(b).add(spec.key));
    set.mutate({ path: spec.key, value: r.value! }, {
      onSuccess: (res) => {
        qc.setQueriesData<SettingsGet>({ queryKey: ["admin", id, "settings_get"] }, (old) => (old ? { ...old, values: { ...old.values, [spec.key]: res.after } } : old));
        toastOk(t("admin.world.settingNow", { label: spec.label(), value: spec.kind === "minutes" ? t("admin.world.minutesValue", { n: Math.round(Number(res.after) / NS_PER_MIN) }) : optionLabel(String(res.after)) }));
      },
      onError: (e) => setErrors((x) => ({ ...x, [spec.key]: e.message })),
      onSettled: () => setBusy((b) => { const n = new Set(b); n.delete(spec.key); return n; }),
    });
  };
  const keep = async () => {
    if (!(await confirm({ title: t("admin.world.permanentTitle"), body: <p>{t("admin.world.permanentBody")}</p>, ok: t("admin.world.saveToConfig") }))) return;
    save.mutate({});
  };
  return (
    <Card title={t("admin.world.settingsTitle")} sub={t("admin.world.settingsSub")}
      actions={<>
        <Button size="s" variant="ghost" onClick={() => invalidateAdmin(qc, id, ["settings_get"])}>{t("admin.world.reread")}</Button>
        {hasSave ? <Button size="s" disabled={!q.data} loading={save.isPending} onClick={keep}>{t("admin.world.makePermanent")}</Button> : null}
      </>}>
      {q.error ? <Banner tone="warn">{q.error.message}</Banner> : null}
      {q.data && !q.data.write_supported ? <Banner tone="warn">{t("admin.world.readOnly")}</Banner> : null}
      {!q.data && !q.error ? <div className="k-muted">{t("admin.world.readingSettings")}</div> : null}
      {q.data ? (
        <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
          {groups.map(([g, list]) => (
            <div key={g} className="k-stack" style={{ gap: 4 }}>
              <div className="k-label" style={{ textTransform: "uppercase", letterSpacing: ".08em", fontSize: 11 }}>{groupLabel(g)}</div>
              <div className="ka-setting-group">
                {list.map((spec) => <SettingRow key={spec.key} spec={spec} value={values[spec.key]} busy={busy.has(spec.key)} error={errors[spec.key]} onChange={(v) => change(spec, v)} />)}
              </div>
            </div>
          ))}
        </div>
      ) : null}
    </Card>
  );
}

function SettingRow({ spec, value, busy, error, onChange }: { spec: SettingSpec; value: unknown; busy: boolean; error?: string; onChange: (v: unknown) => void }) {
  const t = useT();
  const label = spec.label();
  const lbl = (
    <div className="ka-setting-lbl">
      <b>{label}</b>
      {error ? <small className="k-error-text">{error}</small> : spec.hint ? <small>{spec.hint()}</small> : null}
    </div>
  );
  if (value === undefined) return <div className="ka-setting">{lbl}<span className="k-muted">{t("admin.world.notReported")}</span></div>;
  let ctl;
  if (spec.kind === "bool") ctl = <Toggle checked={value === true} onChange={onChange} disabled={busy} label={value ? t("admin.world.on") : t("admin.world.off")} />;
  else if (spec.kind === "enum") {
    const opts = (spec.options ?? []).includes(String(value)) ? spec.options! : [String(value), ...(spec.options ?? [])];
    ctl = <Select aria-label={label} value={String(value)} onChange={onChange} disabled={busy} options={opts.map((o) => ({ value: o, label: optionLabel(o) }))} />;
  } else {
    const shown = Number(shownSetting(spec, value));
    const step = spec.kind === "factor" ? 0.05 : 1;
    ctl = <Slider aria-label={label} value={shown} min={spec.min!} max={spec.max!} step={step} integer={spec.kind !== "factor"} onCommit={onChange} disabled={busy} />;
  }
  return (
    <div className={"ka-setting" + (busy ? " busy" : "")}>
      {lbl}
      {ctl}
    </div>
  );
}

export default withAdmin(WorldPage);
