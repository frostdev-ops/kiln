// The page tells the backend once that its first screen is up: `app_started`, after React's first commit and two
// frames (the second frame callback runs once the first frame was painted). On Linux the backend's render watchdog
// (src-tauri/src/render.rs) starts the launcher again without the GPU when this call never comes. Desktop and Deck UI
// alike: main.tsx mounts <Started /> next to the app. Cheap: one IPC call per start; a failure is ignored.
import { useEffect } from "react";
import * as api from "../api/commands";

/** A function that sends the ready call the first time it runs, after two frames, and never again. */
export function startedReporter(send: () => Promise<unknown>, frame: (cb: () => void) => void): () => void {
  let told = false;
  return () => {
    if (told) return;
    told = true;
    frame(() => frame(() => { send().catch(() => {}); }));
  };
}

export const tellStarted = startedReporter(() => api.appStarted(), (cb) => { requestAnimationFrame(() => cb()); });

/** Mounted once at the root: its effect runs after React's first commit. */
export function Started() {
  useEffect(() => tellStarted(), []);
  return null;
}
