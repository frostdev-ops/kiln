import type { GameStatus, StoppedStart } from "../api/commands";
import { t } from "../i18n";
export const stopKey = (s: StoppedStart) => `${s.pid}:${s.utc}:${s.exitCode}`;
export const stopSentence = (s: StoppedStart) => s.kind === "roster"
  ? (s.names.length ? t("shell.stoppedStart.rosterNames", { names: s.names.join(", ") }) : t("shell.stoppedStart.roster"))
  : s.kind === "content"
  ? (s.names.length ? t("shell.stoppedStart.contentNames", { names: s.names.join(", ") }) : t("shell.stoppedStart.content"))
  : s.names.length ? t("shell.stoppedStart.reasonNames", { reason: s.reason, names: s.names.join(", ") }) : s.reason;
// The roster and the content stop say what failed in a sentence of their own; the reason is the detail under it.
export const stopShowsReason = (s: StoppedStart) => s.kind !== "boot-mod";
export const stopFilter = (ids: string[]) => `ids:${ids.map(encodeURIComponent).join(",")}`;
export function stopIds(text: string): string[] | null {
  if (!text.startsWith("ids:")) return null;
  try { return text.slice(4).split(",").filter(Boolean).map(decodeURIComponent); } catch { return []; }
}

/** Keep a refusal until a proven later process. Host and runtime pids differ under Proton. */
export function stopForProcess(stop: StoppedStart | null, status: Pick<GameStatus, "running" | "started"> | null | undefined): StoppedStart | null {
  if (!stop || !status?.running || !status.started) return stop;
  return Date.parse(status.started) > Date.parse(stop.utc) ? null : stop;
}
