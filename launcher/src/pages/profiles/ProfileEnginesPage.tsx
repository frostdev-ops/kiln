// /profiles/:id/engines: mods made for other engines (EML, ShroudForge, SET, Greeneye, Flame, Shroudtopia, Enshwick,
// shroudlink, Ember), each run by its Kiln engine plugin (docs/kiln/engines.md). One card per engine whose mods are in
// the library or the profile, with the same flame switch, versions and add-from-library as Kiln mods; Settings for
// ShroudForge and Shroudtopia mods. Ember's card says its mods don't run yet (no published Ember ABI). EML's "Restore
// vanilla game data" offer on top.
import { useState } from "react";
import type { ModKind } from "../../api/commands";
import { useGame, useLibrary } from "../../api/hooks";
import { IMPORTED_KINDS, KIND_NAME, KIND_PLUGIN, KIND_SHORT, KIND_TONE, NOT_RUNNING_NOTE, SETTINGS_KINDS, runsYet } from "../../api/kinds";
import { EngineSettingsModal } from "../../components/EngineSettings";
import { RestoreVanillaOffer } from "../../components/EngineStatus";
import { Badge, Banner, EmptyState, Skeleton, errorText } from "../../ui";
import { ModList, libName } from "./ModList";
import { ProfileFacts } from "./ProfileLayout";
import { useProfileEdit, useProfileId } from "./useProfileEdit";

const HINT: Partial<Record<ModKind, string>> = {
  eml: "Lua data patches. kiln-eml patches the game data at each start; with EML's own loader in the game it steps aside.",
  shroudforge: "Copied into ShroudForge's mods folder when the profile is applied; the switch there follows this one.",
  set: "Lua mods, copied into shrouded_engine_tweaks/mods when the profile is applied.",
  greeneye: "DLL mods on the client, loaded by kiln-greeneye as Greeneye Loader would.",
  flame: "DLL mods started by kiln-flame (FlameStartRuntime).",
  shroudtopia: "DLL mods, run by kiln-shroudtopia. Their settings live in this profile's shroudtopia.json.",
  enshwick: "DLL mods, data packs and Luau mods. kiln-enshwick copies them into the game's mods folder and runs Enshwick's own loader; players need the same both-side mods to join. Server-only packs do not need a client copy.",
  shroudlink: "Server mod DLLs, run by kiln-shroudlink (shroudlink's own server core). Players use shroudlink's client addon.",
  ember: "Server mods, one library each. kiln-ember finds and reports them.",
};

export default function ProfileEnginesPage() {
  const id = useProfileId();
  const { profile, toggle, remove, update } = useProfileEdit(id);
  const { game } = useGame();
  const lib = useLibrary(null);
  const [settings, setSettings] = useState<{ kind: ModKind; id: string; name: string } | null>(null);
  if (!profile) return null;
  const has = (k: ModKind) => (profile.mods[k] ?? []).length > 0 || (lib.data ?? []).some((l) => l.kind === k);
  const kinds = IMPORTED_KINDS.filter(has);
  return (
    <div className="k-scroll k-stack" style={{ gap: 18, paddingBottom: 20 }}>
      {game && <RestoreVanillaOffer gameId={game.id} />}
      {lib.isLoading && <Skeleton lines={3} />}
      {lib.error && <EmptyState title="The library is not readable" hint={errorText(lib.error)} />}
      {kinds.length === 0 ? (
        !lib.isLoading && !lib.error && <EmptyState title="No other engines' mods." hint="Drop an EML, ShroudForge, SET, Greeneye, Flame, Shroudtopia, Enshwick, shroudlink or Ember mod on the Library page. Kiln runs supported mods with their engine plugins; Ember mods are stored but don't run yet." />
      ) : kinds.map((k) => (
        <section key={k} className="k-card" data-kind={k}>
          <header className="k-card-head">
            <div className="k-card-titles">
              <h2 className="k-card-title">{KIND_NAME[k]} <Badge tone={KIND_TONE[k]}>{KIND_SHORT[k]}</Badge>{!runsYet(k) && <> <Badge tone="warn">doesn't run yet</Badge></>}</h2>
              <span className="k-card-sub">{KIND_PLUGIN[k] && <span className="k-mono">{KIND_PLUGIN[k]}</span>}{KIND_PLUGIN[k] && HINT[k] ? ". " : ""}{HINT[k] ?? ""}</span>
            </div>
          </header>
          {!runsYet(k) && <Banner tone="warn" title="These mods don't run yet">{NOT_RUNNING_NOTE[k]}</Banner>}
          <ModList kind={k} label={`${KIND_SHORT[k]} mods`} mods={profile.mods[k] ?? []} onToggle={(m, on) => toggle(k, m, on)} onRemove={(m) => remove(k, m)}
            onAdd={(ms) => update((p) => { p.mods[k] = [...(p.mods[k] ?? []), ...ms]; })}
            onVersion={(m, version, hash) => update((p) => { p.mods[k] = (p.mods[k] ?? []).map((x) => (x.id === m ? { ...x, version, hash } : x)); })}
            actions={SETTINGS_KINDS.includes(k) ? (m) => [{ label: "Settings", onSelect: () => setSettings({ kind: k, id: m.id, name: libName(lib.data, k, m.id) }) }] : undefined}
            emptyHint={`No ${KIND_SHORT[k]} mods in this profile.`} />
        </section>
      ))}
      <EngineSettingsModal target={settings} onClose={() => setSettings(null)} profileId={id} gameId={game?.id ?? null} />
      <ProfileFacts hint="Each engine's mods run through Kiln's plugin for that engine. EMF mods have their own tab." />
    </div>
  );
}
