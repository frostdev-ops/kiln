// Small formatters.
import { toKilnError } from "../api/commands";

export function fmtBytes(n: number | null | undefined): string {
  if (n === null || n === undefined) return "-";
  if (n < 1024) return `${n} B`;
  const u = ["KB", "MB", "GB", "TB"];
  let v = n / 1024, i = 0;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v < 10 ? v.toFixed(1) : Math.round(v)} ${u[i]}`;
}

/** "3 min ago", "2 h ago", "yesterday", or a date. Accepts RFC 3339 or ms. */
export function fmtAgo(t: string | number | null | undefined, now = Date.now()): string {
  if (t === null || t === undefined || t === "") return "-";
  const ms = typeof t === "number" ? t : Date.parse(t);
  if (Number.isNaN(ms)) return String(t);
  const s = Math.round((now - ms) / 1000);
  if (s < 0) return fmtTime(ms);
  if (s < 45) return "just now";
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  if (s < 172800) return "yesterday";
  return new Date(ms).toLocaleDateString();
}

export function fmtTime(t: string | number | null | undefined): string {
  if (t === null || t === undefined || t === "") return "-";
  const d = new Date(typeof t === "number" ? t : Date.parse(t));
  return Number.isNaN(d.getTime()) ? String(t) : d.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}

/** `kdh1:3f2a9c1d…` → `3f2a9c1d`; a sha256 → its first 8. */
export function shortHash(h: string | null | undefined): string {
  if (!h) return "-";
  const hex = h.includes(":") ? h.split(":")[1] : h;
  return hex.slice(0, 8);
}

/** An install's note for its toast: "1 changed file kept in backups" when it backed up files changed by hand, else
 * undefined. */
export function installNote(r: { changedBackedUp?: string[] } | null | undefined): string | undefined {
  const n = r?.changedBackedUp?.length ?? 0;
  return n ? `${n} changed file${n === 1 ? "" : "s"} kept in backups` : undefined;
}

export function errorText(e: unknown): string {
  return toKilnError(e).message;
}
