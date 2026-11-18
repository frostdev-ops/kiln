// Short lines for the Deck's Home and Kiln screens, read from the same state files as the desktop's Play page.
import type { BootState, DetectKind, ModKind } from "../api/commands";
import { t } from "../i18n";

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
  if (!boot) return { text: t("deck.status.noStart"), tone: "idle" };
  if (boot.playOnce?.mode === "vanilla") return { text: t("deck.status.lastVanilla"), tone: "idle" };
  if (boot.safeIdle) return { text: t("deck.status.safeIdle", { reason: boot.safeIdle }), tone: "warn" };
  if (boot.profile?.mode === "invalid") return { text: t("deck.status.listUnreadable"), tone: "err" };
  if (boot.profile?.mode === "no-mods") return { text: t("deck.status.lastNoMods"), tone: "idle" };
  const since = Date.parse(boot.written) - 2000;
  const same = (f: { pid: number; written: string } | null | undefined) => !!f && f.pid === boot.pid && Date.parse(f.written) >= since;
  const mods = same(st?.mods) ? st!.mods!.mods : [];
  const natives = same(st?.native) ? st!.native!.mods : [];
  const failed = mods.filter((m) => m.state === "failed").length + natives.filter((m) => m.state === "failed").length
    + boot.chain.filter((c) => c.status === "failed").length + (boot.plugins ?? []).filter((p) => p.status === "failed").length;
  const errors = st?.log ? st.log.errors : (boot.errors ?? []).length;
  const text = failed ? t("deck.status.bootedFailed", { count: failed })
    : errors ? t("deck.status.bootedErrors", { count: errors })
    : t("deck.status.bootedClean");
  return { text, tone: failed ? "err" : errors ? "warn" : "ok" };
}

/** Each kind's word, read in the current language at each use. */
export const KIND_WORD: Readonly<Record<ModKind | DetectKind, string>> = {
  get kiln() { return t("deck.kinds.kiln"); },
  get native() { return t("deck.kinds.native"); },
  get plugin() { return t("deck.kinds.plugin"); },
  get emf() { return t("deck.kinds.emf"); },
  get modpack() { return t("deck.kinds.modpack"); },
  get runtime() { return t("deck.kinds.runtime"); },
  get "bare-dll"() { return t("deck.kinds.bareDll"); },
  get unknown() { return t("deck.kinds.unknown"); },
  get eml() { return t("deck.kinds.eml"); },
  get xhl() { return t("deck.kinds.xhl"); },
  get shroudforge() { return t("deck.kinds.shroudforge"); },
  get set() { return t("deck.kinds.set"); },
  get enshwick() { return t("deck.kinds.enshwick"); },
  get greeneye() { return t("deck.kinds.greeneye"); },
  get shroudtopia() { return t("deck.kinds.shroudtopia"); },
  get flame() { return t("deck.kinds.flame"); },
  get shroudlink() { return t("deck.kinds.shroudlink"); },
  get ember() { return t("deck.kinds.ember"); },
  get proxy() { return t("deck.kinds.proxy"); },
  get data() { return t("deck.kinds.data"); },
  get save() { return t("deck.kinds.save"); },
  get reshade() { return t("deck.kinds.reshade"); },
  get "cheat-table"() { return t("deck.kinds.cheatTable"); },
  get asi() { return t("deck.kinds.asi"); },
};

/** "1.2 MB". */
export function size(n: number): string {
  if (n < 1024) return t("deck.status.bytes", { n });
  if (n < 1024 * 1024) return t("deck.status.kb", { n: Math.round(n / 1024) });
  return t("deck.status.mb", { n: (n / 1024 / 1024).toFixed(n < 10 * 1024 * 1024 ? 1 : 0) });
}

/** "3 min ago", "2 days ago". */
export function ago(at: string | null | undefined, now = Date.now()): string {
  if (!at) return "";
  const s = Math.max(0, (now - Date.parse(at)) / 1000);
  if (s < 90) return t("deck.status.justNow");
  if (s < 3600) return t("deck.status.minAgo", { n: Math.round(s / 60) });
  if (s < 86400 * 1.5) return t("deck.status.hoursAgo", { n: Math.round(s / 3600) });
  return t("deck.status.daysAgo", { n: Math.round(s / 86400) });
}

/** The exact launch option Valve's Proton needs for Kiln's dinput8.dll (the backend's advice is the same text). */
export const LAUNCH_OPTION = 'WINEDLLOVERRIDES="dinput8=n,b" %command%';
