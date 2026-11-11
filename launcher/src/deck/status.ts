// Short lines for the Deck's Home and Kiln screens, read from the same state files as the desktop's Play page.
import type { BootState, DetectKind, ModKind } from "../api/commands";

/** "0.2.0-dev (20261104)". */
export function kilnLabel(v: string): string {
  const [base, build] = v.split("+");
  const date = build?.split(".")[0];
  return date ? `${base} (${date})` : base;
}

export interface BootLine {
  text: string;
  tone: "ok" | "warn" | "err" | "idle";
}

/** The last boot in one line: "Booted, no errors", "Booted, 1 failed", "No start with Kiln yet". It counts no loaded
 *  mods: Kiln's state files list Kiln and native mods only, and every kind counts as a mod. Mod results count only
 *  when the same run wrote them (same pid, written after boot.json). */
export function bootLine(st: BootState | null | undefined): BootLine {
  const boot = st?.boot;
  if (!boot) return { text: "No start with Kiln yet", tone: "idle" };
  if (boot.playOnce?.mode === "vanilla") return { text: "Last start: vanilla", tone: "idle" };
  if (boot.safeIdle) return { text: `Safe idle: ${boot.safeIdle}`, tone: "warn" };
  if (boot.profile?.mode === "invalid") return { text: "Mod list unreadable: no mods loaded", tone: "err" };
  if (boot.profile?.mode === "no-mods") return { text: "Last start: without mods", tone: "idle" };
  const since = Date.parse(boot.written) - 2000;
  const same = (f: { pid: number; written: string } | null | undefined) => !!f && f.pid === boot.pid && Date.parse(f.written) >= since;
  const mods = same(st?.mods) ? st!.mods!.mods : [];
  const natives = same(st?.native) ? st!.native!.mods : [];
  const failed = mods.filter((m) => m.state === "failed").length + natives.filter((m) => m.state === "failed").length
    + boot.chain.filter((c) => c.status === "failed").length + (boot.plugins ?? []).filter((p) => p.status === "failed").length;
  const errors = st?.log ? st.log.errors : (boot.errors ?? []).length;
  const parts = ["Booted"];
  if (failed) parts.push(`${failed} failed`);
  else if (errors) parts.push(`${errors} error${errors === 1 ? "" : "s"}`);
  else parts.push("no errors");
  return { text: parts.join(", "), tone: failed ? "err" : errors ? "warn" : "ok" };
}

export const KIND_WORD: Record<ModKind | DetectKind, string> = {
  kiln: "Kiln mod", native: "Native mod", plugin: "Plugin", emf: "EMF mod", modpack: "Modpack", runtime: "Kiln runtime", "bare-dll": "DLL mod", unknown: "Mods",
  eml: "EML mod", xhl: "XHL companion (requires XHL-Core)", shroudforge: "ShroudForge mod", set: "SET mod", enshwick: "Enshwick mod", greeneye: "Greeneye mod",
  shroudtopia: "Shroudtopia mod", flame: "Flame mod", shroudlink: "shroudlink mod", ember: "Ember mod (doesn't run yet)", proxy: "Loader",
  data: "Game data", save: "Save file", reshade: "ReShade preset", "cheat-table": "Cheat table", asi: "ASI plugin",
};

/** "1.2 MB". */
export function size(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${Math.round(n / 1024)} KB`;
  return `${(n / 1024 / 1024).toFixed(n < 10 * 1024 * 1024 ? 1 : 0)} MB`;
}

/** "3 min ago", "2 days ago". */
export function ago(t: string | null | undefined, now = Date.now()): string {
  if (!t) return "";
  const s = Math.max(0, (now - Date.parse(t)) / 1000);
  if (s < 90) return "just now";
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400 * 1.5) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} days ago`;
}

/** The exact launch option Valve's Proton needs for Kiln's dinput8.dll (the backend's advice is the same text). */
export const LAUNCH_OPTION = 'WINEDLLOVERRIDES="dinput8=n,b" %command%';
