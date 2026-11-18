// Long tasks: a store fed by `kiln://task`, <TaskProgress/> (the cone pack: pyrometric cones bend one per step, in
// place of a progress bar), the header's <TaskTray/> (a label and three small cones), and `runTask(start)` which awaits
// a TaskId command's result:
//   const receipt = await runTask(() => runtimeInstall(gameId, version), { done: "Installed" });
import { X } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { create } from "zustand";
import { onKilnEvent, taskCancel, taskList, type KilnError, type Task, type TaskId } from "../api/commands";
import { t, useT } from "../i18n";
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
const finished = (task: Task) => task.state === "done" || task.state === "failed" || task.state === "canceled";

function settle(task: Task) {
  const ws = waiters.get(task.id);
  if (!ws || !finished(task)) return;
  waiters.delete(task.id);
  for (const w of ws) {
    if (task.state === "done") w.resolve(task.result ?? null);
    else w.reject(task.error ?? { code: task.state === "canceled" ? "canceled" : "internal", message: task.message ?? endedText(task) });
  }
}

/** "Install Kiln failed", "Install Kiln canceled": a finished task's sentence when it left no message. */
function endedText(task: Task): string {
  return task.state === "canceled" ? t("ui.taskProgress.titleCanceled", { title: task.title }) : t("ui.taskProgress.titleFailed", { title: task.title });
}

export const useTasks = create<TaskStore>((set, get) => ({
  tasks: {},
  order: [],
  upsert: (task) => {
    const s = get();
    set({ tasks: { ...s.tasks, [task.id]: task }, order: s.order.includes(task.id) ? s.order : [task.id, ...s.order].slice(0, 30) });
    settle(task);
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
    onKilnEvent("kiln://task", (task) => useTasks.getState().upsert(task)).then((f) => (live ? (off = f) : f()));
    taskList().then((ts) => ts.forEach((task) => useTasks.getState().upsert(task))).catch(() => {});
    return () => { live = false; off?.(); };
  }, []);
}

/** Resolves with the task's result, rejects with its KilnError. */
export function waitTask<R = unknown>(id: TaskId): Promise<R> {
  const task = useTasks.getState().tasks[id];
  if (task && finished(task)) return task.state === "done" ? Promise.resolve(task.result as R) : Promise.reject(task.error ?? { code: "internal", message: task.message ?? t("ui.taskProgress.taskFailed") });
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
export function useTaskStep(task: Task | undefined): number {
  const [tick, setTick] = useState(0);
  const indet = !!task && !finished(task) && (task.progress === null || task.progress === undefined);
  useEffect(() => {
    if (!indet) return;
    const id = setInterval(() => setTick((n) => n + 1), 900);
    return () => clearInterval(id);
  }, [indet]);
  if (!task) return -1;
  if (task.state === "done") return 3;
  if (finished(task)) return -1;
  if (indet) return tick % 3;
  return Math.min(2, Math.floor((task.progress ?? 0) * 3));
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
  const t = useT();
  const fromStore = useTasks((s) => (taskId ? s.tasks[taskId] : undefined));
  const tk = task ?? fromStore;
  const step = useTaskStep(tk);
  if (!tk) return null;
  const running = !finished(tk);
  const word = tk.state === "done" ? t("ui.taskProgress.done") : tk.state === "failed" ? t("ui.taskProgress.failed") : tk.state === "canceled" ? t("ui.taskProgress.canceled") : null;
  return (
    <div className="k-task" role="status" aria-label={word ? t("ui.taskProgress.titleState", { title: tk.title, state: word }) : tk.title}>
      <div className="k-task-head">
        <Cones step={step} small />
        <span className="k-ellipsis" style={{ flex: 1 }}>{tk.title}</span>
        {word && <span className={`k-word-sm k-tone-${tk.state === "done" ? "ok" : tk.state === "failed" ? "err" : "idle"}`}>{word}</span>}
        {running && tk.progress !== null && tk.progress !== undefined && <span className="k-task-pct">{Math.round(tk.progress * 100)}%</span>}
        {running && cancel && <IconButton label={t("ui.taskProgress.cancelTask", { title: tk.title })} size="s" icon={<X />} onClick={() => taskCancel(tk.id).catch((e) => toast.error(e))} />}
      </div>
      {(tk.error?.message ?? tk.message) && <div className="k-task-msg" title={tk.error?.message ?? tk.message ?? ""}>{tk.error?.message ?? tk.message}</div>}
    </div>
  );
}

/** The header's tasks: the running task's name and three small cones; opens the list. Nothing shows while idle. */
export function TaskTray() {
  const t = useT();
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
  const label = lead ? (runningIds.length > 1 ? t("ui.taskProgress.andMore", { title: lead.title, count: runningIds.length - 1 }) : lead.title) : t("ui.taskProgress.tasks");
  return (
    <div className="k-tray" ref={ref}>
      <button type="button" className={"k-tray-btn" + (lead ? "" : " idle")} onClick={() => setOpen((o) => !o)} aria-expanded={open} aria-haspopup="dialog"
        aria-label={lead ? t("ui.taskProgress.tasksLabel", { label }) : t("ui.taskProgress.tasks")} title={label}>
        <span className="k-sc">{label}</span>
        {lead && <Cones step={step} small />}
      </button>
      {open && (
        <div className="k-tray-pop" role="dialog" aria-label={t("ui.taskProgress.tasks")}>
          <div className="k-tray-head">
            <span className="k-panel-title">{t("ui.taskProgress.tasks")}</span>
            {order.length > runningIds.length && <button type="button" className="k-link" style={{ fontSize: 15 }} onClick={clearFinished}>{t("ui.taskProgress.clearFinished")}</button>}
          </div>
          {order.map((id) => <TaskProgress key={id} task={tasks[id]} />)}
        </div>
      )}
    </div>
  );
}
