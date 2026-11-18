// Deck Mods: every mod of every kind (the desktop's main list, shell/mods.ts), one row each: A switches it on or off
// (one the profile lacks goes in, on), X removes it (out of the list, its files out of the Library), Y cooks a Kiln mod
// that has something to cook again ("Cook again", shell/cook.ts), and a mod found in the game folder says its state, A
// doing its one fix. The count covers every kind. Saves right away; the game's profile.json follows (applies at the
// next start while the game runs). Profiles are in the desktop UI's Settings.
import { SfSelfHostedNotice } from "../../components/SfSelfHostedNotice";
import { ShroudlinkNotice } from "../../components/ShroudlinkNotice";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useSearchParams, useNavigate } from "react-router-dom";
import * as api from "../../api/commands";
import type { AddReport } from "../../api/commands";
import { qk, useGame, useGameStatus } from "../../api/hooks";
import { KIND_SHORT } from "../../api/kinds";
import { cookAgain } from "../../shell/cook";
import { useEnsureProfile } from "../../shell/firstRun";
import { filterRows, bulkPlan, type ModRow } from "../../shell/mods";
import { notifyAdd } from "../../shell/notices";
import { notRemovableText, removable, removeMany, removeMod, useRemoveCheck, useRemoveMany, withoutRows, type RemoveTarget } from "../../shell/removeMod";
import { NONE, ownerOf, pick as pickSelected, pruneSelection, setSelection, toggle as toggleSelected, useOwnedSelection } from "../../shell/selection";
import { useMods } from "../../shell/useMods";
import { withDropin } from "../../pages/profiles/dropins";
import { runTask, toast } from "../../ui";
import { DeckButton, Dialog, Row, Screen, Word } from "../ui";
import { KIND_WORD } from "../status";
import type { DeckAction } from "../layers";
import { useT } from "../../i18n";

export default function Mods() {
  const t = useT();
  const qc = useQueryClient();
  const nav = useNavigate();
  const [params] = useSearchParams();
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
      if (rep.added.length || rep.already.length) toast.ok(t("deck.shared.isOn", { name: f.name }));
      else toast.error({ code: "refused", message: rep.import.skipped[0]?.reason ?? t("deck.mods.notTaken") }, t("deck.mods.notTakenTitle", { name: f.name }));
    } catch (e) { toast.error(e, t("deck.mods.couldNotAdd", { name: f.name })); }
    finally {
      setBusy(null);
      void qc.invalidateQueries({ queryKey: ["found_scan"] });
      void qc.invalidateQueries({ queryKey: qk.profile(mods.profileId) });
      void qc.invalidateQueries({ queryKey: ["library_list"] });
    }
  };

  // the selection (shell/selection.ts, the same one the desktop list keeps): L2 selects the focused row, R2 opens what
  // can be done with the selected ones
  const gid = game?.id ?? "";
  const owner = ownerOf(mods.profileId, mods.profile);
  const { sel } = useOwnedSelection(gid, owner);
  const picked = pickSelected(sel, mods.rows);
  // a row that is gone (removed elsewhere, or by a filter of the profile) leaves the selection
  const rowKeys = mods.rows.map((r) => r.key).join("\n");
  const read = !mods.loading && mods.rows.length > 0;
  useEffect(() => {
    if (gid) pruneSelection(gid, owner, rowKeys ? rowKeys.split("\n") : [], read);
  }, [rowKeys, read, gid, owner]);
  const [selMenu, setSelMenu] = useState(false);
  const [removingMany, setRemovingMany] = useState<ModRow[] | null>(null);
  const many = useRemoveMany(removingMany ?? [], mods.profileId, !!removingMany);
  useLayoutEffect(() => {
    setFocus(null); setSelMenu(false); setRemoving(null); setRemovingMany(null);
  }, [gid, mods.profileId]);
  const live = useRef(mods);
  live.current = mods;
  const switchPicked = (on: boolean) => {
    const r = mods.setAll(picked, on);
    setSelMenu(false);
    if (!r?.undo) return;
    const { plan, undo } = r;
    const n = plan.change.length;
    toast.ok(on ? t("deck.mods.switchedOn", { count: n }) : t("deck.mods.switchedOff", { count: n }),
      plan.left.length ? t("deck.mods.skippedWhy", { count: plan.left.length, why: [...new Set(plan.left.map((l) => l.why))].join("; ") }) : undefined,
      { ms: 20_000, actions: [{ label: t("common.undo"), onClick: async () => {
        const res = await live.current.undoAll(undo);
        if (!res.ok) toast.info(t("deck.mods.cantUndo"), `${res.why[0].toUpperCase()}${res.why.slice(1)}.`);
        else if (res.kept) toast.info(t("deck.mods.undoKept"), t("deck.mods.undoKeptDetail", { count: res.kept }));
      } }] });
  };
  const selOn = bulkPlan(picked, true);
  const selOff = bulkPlan(picked, false);
  const toRemove = picked.filter(removable);

  const actions: DeckAction[] = [];
  if (focus && gid) {
    const f = focus;
    actions.push({ button: "lt", label: sel.keys.includes(f.key) ? t("deck.mods.unselect") : t("deck.app.select"), run: () => setSelection(gid, owner, toggleSelected(sel, f.key)) });
  }
  if (picked.length) actions.push({ button: "rt", label: t("deck.mods.selected", { count: picked.length }), run: () => setSelMenu(true) });
  if (focus && !focus.found && loaded) actions.push({ button: "x", label: t("common.remove"), run: () => setRemoving({ row: focus, version: null }), disabled: running });
  if (game && focus && !focus.found && focus.kind === "kiln" && focus.on && cookable.has(focus.id)) {
    const r = focus;
    actions.push({ button: "y", label: cooking === r.key ? t("deck.mods.cooking") : t("deck.mods.cookAgain"), disabled: running || !!cooking, run: () => {
      setCooking(r.key);
      void cookAgain(game.id, r.id, r.name).finally(() => {
        setCooking(null);
        void qc.invalidateQueries({ queryKey: ["cook_plan"] });
      });
    } });
  }

  let say: string;
  if (!game) say = t("deck.shared.noGame");
  else if (status.data && !installed) say = t("deck.shared.kilnNotInGame");
  else if (!mods.profileId) say = t("deck.mods.noProfile");
  else if (mods.error) say = t("deck.mods.notReadable", { error: api.toKilnError(mods.error).message });
  else if (mods.loading) say = t("deck.mods.reading");
  else {
    const count = mods.count.off ? t("deck.shared.modsOnOff", { count: mods.count.on, off: mods.count.off }) : t("deck.shared.modsOn", { count: mods.count.on });
    say = running ? t("deck.mods.appliesNext", { mods: count }) : count;
  }

  const word = (r: ModRow) => {
    if (r.found) return busy === r.key ? <Word tone="ember">{t("deck.shared.adding")}</Word> : <Word tone={r.found.state === "loads" ? "ok" : r.found.state === "off" ? "warn" : "idle"}>{r.note?.text ?? ""}</Word>;
    if (cooking === r.key) return <Word tone="ember">{t("deck.mods.cooking")}</Word>;
    return <Word tone={r.on ? "ember" : "idle"}>{r.on ? t("deck.shared.on") : t("deck.shared.off")}</Word>;
  };
  const sub = (r: ModRow) => [r.found ? t("deck.mods.gameFolder") : r.kind ? KIND_WORD[r.kind] : "", r.version, !r.found && r.note ? r.note.text : ""].filter(Boolean).join(" · ");

  return (
    <Screen name="mods" title={t("deck.app.tabs.mods")} say={say} actions={actions}>
      <SfSelfHostedNotice profile={mods.profile} found={mods.found.data} />
      <ShroudlinkNotice game={game} mods={mods} deck />
      {params.has("filter") && <DeckButton id="clear-filter" onClick={() => nav("/deck/mods")}>{t("deck.mods.showAll")}</DeckButton>}
      {!game ? (
        <DeckButton id="go-home" onClick={() => nav("/deck")}>{t("deck.shared.home")}</DeckButton>
      ) : status.data && !installed ? (
        <DeckButton id="go-kiln" primary onClick={() => nav("/deck/kiln")}>{t("deck.shared.installKiln")}</DeckButton>
      ) : mods.error ? null : !mods.rows.length && !mods.loading ? (
        <DeckButton id="go-add" primary onClick={() => nav("/deck/add")}>{t("deck.app.tabs.addMods")}</DeckButton>
      ) : (
        <div className="deck-list deck-scroll" role="list" aria-label={t("deck.app.tabs.mods")}>
          {filterRows(mods.rows, params.get("filter") ?? "", "").map((r) => r.found ? (
            <Row key={r.key} id={`found-${r.found.path}`} title={sel.keys.includes(r.key) ? `✓ ${r.name}` : r.name} sub={sub(r)} end={word(r)} dim={r.found.state !== "loads"}
              a={r.found.fix === "dropins" && r.found.kind ? t("deck.mods.loadKind", { kind: KIND_SHORT[r.found.kind] }) : r.found.fix === "add" ? t("common.add") : "-"}
              disabled={!r.found.fix || !loaded || !!busy} onFocus={() => setFocus(r)} onClick={() => void fix(r)}
              aria-label={sel.keys.includes(r.key) ? t("deck.mods.foundAriaSelected", { name: r.name, note: r.note?.text ?? "" }) : t("deck.mods.foundAria", { name: r.name, note: r.note?.text ?? "" })} />
          ) : (
            <Row key={r.key} id={`mod-${r.kind}-${r.id}`} lit={r.on} a={r.on ? t("deck.mods.turnOff") : t("deck.mods.turnOn")} title={sel.keys.includes(r.key) ? `✓ ${r.name}` : r.name} sub={sub(r)} end={word(r)}
              disabled={!loaded} onFocus={() => setFocus(r)} onClick={() => mods.toggle(r, !r.on)} aria-label={sel.keys.includes(r.key) ? (r.on ? t("deck.mods.ariaOnSelected", { name: r.name }) : t("deck.mods.ariaOffSelected", { name: r.name })) : r.on ? t("deck.mods.ariaOn", { name: r.name }) : t("deck.mods.ariaOff", { name: r.name })} />
          ))}
        </div>
      )}
      {selMenu && (
        <Dialog name="selected" title={t("deck.mods.selected", { count: picked.length })} onClose={() => setSelMenu(false)} backLabel={t("common.close")}>
          <div className="deck-list" role="group" aria-label={t("deck.mods.selectedAria")}>
            <Row id="sel-on" title={t("deck.mods.switchOn")} sub={selOn.left.length ? t("deck.mods.ofSkipped", { n: selOn.change.length, total: picked.length, skipped: selOn.left.length, why: selOn.left[0].why }) : t("deck.mods.of", { n: selOn.change.length, total: picked.length })} a={t("deck.mods.switchOn")}
              disabled={!loaded || !selOn.change.length} onClick={() => switchPicked(true)} />
            <Row id="sel-off" title={t("deck.mods.switchOff")} sub={selOff.left.length ? t("deck.mods.ofSkipped", { n: selOff.change.length, total: picked.length, skipped: selOff.left.length, why: selOff.left[0].why }) : t("deck.mods.of", { n: selOff.change.length, total: picked.length })} a={t("deck.mods.switchOff")}
              disabled={!loaded || !selOff.change.length} onClick={() => switchPicked(false)} />
            <Row id="sel-remove" title={t("deck.mods.removeEllipsis")} sub={running ? t("deck.shared.closeGameFirst") : picked.length > toRemove.length ? t("deck.mods.ofSkipped", { n: toRemove.length, total: picked.length, skipped: picked.length - toRemove.length, why: notRemovableText() }) : t("deck.mods.of", { n: toRemove.length, total: picked.length })} a={t("common.remove")}
              disabled={running || !toRemove.length} onClick={() => { setSelMenu(false); setRemovingMany(toRemove); }} />
            <Row id="sel-clear" title={t("deck.mods.clear")} sub={t("deck.mods.unselectAll", { count: picked.length })} a={t("deck.mods.clear")} onClick={() => { setSelection(gid, owner, NONE); setSelMenu(false); }} />
          </div>
        </Dialog>
      )}
      {removingMany && (
        <Dialog name="remove-many" title={t("deck.mods.removeManyTitle", { count: removingMany.length })} onClose={() => setRemovingMany(null)} wide
          buttons={<>
            <DeckButton onClick={() => setRemovingMany(null)}>{t("common.cancel")}</DeckButton>
            <DeckButton id="remove-many-confirm" primary disabled={!many.ready || running} onClick={async () => {
              const rows = removingMany;
              setRemovingMany(null);
              setFocus(null);
              try {
                const r = await removeMany(qc, rows, many.users, async () => { mods.edit.update((p) => withoutRows(p, rows)); return mods.edit.flush(); });
                setSelection(gid, owner, NONE);
                if (!r.left.length) toast.ok(t("deck.mods.removedMany", { count: r.removed.length }));
                else toast.info(r.removed.length ? t("deck.mods.removedLeft", { removed: r.removed.length, left: r.left.length }) : t("deck.mods.nothingRemoved"),
                  t("deck.mods.leftDetail", { count: r.left.length, lines: r.left.map((l) => `${l.name}: ${l.why}`).join(" ") }), { ms: 0 });
              } catch (e) { toast.error(e); }
            }}>{t("deck.mods.removeCount", { count: removingMany.length })}</DeckButton>
          </>}>
          <p className="deck-note">{!many.ready ? t("deck.mods.checkingUses") : many.error ? t("deck.mods.checkFailed", { error: many.error }) : t("deck.mods.filesLeave")}</p>
          <ul className="deck-remove-list deck-scroll" aria-label={t("deck.mods.toRemoveAria")}>
            {removingMany.map((r) => {
              const o = many.others.get(r.key) ?? [];
              return <li key={r.key}>{r.name}{o.length ? <span className="deck-dim"> {t("deck.mods.listsItToo", { count: o.length, names: o.join(", ") })}</span> : null}</li>;
            })}
          </ul>
        </Dialog>
      )}
      {removing && (
        <Dialog name="remove" title={t("deck.mods.removeTitle", { name: removing.row.name })} onClose={() => setRemoving(null)}
          buttons={<>
            <DeckButton onClick={() => setRemoving(null)}>{t("common.cancel")}</DeckButton>
            <DeckButton id="remove-confirm" primary disabled={!checked} onClick={async () => {
              const target = removing;
              setRemoving(null);
              setFocus(null);
              try {
                await removeMod(qc, target, check.users.data, async () => { mods.drop(target.row); return mods.edit.flush(); });
                toast.ok(t("deck.mods.removed", { name: target.row.name }));
              } catch (e) { toast.error(e); }
            }}>{t("common.remove")}</DeckButton>
          </>}>
          <p className="deck-note">{check.message}</p>
        </Dialog>
      )}
    </Screen>
  );
}
