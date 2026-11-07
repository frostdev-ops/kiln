// Deck Mods: the active profile's mods, each one row: A turns it on or off, X takes it out of the profile (the library
// keeps it), Y switches profile. Saves right away; the game's profile.json follows (applies at the next start while
// the game runs).
import { useState } from "react";
import { useNavigate } from "react-router-dom";
import type { ModKind, ProfileMod } from "../../api/commands";
import { useActiveProfile, useGame, useGameStatus, useLibrary, useProfiles } from "../../api/hooks";
import { useProfileEdit } from "../../pages/profiles/useProfileEdit";
import { ProfileDialog } from "../dialogs";
import { DeckButton, Dialog, Row, Screen, Word } from "../ui";
import { KIND_WORD } from "../status";
import type { DeckAction } from "../layers";

const KINDS: ModKind[] = ["kiln", "native", "emf", "plugin"];

export default function Mods() {
  const nav = useNavigate();
  const { game } = useGame();
  const status = useGameStatus(game?.id);
  const active = useActiveProfile(game?.id);
  const profiles = useProfiles();
  const pid = active.data?.profileId ?? "";
  const edit = useProfileEdit(pid);
  const lib = useLibrary(null);
  const [focus, setFocus] = useState<{ kind: ModKind; mod: ProfileMod } | null>(null);
  const [removing, setRemoving] = useState<{ kind: ModKind; mod: ProfileMod } | null>(null);
  const [picking, setPicking] = useState(false);
  const running = !!status.data?.running;
  const installed = !!status.data?.runtime.installed;
  const p = pid ? edit.profile : null;
  const nameOf = (kind: ModKind, id: string) => lib.data?.find((l) => l.kind === kind && l.id === id)?.name ?? id;
  const rows = p ? KINDS.flatMap((kind) => p.mods[kind].map((mod) => ({ kind, mod }))) : [];
  const on = rows.filter((r) => r.mod.enabled).length;
  const hasProfiles = (profiles.data?.length ?? 0) > 0;

  const actions: DeckAction[] = [];
  if (focus && p) actions.push({ button: "x", label: "Remove", run: () => setRemoving(focus) });
  if (game && installed && hasProfiles) actions.push({ button: "y", label: "Profile", run: () => setPicking(true), disabled: running });

  let say: string;
  if (!game) say = "No game yet.";
  else if (status.data && !installed) say = "Kiln is not in the game yet.";
  else if (!pid) say = hasProfiles ? "No profile is active." : "No mods yet.";
  else if (!p) say = "Reading the profile.";
  else say = `${on} of ${rows.length} on${running ? " · applies at the next start" : ""}`;

  return (
    <Screen name="mods" title={p?.name ?? "Mods"} say={say} actions={actions}>
      {!game ? (
        <DeckButton id="go-home" onClick={() => nav("/deck")}>Home</DeckButton>
      ) : status.data && !installed ? (
        <DeckButton id="go-kiln" primary onClick={() => nav("/deck/kiln")}>Install Kiln</DeckButton>
      ) : !pid ? (
        <div className="deck-row-buttons">
          {hasProfiles && <DeckButton id="pick" primary disabled={running} onClick={() => setPicking(true)}>Choose a profile</DeckButton>}
          <DeckButton id="go-add" onClick={() => nav("/deck/add")}>Add mods</DeckButton>
        </div>
      ) : p && !rows.length ? (
        <DeckButton id="go-add" primary onClick={() => nav("/deck/add")}>Add mods</DeckButton>
      ) : (
        <div className="deck-list deck-scroll" role="list" aria-label="Mods">
          {rows.map(({ kind, mod }) => (
            <Row key={`${kind}:${mod.id}`} id={`mod-${kind}-${mod.id}`} lit={mod.enabled} a={mod.enabled ? "Turn off" : "Turn on"}
              title={nameOf(kind, mod.id)} sub={`${KIND_WORD[kind]} · ${mod.version}`}
              end={<Word tone={mod.enabled ? "ember" : "idle"}>{mod.enabled ? "On" : "Off"}</Word>}
              onFocus={() => setFocus({ kind, mod })}
              onClick={() => edit.toggle(kind, mod.id, !mod.enabled)} aria-label={`${nameOf(kind, mod.id)}: ${mod.enabled ? "on" : "off"}`} />
          ))}
        </div>
      )}
      {removing && (
        <Dialog name="remove" title={`Remove ${nameOf(removing.kind, removing.mod.id)}?`} onClose={() => setRemoving(null)}
          buttons={<>
            <DeckButton onClick={() => setRemoving(null)}>Cancel</DeckButton>
            <DeckButton primary onClick={() => { edit.remove(removing.kind, removing.mod.id); setRemoving(null); setFocus(null); }}>Remove</DeckButton>
          </>} />
      )}
      {picking && game && <ProfileDialog game={game} onClose={() => setPicking(false)} />}
    </Screen>
  );
}
