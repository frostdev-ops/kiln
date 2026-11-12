// Settings, Library card: the "Data folder" row (the path at once, its size when measured, Change, Open) and the dialogs
// of a change (shell/dataFolder.ts has the steps, shell/dataFolderText.ts the words).
import type { ReactNode } from "react";
import * as api from "../api/commands";
import { useAppInfo, useDataFolder, useDataFolderSize } from "../api/hooks";
import { pickFolder, useDataFolderChange, type DataFolderChange } from "../shell/dataFolder";
import { doneLines, movingLine, planView, rowView, sizeView, stoppedView } from "../shell/dataFolderText";
import { Button, KeyValue, Modal, PathText, TaskProgress, toast, useTasks } from "../ui";

const parent = (p: string) => p.replace(/[\\/]+$/, "").replace(/[\\/][^\\/]*$/, "") || undefined;

/** The value of the Library card's "Data folder" line. */
export function DataFolderValue() {
  const f = useDataFolder();
  const info = useAppInfo();
  const view = rowView(f.data, f.error, info.data?.dataDir);
  const size = sizeView(useDataFolderSize(!!f.data).data);
  const flow = useDataFolderChange();
  const change = async () => {
    try {
      const p = await pickFolder(f.data ? parent(f.data.path) : undefined);
      if (p) await flow.check(p);
    } catch (e) { toast.error(e, "Could not open the folder dialog"); }
  };
  const checking = flow.step.at === "checking";
  return (
    <span className="k-stack" style={{ gap: 2, minWidth: 0 }} data-testid="data-folder">
      <span className="k-row" style={{ gap: 10, minWidth: 0, flexWrap: "nowrap" }}>
        {view.path ? <PathText path={view.path} testId="data-folder-path" /> : <span className="k-hint">…</span>}
        <span className="k-hint" style={{ flex: "none" }} data-testid="data-folder-size" title={size?.title} aria-busy={!size || undefined}>{size?.text ?? "…"}</span>
        <button type="button" className="k-link" style={{ flex: "none" }} disabled={!view.canChange || checking} title={view.why ?? undefined}
          onClick={() => void change()}>{checking ? "Checking…" : "Change"}</button>
        {view.path && <button type="button" className="k-link" style={{ flex: "none" }} onClick={() => api.appOpenPath(view.path!).catch((e) => toast.error(e))}>Open</button>}
      </span>
      {view.why && <span className="k-hint" data-testid="data-folder-why">{view.why}</span>}
      <ChangeDialogs flow={flow} current={view.path ?? ""} again={() => void change()} />
    </span>
  );
}

/** One dialog per step; while the move or the restart runs it cannot be dismissed. */
export function ChangeDialogs({ flow, current, again }: { flow: DataFolderChange; current: string; again: () => void }) {
  const s = flow.step;
  if (s.at === "idle" || s.at === "checking") return null;
  if (s.at === "plan") {
    const v = planView(s.plan);
    const refuse = s.plan.action === "refuse";
    return (
      <Modal open onClose={flow.close} title={v.title} width={560} locked={s.busy}
        footer={<>
          <Button onClick={flow.close} disabled={s.busy}>{v.dismiss}</Button>
          <Button variant="primary" loading={s.busy} onClick={refuse ? () => { flow.close(); again(); } : () => void flow.confirm()}>{v.confirm}</Button>
        </>}>
        <Facts facts={v.facts} />
        {v.lines.map((l) => <p key={l} className="k-df-line">{l}</p>)}
        {s.error && <p className="k-df-line k-tone-err" role="alert">{s.error}</p>}
      </Modal>
    );
  }
  if (s.at === "moving") {
    return (
      <Modal open locked onClose={() => {}} title="Moving the data folder" width={560}
        footer={<Button onClick={flow.cancel} disabled={s.canceling} loading={s.canceling}>Cancel</Button>}>
        <Facts facts={[{ label: "To", value: s.plan.target, path: true }]} />
        <TaskProgress taskId={s.taskId} cancel={false} />
        <MovingLine taskId={s.taskId} />
      </Modal>
    );
  }
  if (s.at === "stopped") {
    const v = stoppedView(s.how, s.error);
    return (
      <Modal open onClose={flow.close} title={v.title} width={560} footer={<Button variant="primary" onClick={flow.close}>Close</Button>}>
        {v.lines.map((l, i) => i < v.lines.length - 1
          ? <p key={l} className="k-df-line k-tone-err" role="alert">{l}</p>
          : <p key={l} className="k-df-line">{l} <PathText path={current} /></p>)}
      </Modal>
    );
  }
  if (s.at === "restarting") return <Modal open locked onClose={() => {}} title="Restarting" width={480} />;
  // done: the move's warnings, or a restart that did not start
  return (
    <Modal open locked onClose={() => {}} title={s.moved ? "Data folder moved" : "Restart Kiln"} width={560}
      footer={<Button variant="primary" onClick={flow.restart}>Restart</Button>}>
      {s.moved && <Facts facts={[{ label: "Now in", value: s.moved.to, path: true }]} />}
      {(doneLines(s.moved) ?? []).map((w) => <p key={w} className="k-df-line">{w}</p>)}
      {s.error && <p className="k-df-line" role="alert">{s.error}</p>}
    </Modal>
  );
}

/** The move's last phase, from the task's message. */
function MovingLine({ taskId }: { taskId: string }) {
  const line = movingLine(useTasks((x) => x.tasks[taskId]?.message));
  return line ? <p className="k-df-line" data-testid="data-folder-removing">{line}</p> : null;
}

function Facts({ facts }: { facts: { label: string; value: string; path?: boolean }[] }) {
  return <KeyValue rows={facts.map((f): [ReactNode, ReactNode] => [f.label, f.path ? <PathText path={f.value} /> : f.value])} />;
}
