// The online catalog on Nexus Mods and Mod Manager Download (nxm://) links: types and wrappers for the nexus_*
// commands (launcher/src-tauri/src/commands/nexus.rs, kiln_manage::nexus; docs/kiln/launcher/catalog.md). The browser
// mock answers from ./mockNexus.ts. The API key never comes back from the backend: the account says only whether one
// is set, where, the account name and premium.
import { invoke as tauriInvoke } from "@tauri-apps/api/core";
import { isTauri, toKilnError, type AddReport, type GameId, type KilnError, type TaskId } from "./commands";
import { mockInvoke } from "./mock";

async function call<T>(cmd: string, args: Record<string, unknown> = {}): Promise<T> {
  try {
    return isTauri() ? await tauriInvoke<T>(cmd, args) : await mockInvoke<T>(cmd, args);
  } catch (e) {
    throw toKilnError(e);
  }
}

/** Emitted when an Enshrouded nxm link arrived: `{count}` wait in nexus_links. */
export const NXM_EVENT = "kiln://nxm";

export type ModType = "kiln" | "native" | "emf" | "dll" | "modpack" | "other";
export type CatalogFilter = "kiln" | "all" | "installed";
export type CatalogSort = "endorsements" | "updated" | "new" | "downloads" | "name";

export interface CatalogQuery {
  text?: string;
  filter?: CatalogFilter;
  sort?: CatalogSort;
  page?: number;
  pageSize?: number;
  adult?: boolean;
  refresh?: boolean;
}

export interface InstalledInfo {
  version: string;
  versionGsid: string;
  fileName: string;
  inLibrary: boolean;
  latestVersion: string | null;
  latestGsid: string | null;
  updateAvailable: boolean;
}

export interface CatalogMod {
  /** The game-scoped id (the number in the page URL). */
  id: string;
  uid: string;
  name: string;
  summary: string;
  version: string | null;
  author: string;
  category: string | null;
  thumbnail: string | null;
  endorsements: number;
  downloads: number;
  created: string;
  updated: string;
  adult: boolean;
  /** null: not looked at (older than Kiln, or no API key). */
  kind: ModType | null;
  url: string;
  installed: InstalledInfo | null;
}

export interface CatalogPage {
  mods: CatalogMod[];
  total: number;
  page: number;
  pageSize: number;
  /** Mods that may be Kiln mods and wait to be looked at: ask again. */
  unchecked: number;
  needsKey: boolean;
  fetched: string;
}

export interface CatalogFile {
  fileId: string;
  fileName: string;
  versionId: string;
  versionGsid: string;
  version: string;
  category: string;
  uploaded: string;
  primary: boolean;
  kind: ModType;
  archive: string;
}

export interface CatalogRequirement {
  modId: string;
  name: string;
  fileName: string;
  minVersion: string | null;
  installed: boolean;
  /** Kiln itself (the launcher is that). */
  kiln: boolean;
  otherGame: boolean;
  url: string;
}

export interface CatalogDetails extends CatalogMod {
  description: string;
  credit: string | null;
  tags: string[];
  uniqueDownloads: number;
  images: { url: string; thumb: string | null; title: string | null }[];
  files: CatalogFile[];
  bestFile: CatalogFile | null;
  requirements: CatalogRequirement[];
  /** The files tab with the best file picked and its Mod Manager Download shown. */
  filesUrl: string;
  needsKey: boolean;
}

export interface InstalledCatalogMod {
  modId: string;
  name: string;
  fileId: string;
  fileName: string;
  versionId: string;
  versionGsid: string;
  version: string;
  refs: { kind: string; id: string; version: string; hash: string }[];
  installed: string;
  source: "nxm" | "catalog";
  info: InstalledInfo;
}

export interface NexusAccount {
  keySet: boolean;
  keyPlace: "keyring" | "file" | null;
  name: string | null;
  premium: boolean;
  error: KilnError | null;
  rate: { hourlyRemaining: number | null; dailyRemaining: number | null; blockedFor: number; calls: number };
  keyPage: string;
}

export interface HandlerStatus {
  supported: boolean;
  registered: boolean;
  active: boolean;
  previous: string | null;
}

export interface PendingLink {
  id: string;
  modId: string;
  fileId: string;
  expired: boolean;
  received: string;
}

export interface CheckReport { files: number; matched: number; hashed: number; noManifest: boolean; sha256: string; size: number }

/** The result of nexus_download / nexus_link_install. Undo: profileRevert(add.profileId, add.changes). */
export interface InstallReport {
  add: AddReport;
  modId: string;
  name: string;
  version: string;
  fileName: string;
  check: CheckReport;
  requirements: CatalogRequirement[];
  download: string;
}

export const nexusAccount = () => call<NexusAccount>("nexus_account");
/** Checks the key with Nexus Mods first; a refused key is not stored (auth_failed). */
export const nexusKeySet = (key: string) => call<NexusAccount>("nexus_key_set", { key });
export const nexusKeyClear = () => call<NexusAccount>("nexus_key_clear");
export const nexusSearch = (query: CatalogQuery) => call<CatalogPage>("nexus_search", { query });
export const nexusMod = (modId: string) => call<CatalogDetails>("nexus_mod", { modId });
export const nexusInstalled = (check = false) => call<InstalledCatalogMod[]>("nexus_installed", { check });
/** Premium only (task, result InstallReport); free accounts get `refused` with `detail.url` (the file's page). */
export const nexusDownload = (gameId: GameId, modId: string, fileId: string) => call<TaskId>("nexus_download", { gameId, modId, fileId });
export const nexusLinks = () => call<PendingLink[]>("nexus_links");
export const nexusLinkInstall = (gameId: GameId, linkId: string) => call<TaskId>("nexus_link_install", { gameId, linkId });
export const nexusLinkDismiss = (linkId: string) => call<null>("nexus_link_dismiss", { linkId });
export const nexusHandlerStatus = () => call<HandlerStatus>("nexus_handler_status");
export const nexusHandlerSet = (on: boolean) => call<HandlerStatus>("nexus_handler_set", { on });

/** Query keys. */
export const nk = {
  account: ["nexus_account"] as const,
  search: (q: CatalogQuery) => ["nexus_search", q] as const,
  mod: (id: string) => ["nexus_mod", id] as const,
  installed: ["nexus_installed"] as const,
  handler: ["nexus_handler_status"] as const,
};

export const TYPE_WORD: Record<ModType, string> = { kiln: "Kiln mod", native: "Native mod", emf: "EMF mod", dll: "DLL mod", modpack: "Modpack", other: "Not a Kiln mod" };

/** "1.2k" */
export function count(n: number): string {
  return n >= 10_000 ? `${Math.round(n / 1000)}k` : n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n);
}
