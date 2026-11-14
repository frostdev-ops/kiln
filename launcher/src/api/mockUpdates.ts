// Mock answers for updates_check (browser mock mode; src/api/mock.ts registers them). Three fixture mods of the mock
// Library show the three cases of the guided check:
//   new-items     its manifest names example-org/new-items on GitHub: 1.1.0 is published (update available)
//   big-stacks    no source at all (unsupported), like every other Library mod
//   night-sky     a GitHub source that was "up to date" two days ago: stale, so shown as unknown, never as up to date
// and the mod the mock Nexus catalog installed (weather-plus) follows mockNexus.ts's state.
// Tests and the console: `__kilnUpdatesMock.calls()` (every request so far), `.githubRequests()` (how many times a
// GitHub source was asked online), `.offline(true)` (the provider is not reachable: an online check answers like an
// offline one, with reason network), `.set(id, patch)`.
import type { KilnError, LibraryEntry } from "./commands";
import type { InstalledCatalogMod } from "./nexus";
import { NEXUS_MOCK_ENABLED } from "./mockNexus";
import type { CheckReport, ModUpdateStatus, SourceChange, UpdateSourceInfo, UpdatesRequest, UpdatesSpec } from "./updates";

type Handler = (args: Record<string, unknown>) => unknown;
type Emit = (name: string, payload: unknown) => void;

const iso = (ms: number) => new Date(ms).toISOString().replace(/\.\d+Z$/, "Z");

export function updatesMockHandlers(handlers: Record<string, Handler>, emit: Emit): Record<string, Handler> {
  const calls: UpdatesRequest[] = [];
  let unreachable = false;
  let githubRequests = 0;
  // the accepted source per mod, and what a check last saw there
  const sources = new Map<string, { spec: UpdatesSpec; origin: UpdateSourceInfo["origin"] }>([
    ["kiln/new-items", { spec: { provider: "github", repository: "example-org/new-items" }, origin: "manifest" }],
    ["kiln/night-sky", { spec: { provider: "github", repository: "example-org/night-sky" }, origin: "manifest" }],
  ]);
  const published = new Map<string, string>([["example-org/new-items", "1.1.0"], ["example-org/night-sky", "0.9.0"]]);
  const lastAnswer = new Map<string, number>([["kiln/new-items", Date.now() - 3_600_000], ["kiln/night-sky", Date.now() - 2 * 86_400_000]]);
  const patches = new Map<string, Partial<ModUpdateStatus>>();
  const reject = (code: KilnError["code"], message: string): never => { throw { code, message } satisfies KilnError; };
  const dest = (s: UpdatesSpec) => (s.provider === "github" ? s.repository : `enshrouded/${s.modId}/${s.fileChainId}`);

  const change = (c: SourceChange) => {
    const key = `${c.kind}/${c.id}`;
    if (c.action === "unmap") { if (sources.get(key)?.origin === "manual") sources.delete(key); return; }
    if (c.action === "confirm") return;
    const spec = c.spec;
    if (spec.provider === "github" ? !/^[A-Za-z0-9_.-]{1,100}\/[A-Za-z0-9_.-]{1,100}$/.test(spec.repository) : !(spec.modId > 0 && /^[A-Za-z0-9_-]{1,32}$/.test(spec.fileChainId)))
      reject("invalid_argument", "That is not an update source: 'repository' must be owner/repo.");
    const old = sources.get(key);
    if (old && dest(old.spec).toLowerCase() !== dest(spec).toLowerCase() && !c.confirm)
      reject("conflict", `${c.id} is checked against ${dest(old.spec)} (${old.spec.provider}). Confirm the change to ${dest(spec)} (${spec.provider}) to use the new source.`);
    sources.set(key, { spec, origin: "manual" });
    lastAnswer.delete(key);
  };

  const status = (e: LibraryEntry, online: boolean, now: number): ModUpdateStatus => {
    const key = `${e.kind}/${e.id}`;
    const v = e.versions[0];
    const base: ModUpdateStatus = {
      libraryRef: { kind: e.kind, id: e.id, version: v?.version ?? "0.0.0", hash: v?.hash ?? "" }, name: e.name, source: null, installedVersion: v?.version ?? "0.0.0",
      candidateVersion: null, candidateIdentity: null, state: "unsupported", compatibility: "unknown", coverage: "incomplete", checkedAt: null, stale: false, reason: null, pageUrl: null,
    };
    // a build with Nexus Mods off asks nothing of it (the real NexusProvider has no catalog there)
    const nexus = ((NEXUS_MOCK_ENABLED ? handlers.nexus_installed?.({}) as InstalledCatalogMod[] | undefined : undefined) ?? []).find((i) => i.refs.some((r) => r.kind === e.kind && r.id === e.id));
    const src = sources.get(key);
    let s = base;
    if (src?.spec.provider === "github") {
      const repo = src.spec.repository;
      if (online) { githubRequests += 1; lastAnswer.set(key, now); }
      const at = lastAnswer.get(key);
      const newest = published.get(repo.toLowerCase());
      s = { ...base, source: { provider: "github", destination: repo, origin: src.origin, authenticated: false }, pageUrl: `https://github.com/${repo}/releases`, state: "unknown", reason: "not_checked" };
      if (at !== undefined && !newest) s = { ...s, reason: "not_found", checkedAt: iso(at), coverage: "complete" };
      else if (at !== undefined && newest) {
        const newer = newest !== s.installedVersion;
        s = { ...s, candidateVersion: newest, candidateIdentity: { releaseId: "9001", tag: `v${newest}`, assetId: null, assetName: null, artifactSha256: null, fileVersionId: null },
          coverage: "complete", checkedAt: iso(at), pageUrl: `https://github.com/${repo}/releases/tag/v${newest}`,
          ...(newer ? { state: "available" as const, reason: null } : { state: "unknown" as const, reason: "same_version_unbound" as const }) };
        // an answer older than a day is stale: an update seen then stays, anything else is unknown
        if (now - at > 86_400_000 || (!online && unreachable)) s = { ...s, stale: true, ...(newer ? { reason: "network" as const } : { state: "unknown" as const, reason: "network" as const }) };
      } else if (!online) s = { ...s, reason: unreachable ? "network" : "not_checked" };
    } else if (nexus) {
      const i = nexus.info;
      s = { ...base, source: { provider: "nexus", destination: `enshrouded/${nexus.modId}/${nexus.fileId}`, origin: "receipt", authenticated: true }, installedVersion: nexus.version,
        candidateVersion: i.latestVersion, candidateIdentity: i.latestGsid ? { releaseId: null, tag: null, assetId: null, assetName: null, artifactSha256: null, fileVersionId: i.latestGsid } : null,
        state: i.updateAvailable ? "available" : i.latestVersion ? "current" : "unknown", reason: i.latestVersion ? null : "network", coverage: "complete", checkedAt: iso(now - 600_000),
        pageUrl: `https://www.nexusmods.com/enshrouded/mods/${nexus.modId}?tab=files`, name: nexus.name };
    }
    return { ...s, ...(patches.get(e.id) ?? {}) };
  };

  if (typeof window !== "undefined") {
    (window as unknown as { __kilnUpdatesMock?: unknown }).__kilnUpdatesMock = {
      calls: () => JSON.parse(JSON.stringify(calls)),
      githubRequests: () => githubRequests,
      offline: (on: boolean) => { unreachable = on; },
      set: (id: string, patch: Partial<ModUpdateStatus>) => { patches.set(id, patch); },
      publish: (repo: string, version: string) => { published.set(repo.toLowerCase(), version); },
    };
  }

  return {
    updates_check: (a) => {
      const req = ((a.request ?? {}) as UpdatesRequest);
      calls.push(JSON.parse(JSON.stringify(req)));
      if (req.source) change(req.source);
      const now = Date.now();
      // a provider the request does not name answers from what is remembered
      const online = !req.offline && !unreachable && (!req.providers || req.providers.includes("github"));
      const library = (handlers.library_list({}) as LibraryEntry[]);
      const report: CheckReport = { format: 1, checkedAt: iso(now), offline: !!req.offline, mods: library.map((e) => status(e, online, now)) };
      emit("kiln://mod-updates", JSON.parse(JSON.stringify(report)));
      return report;
    },
  };
}
