// The data folder at start-up (main.tsx, around both UIs):
// - DataFolderGate asks data_folder_get first. When another launcher is moving the data folder (`elsewhere`), only
//   "Kiln is moving its data in another window" and Quit. When the pointer names a folder that cannot be used, nothing else of the
//   launcher mounts (every other command fails then, so no page, toast or error boundary can come up): only a dialog
//   that cannot be dismissed, with the path and the reason, Choose folder (the folder dialog, then data_folder_use),
//   Start fresh (data_folder_reset) and Quit; the first two restart the launcher. A backend without the command (or
//   one that does not answer in 5 s) gets the launcher as before.
// - DataFolderNotice (the desktop shell; the Deck has its own in deck/DataFolder.tsx): the first start after a move
//   says where the data is now and what stayed in the old folder, until OK (data_folder_notice_ack).
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type ReactNode } from "react";
import * as api from "../api/commands";
import type { DataFolder } from "../api/commands";
import { qk, useDataFolder } from "../api/hooks";
import { Button, KeyValue, Modal, PathText, Splash, errorText, toast } from "../ui";
import { pickFolder, restartLauncher } from "./dataFolder";
import { BROKEN_TITLE, ELSEWHERE_TITLE, noticeView } from "./dataFolderText";

const GATE_WAIT_MS = 5000;

export function DataFolderGate({ children }: { children: ReactNode }) {
  const qc = useQueryClient();
  // undefined: asking; null: go on; a folder: its pointer is broken
  const [broken, setBroken] = useState<DataFolder | null | undefined>(undefined);
  useEffect(() => {
    let live = true;
    const t = setTimeout(() => { if (live) setBroken((b) => (b === undefined ? null : b)); }, GATE_WAIT_MS);
    api.dataFolderGet().then((f) => {
      if (!live) return;
      qc.setQueryData(qk.dataFolder, f);
      // a late answer with a problem still takes the window
      setBroken(f.problem || f.elsewhere ? f : null);
    }, () => { if (live) setBroken((b) => (b === undefined ? null : b)); })
      .finally(() => clearTimeout(t));
    return () => { live = false; clearTimeout(t); };
  }, [qc]);
  if (broken === undefined) return <Splash done={false} />;
  if (broken?.problem) return <BrokenPointer folder={broken} />;
  if (broken?.elsewhere) return <MovingElsewhere />;
  return <>{children}</>;
}

/** The pointer names a folder that cannot be used. */
export function BrokenPointer({ folder }: { folder: DataFolder }) {
  const [busy, setBusy] = useState<null | "pick" | "fresh">(null);
  const [error, setError] = useState<string | null>(null);
  const [restarting, setRestarting] = useState(false);
  const p = folder.problem!;
  const restart = async () => {
    setRestarting(true);
    const e = await restartLauncher();
    setRestarting(false);
    setError(e);
  };
  const choose = async () => {
    setBusy("pick");
    setError(null);
    try {
      const path = await pickFolder();
      if (!path) return;
      await api.dataFolderUse(path);
      await restart();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(null);
    }
  };
  const fresh = async () => {
    setBusy("fresh");
    setError(null);
    try {
      await api.dataFolderReset();
      await restart();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(null);
    }
  };
  const quit = () => { api.deckQuit().catch((e) => setError(errorText(e))); };
  return (
    <div className="k-df-gate">
      <Modal open locked onClose={() => {}} title={restarting ? "Restarting" : BROKEN_TITLE} width={580}
        footer={restarting ? undefined : <>
          <Button variant="ghost" onClick={quit} disabled={!!busy}>Quit</Button>
          <Button onClick={() => void fresh()} loading={busy === "fresh"} disabled={!!busy} title={`An empty data folder in ${folder.defaultPath}`}>Start fresh</Button>
          <Button variant="primary" data-autofocus onClick={() => void choose()} loading={busy === "pick"} disabled={!!busy}>Choose folder</Button>
        </>}>
        {!restarting && <>
          <KeyValue rows={[["Folder", <PathText path={p.path} testId="data-folder-problem-path" />]]} />
          <p className="k-df-line">{p.reason}</p>
        </>}
        {error && <p className="k-df-line k-tone-err" role="alert">{error}</p>}
      </Modal>
    </div>
  );
}

/** Another launcher holds the data folder (it is moving it): nothing else mounts; Quit. */
export function MovingElsewhere() {
  const [error, setError] = useState<string | null>(null);
  return (
    <div className="k-df-gate">
      <Modal open locked onClose={() => {}} title={ELSEWHERE_TITLE} width={520}
        footer={<Button variant="primary" data-autofocus onClick={() => { api.deckQuit().catch((e) => setError(errorText(e))); }}>Quit</Button>}>
        {error && <p className="k-df-line k-tone-err" role="alert">{error}</p>}
      </Modal>
    </div>
  );
}

/** The first start after a move (desktop). */
export function DataFolderNotice() {
  const qc = useQueryClient();
  const f = useDataFolder();
  const n = f.data?.notice;
  if (!f.data || !n) return null;
  const v = noticeView(n);
  const ack = () => {
    qc.setQueryData<DataFolder>(qk.dataFolder, { ...f.data!, notice: null });
    api.dataFolderNoticeAck().catch(() => { /* it shows again at the next start */ });
  };
  return (
    <Modal open onClose={ack} title={v.title} width={600}
      footer={<>
        {v.open && <Button onClick={() => api.appOpenPath(n.kind === "undone" ? n.to : n.from).catch((e) => toast.error(e))}>Open old folder</Button>}
        <Button variant="primary" onClick={ack}>OK</Button>
      </>}>
      <NoticeBody v={v} />
    </Modal>
  );
}

/** The notice's facts, lines and lists (desktop). */
function NoticeBody({ v }: { v: ReturnType<typeof noticeView> }) {
  return <>
    {v.facts.length > 0 && <KeyValue rows={v.facts.map((x): [string, ReactNode] => [x.label, <PathText path={x.value} />])} />}
    {v.lines.map((l) => <p key={l} className="k-df-line">{l}</p>)}
    {v.lists.map((l) => (
      <div key={l.line}>
        <p className="k-df-line">{l.line}</p>
        <ul className="k-df-list" aria-label={l.line}>{l.items.map((i) => <li key={i.title + i.text} className="k-mono" title={i.title}>{i.text}</li>)}</ul>
      </div>
    ))}
  </>;
}
