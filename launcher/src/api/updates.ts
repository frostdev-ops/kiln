// Update checks for installed mods: types and the wrapper of the one command, updates_check
// (launcher/src-tauri/src/commands/updates.rs, kiln_manage::updates; docs/kiln/launcher/catalog.md "Update notices").
// The result is a notice and a link per mod: nothing here downloads, installs or switches a mod. The browser mock
// answers from ./mockUpdates.ts. The same value arrives as the event `kiln://mod-updates` after every check.
import { invoke as tauriInvoke } from "@tauri-apps/api/core";
import { t } from "../i18n";
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

/** Why a status is unknown (or could not be refreshed), in plain words. */
export function reasonWords(r: UnknownReason | null): string {
  if (!r) return "";
  switch (r) {
    case "incomplete_list": return t("api.updates.reason.incompleteList");
    case "invalid_metadata": return t("api.updates.reason.invalidMetadata");
    case "missing_asset": return t("api.updates.reason.missingAsset");
    case "ambiguous_asset": return t("api.updates.reason.ambiguousAsset");
    case "ambiguous_release": return t("api.updates.reason.ambiguousRelease");
    case "non_semver": return t("api.updates.reason.nonSemver");
    case "same_version_unbound": return t("api.updates.reason.sameVersionUnbound");
    case "network": return t("api.updates.reason.network");
    case "source_conflict": return t("api.updates.reason.sourceConflict");
    case "rate_limited": return t("api.updates.reason.rateLimited");
    case "not_found": return t("api.updates.reason.notFound");
    case "not_checked": return t("api.updates.reason.notChecked");
    case "release_removed": return t("api.updates.reason.releaseRemoved");
    default: return t("api.updates.reason.unknown");
  }
}

export interface UpdateWords { word: string; tone: "warn" | "ok" | "idle" | "info"; detail: string }

/** What a status says, in a few words and one sentence. A stale or unknown status never reads as "up to date". */
export function updateWords(s: ModUpdateStatus): UpdateWords {
  // whole sentences, joined by a space as before
  const stale = s.stale ? (s.reason ? t("api.updates.staleReason", { reason: reasonWords(s.reason) }) : t("api.updates.stale")) : "";
  const join = (...parts: string[]) => parts.filter(Boolean).join(" ");
  switch (s.state) {
    case "available": {
      const v = s.candidateVersion ?? "";
      const word = v.trim() ? (s.stale ? t("api.updates.word.updateToStale", { version: v.trimEnd() }) : t("api.updates.word.updateTo", { version: v.trimEnd() }))
        : s.stale ? t("api.updates.word.updateStale") : t("api.updates.word.update");
      return { word, tone: "warn",
        detail: join(t("api.updates.detail.available", { candidate: s.candidateVersion ?? "?", installed: s.installedVersion }),
          s.compatibility === "incompatible" ? t("api.updates.detail.incompatible") : "", stale) };
    }
    case "new-upload": return { word: s.stale ? t("api.updates.word.newUploadStale") : t("api.updates.word.newUpload"), tone: "warn",
      detail: join(t("api.updates.detail.newUpload", { installed: s.installedVersion }), stale) };
    case "current": return { word: t("api.updates.word.current"), tone: "ok", detail: t("api.updates.detail.current", { installed: s.installedVersion }) };
    case "unsupported": return { word: t("api.updates.word.unsupported"), tone: "idle", detail: t("api.updates.detail.unsupported") };
    case "disabled": return { word: t("api.updates.word.disabled"), tone: "idle", detail: t("api.updates.detail.disabled") };
    default: return { word: s.stale ? t("api.updates.word.unknownStale") : t("api.updates.word.unknown"), tone: "idle",
      detail: t("api.updates.detail.unknown", { reason: reasonWords(s.reason) || t("api.updates.reason.unknown") }) };
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
