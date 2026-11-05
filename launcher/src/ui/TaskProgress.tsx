// Long tasks: a store fed by `kiln://task`, <TaskProgress/>, the shell's <TaskTray/>, and
// `runTask(start)` which awaits a TaskId command's result:
//   const receipt = await runTask(() => runtimeInstall(gameId, version), { done: "Installed" });
import { Activity, CircleAlert, CircleCheck, X } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { create } from "zustand";
import { onKilnEvent, taskCancel, taskList, type KilnError, type Task, type TaskId } from "../api/commands";
import { IconButton } from "./Button";
import { toast } from "./Toast";

interface Waiter { resolve: (r: unknown) => void; reject: (e: KilnError) => void }

interface TaskStore {
  tasks: Record<TaskId, Task>;
  order: TaskId[];
  upsert: (t: Task) => void;
  clearFinished: () => void;
}

const waiters = new Map<TaskId, Waiter[]>();
const finished = (t: Task) => t.state === "done" || t.state === "failed" || t.state === "canceled";

function settle(t: Task) {
  const ws = waiters.get(t.id);
  if (!ws || !finished(t)) return;
  waiters.delete(t.id);
  for (const w of ws) {
    if (t.state === "done") w.resolve(t.result ?? null);
    else w.reject(t.error ?? { code: t.state === "canceled" ? "canceled" : "internal", message: t.message ?? `${t.title} ${t.state}` });
  }
}

export const useTasks = create<TaskStore>((set, get) => ({
  tasks: {},
  order: [],
  upsert: (t) => {
    const s = get();
    set({ tasks: { ...s.tasks, [t.id]: t }, order: s.order.includes(t.id) ? s.order : [t.id, ...s.order].slice(0, 30) });
    settle(t);
  },
  clearFinished: () => {
    const s = get();
    const keep = s.order.filter((id) => !finished(s.tasks[id]));
    set({ order: keep, tasks: Object.fromEntries(keep.map((id) => [id, s.tasks[id]])) });
  },
}));

/** Mount once (the shell does): subscribes to kiln://task and loads running tasks. */
export function useTaskFeed() {
  useEffect(() => {
    let off: (() => void) | undefined;
    let live = true;
    onKilnEvent("kiln://task", (t) => useTasks.getState().upsert(t)).then((f) => (live ? (off = f) : f()));
    taskList().then((ts) => ts.forEach((t) => useTasks.getState().upsert(t))).catch(() => {});
    return () => { live = false; off?.(); };
  }, []);
}

/** Resolves with the task's result, rejects with its KilnError. */
export function waitTask<R = unknown>(id: TaskId): Promise<R> {
  const t = useTasks.getState().tasks[id];
  if (t && finished(t)) return t.state === "done" ? Promise.resolve(t.result as R) : Promise.reject(t.error ?? { code: "internal", message: t.message ?? "task failed" });
  return new Promise<R>((resolve, reject) => {
    waiters.set(id, [...(waiters.get(id) ?? []), { resolve: resolve as (r: unknown) => void, reject }]);
  });
}

export interface RunTaskOptions {
  /** Success toast. */
  done?: string;
  /** Error toast title (the error message is the detail). */
  failed?: string;
  /** No error toast (the caller shows the error). */
  quiet?: boolean;
}

/** Starts a TaskId command and awaits its result; toasts on success/failure. */
export async function runTask<R = unknown>(start: () => Promise<TaskId>, opts: RunTaskOptions = {}): Promise<R> {
  try {
    const id = await start();
    const r = await waitTask<R>(id);
    if (opts.done) toast.ok(opts.done);
    return r;
  } catch (e) {
    const k = e as KilnError;
    if (!opts.quiet && k?.code !== "canceled") toast.error(e, opts.failed);
    throw e;
  }
}

export function ProgressBar({ value }: { value: number | null | undefined }) {
  const indet = value === null || value === undefined;
  return (
    <div className={"k-progress" + (indet ? " indeterminate" : "")} role="progressbar" aria-valuemin={0} aria-valuemax={100}
      aria-valuenow={indet ? undefined : Math.round(value * 100)}>
      <div className="k-progress-bar" style={{ width: indet ? undefined : `${Math.max(2, value * 100)}%` }} />
    </div>
  );
}

/** One task: title, message, bar, cancel. Pass a task or its id. */
export function TaskProgress({ task, taskId, cancel = true }: { task?: Task; taskId?: TaskId; cancel?: boolean }) {
  const fromStore = useTasks((s) => (taskId ? s.tasks[taskId] : undefined));
  const t = task ?? fromStore;
  if (!t) return null;
  const running = !finished(t);
  return (
    <div className="k-task">
      <div className="k-task-head">
        {t.state === "done" ? <CircleCheck size={15} color="var(--k-ok)" /> : t.state === "failed" ? <CircleAlert size={15} color="var(--k-err)" /> : <Activity size={15} color="var(--k-ember-400)" />}
        <span className="k-ellipsis" style={{ flex: 1 }}>{t.title}</span>
        {running && t.progress !== null && <span className="k-muted k-num" style={{ fontSize: 12 }}>{Math.round(t.progress * 100)}%</span>}
        {running && cancel && <IconButton label="Cancel" size="s" icon={<X />} onClick={() => taskCancel(t.id).catch((e) => toast.error(e))} />}
      </div>
      {running && <ProgressBar value={t.progress} />}
      {(t.error?.message ?? t.message) && <div className="k-task-msg" title={t.error?.message ?? t.message ?? ""}>{t.error?.message ?? t.message}</div>}
    </div>
  );
}

/** The shell's task button + popover. */
export function TaskTray() {
  const { tasks, order, clearFinished } = useTasks();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const running = order.filter((id) => !finished(tasks[id])).length;
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", close);
    document.addEventListener("keydown", esc);
    return () => { document.removeEventListener("mousedown", close); document.removeEventListener("keydown", esc); };
  }, [open]);
  return (
    <div className="k-tray" ref={ref}>
      <IconButton label={running ? `${running} running` : "Tasks"} icon={running ? <span className="k-spin" /> : <Activity />} onClick={() => setOpen((o) => !o)} aria-expanded={open} />
      {running > 0 && <span className="k-tray-count">{running}</span>}
      {open && (
        <div className="k-tray-pop" role="dialog" aria-label="Tasks">
          <div className="k-row-between">
            <span className="k-panel-title">Tasks</span>
            {order.length > running && <button type="button" className="k-link" style={{ fontSize: 12 }} onClick={clearFinished}>Clear</button>}
          </div>
          {order.length === 0 ? <div className="k-muted" style={{ fontSize: 13, padding: "8px 2px" }}>No tasks.</div> : order.map((id) => <TaskProgress key={id} task={tasks[id]} />)}
        </div>
      )}
    </div>
  );
}
