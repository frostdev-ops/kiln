// The page tells the backend once that its first screen is up: `app_started`, after React's first commit and two
// frames (the second frame callback runs once the first frame was painted). On Linux the backend's render watchdog
// (src-tauri/src/render.rs) starts the launcher again without the GPU when this call never comes. Desktop and Deck UI
// alike: main.tsx mounts <Started /> next to the app. Cheap: one IPC call per start; a failure is ignored.
//
// A hidden page (the window minimized or on another workspace at the start) gets no frames until it is shown, so it
// says it started at once, or when it is hidden before its frames came: there is no black window anyone looks at, and
// waiting would make the watchdog switch a working GPU off.
import { useEffect } from "react";
import * as api from "../api/commands";

/** A function that sends the ready call the first time it runs, after two frames (at once when `hidden()`, or when
 *  `onHidden` reports the page hidden before them), and never again. */
export function startedReporter(
  send: () => Promise<unknown>,
  frame: (cb: () => void) => void,
  hidden: () => boolean = () => false,
  onHidden: (cb: () => void) => void = () => {},
): () => void {
  let told = false;
  let sent = false;
  const go = () => {
    if (sent) return;
    sent = true;
    send().catch(() => {});
  };
  return () => {
    if (told) return;
    told = true;
    if (hidden()) {
      go();
      return;
    }
    onHidden(go);
    frame(() => frame(go));
  };
}

const pageHidden = () => typeof document !== "undefined" && document.visibilityState === "hidden";

export const tellStarted = startedReporter(
  () => api.appStarted(),
  (cb) => { requestAnimationFrame(() => cb()); },
  pageHidden,
  (cb) => {
    const on = () => {
      if (!pageHidden()) return;
      document.removeEventListener("visibilitychange", on);
      cb();
    };
    document.addEventListener("visibilitychange", on);
  },
);

/** Mounted once at the root: its effect runs after React's first commit. */
export function Started() {
  useEffect(() => tellStarted(), []);
  return null;
}
