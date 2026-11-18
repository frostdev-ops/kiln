// The launcher's update channels as players see them (selfupdate/feed.rs, Channel). The stored values stay "stable",
// "dev" and "beta" (settings.json `updateChannel`; a 0.2.0-dev.28 launcher reads "stable" and "dev" as before and
// follows its own channel for "beta"). Every public release so far is a dev build (`X.Y.Z-dev.N`), so the dev line is
// the release players have: it is called "Release" until a stable release exists (STABLE_RELEASED). "Stable" is offered
// only where it is already chosen (it has no builds yet).
import { t } from "../i18n";
import type { UpdateChannel } from "./commands";

/** Is there a stable release (an `X.Y.Z` without `-dev.N`)? Flip with the first one: then "stable" is the Release and
 *  the dev line becomes "Dev builds". */
export const STABLE_RELEASED = false;

/** The channel's name in the UI. */
export function channelName(ch: UpdateChannel): string {
  switch (ch) {
    case "beta": return t("api.channel.name.beta");
    case "stable": return STABLE_RELEASED ? t("api.channel.name.release") : t("api.channel.name.stableNone");
    default: return STABLE_RELEASED ? t("api.channel.name.devBuilds") : t("api.channel.name.release");
  }
}

/** The short word for marks and rows: "Release", "Beta". */
export function channelWord(ch: UpdateChannel): string {
  return ch === "beta" ? t("api.channel.word.beta") : ch === "stable" ? (STABLE_RELEASED ? t("api.channel.word.release") : t("api.channel.word.stable")) : STABLE_RELEASED ? t("api.channel.word.dev") : t("api.channel.word.release");
}

/** The release line a tester goes back to: the stable channel once there is a stable release, else dev. */
export const RELEASE_CHANNEL: UpdateChannel = STABLE_RELEASED ? "stable" : "dev";

/** The choices of Settings > Updates > Channel, the release line first. `current`: the channel in use (a "stable" chosen
 *  before there is a stable release stays listed, so the select shows it). */
export function channelOptions(current: UpdateChannel): { value: UpdateChannel; label: string }[] {
  const order: UpdateChannel[] = STABLE_RELEASED ? ["stable", "dev", "beta"] : ["dev", "beta", ...(current === "stable" ? ["stable" as const] : [])];
  return order.map((value) => ({ value, label: channelName(value) }));
}

/** A build id as the updater writes it (selfupdate/feed.rs, Build): `0.2.0-dev.29+beta.3` is test build 3 of
 *  0.2.0-dev.29, the beta number as build metadata; any other build metadata is not a beta. */
export function parseBuild(id: string): { version: string; beta: number | null } {
  const m = /^([^+]+)\+beta\.([1-9][0-9]{0,8})$/.exec(id.trim());
  return m ? { version: m[1], beta: Number(m[2]) } : { version: id.trim(), beta: null };
}

/** A build for people: "0.2.0-dev.29 beta 3", "0.2.0-dev.28". */
export function buildLabel(id: string, beta: number | null = null): string {
  const b = parseBuild(id);
  const n = b.beta ?? beta;
  return n ? t("api.channel.buildBeta", { version: b.version, n }) : b.version;
}

/** What choosing `target` means on this build: "to-beta" (a release build installs the newest test build now), "back"
 *  (a test build installs the newest release now, also when it is older), or null (only the setting changes: the next
 *  update follows it). */
export type SwitchKind = "to-beta" | "back";
export function switchKind(s: { buildChannel?: UpdateChannel | null }, target: UpdateChannel): SwitchKind | null {
  const build = s.buildChannel ?? "dev";
  if (target === "beta" && build !== "beta") return "to-beta";
  if (target !== "beta" && build === "beta") return "back";
  return null;
}

/** Said before every switch: worlds and multiplayer. */
export const worldWarning = (): string => t("api.channel.worldWarning");

/** The switch dialog's words (desktop and Deck). */
export function switchWords(kind: SwitchKind, targetVersion?: string): { title: string; say: string[]; confirm: string; task: string } {
  return kind === "to-beta"
    ? {
      title: t("api.channel.toBeta.title"),
      say: [
        t("api.channel.toBeta.what"),
        t("api.channel.toBeta.back"),
        worldWarning(),
      ],
      confirm: t("api.channel.toBeta.confirm"),
      task: t("api.channel.toBeta.task"),
    }
    : {
      title: t("api.channel.back.title"),
      say: [
        t("api.channel.back.what", { advice: releaseRuntimeAdvice(targetVersion) }),
        worldWarning(),
      ],
      confirm: t("api.channel.back.confirm"),
      task: t("api.channel.back.task"),
    };
}

/** The restore choice of a switch back (a "before beta" copy exists). */
export function restoreWords(taken: string): { label: string; hint: string } {
  const d = new Date(taken);
  const when = Number.isNaN(d.getTime()) ? taken : d.toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
  return {
    label: t("api.channel.restore.label"),
    hint: t("api.channel.restore.hint", { when }),
  };
}

/** The Play page's notice when the game's Kiln and the launcher's channel differ, or null.
 *  `switchBack`: the game's Kiln came with a test build and this launcher's own is older (InstallState.switchBack).
 *  `betaLauncher`: this launcher is a test build and has a newer Kiln than the game's (upgradeAvailable). */
export function channelNotice(rt: { installed: boolean; upgradeAvailable: string | null; switchBack?: { from: string; to: string } | null } | null | undefined,
                              betaLauncher: boolean, label: (v: string) => string = (v) => v): string | null {
  if (!rt?.installed) return null;
  if (rt.switchBack) return t("api.channel.notice.switchBack", { from: label(rt.switchBack.from), to: label(rt.switchBack.to) });
  if (betaLauncher && rt.upgradeAvailable) return t("api.channel.notice.betaLauncher", { version: label(rt.upgradeAvailable) });
  return null;
}

export function acceptedRestore(s: { phase: string; restoreRequested?: boolean }, local = false): boolean {
  return s.phase === "ready" || s.phase === "downloading" ? s.restoreRequested === true : local;
}

export function releaseRuntimeAdvice(target?: string): string {
  if (!target) return t("api.channel.advice.unknown");
  const v = /^(\d+)\.(\d+)\.(\d+)(?:-dev\.(\d+)(?:\.\d+)?)?$/.exec(parseBuild(target).version);
  const automatic = v && (Number(v[1]) > 0 || Number(v[2]) > 2 || (Number(v[2]) === 2 && (Number(v[3]) > 0 || (Number(v[3]) === 0 && (v[4] === undefined || Number(v[4]) >= 29)))));
  return automatic ? t("api.channel.advice.automatic") : t("api.channel.advice.manual");
}
