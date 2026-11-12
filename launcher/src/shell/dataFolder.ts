// Changing the data folder, the same steps on the desktop (components/DataFolderSettings.tsx) and on the Deck
// (deck/DataFolder.tsx): a folder the player picked -> data_folder_check -> the dialog its plan becomes -> Move (a task:
// progress and Cancel) or Use it -> the launcher restarts. A move that fails or is canceled leaves the old folder in
// use as it was; the dialog says so. Texts: ./dataFolderText.ts.
import { useEffect, useRef, useState } from "react";
import * as api from "../api/commands";
import type { DataFolderMoved, DataFolderPlan, TaskId } from "../api/commands";
import { errorText, pickPaths, toast, waitTask } from "../ui";
import { doneLines } from "./dataFolderText";

export type ChangeStep =
  | { at: "idle" }
  | { at: "checking"; picked: string }
  /** The plan's dialog; `busy` while Use it runs, `error` when it failed. */
  | { at: "plan"; plan: DataFolderPlan; busy?: boolean; error?: string | null }
  | { at: "moving"; plan: DataFolderPlan; taskId: TaskId; canceling: boolean }
  | { at: "stopped"; how: "failed" | "canceled"; error: string | null }
  /** Done, and the restart is not under way: the move's warnings to read first, or a restart that did not start. */
  | { at: "done"; moved: DataFolderMoved | null; error: string | null }
  | { at: "restarting" };

/** The folder dialog (Tauri), or in a plain browser the mock's queued answer (tests: __kilnDataFolderMock.pick). */
export async function pickFolder(defaultPath?: string): Promise<string | null> {
  if (!api.isTauri()) return (await api.loadMock()).mockPickFolder();
  const r = await pickPaths({ directory: true, title: "Data folder", defaultPath });
  return r?.[0] ?? null;
}

/** Restarts the launcher; resolves only when the restart could not start (with its error). */
export async function restartLauncher(): Promise<string> {
  try {
    await api.dataFolderRestart();
    return "Restart Kiln to use the new folder.";
  } catch (e) {
    return `${errorText(e)} Restart Kiln to use the new folder.`;
  }
}

export function useDataFolderChange() {
  const [step, setStep] = useState<ChangeStep>({ at: "idle" });
  const live = useRef(true);
  // (StrictMode mounts twice: set it again on every mount)
  useEffect(() => { live.current = true; return () => { live.current = false; }; }, []);
  const put = (s: ChangeStep) => { if (live.current) setStep(s); };

  const restart = async (moved: DataFolderMoved | null) => {
    put({ at: "restarting" });
    const error = await restartLauncher();
    put({ at: "done", moved, error });
  };

  /** Looks at a picked folder; the plan's dialog follows (a check that fails is a toast). */
  const check = async (picked: string) => {
    put({ at: "checking", picked });
    try {
      put({ at: "plan", plan: await api.dataFolderCheck(picked) });
    } catch (e) {
      toast.error(e, "Could not check this folder");
      put({ at: "idle" });
    }
  };

  /** Move or Use it, for the plan on screen. */
  const confirm = async () => {
    if (step.at !== "plan" || step.busy) return;
    const plan = step.plan;
    if (plan.action === "use") {
      put({ at: "plan", plan, busy: true });
      try {
        // the running launcher keeps the old folder until the restart
        await api.dataFolderUse(plan.picked);
      } catch (e) {
        put({ at: "plan", plan, error: errorText(e) });
        return;
      }
      await restart(null);
      return;
    }
    if (plan.action !== "move") return;
    let taskId: TaskId;
    try {
      taskId = await api.dataFolderMove(plan.picked);
    } catch (e) {
      put({ at: "stopped", how: "failed", error: errorText(e) });
      return;
    }
    put({ at: "moving", plan, taskId, canceling: false });
    let moved: DataFolderMoved;
    try {
      moved = await waitTask<DataFolderMoved>(taskId);
    } catch (e) {
      const k = api.toKilnError(e);
      put({ at: "stopped", how: k.code === "canceled" ? "canceled" : "failed", error: k.code === "canceled" ? null : k.message });
      return;
    }
    // warnings, or a removal of the old copy that was stopped: said first (then Restart); else the restart at once
    if (doneLines(moved)) put({ at: "done", moved, error: null });
    else await restart(moved ?? null);
  };

  /** Cancel while the move runs: the task stops and the old folder stays in use. */
  const cancel = () => {
    if (step.at !== "moving" || step.canceling) return;
    put({ ...step, canceling: true });
    api.taskCancel(step.taskId).catch((e) => toast.error(e, "Could not cancel the move"));
  };

  /** Closes the dialog (not while a move or the restart runs). */
  const close = () => { if (step.at !== "moving" && step.at !== "restarting") put({ at: "idle" }); };

  return { step, check, confirm, cancel, close, restart: () => void restart(step.at === "done" ? step.moved : null) };
}
export type DataFolderChange = ReturnType<typeof useDataFolderChange>;
