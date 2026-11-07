// Launcher updates in Settings (automatic checks, channel, Check now) and in About (the state and Check now). The
// state comes from the shell's update feed (api/update.ts). Short words only; details are in launcher.log.
// The Nexus Mods build (api/distribution.ts) has none of that: one line, "New versions: Nexus Mods", whose link opens
// the Kiln page there; no update command is called.
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import * as api from "../api/commands";
import type { LauncherSettings, UpdateChannel, UpdateStatus } from "../api/commands";
import { NEXUS_PAGE } from "../api/distribution";
import { useAppInfo, useDistribution, useSettings } from "../api/hooks";
import { useUpdate, useUpdateStore } from "../api/update";
import { saveSettings } from "../deck/mode";
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

/** The Nexus build's whole update UI: where new versions are, as a link (opened in the browser, nothing fetched). */
export function NexusVersionsLink() {
  return (
    <button type="button" className="k-link" data-testid="nexus-page" title={NEXUS_PAGE}
      onClick={() => api.appOpenUrl(NEXUS_PAGE).catch(() => toast.error({ code: "internal", message: "Could not open the page" }))}>
      Nexus Mods
    </button>
  );
}

/** Settings > Updates: the self-update's settings (direct builds), or the Nexus line. */
export function UpdatesCard() {
  const dist = useDistribution();
  if (dist === "nexus") return <NexusUpdatesCard />;
  if (dist === "direct") return <SelfUpdatesCard />;
  return <Card title="Updates"><Skeleton /></Card>;
}

function NexusUpdatesCard() {
  const info = useAppInfo();
  return (
    <Card title="Updates" sub={<span className="k-mono">{info.data?.version}</span>}>
      <span className="k-hint" data-testid="nexus-versions">New versions: <NexusVersionsLink /></span>
    </Card>
  );
}

function SelfUpdatesCard() {
  const qc = useQueryClient();
  const s = useUpdate();
  const settings = useSettings();
  const check = useCheckNow();
  const save = useMutation({
    mutationFn: (patch: Partial<LauncherSettings>) => saveSettings(qc, () => patch),
    onSuccess: (v) => { if (v) api.updateStatus().then((x) => useUpdateStore.getState().setStatus(x)).catch(() => {}); },
  });
  if (!s || !settings.data) return <Card title="Updates"><Skeleton /></Card>;
  const v = settings.data;
  const busy = check.busy || s.phase === "checking" || s.phase === "downloading";
  const setChannel = (ch: string) => save.mutate({ updateChannel: ch as UpdateChannel }, { onSuccess: (saved) => { if (saved && saved.updateCheck !== false) check.run(); } });
  const undo = () => api.updateSkip(null).catch(() => toast.error({ code: "internal", message: "Undo failed" }));
  return (
    <Card title="Updates" sub={<span className="k-mono">{s.current}</span>}>
      <Toggle checked={v.updateCheck !== false} onChange={(on) => save.mutate({ updateCheck: on })} label="Check automatically" hint="Sends no personal data." />
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

/** About: the state and Check now (direct builds); the Nexus build's row is "New versions: Nexus Mods" (AboutPage). */
export function UpdateAboutLine() {
  return useDistribution() === "direct" ? <SelfUpdateAboutLine /> : null;
}

function SelfUpdateAboutLine() {
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
