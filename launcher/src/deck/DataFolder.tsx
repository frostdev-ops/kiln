// Deck Settings' "Data folder" row and its change, the desktop's steps (shell/dataFolder.ts) in the Deck's dialogs: the
// folder browser (deck_browse, "Use this folder"), the plan's dialog, the move with its progress and Cancel, then the
// restart. Also the notice at the first start after a move (DeckApp).
import { useQueryClient } from "@tanstack/react-query";
import { useRef, useState } from "react";
import * as api from "../api/commands";
import type { DataFolder } from "../api/commands";
import { qk, useAppInfo, useDataFolder, useDataFolderSize } from "../api/hooks";
import { useDataFolderChange } from "../shell/dataFolder";
import { doneLines, movingLine, noticeView, planView, rowView, sizeView, stoppedView } from "../shell/dataFolderText";
import { PathText, toast, useTasks } from "../ui";
import { FolderView, useBrowse } from "./dialogs";
import { useLayer } from "./layers";
import { DeckButton, Dialog, Overlay, Row, Word, dialogFirst, useFirstFocus, useReturnFocus } from "./ui";

export function DeckDataFolderRow() {
  const f = useDataFolder();
  const info = useAppInfo();
  const view = rowView(f.data, f.error, info.data?.dataDir);
  const size = sizeView(useDataFolderSize(!!f.data).data);
  const flow = useDataFolderChange();
  const [browse, setBrowse] = useState(false);
  const s = flow.step;
  const current = view.path ?? "";
  return (
    <>
      <Row id="set-data" title="Data folder" a="Change" disabled={!view.canChange || s.at === "checking"}
        sub={view.why ?? (view.path ? <PathText path={view.path} testId="deck-data-folder-path" /> : undefined)}
        end={<Word tone="idle">{size?.text ?? "…"}</Word>} onClick={() => setBrowse(true)} />
      {browse && <BrowseDialog onClose={() => setBrowse(false)} onUse={(dir) => { setBrowse(false); void flow.check(dir); }} />}
      {s.at === "plan" && (() => {
        const v = planView(s.plan);
        const refuse = s.plan.action === "refuse";
        return (
          <Dialog name="data-plan" title={v.title} onClose={flow.close} locked={s.busy} wide backLabel={v.dismiss}
            buttons={<>
              <DeckButton onClick={flow.close} disabled={s.busy}>{v.dismiss}</DeckButton>
              <DeckButton primary disabled={s.busy} onClick={refuse ? () => { flow.close(); setBrowse(true); } : () => void flow.confirm()}>{v.confirm}</DeckButton>
            </>}>
            <dl className="deck-facts deck-df">
              {v.facts.map((x) => <div key={x.label}><dt>{x.label}</dt><dd>{x.path ? <PathText path={x.value} /> : x.value}</dd></div>)}
            </dl>
            {v.lines.map((l) => <p key={l} className="deck-note">{l}</p>)}
            {s.error && <p className="deck-note" role="alert"><Word tone="err">{s.error}</Word></p>}
          </Dialog>
        );
      })()}
      {s.at === "moving" && <MovingDialog taskId={s.taskId} canceling={s.canceling} onCancel={flow.cancel} />}
      {s.at === "stopped" && (() => {
        const v = stoppedView(s.how, s.error);
        return (
          <Dialog name="data-stopped" title={v.title} onClose={flow.close} backLabel="Close" buttons={<DeckButton primary onClick={flow.close}>Close</DeckButton>}>
            {v.lines.map((l, i) => i < v.lines.length - 1
              ? <p key={l} className="deck-note" role="alert"><Word tone="err">{l}</Word></p>
              : <p key={l} className="deck-note">{l} <PathText path={current} /></p>)}
          </Dialog>
        );
      })()}
      {s.at === "restarting" && <Dialog name="data-restarting" title="Restarting" onClose={() => {}} locked />}
      {s.at === "done" && (
        <Dialog name="data-done" title={s.moved ? "Data folder moved" : "Restart Kiln"} onClose={() => {}} locked
          buttons={<DeckButton primary onClick={flow.restart}>Restart</DeckButton>}>
          {(doneLines(s.moved) ?? []).map((w) => <p key={w} className="deck-note">{w}</p>)}
          {s.error && <p className="deck-note" role="alert">{s.error}</p>}
        </Dialog>
      )}
    </>
  );
}

/** Pick the new place: browse to it, then "Use this folder". */
function BrowseDialog({ onClose, onUse }: { onClose: () => void; onUse: (dir: string) => void }) {
  const ref = useRef<HTMLDivElement>(null);
  // from Downloads, as Add mods; the places (Home, the SD card) are one press away
  const b = useBrowse(null, ref);
  const opener = useRef<HTMLElement | null>(document.activeElement as HTMLElement | null);
  useFirstFocus(ref, null, dialogFirst);
  useReturnFocus(opener);
  useLayer(ref, { modal: true, backLabel: "Up", back: () => { if (!b.up()) onClose(); }, actions: [{ button: "x", label: "Close", run: onClose }] });
  const f = b.q.data;
  return (
    <Overlay name="data-browse">
      <div ref={ref} className="deck-dialog wide tall" role="dialog" aria-modal="true" aria-label="Data folder">
        <h2 className="deck-dialog-title">Data folder</h2>
        <FolderView f={f} loading={b.q.isLoading} error={b.q.error} mode="folder" onOpen={(d) => b.open(d)} onPlace={(d) => b.jump(d)}
          onUse={() => f && onUse(f.dir)} />
      </div>
    </Overlay>
  );
}

function MovingDialog({ taskId, canceling, onCancel }: { taskId: api.TaskId; canceling: boolean; onCancel: () => void }) {
  const t = useTasks((x) => x.tasks[taskId]);
  return (
    <Dialog name="data-moving" title="Moving the data folder" onClose={() => {}} locked
      buttons={<DeckButton primary disabled={canceling} onClick={onCancel}>{canceling ? "Canceling" : "Cancel"}</DeckButton>}>
      <p className="deck-line" data-testid="deck-data-task">
        <span className="deck-dim">{t?.message ?? "Starting"}</span>
        {t?.progress != null && <span className="deck-bar" aria-hidden><i style={{ width: `${Math.round(t.progress * 100)}%` }} /></span>}
      </p>
      {movingLine(t?.message) && <p className="deck-note" data-testid="deck-data-removing">{movingLine(t?.message)}</p>}
    </Dialog>
  );
}

/** The first start after a move (Deck mode). */
export function DeckDataFolderNotice() {
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
    <Dialog name="data-notice" title={v.title} onClose={ack} backLabel="Close" wide
      buttons={<>
        {v.open && <DeckButton onClick={() => api.appOpenPath(n.kind === "undone" ? n.to : n.from).catch((e) => toast.error(e))}>{v.openLabel}</DeckButton>}
        <DeckButton primary onClick={ack}>OK</DeckButton>
      </>}>
      {v.facts.length > 0 && (
        <dl className="deck-facts deck-df">
          {v.facts.map((x) => <div key={x.label}><dt>{x.label}</dt><dd><PathText path={x.value} /></dd></div>)}
        </dl>
      )}
      {v.lines.map((l) => <p key={l} className="deck-note">{l}</p>)}
      {v.lists.map((l) => (
        <div key={l.line}>
          <p className="deck-note">{l.line}</p>
          <ul className="k-df-list deck-mono" aria-label={l.line}>{l.items.map((i) => <li key={i.title + i.text} title={i.title}>{i.text}</li>)}</ul>
        </div>
      ))}
    </Dialog>
  );
}
