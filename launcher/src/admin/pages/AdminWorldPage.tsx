// /servers/:id/admin/world: time of day (time_get/time_set), weather (weather_reset), a
// broadcast to everyone, and the live world settings (settings_get/settings_set with the game's ranges; settings_save
// makes them permanent).
import { useQueryClient } from "@tanstack/react-query";
import { useMemo, useState } from "react";
import { invalidateAdmin, useAdminCall, useAdminQuery, useHasOp } from "../../api/admin";
import { NeedsOp, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { GAME_SETTINGS, NS_PER_MIN, checkSetting, shownSetting, type SettingSpec } from "../gameSettings";
import type { SettingsGet, SettingsSet, TimeState } from "../types";
import { Badge, Banner, Button, Card, Page, PageHeader, Select, Slider, TextInput, Toggle, confirm, pretty, toastError, toastOk } from "../ui";

function WorldPage() {
  return (
    <Page>
      <PageHeader title="World" sub="Time, weather and the world's settings, live on the running server." />
      <OfflineNote />
      <div className="k-grid k-grid-2">
        <NeedsOp ops={["time_get", "time_set"]} what="Time of day">
          <TimeCard />
        </NeedsOp>
        <NeedsOp ops="broadcast" what="Broadcast">
          <BroadcastCard />
        </NeedsOp>
      </div>
      <NeedsOp ops={["settings_get", "settings_set"]} what="Live world settings">
        <SettingsCard />
      </NeedsOp>
    </Page>
  );
}

const PRESETS: [string, number][] = [["Dawn", 6], ["Morning", 9], ["Noon", 12], ["Dusk", 19], ["Night", 22], ["Midnight", 0]];

function TimeCard() {
  const id = useServerId();
  const qc = useQueryClient();
  const t = useAdminQuery<TimeState>("time_get", {}, { refetchInterval: 4000 });
  const set = useAdminCall<TimeState>("time_set", { onSuccess: (r) => { qc.setQueriesData({ queryKey: ["admin", id, "time_get"] }, r); toastOk(`Time is now ${r.clock}`); }, onError: (e) => toastError(e) });
  const weather = useAdminCall("weather_reset", { onSuccess: () => toastOk("Weather reset"), onError: (e) => toastError(e) });
  const hasWeather = useHasOp("weather_reset");
  const [clock, setClock] = useState("");
  const [game, setGame] = useState(false);
  const mode: Record<string, string> = game ? { mode: "game" } : {};
  const setClockText = () => {
    const m = /^(\d{1,2}):(\d{2})$/.exec(clock.trim());
    if (!m || +m[1] > 23 || +m[2] > 59) return toastError({ message: "The time looks like 19:45." });
    set.mutate({ clock: clock.trim(), ...mode });
  };
  const s = t.data;
  return (
    <Card title="Time of day" sub={s ? `Day ${s.dayMinutes} min · night ${s.nightMinutes} min` : undefined}
      actions={s ? <Badge tone={s.day ? "warn" : "info"}>{s.day ? "day" : "night"}</Badge> : null}>
      <div className="k-stack" style={{ gap: 14 }}>
        <div className="ka-time">
          <span className="ka-clock">{s?.clock ?? "--:--"}</span>
          <div className="k-row">
            {PRESETS.map(([l, h]) => (
              <Button key={l} size="s" variant="secondary" disabled={set.isPending} onClick={() => set.mutate({ hours: h, ...mode })}>{l}</Button>
            ))}
          </div>
        </div>
        {s ? <Slider aria-label="Hour" value={Math.round(s.hours * 4) / 4} min={0} max={23.75} step={0.25} onCommit={(h) => set.mutate({ hours: h, ...mode })} /> : null}
        <div className="ka-inline">
          <TextInput aria-label="Clock" placeholder="19:45" value={clock} onChange={(e) => setClock(e.target.value)} onKeyDown={(e) => e.key === "Enter" && setClockText()} style={{ width: 100 }} mono />
          <Button size="s" variant="primary" loading={set.isPending} onClick={setClockText}>Set time</Button>
          <Toggle checked={game} onChange={setGame} label="Reset the game's clock" hint="Off: the world jumps forward to that time (plants grow, stations run)." />
        </div>
        {s?.weather && hasWeather ? (
          <div className="ka-inline">
            <Button size="s" loading={weather.isPending} onClick={() => weather.mutate({})}>Reset weather</Button>
            <span className="k-hint">Ends the current weather; the game rolls new weather as usual.</span>
          </div>
        ) : null}
      </div>
    </Card>
  );
}

function BroadcastCard() {
  const [text, setText] = useState("");
  const send = useAdminCall<{ delivered: number }>("broadcast", { onSuccess: (r) => { toastOk(`Sent to ${r.delivered} player${r.delivered === 1 ? "" : "s"}`); setText(""); }, onError: (e) => toastError(e) });
  const go = () => {
    const t = text.trim();
    if (!t || t.length > 500) return toastError({ message: "A message of 1 to 500 characters." });
    send.mutate({ text: t });
  };
  return (
    <Card title="Broadcast" sub="A chat line every player in the world sees">
      <div className="k-stack">
        <TextInput aria-label="Broadcast text" value={text} maxLength={500} placeholder="Server restarts in 5 minutes." onChange={(e) => setText(e.target.value)} onKeyDown={(e) => e.key === "Enter" && go()} />
        <div className="k-row"><span className="k-hint">{text.length} / 500</span><span className="k-spacer" /><Button variant="primary" size="s" loading={send.isPending} onClick={go}>Send to everyone</Button></div>
      </div>
    </Card>
  );
}

function SettingsCard() {
  const id = useServerId();
  const qc = useQueryClient();
  const q = useAdminQuery<SettingsGet>("settings_get", {}, { staleTime: 10_000 });
  const [busy, setBusy] = useState<Set<string>>(new Set());
  const [errors, setErrors] = useState<Record<string, string>>({});
  const set = useAdminCall<SettingsSet>("settings_set");
  const save = useAdminCall("settings_save", { onSuccess: () => toastOk("Saved to the server config"), onError: (e) => toastError(e) });
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
        toastOk(`${spec.label}: now ${spec.kind === "minutes" ? `${Math.round(Number(res.after) / NS_PER_MIN)} min` : pretty(String(res.after))} (live)`);
      },
      onError: (e) => setErrors((x) => ({ ...x, [spec.key]: e.message })),
      onSettled: () => setBusy((b) => { const n = new Set(b); n.delete(spec.key); return n; }),
    });
  };
  const keep = async () => {
    if (!(await confirm({ title: "Make the live values permanent?", body: <p>Kiln writes the current values into the server's config (enshrouded_server.json), so they survive restarts.</p>, ok: "Save to config" }))) return;
    save.mutate({});
  };
  return (
    <Card title="World settings" sub="Changes apply at once and last until the server restarts, unless you make them permanent."
      actions={<>
        <Button size="s" variant="ghost" onClick={() => invalidateAdmin(qc, id, ["settings_get"])}>Re-read</Button>
        {hasSave ? <Button size="s" variant="primary" disabled={!q.data} loading={save.isPending} onClick={keep}>Make permanent</Button> : null}
      </>}>
      {q.error ? <Banner tone="warn">{q.error.message}</Banner> : null}
      {q.data && !q.data.write_supported ? <Banner tone="warn">This server build reports the settings as read-only right now (the world may still be loading).</Banner> : null}
      {!q.data && !q.error ? <div className="k-muted">Reading the live settings…</div> : null}
      {q.data ? (
        <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
          {groups.map(([g, list]) => (
            <div key={g} className="k-stack" style={{ gap: 4 }}>
              <div className="k-label" style={{ textTransform: "uppercase", letterSpacing: ".08em", fontSize: 11 }}>{g}</div>
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
  const lbl = (
    <div className="ka-setting-lbl">
      <b>{spec.label}</b>
      {error ? <small className="k-error-text">{error}</small> : spec.hint ? <small>{spec.hint}</small> : null}
    </div>
  );
  if (value === undefined) return <div className="ka-setting">{lbl}<span className="k-muted">Not reported by the game</span></div>;
  let ctl;
  if (spec.kind === "bool") ctl = <Toggle checked={value === true} onChange={onChange} disabled={busy} label={value ? "On" : "Off"} />;
  else if (spec.kind === "enum") {
    const opts = (spec.options ?? []).includes(String(value)) ? spec.options! : [String(value), ...(spec.options ?? [])];
    ctl = <Select aria-label={spec.label} value={String(value)} onChange={onChange} disabled={busy} options={opts.map((o) => ({ value: o, label: pretty(o) }))} />;
  } else {
    const shown = Number(shownSetting(spec, value));
    const step = spec.kind === "factor" ? 0.05 : 1;
    ctl = <Slider aria-label={spec.label} value={shown} min={spec.min!} max={spec.max!} step={step} integer={spec.kind !== "factor"} onCommit={onChange} disabled={busy} />;
  }
  return (
    <div className={"ka-setting" + (busy ? " busy" : "")}>
      {lbl}
      {ctl}
    </div>
  );
}

export default withAdmin(WorldPage);
