// The launcher update in the header: a word beside the tasks (Update, Updating, Restart, Updated) and its notice,
// which drops under the header by itself when an update is found or was just installed. Each state is one line and
// its buttons; sources, fallbacks and failures go to launcher.log, and a failed download is a short toast.
//   available     "Kiln <version>"        Skip, Update (or Download for a .deb or development build)
//   downloading   "Updating…" and a bar
//   ready         "Ready to restart"       Later, Restart
//   just updated  "Updated to <version>"   Later, Update game (installs the runtime it brought; held while the game runs)
import { useQueryClient } from "@tanstack/react-query";
import { X } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import * as api from "../api/commands";
import type { UpdateStatus } from "../api/commands";
import { invalidateGame, useGame, useGameStatus } from "../api/hooks";
import { useUpdate, useUpdateFeed, useUpdateStore } from "../api/update";
import { Button, IconButton, ProgressBar, installNote, runTask, toast, useTasks, waitTask } from "../ui";
import "./update.css";

/** A short error toast; the detail is in launcher.log. */
const failed = (title: string) => toast.error({ code: "internal", message: title });

function wordFor(s: UpdateStatus): { text: string; tone: "info" | "ember" | "ok" } | null {
  if (s.justUpdated) return { text: "Updated", tone: "ok" };
  if (s.phase === "ready") return { text: "Restart", tone: "ok" };
  if (s.phase === "downloading") return { text: "Updating", tone: "ember" };
  if (s.phase === "available" && s.available) return { text: "Update", tone: "info" };
  return null;
}

export function UpdateTray() {
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
        aria-label={`Launcher update: ${word.text}`}>
        <span className="k-sc">{word.text}</span>
      </button>
      {open && <UpdateCard s={s} onClose={() => setOpen(false)} />}
    </div>
  );
}

function Card({ title, onDismiss, children }: { title: string; onDismiss: () => void; children?: ReactNode }) {
  return (
    <div className="k-upd-pop" role="dialog" aria-label={title}>
      <div className="k-upd-head">
        <span className="k-upd-title">{title}</span>
        <IconButton label="Dismiss" size="s" icon={<X />} onClick={onDismiss} />
      </div>
      {children}
    </div>
  );
}

function UpdateCard({ s, onClose }: { s: UpdateStatus; onClose: () => void }) {
  if (s.justUpdated) return <JustUpdated s={s} onClose={onClose} />;
  if (s.phase === "ready") return <ReadyCard onClose={onClose} />;
  if (s.phase === "downloading") {
    const p = s.progress;
    return (
      <Card title="Updating…" onDismiss={onClose}>
        <ProgressBar value={p?.total ? p.received / p.total : null} />
      </Card>
    );
  }
  return <Offer s={s} onClose={onClose} />;
}

function Offer({ s, onClose }: { s: UpdateStatus; onClose: () => void }) {
  const [busy, setBusy] = useState(false);
  const a = s.available;
  if (!a) return <Card title="Up to date" onDismiss={onClose} />;
  const update = async () => {
    setBusy(true);
    try {
      await waitTask(await api.updateInstall());
    } catch {
      failed("Update failed");
    } finally {
      setBusy(false);
    }
  };
  const skip = () => api.updateSkip(a.version).then(onClose, () => failed("Skip failed"));
  return (
    <Card title={`Kiln ${a.version}`} onDismiss={onClose}>
      <div className="k-upd-actions">
        <Button variant="ghost" onClick={skip}>Skip</Button>
        {s.install.inPlace
          ? <Button variant="primary" loading={busy} onClick={update}>Update</Button>
          : <Button variant="primary" onClick={() => api.appOpenUrl(a.page || s.page).catch(() => failed("Could not open the page"))}>Download</Button>}
      </div>
    </Card>
  );
}

function ReadyCard({ onClose }: { onClose: () => void }) {
  const tasks = useTasks((t) => t.tasks);
  const order = useTasks((t) => t.order);
  const blocking = order.map((id) => tasks[id]).find((x) => x && (x.state === "running" || x.state === "queued") && x.kind !== "update_install");
  const [busy, setBusy] = useState(false);
  const restart = async () => {
    setBusy(true);
    try { await api.updateRestart(); } catch { failed("Restart failed"); setBusy(false); }
  };
  return (
    <Card title="Ready to restart" onDismiss={onClose}>
      <div className="k-upd-actions">
        <Button variant="ghost" onClick={onClose}>Later</Button>
        <Button variant="primary" loading={busy} disabled={!!blocking} title={blocking ? `Waiting for ${blocking.title}` : undefined} onClick={restart}>Restart</Button>
      </div>
    </Card>
  );
}

function JustUpdated({ s, onClose }: { s: UpdateStatus; onClose: () => void }) {
  const qc = useQueryClient();
  const { game } = useGame();
  const gs = useGameStatus(game?.id);
  const [busy, setBusy] = useState(false);
  const u = s.justUpdated!;
  const rt = gs.data?.runtime;
  const running = !!gs.data?.running;
  // the launcher ships a runtime: after an update it is in the launcher, not yet in the game
  const next = rt?.installed ? rt.upgradeAvailable : null;
  const ack = () => { api.updateAck().catch(() => {}); onClose(); };
  const updateGame = async () => {
    if (!game || !next) return;
    setBusy(true);
    try {
      // the parts the game has now
      const components = rt?.receipt?.runtime.components ?? null;
      await runTask<api.InstallResult>(() => api.runtimeInstall(game.id, next, components ? { components } : {}), { done: `Kiln ${next} installed`, detail: installNote, quiet: true });
      ack();
    } catch {
      failed("Update failed");
    } finally {
      setBusy(false);
      invalidateGame(qc, game.id);
    }
  };
  return (
    <Card title={`Updated to ${u.to}`} onDismiss={ack}>
      {next && (
        <div className="k-upd-actions">
          <Button variant="ghost" onClick={onClose}>Later</Button>
          <Button variant="primary" loading={busy} disabled={running} title={running ? "Close the game first" : undefined} onClick={updateGame}>Update game</Button>
        </div>
      )}
    </Card>
  );
}
