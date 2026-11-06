// Long tasks: a store fed by `kiln://task`, <TaskProgress/> (the cone pack: pyrometric cones bend one per step, in
// place of a progress bar), the header's <TaskTray/> (a label and three small cones), and `runTask(start)` which awaits
// a TaskId command's result:
//   const receipt = await runTask(() => runtimeInstall(gameId, version), { done: "Installed" });
import { X } from "lucide-react";
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

export interface RunTaskOptions<R = unknown> {
  /** Success toast. */
  done?: string;
  /** The success toast's second line, from the result (none when it returns nothing). */
  detail?: (result: R) => string | undefined;
  /** Error toast title (the error message is the detail). */
  failed?: string;
  /** No error toast (the caller shows the error). */
  quiet?: boolean;
}

/** Starts a TaskId command and awaits its result; toasts on success/failure. */
export async function runTask<R = unknown>(start: () => Promise<TaskId>, opts: RunTaskOptions<R> = {}): Promise<R> {
  try {
    const id = await start();
    const r = await waitTask<R>(id);
    if (opts.done) toast.ok(opts.done, opts.detail?.(r));
    return r;
  } catch (e) {
    const k = e as KilnError;
    if (!opts.quiet && k?.code !== "canceled") toast.error(e, opts.failed);
    throw e;
  }
}

/** One cone: upright, bending (this step) or slumped (done). */
export function Cone({ state }: { state: "" | "bend" | "done" }) {
  return <span className={"k-cone" + (state ? ` ${state}` : "")} aria-hidden><i className="lo" /><i className="up" /></span>;
}

/** The cone pack: `count` cones, those before `step` slumped, the one at `step` bending. */
export function Cones({ step, count = 3, small }: { step: number; count?: number; small?: boolean }) {
  return (
    <span className={small ? "k-cones-s" : "k-cones"} aria-hidden>
      {Array.from({ length: count }, (_, i) => <Cone key={i} state={i < step ? "done" : i === step ? "bend" : ""} />)}
    </span>
  );
}

/** Which cone bends for a task: by its progress, or stepping along while it has none. */
export function useTaskStep(t: Task | undefined): number {
  const [tick, setTick] = useState(0);
  const indet = !!t && !finished(t) && (t.progress === null || t.progress === undefined);
  useEffect(() => {
    if (!indet) return;
    const id = setInterval(() => setTick((n) => n + 1), 900);
    return () => clearInterval(id);
  }, [indet]);
  if (!t) return -1;
  if (t.state === "done") return 3;
  if (finished(t)) return -1;
  if (indet) return tick % 3;
  return Math.min(2, Math.floor((t.progress ?? 0) * 3));
}

/** A determinate bar, kept for callers outside the cone pack. */
export function ProgressBar({ value }: { value: number | null | undefined }) {
  const v = value ?? 0;
  return (
    <div className="k-progress" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(v * 100)}>
      <div className="k-progress-bar" style={{ width: `${Math.max(2, v * 100)}%` }} />
    </div>
  );
}

/** One task: title, cones, message, cancel. Pass a task or its id. */
export function TaskProgress({ task, taskId, cancel = true }: { task?: Task; taskId?: TaskId; cancel?: boolean }) {
  const fromStore = useTasks((s) => (taskId ? s.tasks[taskId] : undefined));
  const t = task ?? fromStore;
  const step = useTaskStep(t);
  if (!t) return null;
  const running = !finished(t);
  const word = t.state === "done" ? "Done" : t.state === "failed" ? "Failed" : t.state === "canceled" ? "Canceled" : null;
  return (
    <div className="k-task" role="status" aria-label={`${t.title}${word ? `: ${word}` : ""}`}>
      <div className="k-task-head">
        <Cones step={step} small />
        <span className="k-ellipsis" style={{ flex: 1 }}>{t.title}</span>
        {word && <span className={`k-word-sm k-tone-${t.state === "done" ? "ok" : t.state === "failed" ? "err" : "idle"}`}>{word}</span>}
        {running && t.progress !== null && t.progress !== undefined && <span className="k-task-pct">{Math.round(t.progress * 100)}%</span>}
        {running && cancel && <IconButton label={`Cancel ${t.title}`} size="s" icon={<X />} onClick={() => taskCancel(t.id).catch((e) => toast.error(e))} />}
      </div>
      {(t.error?.message ?? t.message) && <div className="k-task-msg" title={t.error?.message ?? t.message ?? ""}>{t.error?.message ?? t.message}</div>}
    </div>
  );
}

/** The header's tasks: the running task's name and three small cones; opens the list. Nothing shows while idle. */
export function TaskTray() {
  const { tasks, order, clearFinished } = useTasks();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const runningIds = order.filter((id) => !finished(tasks[id]));
  const lead = runningIds.length ? tasks[runningIds[0]] : undefined;
  const step = useTaskStep(lead);
  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    const esc = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", close);
    document.addEventListener("keydown", esc);
    return () => { document.removeEventListener("mousedown", close); document.removeEventListener("keydown", esc); };
  }, [open]);
  if (!order.length) return null;
  const label = lead ? (runningIds.length > 1 ? `${lead.title} and ${runningIds.length - 1} more` : lead.title) : "Tasks";
  return (
    <div className="k-tray" ref={ref}>
      <button type="button" className={"k-tray-btn" + (lead ? "" : " idle")} onClick={() => setOpen((o) => !o)} aria-expanded={open} aria-haspopup="dialog"
        aria-label={lead ? `Tasks: ${label}` : "Tasks"} title={label}>
        <span className="k-sc">{label}</span>
        {lead && <Cones step={step} small />}
      </button>
      {open && (
        <div className="k-tray-pop" role="dialog" aria-label="Tasks">
          <div className="k-tray-head">
            <span className="k-panel-title">Tasks</span>
            {order.length > runningIds.length && <button type="button" className="k-link" style={{ fontSize: 15 }} onClick={clearFinished}>Clear finished</button>}
          </div>
          {order.map((id) => <TaskProgress key={id} task={tasks[id]} />)}
        </div>
      )}
    </div>
  );
}
