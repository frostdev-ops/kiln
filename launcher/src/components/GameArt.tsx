// Game art: the save editor's art, the items of Kiln mods, and the admin map's relief, all made from
// the user's own game (app_assets_import(kind, null), kiln_manage::gameart) without any button. <GameArtAuto/> (the
// shell) checks the status every 15 s and when the window gets focus, and runs the work in the background when:
// the art is missing, the installed game's build changed, or the mods changed (active profile, library, kiln/mods,
// the game's kiln/state/content-definitions.json). While it runs: the header's task tray. Errors: a toast (once per
// state) and <data>/logs/launcher.log. Docs: docs/kiln/launcher/gameart.md.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef } from "react";
import * as api from "../api/commands";
import type { AssetsStatus, KilnError, Task } from "../api/commands";
import { usePoll } from "../api/hooks";
import { runTask, toast, useTasks } from "../ui";

export const ASSETS_KEY = ["app_assets_status"] as const;
export type ArtKind = "editor" | "admin";
/** The backend's task titles (commands/app.rs extract_editor_task / extract_map_task). */
const TITLE: Record<ArtKind, string> = { editor: "Prepare game art", admin: "Extract map art" };
const RETRY_MS = 10 * 60 * 1000;

export function useAssetsStatus() {
  return useQuery({ queryKey: ASSETS_KEY, queryFn: api.appAssetsStatus, staleTime: 10_000, refetchInterval: usePoll(15_000), refetchOnWindowFocus: true });
}

const running = (t?: Task) => !!t && (t.state === "running" || t.state === "queued");

/** The running task of `kind` (from the task feed; the status names it too). */
export function useExtractTask(st?: AssetsStatus, kind: ArtKind = "editor"): Task | undefined {
  return useTasks((s) => {
    const tid = kind === "editor" ? st?.editor.task : st?.admin.task;
    const byId = tid ? s.tasks[tid] : undefined;
    if (running(byId)) return byId;
    for (const id of s.order) {
      const t = s.tasks[id];
      if (running(t) && t.kind === "app_assets_import" && t.title === TITLE[kind]) return t;
    }
    return undefined;
  });
}

/** What needs doing for `kind`, as a state signature (null: nothing). */
export function artWork(st: AssetsStatus, kind: ArtKind): string | null {
  if (kind === "editor") {
    const e = st.editor;
    if (e.task) return null;
    const art = !!e.canExtract && (!(e.source || e.present) || e.current === false);
    const mods = !!e.modded && !e.modded.current;
    return art || mods ? `editor|${e.gameId ?? ""}@${e.gameBuild ?? ""}|${e.modded?.version ?? ""}|${(e.modded?.mods ?? []).join(",")}` : null;
  }
  const a = st.admin;
  if (a.task || !a.canExtract) return null;
  return !a.map || a.current === false ? `admin|${a.gameBuild ?? ""}` : null;
}

/** Mount once (the shell does): runs the game-art work on its own; the task shows in the header's task tray. */
export function GameArtAuto() {
  const qc = useQueryClient();
  const { data: st } = useAssetsStatus();
  const editorTask = useExtractTask(st, "editor");
  const adminTask = useExtractTask(st, "admin");
  const busy = useRef(false);
  // signature -> when it failed (no retry of the same state for 10 min; a new game build or mod set retries at once)
  const failed = useRef(new Map<string, number>());
  useEffect(() => {
    if (!st || !api.isTauri() || busy.current || editorTask || adminTask) return;
    const kind: ArtKind | null = artWork(st, "editor") ? "editor" : artWork(st, "admin") ? "admin" : null;
    if (!kind) return;
    const sig = artWork(st, kind)!;
    const at = failed.current.get(sig);
    if (at && Date.now() - at < RETRY_MS) return;
    busy.current = true;
    runTask<AssetsStatus>(() => api.appAssetsImport(kind, null), { quiet: true })
      .catch((e: KilnError) => {
        if (e?.code === "steam_updating" || e?.code === "canceled") return; // try again at the next check
        failed.current.set(sig, Date.now());
        toast.error(e, kind === "editor" ? "Game art could not be prepared" : "Map art could not be prepared");
      })
      .finally(() => {
        busy.current = false;
        qc.invalidateQueries({ queryKey: ASSETS_KEY });
      });
  }, [st, editorTask, adminTask, qc]);
  return null;
}
