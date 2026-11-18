// The launcher update in the header: a word beside the tasks (Update, Updating, Restart, Updated) and its notice,
// which drops under the header by itself when an update is found or was just installed. Each state is one line and
// its buttons; sources, fallbacks and failures go to launcher.log, and a failed download is a short toast.
//   available     "Kiln <version>"        Skip, Update (or Download for a .deb or development build)
//   downloading   "Updating…" and a bar
//   ready         "Ready to restart"       Later, Restart
//   just updated  "Updated to <version>"   Later, Update game (installs the runtime it brought; held while the game runs)
// Only in builds that update themselves: the Nexus Mods build (app_info.distribution "nexus") has no word, no notice
// and no update feed (api/distribution.ts).
import { useQueryClient } from "@tanstack/react-query";
import { X } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import * as api from "../api/commands";
import type { UpdateStatus } from "../api/commands";
import { invalidateGame, useDistribution, useGame, useGameStatus } from "../api/hooks";
import { useUpdate, useUpdateFeed, useUpdateStore } from "../api/update";
import { buildLabel } from "../api/channel";
import { Button, IconButton, ProgressBar, installNote, runTask, toast, useTasks, waitTask } from "../ui";
import { notifyInstall } from "./notices";
import { t, useT } from "../i18n";
import "./update.css";

/** A short error toast; the detail is in launcher.log. */
const failed = (title: string) => toast.error({ code: "internal", message: title });

function wordFor(s: UpdateStatus): { text: string; tone: "info" | "ember" | "ok" } | null {
  if (s.justUpdated) return { text: t("shell.updateNotice.updated"), tone: "ok" };
  if (s.phase === "ready") return { text: t("shell.updateNotice.restart"), tone: "ok" };
  if (s.phase === "downloading") return { text: t("shell.gameState.updating"), tone: "ember" };
  if (s.phase === "available" && s.available) return { text: t("common.update"), tone: "info" };
  return null;
}

/** The header's update word and notice: direct builds only (none in the Nexus build, nor before app_info answers). */
export function UpdateTray() {
  return useDistribution() === "direct" ? <SelfUpdateTray /> : null;
}

function SelfUpdateTray() {
  const t = useT();
  useUpdateFeed();
  const s = useUpdate();
  const open = useUpdateStore((x) => x.open);
  const setOpen = useUpdateStore((x) => x.setOpen);
  useEffect(() => {
    if (!open) return;
    const esc = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("keydown", esc);
    return () => document.removeEventListener("keydown", esc);
  }, [open, setOpen]);
  const word = s && wordFor(s);
  if (!s || !word) return null;
  return (
    <div className="k-upd">
      <button type="button" className={`k-upd-btn k-tone-${word.tone}`} onClick={() => setOpen(!open)} aria-expanded={open} aria-haspopup="dialog"
        aria-label={t("shell.updateNotice.trayAria", { word: word.text })}>
        <span className="k-sc">{word.text}</span>
      </button>
      {open && <UpdateCard s={s} onClose={() => setOpen(false)} />}
    </div>
  );
}

function Card({ title, onDismiss, children }: { title: string; onDismiss: () => void; children?: ReactNode }) {
  const t = useT();
  return (
    <div className="k-upd-pop" role="dialog" aria-label={title}>
      <div className="k-upd-head">
        <span className="k-upd-title">{title}</span>
        <IconButton label={t("common.dismiss")} size="s" icon={<X />} onClick={onDismiss} />
      </div>
      {children}
    </div>
  );
}

function UpdateCard({ s, onClose }: { s: UpdateStatus; onClose: () => void }) {
  const t = useT();
  if (s.justUpdated) return <JustUpdated s={s} onClose={onClose} />;
  if (s.phase === "ready") return <ReadyCard onClose={onClose} />;
  if (s.phase === "downloading") {
    const p = s.progress;
    return (
      <Card title={t("shell.updateNotice.updating")} onDismiss={onClose}>
        <ProgressBar value={p?.total ? p.received / p.total : null} />
      </Card>
    );
  }
  return <Offer s={s} onClose={onClose} />;
}

function Offer({ s, onClose }: { s: UpdateStatus; onClose: () => void }) {
  const t = useT();
  const [busy, setBusy] = useState(false);
  const a = s.available;
  if (!a) return <Card title={t("shell.updateNotice.upToDate")} onDismiss={onClose} />;
  const update = async () => {
    setBusy(true);
    try {
      await waitTask(await api.updateInstall());
    } catch {
      failed(t("shell.updateNotice.updateFailed"));
    } finally {
      setBusy(false);
    }
  };
  const skip = () => api.updateSkip(a.version).then(onClose, () => failed(t("shell.updateNotice.skipFailed")));
  return (
    <Card title={t("shell.updateNotice.offerTitle", { build: buildLabel(a.version) })} onDismiss={onClose}>
      <div className="k-upd-actions">
        <Button variant="ghost" onClick={skip}>{t("shell.updateNotice.skip")}</Button>
        {s.install.inPlace
          ? <Button variant="primary" loading={busy} onClick={update}>{t("common.update")}</Button>
          : <Button variant="primary" onClick={() => api.appOpenUrl(a.page || s.page).catch(() => failed(t("shell.channelSwitch.openPageFailed")))}>{t("shell.updateNotice.download")}</Button>}
      </div>
    </Card>
  );
}

function ReadyCard({ onClose }: { onClose: () => void }) {
  const t = useT();
  const tasks = useTasks((x) => x.tasks);
  const order = useTasks((x) => x.order);
  const blocking = order.map((id) => tasks[id]).find((x) => x && (x.state === "running" || x.state === "queued") && x.kind !== "update_install");
  const [busy, setBusy] = useState(false);
  const restart = async () => {
    setBusy(true);
    try { await api.updateRestart(); } catch { failed(t("shell.channelSwitch.restartFailed")); setBusy(false); }
  };
  return (
    <Card title={t("shell.updateNotice.readyTitle")} onDismiss={onClose}>
      <div className="k-upd-actions">
        <Button variant="ghost" onClick={onClose}>{t("shell.channelSwitch.later")}</Button>
        <Button variant="primary" loading={busy} disabled={!!blocking} title={blocking ? t("shell.updateNotice.waitingFor", { task: blocking.title }) : undefined} onClick={restart}>{t("shell.updateNotice.restart")}</Button>
      </div>
    </Card>
  );
}

function JustUpdated({ s, onClose }: { s: UpdateStatus; onClose: () => void }) {
  const t = useT();
  const qc = useQueryClient();
  const { game } = useGame();
  const gs = useGameStatus(game?.id);
  const [busy, setBusy] = useState(false);
  const u = s.justUpdated!;
  const rt = gs.data?.runtime;
  const running = !!gs.data?.running;
  // the launcher ships a runtime: after an update it is in the launcher, not yet in the game; after a switch back from a
  // test build it is older than the game's, which came with the test build (InstallState.switchBack)
  const back = rt?.installed ? rt.switchBack ?? null : null;
  const next = rt?.installed ? rt.upgradeAvailable ?? back?.to ?? null : null;
  const ack = () => { api.updateAck().catch(() => {}); onClose(); };
  const updateGame = async () => {
    if (!game || !next) return;
    setBusy(true);
    try {
      // no explicit parts: the backend keeps the game's parts and adds the ones that are new or became defaults, unless
      // the player turned them off (passing the receipt's list here froze it and skipped new parts)
      notifyInstall(await runTask<api.InstallResult>(() => api.runtimeInstall(game.id, next, {}), { done: t("shell.play.kilnInstalled", { version: next }), detail: installNote, quiet: true }));
      ack();
    } catch {
      failed(t("shell.updateNotice.updateFailed"));
    } finally {
      setBusy(false);
      invalidateGame(qc, game.id);
    }
  };
  return (
    <Card title={t("shell.updateNotice.updatedTo", { build: buildLabel(u.to) })} onDismiss={ack}>
      {back && !rt?.upgradeAvailable && <span className="k-hint" data-testid="switch-back-runtime">{t("shell.updateNotice.switchBack", { from: back.from, to: back.to })}</span>}
      {next && (
        <div className="k-upd-actions">
          <Button variant="ghost" onClick={onClose}>{t("shell.channelSwitch.later")}</Button>
          <Button variant="primary" loading={busy} disabled={running} title={running ? t("shell.updateNotice.closeGame") : undefined} onClick={updateGame}>{t("shell.updateNotice.updateGame")}</Button>
        </div>
      )}
    </Card>
  );
}
