// Other engines' plugins at a glance (engine_status: `kiln/state/<engine>.json`), and EML's "Restore vanilla game data"
// (eml_backup / eml_restore). Used by the Logs page's Engines tab and the profile's Engines tab. docs/kiln/engines.md.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import * as api from "../api/commands";
import type { EngineStatus, GameId, Json, ModKind } from "../api/commands";
import { useActiveProfile, useGameStatus, usePoll, useProfile } from "../api/hooks";
import { KIND_NAME, KIND_SHORT, KIND_TONE, MOD_KINDS } from "../api/kinds";
import { Badge, Banner, Button, EmptyState, Modal, Skeleton, errorText, toast, type Tone } from "../ui";

type Obj = Record<string, Json>;
const isObj = (v: unknown): v is Obj => !!v && typeof v === "object" && !Array.isArray(v);
const str = (v: Json | undefined): string | null => (typeof v === "string" && v ? v : typeof v === "number" ? String(v) : null);

/** The state word of a plugin's state file: EML `phase`, ShroudForge `mode`, else `state`/`status`; shroudlink and Ember
 *  say `steppedAside`/`started`, and Ember's `abi: null` means it is waiting for Ember to publish its mod ABI. */
export function headline(state: Json | null): string | null {
  if (!isObj(state)) return null;
  // Enshwick's phase is when its loader starts (early/world), not whether it succeeded.
  const word = str(state.status) ?? str(state.phase) ?? str(state.mode) ?? str(state.state);
  if (word) return word;
  if (state.steppedAside === true) return "stepped aside";
  if ("abi" in state && state.abi === null) return "waiting for Ember's ABI";
  if (typeof state.started === "boolean") return state.started ? "running" : "idle";
  return null;
}

const toneOf = (word: string | null): Tone => {
  if (!word) return "idle";
  if (/fail|error|refus/i.test(word)) return "err";
  if (/aside|off|not-installed|skip|disabled|idle|waiting/i.test(word)) return "idle";
  if (/done|hosted|loaded|running|active|started|ok/i.test(word)) return "ok";
  return "info";
};

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
  const q = useQuery({ queryKey: engineStatusKey(gameId), queryFn: () => api.engineStatus(gameId), refetchInterval: usePoll(10_000) });
  const active = useActiveProfile(gameId);
  const profile = useProfile(active.data?.profileId);
  const inProfile = (k: ModKind) => (profile.data?.mods[k] ?? []).length > 0;
  if (q.isLoading) return <Skeleton lines={3} />;
  if (q.error) return <EmptyState title="The engine state files are not readable" hint={errorText(q.error)} />;
  const rows = (q.data ?? []).filter((s) => s.state !== null || !!s.error || inProfile(s.kind) || !!s.ownLoader || s.prerequisite?.available || (s.effects && s.effects.state !== "unavailable"))
    .sort((a, b) => MOD_KINDS.indexOf(a.kind) - MOD_KINDS.indexOf(b.kind));
  if (!rows.length) return <EmptyState title="No other engine has run yet." hint="Kiln's engine plugins write their state at each start." />;
  return (
    <div className="k-list" aria-label="Engine plugins">
      {rows.map((s) => <EngineRow key={s.kind} s={s} />)}
    </div>
  );
}

export function EngineRow({ s }: { s: EngineStatus }) {
  const word = headline(s.state);
  const reason = s.prerequisite?.text ?? s.error ?? (isObj(s.state) ? str(s.state.reason) ?? str(s.state.error) : null);
  const mods = modLines(s.state);
  const effects = s.kind === "shroudforge" ? s.effects : null;
  const current = effects?.state === "current";
  return (
    <div className="k-list-item" data-engine={s.kind} style={{ flexWrap: "wrap", alignItems: "baseline", rowGap: 2 }} title={s.file}>
      <Badge tone={KIND_TONE[s.kind]} title={KIND_NAME[s.kind]}>{KIND_SHORT[s.kind]}</Badge>
      <span className="k-mono k-muted">{s.plugin}</span>
      <span className="k-spacer" />
      {s.prerequisite ? <span className={`k-word-sm k-tone-${s.prerequisite.available ? "info" : "warn"}`}>{s.prerequisite.available ? "Host recognized (not verified)" : "Requires XHL-Core"}</span> : s.error ? <span className="k-word-sm k-tone-err">Unreadable</span> : s.state === null ? <span className="k-hint">{current ? "Runtime report available" : "Not run yet"}</span> : (
        <>
          {word && <span className={`k-word-sm k-tone-${toneOf(word)}`}>{word}</span>}
          {mods.length > 0 && <span className="k-hint">{mods.length} mod{mods.length === 1 ? "" : "s"}</span>}
        </>
      )}
      {(reason || mods.length > 0) && (
        <span className="k-hint" style={{ flexBasis: "100%" }}>
          {reason && <span>{reason}. </span>}
          {mods.map((m, i) => (
            <span key={m.id + i}>{i ? ", " : ""}<span className="k-mono">{m.id}</span>{m.state ? ` ${m.state}` : ""}{m.reason ? ` (${m.reason})` : ""}</span>
          ))}
        </span>
      )}
      {effects && (
        <div className="k-hint" style={{ flexBasis: "100%" }} title={effects.file} aria-label="ShroudForge mod effects">
          {!current ? <span className={effects.state === "invalid" ? "k-tone-err" : "k-tone-warn"}>{effects.reason ?? `ShroudForge report: ${effects.state}`}</span> : (
            <>
              {effects.mods.length === 0 && <span>No per-mod effects reported yet.</span>}
              {effects.mods.map((m) => (
                <div key={m.id}>
                  <span className="k-mono">{m.id}</span>{" "}
                  <span className={/fail|error|blocked/i.test(m.state) ? "k-tone-err" : /no-target|waiting|restart/i.test(m.state) ? "k-tone-warn" : ""}>{m.state}</span>
                  {m.detail && <span>: {m.detail}</span>}
                </div>
              ))}
              {effects.mods.some((m) => m.state === "write-confirmed") && <div>Memory write confirmed; not proof of the gameplay effect.</div>}
            </>
          )}
        </div>
      )}
      {/* The engine's own loader is in the game: what that means for the active profile's mods (own_loaders.rs). */}
      {s.ownLoader && (
        <span className={s.ownLoader.notCopied.length ? "k-hint k-tone-warn" : "k-hint"} data-own-loader={s.ownLoader.file} style={{ flexBasis: "100%" }}>
          {s.ownLoader.text}.
        </span>
      )}
    </div>
  );
}

/** "Restore vanilla game data": offered when EML's backup says a `.kfc` is patched and the active profile runs no EML
 *  mod (nothing would patch it again, so the game would keep stale EML changes). */
export function RestoreVanillaOffer({ gameId }: { gameId: GameId }) {
  const qc = useQueryClient();
  const backup = useQuery({ queryKey: ["eml_backup", gameId], queryFn: () => api.emlBackup(gameId) });
  const active = useActiveProfile(gameId);
  const profile = useProfile(active.data?.profileId);
  const status = useGameStatus(gameId);
  const [confirm, setConfirm] = useState(false);
  const restore = useMutation({
    mutationFn: () => api.emlRestore(gameId),
    onSuccess: (files) => {
      toast.ok("Vanilla game data back", files.length ? files.map((f) => f.split(/[\\/]/).pop()).join(", ") : "Nothing needed restoring.");
      setConfirm(false);
      qc.invalidateQueries({ queryKey: ["eml_backup", gameId] });
      qc.invalidateQueries({ queryKey: engineStatusKey(gameId) });
    },
    onError: (e) => toast.error(e, "Not restored"),
  });
  const patched = (backup.data ?? []).filter((b) => b.patched);
  const emlOn = (profile.data?.mods.eml ?? []).some((m) => m.enabled);
  if (backup.error) return <EmptyState title="The game data backup is not readable" hint={errorText(backup.error)} />;
  if (active.error) return <EmptyState title="The active profile is not readable" hint={errorText(active.error)} />;
  if (!patched.length || active.isLoading || (active.data && !profile.data) || emlOn) return null;
  const running = !status.data || !!status.data.running;
  const names = patched.map((b) => b.file.split(/[\\/]/).pop()).join(", ");
  return (
    <>
      <Banner tone="warn" title="EML changes still in the game data"
        actions={<Button size="s" disabled={running} title={running ? "Close the game first" : undefined} onClick={() => setConfirm(true)}>Restore vanilla game data</Button>}>
        {names} still {patched.length === 1 ? "has" : "have"} EML's patches, and no EML mod is on in the active profile.
      </Banner>
      <Modal open={confirm} onClose={() => setConfirm(false)} title="Restore vanilla game data?" width={500} locked={restore.isPending}
        footer={<><Button variant="ghost" onClick={() => setConfirm(false)}>Cancel</Button>
          <Button variant="primary" loading={restore.isPending} disabled={running} onClick={() => restore.mutate()}>Restore</Button></>}>
        EML's backup of the original file goes back over {names}. Turn an EML mod on later and kiln-eml patches it again.
      </Modal>
    </>
  );
}
