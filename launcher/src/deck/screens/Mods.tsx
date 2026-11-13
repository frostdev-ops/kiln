// Deck Mods: every mod of every kind (the desktop's main list, shell/mods.ts), one row each: A switches it on or off
// (one the profile lacks goes in, on), X removes it (out of the list, its files out of the Library), Y cooks a Kiln mod
// that has something to cook again ("Cook again", shell/cook.ts), and a mod found in the game folder says its state, A
// doing its one fix. The count covers every kind. Saves right away; the game's profile.json follows (applies at the
// next start while the game runs). Profiles are in the desktop UI's Settings.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "../../api/commands";
import type { AddReport } from "../../api/commands";
import { qk, useGame, useGameStatus } from "../../api/hooks";
import { KIND_SHORT } from "../../api/kinds";
import { cookAgain } from "../../shell/cook";
import { useEnsureProfile } from "../../shell/firstRun";
import type { ModRow } from "../../shell/mods";
import { notifyAdd } from "../../shell/notices";
import { removeMod, useRemoveCheck, type RemoveTarget } from "../../shell/removeMod";
import { useMods } from "../../shell/useMods";
import { withDropin } from "../../pages/profiles/dropins";
import { runTask, toast } from "../../ui";
import { DeckButton, Dialog, Row, Screen, Word } from "../ui";
import { KIND_WORD } from "../status";
import type { DeckAction } from "../layers";

export default function Mods() {
  const qc = useQueryClient();
  const nav = useNavigate();
  const { game } = useGame();
  const status = useGameStatus(game?.id);
  const mods = useMods(game);
  useEnsureProfile(game);
  const [focus, setFocus] = useState<ModRow | null>(null);
  const [removing, setRemoving] = useState<RemoveTarget | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const check = useRemoveCheck(removing, mods.profileId);
  const running = !!status.data?.running;
  // the confirm's Remove waits for the check of what else uses the mod; once it can be pressed, it has the focus (A)
  const checked = check.ready && !check.users.isFetching;
  useEffect(() => {
    if (removing && checked) document.querySelector<HTMLElement>('[data-deck-dialog="remove"] [data-deck-id="remove-confirm"]')?.focus();
  }, [removing, checked]);
  const installed = !!status.data?.runtime.installed;
  const loaded = !!mods.profile;
  // the Kiln mods the launcher cooks for this game (made from the game: models, animations, sounds): Y cooks one again
  const cook = useQuery({ queryKey: ["cook_plan", game?.id ?? ""], queryFn: () => api.cookPlan(game!.id), enabled: !!game && installed });
  const cookable = new Set((cook.data ?? []).map((j) => j.id));
  const [cooking, setCooking] = useState<string | null>(null);

  const fix = async (r: ModRow) => {
    const f = r.found;
    if (!f || !game || !mods.profileId || busy) return;
    if (f.fix === "dropins" && f.kind) { const k = f.kind; mods.edit.update((p) => withDropin(p, k, true)); return; }
    if (f.fix !== "add") return;
    setBusy(r.key);
    try {
      const rep = await runTask<AddReport>(() => api.foundAdd(game.id, mods.profileId!, f.path), { quiet: true });
      notifyAdd(rep);
      if (rep.added.length || rep.already.length) toast.ok(`${f.name} is on`);
      else toast.error({ code: "refused", message: rep.import.skipped[0]?.reason ?? "not taken" }, `The Library did not take ${f.name}`);
    } catch (e) { toast.error(e, `Could not add ${f.name}`); }
    finally {
      setBusy(null);
      void qc.invalidateQueries({ queryKey: ["found_scan"] });
      void qc.invalidateQueries({ queryKey: qk.profile(mods.profileId) });
      void qc.invalidateQueries({ queryKey: ["library_list"] });
    }
  };

  const actions: DeckAction[] = [];
  if (focus && !focus.found && loaded) actions.push({ button: "x", label: "Remove", run: () => setRemoving({ row: focus, version: null }), disabled: running });
  if (game && focus && !focus.found && focus.kind === "kiln" && focus.on && cookable.has(focus.id)) {
    const r = focus;
    actions.push({ button: "y", label: cooking === r.key ? "Cooking" : "Cook again", disabled: running || !!cooking, run: () => {
      setCooking(r.key);
      void cookAgain(game.id, r.id, r.name).finally(() => {
        setCooking(null);
        void qc.invalidateQueries({ queryKey: ["cook_plan"] });
      });
    } });
  }

  let say: string;
  if (!game) say = "No game yet.";
  else if (status.data && !installed) say = "Kiln is not in the game yet.";
  else if (!mods.profileId) say = "No profile is loaded: the mods in the game folder load as they are.";
  else if (mods.error) say = `Your mods are not readable: ${api.toKilnError(mods.error).message}`;
  else if (mods.loading) say = "Reading your mods.";
  else say = `${mods.count.on} mod${mods.count.on === 1 ? "" : "s"} on${mods.count.off ? `, ${mods.count.off} off` : ""}${running ? " · applies at the next start" : ""}`;

  const word = (r: ModRow) => {
    if (r.found) return busy === r.key ? <Word tone="ember">Adding</Word> : <Word tone={r.found.state === "loads" ? "ok" : r.found.state === "off" ? "warn" : "idle"}>{r.note?.text ?? ""}</Word>;
    if (cooking === r.key) return <Word tone="ember">Cooking</Word>;
    return <Word tone={r.on ? "ember" : "idle"}>{r.on ? "On" : "Off"}</Word>;
  };
  const sub = (r: ModRow) => [r.found ? "Game folder" : r.kind ? KIND_WORD[r.kind] : "", r.version, !r.found && r.note ? r.note.text : ""].filter(Boolean).join(" · ");

  return (
    <Screen name="mods" title="Mods" say={say} actions={actions}>
      {!game ? (
        <DeckButton id="go-home" onClick={() => nav("/deck")}>Home</DeckButton>
      ) : status.data && !installed ? (
        <DeckButton id="go-kiln" primary onClick={() => nav("/deck/kiln")}>Install Kiln</DeckButton>
      ) : mods.error ? null : !mods.rows.length && !mods.loading ? (
        <DeckButton id="go-add" primary onClick={() => nav("/deck/add")}>Add mods</DeckButton>
      ) : (
        <div className="deck-list deck-scroll" role="list" aria-label="Mods">
          {mods.rows.map((r) => r.found ? (
            <Row key={r.key} id={`found-${r.found.path}`} title={r.name} sub={sub(r)} end={word(r)} dim={r.found.state !== "loads"}
              a={r.found.fix === "dropins" && r.found.kind ? `Load ${KIND_SHORT[r.found.kind]} mods` : r.found.fix === "add" ? "Add" : "-"}
              disabled={!r.found.fix || !loaded || !!busy} onFocus={() => setFocus(r)} onClick={() => void fix(r)}
              aria-label={`${r.name}: in the game folder, ${r.note?.text ?? ""}`} />
          ) : (
            <Row key={r.key} id={`mod-${r.kind}-${r.id}`} lit={r.on} a={r.on ? "Turn off" : "Turn on"} title={r.name} sub={sub(r)} end={word(r)}
              disabled={!loaded} onFocus={() => setFocus(r)} onClick={() => mods.toggle(r, !r.on)} aria-label={`${r.name}: ${r.on ? "on" : "off"}`} />
          ))}
        </div>
      )}
      {removing && (
        <Dialog name="remove" title={`Remove ${removing.row.name}?`} onClose={() => setRemoving(null)}
          buttons={<>
            <DeckButton onClick={() => setRemoving(null)}>Cancel</DeckButton>
            <DeckButton id="remove-confirm" primary disabled={!checked} onClick={async () => {
              const t = removing;
              setRemoving(null);
              setFocus(null);
              try {
                await removeMod(qc, t, check.users.data, async () => { mods.drop(t.row); return mods.edit.flush(); });
                toast.ok(`Removed ${t.row.name}`);
              } catch (e) { toast.error(e); }
            }}>Remove</DeckButton>
          </>}>
          <p className="deck-note">{check.message}</p>
        </Dialog>
      )}
    </Screen>
  );
}
