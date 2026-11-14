// Update checks for installed mods: types and the wrapper of the one command, updates_check
// (launcher/src-tauri/src/commands/updates.rs, kiln_manage::updates; docs/kiln/launcher/catalog.md "Update notices").
// The result is a notice and a link per mod: nothing here downloads, installs or switches a mod. The browser mock
// answers from ./mockUpdates.ts. The same value arrives as the event `kiln://mod-updates` after every check.
import { invoke as tauriInvoke } from "@tauri-apps/api/core";
import { isTauri, loadMock, toKilnError, type LibraryRef, type ModKind } from "./commands";

export const MOD_UPDATES_EVENT = "kiln://mod-updates";

export type UpdateProviderName = "github" | "nexus";
export type UpdateState = "available" | "new-upload" | "current" | "unknown" | "unsupported" | "disabled";
export type UnknownReason = "incomplete_list" | "invalid_metadata" | "missing_asset" | "ambiguous_asset" | "ambiguous_release" | "non_semver"
  | "same_version_unbound" | "network" | "source_conflict" | "rate_limited" | "not_found" | "not_checked" | "release_removed";

export interface UpdateSourceInfo {
  provider: UpdateProviderName;
  /** `owner/repo`, or `enshrouded/<mod id>/<file id>` on Nexus Mods. */
  destination: string;
  origin: "manual" | "receipt" | "manifest";
  /** The check uses the player's account at the provider (Nexus Mods); false: an anonymous read. */
  authenticated: boolean;
  /** Complete settings for the source dialog; optional in older results. */
  spec?: UpdatesSpec;
}

export interface CandidateIdentity {
  releaseId: string | null;
  tag: string | null;
  assetId: string | null;
  assetName: string | null;
  artifactSha256: string | null;
  fileVersionId: string | null;
}

/** kiln_manage::updates::ModUpdateStatus: every key is always there. */
export interface ModUpdateStatus {
  libraryRef: LibraryRef;
  name: string;
  /** null: no known source (state `unsupported`). */
  source: UpdateSourceInfo | null;
  installedVersion: string;
  candidateVersion: string | null;
  candidateIdentity: CandidateIdentity | null;
  state: UpdateState;
  compatibility: "compatible" | "incompatible" | "unknown";
  coverage: "complete" | "incomplete";
  /** When the provider last answered (RFC 3339 UTC). */
  checkedAt: string | null;
  /** Not confirmed by the provider within a day, or a later refresh failed. A stale status is never `current`. */
  stale: boolean;
  reason: UnknownReason | null;
  /** The page to open, built by Kiln (github.com or nexusmods.com). */
  pageUrl: string | null;
}

export interface CheckReport {
  format: number;
  checkedAt: string;
  /** The check made no request. */
  offline: boolean;
  mods: ModUpdateStatus[];
}

/** The manifest's `updates` shape (docs/kiln/modding/manifest.md). */
export type UpdatesSpec =
  | { provider: "github"; repository: string; tagPattern?: string; asset?: string; channel?: "stable" | "prerelease" }
  | { provider: "nexus"; gameId: 5811; modId: number; fileChainId: string };

/** One change of a mod's update source, applied before the check. `map` to another place than the mod is checked
 *  against is refused with `conflict` unless `confirm`. */
export type SourceChange =
  | { action: "map"; kind: ModKind; id: string; spec: UpdatesSpec; confirm?: boolean }
  | { action: "confirm"; kind: ModKind; id: string }
  | { action: "unmap"; kind: ModKind; id: string };

export interface UpdatesRequest {
  /** No request: what is remembered. */
  offline?: boolean;
  /** Ask again although a recent answer is remembered (a provider's request limit still holds). */
  refresh?: boolean;
  /** The daily background check. */
  automatic?: boolean;
  /** The providers asked; the others answer from what is remembered, with no request. Absent: all. The Catalog page
   *  asks Nexus Mods only, so opening it sends nothing to GitHub. */
  providers?: UpdateProviderName[];
  source?: SourceChange;
}

export async function updatesCheck(request: UpdatesRequest = {}): Promise<CheckReport> {
  try {
    return isTauri() ? await tauriInvoke<CheckReport>("updates_check", { request }) : await (await loadMock()).mockInvoke<CheckReport>("updates_check", { request });
  } catch (e) {
    throw toKilnError(e);
  }
}

export const uk = { report: ["updates_check"] as const };

const REASON: Record<UnknownReason, string> = {
  incomplete_list: "the release list is too long to read to its end",
  invalid_metadata: "the newest release's kiln-release.json is not valid for this mod",
  missing_asset: "the newest release has no file with the expected name",
  ambiguous_asset: "the newest release has several files with the expected name",
  ambiguous_release: "several releases share the highest version",
  non_semver: "the versions are not semantic versions, so they cannot be ordered",
  same_version_unbound: "the same version is published, and Kiln does not know which upload of it you have",
  network: "no answer (offline, or not signed in where a sign-in is needed)",
  source_conflict: "its update source changed and waits for your yes",
  rate_limited: "the provider's request limit is used up for now",
  not_found: "no repository or no matching release was found",
  not_checked: "not checked yet",
  release_removed: "the release this copy came from is gone",
};

/** Why a status is unknown (or could not be refreshed), in plain words. */
export const reasonWords = (r: UnknownReason | null): string => (r ? REASON[r] ?? "unknown reason" : "");

export interface UpdateWords { word: string; tone: "warn" | "ok" | "idle" | "info"; detail: string }

/** What a status says, in a few words and one sentence. A stale or unknown status never reads as "up to date". */
export function updateWords(s: ModUpdateStatus): UpdateWords {
  const stale = s.stale ? ` Not confirmed lately${s.reason ? `: ${reasonWords(s.reason)}` : ""}.` : "";
  switch (s.state) {
    case "available": return { word: `Update ${s.candidateVersion ?? ""}`.trim() + (s.stale ? " (stale)" : ""), tone: "warn",
      detail: `Version ${s.candidateVersion ?? "?"} is published; you have ${s.installedVersion}.${s.compatibility === "incompatible" ? " Its release says it does not load on this Kiln." : ""}${stale}` };
    case "new-upload": return { word: "New upload" + (s.stale ? " (stale)" : ""), tone: "warn", detail: `Version ${s.installedVersion} was published again as another file.${stale}` };
    case "current": return { word: "Up to date", tone: "ok", detail: `${s.installedVersion} is the newest published version.` };
    case "unsupported": return { word: "No update source", tone: "idle", detail: "Kiln does not know where this mod is published. Set a source to get update notices." };
    case "disabled": return { word: "Checks off", tone: "idle", detail: "Its provider is switched off in this build." };
    default: return { word: s.stale ? "Unknown (stale)" : "Unknown", tone: "idle", detail: `Kiln cannot tell whether an update exists: ${reasonWords(s.reason) || "unknown reason"}.` };
  }
}

/** A page the launcher may open for a status: only one Kiln builds itself, on the two provider sites. */
export function pageOf(s: ModUpdateStatus): string | null {
  const u = s.pageUrl;
  return u && (u.startsWith("https://github.com/") || u.startsWith("https://www.nexusmods.com/")) && !/[\s<>"'\\]/.test(u) ? u : null;
}

/** The statuses that are a notice: a newer version or a new upload. */
export const hasUpdate = (s: ModUpdateStatus) => s.state === "available" || s.state === "new-upload";

const DAY = 24 * 60 * 60_000;
/** The daily check's timing: the first one 2 to 15 minutes after the start, then every 24 to 25 hours; while the
 *  launcher is busy it looks again in 5 minutes. `rand`: 0..1. */
export function dailyDelay(kind: "first" | "next" | "busy", rand: number): number {
  const r = Math.min(1, Math.max(0, rand));
  return kind === "first" ? 2 * 60_000 + r * 13 * 60_000 : kind === "next" ? DAY + r * 60 * 60_000 : 5 * 60_000;
}
