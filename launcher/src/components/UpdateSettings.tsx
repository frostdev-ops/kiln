// Launcher updates in Settings (automatic checks, channel, Check now) and in About (the state and Check now). The
// state comes from the shell's update feed (api/update.ts). Short words only; details are in launcher.log.
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import * as api from "../api/commands";
import type { LauncherSettings, UpdateChannel, UpdateStatus } from "../api/commands";
import { qk, useSettings } from "../api/hooks";
import { useUpdate, useUpdateStore } from "../api/update";
import { Button, Card, Field, Select, Skeleton, Toggle, toast } from "../ui";

/** Where updates stand, in a few words. */
export function updateWords(s: UpdateStatus): string {
  switch (s.phase) {
    case "checking": return "Checking…";
    case "upToDate": return "Up to date";
    case "available": return `${s.available?.version} available`;
    case "skipped": return `Skipped ${s.available?.version}`;
    case "failed": return "Check failed";
    case "downloading": return "Updating…";
    case "ready": return "Ready to restart";
    default: return s.auto ? "Not checked yet" : "Off";
  }
}

function useCheckNow() {
  const [busy, setBusy] = useState(false);
  const run = async () => {
    setBusy(true);
    try {
      const s = await api.updateCheck();
      if (s.phase === "available") useUpdateStore.getState().setOpen(true);
      else if (s.phase === "upToDate") toast.ok("Up to date");
      else if (s.phase === "failed") toast.error({ code: "internal", message: "Check failed" });
    } catch {
      toast.error({ code: "internal", message: "Check failed" });
    } finally {
      setBusy(false);
    }
  };
  return { busy, run };
}

export function UpdatesCard() {
  const qc = useQueryClient();
  const s = useUpdate();
  const settings = useSettings();
  const check = useCheckNow();
  const save = useMutation({
    mutationFn: (v: LauncherSettings) => api.appSettingsSet(v),
    onSuccess: (v) => { qc.setQueryData(qk.settings, v); api.updateStatus().then((x) => useUpdateStore.getState().setStatus(x)).catch(() => {}); },
    onError: (e) => { toast.error(e); qc.invalidateQueries({ queryKey: qk.settings }); },
  });
  if (!s || !settings.data) return <Card title="Updates"><Skeleton /></Card>;
  const v = settings.data;
  const busy = check.busy || s.phase === "checking" || s.phase === "downloading";
  const setChannel = (ch: string) => save.mutate({ ...v, updateChannel: ch as UpdateChannel }, { onSuccess: () => { if (v.updateCheck !== false) check.run(); } });
  const undo = () => api.updateSkip(null).catch(() => toast.error({ code: "internal", message: "Undo failed" }));
  return (
    <Card title="Updates" sub={<span className="k-mono">{s.current}</span>}>
      <Toggle checked={v.updateCheck !== false} onChange={(on) => save.mutate({ ...v, updateCheck: on })} label="Check automatically" hint="Sends no personal data." />
      <Field label="Channel" htmlFor="st-upd-channel">
        <Select id="st-upd-channel" value={s.channel} onChange={setChannel} style={{ width: 200 }}
          options={[{ value: "stable", label: "Stable" }, { value: "dev", label: "Dev" }]} />
      </Field>
      <div className="k-row" style={{ justifyContent: "space-between", flexWrap: "nowrap", gap: 14 }}>
        <span className="k-hint" role="status">
          {updateWords(s)}
          {s.phase === "skipped" && <>{" · "}<button type="button" className="k-link" onClick={undo}>Undo</button></>}
        </span>
        <Button size="s" loading={busy} onClick={check.run} style={{ flex: "none" }}>Check now</Button>
      </div>
    </Card>
  );
}

/** About: the state and Check now. */
export function UpdateAboutLine() {
  const s = useUpdate();
  const check = useCheckNow();
  if (!s) return <span>-</span>;
  return (
    <span>
      {updateWords(s)}{" · "}
      <button type="button" className="k-link" disabled={check.busy || s.phase === "checking"} onClick={check.run}>Check now</button>
    </span>
  );
}
