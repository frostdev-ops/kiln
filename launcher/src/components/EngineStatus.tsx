// Other engines' plugins at a glance (engine_status: `kiln/state/<engine>.json`) and read-only shared archive backup guidance
// (eml_backup). Used by the Logs page's Engines tab and the mods' Engines view (/mods/engines). docs/kiln/engines.md.
import { useQuery } from "@tanstack/react-query";
import * as api from "../api/commands";
import type { EngineStatus, GameId, Json, ModKind } from "../api/commands";
import { useActiveProfile, usePoll, useProfile } from "../api/hooks";
import { t, useT } from "../i18n";
import { KIND_NAME, KIND_SHORT, KIND_TONE, MOD_KINDS } from "../api/kinds";
import { Badge, Banner, EmptyState, Skeleton, errorText, type Tone } from "../ui";
import { RestoreDataOffer } from "./RestoreDataOffer";

type Obj = Record<string, Json>;
const isObj = (v: unknown): v is Obj => !!v && typeof v === "object" && !Array.isArray(v);
const str = (v: Json | undefined): string | null => (typeof v === "string" && v ? v : typeof v === "number" ? String(v) : null);

/** The state word of a plugin's state file: EML `phase`, ShroudForge `mode`, else `state`/`status`; shroudlink and Ember
 *  say `steppedAside`/`started`, and Ember's `abi: null` means it is waiting for Ember to publish its mod ABI. */
export function headline(state: Json | null): string | null {
  return headlineText(headlineWord(state));
}

/** The word as the state file says it, or the launcher's own English word (the tone is read from this one). */
function headlineWord(state: Json | null): string | null {
  if (!isObj(state)) return null;
  // Enshwick's phase is when its loader starts (early/world), not whether it succeeded.
  const word = str(state.status) ?? str(state.phase) ?? str(state.mode) ?? str(state.state);
  if (word) return word;
  if (state.steppedAside === true) return "stepped aside";
  if ("abi" in state && state.abi === null) return "waiting for Ember's ABI";
  if (typeof state.started === "boolean") return state.started ? "running" : "idle";
  return null;
}

/** The launcher's own words in the current language; a state file's word as it is. */
function headlineText(word: string | null): string | null {
  switch (word) {
    case "stepped aside": return t("engineStatus.steppedAside");
    case "waiting for Ember's ABI": return t("engineStatus.waitingAbi");
    case "running": return t("engineStatus.running");
    case "idle": return t("engineStatus.idle");
    default: return word;
  }
}

const toneOf = (word: string | null): Tone => {
  if (!word) return "idle";
  if (/fail|error|refus/i.test(word)) return "err";
  if (/aside|off|not-installed|skip|disabled|idle|waiting/i.test(word)) return "idle";
  if (/done|hosted|loaded|running|active|started|ok/i.test(word)) return "ok";
  return "info";
};

/** A ShroudForge effect state (its runtime-mod.schema.json, and kiln-manage's "error"): failures; the plain states
 *  (a write, a loaded DLL, nothing to change); anything else, a word this launcher does not know included, is not a
 *  confirmed write and shows as a warning. */
const effectTone = (state: string) =>
  /fail|error|blocked/i.test(state) ? "k-tone-err" : ["write-confirmed", "loaded", "no-change"].includes(state) ? "" : "k-tone-warn";

export interface ModLine { id: string; state: string | null; reason: string | null }

/** The `mods` list of a state file, each as {id, state, reason} (EML: id/state/reason; Greeneye: status; Shroudtopia: name;
 *  Enshwick: folder/action/note). */
export function modLines(state: Json | null): ModLine[] {
  if (!isObj(state) || !Array.isArray(state.mods)) return [];
  return state.mods.filter(isObj).map((m) => ({
    id: str(m.id) ?? str(m.name) ?? str(m.folder) ?? "?",
    state: str(m.state) ?? str(m.status) ?? str(m.action) ?? (typeof m.active === "boolean" ? (m.active ? "active" : "off") : null),
    reason: str(m.reason) ?? str(m.detail) ?? str(m.error) ?? str(m.note),
  }));
}

export const engineStatusKey = (gameId: GameId) => ["engine_status", gameId] as const;

/** One compact row per engine plugin that wrote a state file or has mods in the active profile. */
export function EngineStatusList({ gameId }: { gameId: GameId }) {
  const t = useT();
  const q = useQuery({ queryKey: engineStatusKey(gameId), queryFn: () => api.engineStatus(gameId), refetchInterval: usePoll(10_000) });
  const active = useActiveProfile(gameId);
  const profile = useProfile(active.data?.profileId);
  const inProfile = (k: ModKind) => (profile.data?.mods[k] ?? []).length > 0;
  if (q.isLoading) return <Skeleton lines={3} />;
  if (q.error) return <EmptyState title={t("engineStatus.unreadable")} hint={errorText(q.error)} />;
  const rows = (q.data ?? []).filter((s) => s.state !== null || !!s.error || inProfile(s.kind) || !!s.ownLoader || s.prerequisite?.available || (s.effects && s.effects.state !== "unavailable")
    || (s.kind === "shroudforge" && !!s.startupFailed))
    .sort((a, b) => MOD_KINDS.indexOf(a.kind) - MOD_KINDS.indexOf(b.kind));
  if (!rows.length) return <EmptyState title={t("engineStatus.none")} hint={t("engineStatus.noneHint")} />;
  return (
    <div className="k-list" aria-label={t("engineStatus.plugins")}>
      {rows.map((s) => <EngineRow key={s.kind} s={s} />)}
    </div>
  );
}

export function EngineRow({ s }: { s: EngineStatus }) {
  const t = useT();
  const selfFile = s.kind === "shroudforge" ? s.ownLoader?.file : null;
  const selfHosted = !selfFile && s.kind === "shroudforge" && isObj(s.state) && s.state.mode === "self-hosted";
  const raw = selfFile || selfHosted ? null : headlineWord(s.state);
  const word = selfFile ? t("engineStatus.ownLoader", { file: selfFile }) : selfHosted ? t("engineStatus.selfHostedGone") : headlineText(raw);
  // the tone of the English word (a translation must not change it): own loader = info, self-hosted = ok
  const tone: Tone = selfFile ? "info" : selfHosted ? "ok" : toneOf(raw);
  const reason = s.prerequisite?.text ?? s.error ?? (isObj(s.state) ? str(s.state.reason) ?? str(s.state.error) : null);
  const mods = modLines(s.state);
  const effects = s.kind === "shroudforge" ? s.effects : null;
  const current = effects?.state === "current";
  // kiln-eml found more than one EML loader in the game (`loaders`): each patches the game files; `reason` says to keep one.
  const emlLoaders = s.kind === "eml" && isObj(s.state) && Array.isArray(s.state.loaders) ? s.state.loaders.length : 0;
  return (
    <div className="k-list-item" data-engine={s.kind} style={{ flexWrap: "wrap", alignItems: "baseline", rowGap: 2 }} title={s.file}>
      <Badge tone={KIND_TONE[s.kind]} title={KIND_NAME[s.kind]}>{KIND_SHORT[s.kind]}</Badge>
      <span className="k-mono k-muted">{s.plugin}</span>
      <span className="k-spacer" />
      {s.prerequisite ? <span className={`k-word-sm k-tone-${s.prerequisite.available ? "info" : "warn"}`}>{s.prerequisite.available ? t("engineStatus.hostRecognized") : t("engineStatus.requiresXhl")}</span> : s.error ? <span className="k-word-sm k-tone-err">{t("engineStatus.unreadableShort")}</span> : s.state === null && !selfFile ? <span className="k-hint">{current ? t("engineStatus.reportAvailable") : t("engineStatus.notRun")}</span> : (
        <>
          {word && <span className={`k-word-sm k-tone-${tone}`}>{word}</span>}
          {mods.length > 0 && <span className="k-hint">{t("engineStatus.mods", { count: mods.length })}</span>}
        </>
      )}
      {(reason || mods.length > 0) && (
        <span className="k-hint" style={{ flexBasis: "100%" }}>
          {reason && <span className={emlLoaders > 1 ? "k-tone-warn" : undefined} data-eml-loaders={emlLoaders > 1 ? emlLoaders : undefined}>{reason}. </span>}
          {mods.map((m, i) => (
            <span key={m.id + i}>{i ? ", " : ""}<span className="k-mono">{m.id}</span>{m.state ? ` ${m.state}` : ""}{m.reason ? ` (${m.reason})` : ""}</span>
          ))}
        </span>
      )}
      {effects && (
        <div className="k-hint" style={{ flexBasis: "100%" }} title={effects.file} aria-label={t("engineStatus.effects")}>
          {!current ? <span className={effects.state === "invalid" ? "k-tone-err" : "k-tone-warn"}>{effects.reason ?? t("engineStatus.sfReport", { state: effects.state })}</span> : (
            <>
              {effects.mods.length === 0 && <span>{t("engineStatus.noEffects")}</span>}
              {effects.mods.map((m) => (
                <div key={m.id}>
                  <span className="k-mono">{m.id}</span>{" "}
                  <span className={effectTone(m.state)}>{m.state}</span>
                  {m.detail && <span>: {m.detail}</span>}
                </div>
              ))}
              {effects.mods.length > 0 && <div>{effects.mods.some((m) => m.state === "write-confirmed")
                ? t("engineStatus.writeConfirmed")
                : t("engineStatus.ownReports")}</div>}
            </>
          )}
        </div>
      )}
      {/* The engine's own loader is in the game: what that means for the active profile's mods (own_loaders.rs). */}
      {s.ownLoader && (
        <span className={s.ownLoader.notCopied.length || s.ownLoader.ownOff?.length || s.ownLoader.ownRefused?.length ? "k-hint k-tone-warn" : "k-hint"} data-own-loader={s.ownLoader.file} style={{ flexBasis: "100%" }}>
          {s.ownLoader.text}.
        </span>
      )}
      {/* ShroudForge's startup pass failed at the last start (sf_startup.rs): whoever ran it, with or without an own loader's note. */}
      {s.startupFailed && (
        <span className="k-hint k-tone-warn" data-startup-failed="" style={{ flexBasis: "100%" }}>
          {s.startupFailed.text}.
        </span>
      )}
    </div>
  );
}

/** What the archive writer left to say about the game's archives, in up to one banner.
 *  - The shared backup differs from the game data while the profile runs no EML mod: how to get vanilla data back. A
 *    difference is not proof of EML ownership; only the archive writer can safely restore its output.
 *  - kiln/state/eml.json `reason` of the last start (idle, done or failed): what the writer kept or moved aside in the
 *    game folder (unproven data, extra containers, recovery files with their sizes, containers moved to
 *    kiln/eml/displaced/). Shown as written, whatever the backup comparison says and whether or not an EML mod is on. */
export function RestoreVanillaOffer({ gameId }: { gameId: GameId }) {
  return <><RestoreDataOffer key={gameId} gameId={gameId} /><VanillaOffer gameId={gameId} /></>;
}

function VanillaOffer({ gameId }: { gameId: GameId }) {
  const t = useT();
  const backup = useQuery({ queryKey: ["eml_backup", gameId], queryFn: () => api.emlBackup(gameId) });
  const engines = useQuery({ queryKey: engineStatusKey(gameId), queryFn: () => api.engineStatus(gameId) });
  const active = useActiveProfile(gameId);
  const profile = useProfile(active.data?.profileId);
  const patched = (backup.data ?? []).filter((b) => b.patched);
  const emlOn = (profile.data?.mods.eml ?? []).some((m) => m.enabled);
  if (backup.error) return <EmptyState title={t("engineStatus.backupUnreadable")} hint={errorText(backup.error)} />;
  if (active.error) return <EmptyState title={t("engineStatus.listUnreadable")} hint={errorText(active.error)} />;
  const eml = engines.data?.find((s) => s.kind === "eml")?.state;
  const phase = isObj(eml) ? str(eml.phase) : null;
  const idle = phase === "idle";
  // one sentence for the player from the last start that finished
  const reason = isObj(eml) && (idle || phase === "done" || phase === "failed") ? str(eml.reason) : null;
  const said = reason && <>{" "}{idle ? t("engineStatus.lastStartNoEml", { reason }) : t("engineStatus.lastStart", { reason })}</>;
  const differs = patched.length > 0 && !active.isLoading && !(active.data && !profile.data) && !emlOn;
  if (!differs) {
    return reason ? <Banner tone="warn" title={t("engineStatus.emlNote")}>{said}</Banner> : null;
  }
  const names = patched.map((b) => b.file.split(/[\\/]/).pop()).join(", ");
  // the backup is of an earlier game build (the game was updated since): nothing to compare or restore against
  const stale = idle && isObj(eml) && eml.staleBackup === true;
  return (
    <Banner tone={reason ? "warn" : "info"} title={t("engineStatus.differsTitle")}>
      {t("engineStatus.differs", { count: patched.length, names })}
      {stale ? <>{" "}{t("engineStatus.staleBackup")}
        {said || <>{" "}{t("engineStatus.nothingToDo")}</>}</> : idle && said ? said : <>
        {" "}{t("engineStatus.removeHow")}{said}</>}
      {" "}{t("engineStatus.othersKept")}
    </Banner>
  );
}
