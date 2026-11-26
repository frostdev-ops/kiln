// The main screen's list of every mod (shell/mods.ts): a switch, the name, the kind, the version, one short note when
// there is something to say, and a menu with what the profile tabs and the Library page offered for the mod (settings,
// configs, the version, its folder, remove). A mod found in the game folder is a row with its state and its fix, no
// switch. Above the list: the filter (with many mods), Notes (what the last activation said about mod folders, when it
// said something), Load order, Engines and Add mods. It scrolls inside the page.
import { SfSelfHostedNotice } from "../../components/SfSelfHostedNotice";
import { EmlRNotice } from "../../components/EmlRNotice";
import { ShroudlinkNotice } from "../../components/ShroudlinkNotice";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, MoreHorizontal } from "lucide-react";
import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from "react";
import { useSearchParams, Link } from "react-router-dom";
import * as api from "../../api/commands";
import type { AddReport, GameInstall, ModKind } from "../../api/commands";
import { qk, useNexusEnabled } from "../../api/hooks";
import { KIND_NAME, KIND_SHORT, KIND_TONE, SETTINGS_KINDS } from "../../api/kinds";
import { EngineSettingsModal } from "../../components/EngineSettings";
import { FolderNotesButton } from "../../components/FolderNotes";
import { RecentAddsButton } from "../../shell/importResult";
import { ConfigEditor } from "../../components/ConfigEditor";
import { cookAgain } from "../../shell/cook";
import { useAddMods } from "../../shell/drop";
import { changedFilesLabel, takeOver } from "../../shell/libraryChanged";
import { MOD_FILE_FILTERS, modFileWords } from "../../shell/modFiles";
import { bulkPlan, filterRows, kindsOf, type ModRow } from "../../shell/mods";
import { notifyAdd } from "../../shell/notices";
import { checkNow, noticeable, statusOf, UpdateBadge, UpdateSourceModal, useModUpdates } from "../../shell/modUpdates";
import { notRemovableText, removable, removeMany, removeMod, settingsNotes, useRemoveCheck, useRemoveMany, wholeMod, withoutRows, type RemoveTarget } from "../../shell/removeMod";
import { canReplaceDll, replacementLabel } from "../../shell/replaceDll";
import { otherVersions, rollBack, rollbackTargets } from "../../shell/rollback";
import { NONE, all as selectAll, ownerOf, pick as pickSelected, pruneSelection, range as selectRange, setSelection, toggle as toggleSelected, useOwnedSelection } from "../../shell/selection";
import type { useMods } from "../../shell/useMods";
import { withDropin } from "../profiles/dropins";
import { Badge, Button, ConfirmDanger, FlameToggle, Menu, Modal, Select, WellInput, pickPaths, runTask, toast, type MenuItem } from "../../ui";
import { useT } from "../../i18n";

/** More rows than this: the filter shows. */
const FILTER_FROM = 6;

type Mods = ReturnType<typeof useMods>;
/** How long the toast with a bulk action's Undo stays: longer than a plain toast's 8 s, since a player may first look
 *  at what changed (another page, the game folder notes) before deciding to undo many switches. */
const BULK_UNDO_MS = 20_000;
/** The mounted list (one at a time): what a bulk action's Undo acts through. */
let liveList: { mods: Mods } | null = null;

export function MainList({ game, mods, running }: { game: GameInstall; mods: Mods; running: boolean }) {
  const t = useT();
  const qc = useQueryClient();
  const nexus = useNexusEnabled();
  const addMods = useAddMods();
  const [params] = useSearchParams();
  const [text, setText] = useState(params.get("filter") ?? "");
  const [kind, setKind] = useState<ModKind | "">("");
  const [settings, setSettings] = useState<{ kind: ModKind; id: string; name: string } | null>(null);
  const [configs, setConfigs] = useState<string | null>(null);
  const [rm, setRm] = useState<RemoveTarget | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  // update notices (shell/modUpdates.tsx): words and a link per mod, shared with the Catalog page
  const updates = useModUpdates();
  const [checking, setChecking] = useState(false);
  const [source, setSource] = useState<{ kind: ModKind; id: string; name: string } | null>(null);
  const [replaceConfirm, setReplaceConfirm] = useState<{ message: string; run: () => Promise<void> } | null>(null);
  const pid = mods.profileId;
  // " (replaced DLL, 15 Nov)" after the version of a build a DLL replacement made
  const replacedNote = (r: ModRow) => {
    const label = replacementLabel(r.lib?.versions.find((v) => v.version === r.entry?.version && v.hash === r.entry?.hash));
    return label ? ` (${label})` : "";
  };
  const replaceDll = async (r: ModRow) => {
    if (!pid || !r.kind || !r.entry) return;
    try {
      const paths = await pickPaths({ multiple: false, filters: [{ name: t("mods.list.windowsDll"), extensions: ["dll"] }], title: t("mods.list.replaceDllTitle"), kind: "replaceDll" });
      if (!paths?.length || !(await mods.edit.flush())) return;
      const entry = { kind: r.kind, id: r.id, version: r.entry.version, hash: r.entry.hash };
      const run = async (confirmed: boolean) => {
        setBusy(r.key);
        try {
          const result = await api.libraryReplaceDll(pid, entry, paths[0], confirmed);
          await Promise.all([qc.invalidateQueries({ queryKey: ["library_list"] }), qc.invalidateQueries({ queryKey: qk.profile(pid) }),
            qc.invalidateQueries({ queryKey: ["profile_resolve"] }), qc.invalidateQueries({ queryKey: ["found_scan"] })]);
          toast.ok(t("mods.list.replacedDll", { name: r.name }), result.notices.join(" ") || undefined);
        } catch (e) {
          const err = e as { message?: string; detail?: { replaceDllConfirmation?: boolean } };
          if (!confirmed && err.detail?.replaceDllConfirmation) setReplaceConfirm({ message: err.message ?? t("mods.list.confirmReplacement"), run: () => run(true) });
          else toast.error(e, t("mods.list.replaceDllFailed", { name: r.name }));
        } finally { setBusy(null); }
      };
      await run(false);
    } catch (e) { toast.error(e, t("mods.list.openDllFailed")); }
  };
  const cfg = useQuery({ queryKey: qk.configs({ profile: pid ?? "" }), queryFn: () => api.configList({ profile: pid! }), enabled: !!pid });
  const withConfig = new Set((cfg.data ?? []).map((f) => f.mod).filter(Boolean) as string[]);
  // the mods the launcher cooks for this game (models, animations, sounds made from the game): "Cook again" in their menu
  const cook = useQuery({ queryKey: ["cook_plan", game.id], queryFn: () => api.cookPlan(game.id) });
  const cookable = new Map((cook.data ?? []).map((j) => [j.id, j]));
  const kinds = kindsOf(mods.rows);
  const many = !!text || mods.rows.length > FILTER_FROM;
  const shown = filterRows(mods.rows, many ? text : "", many ? kind : "");
  const loaded = !!mods.profile;
  const check = useRemoveCheck(rm, pid);

  useEffect(() => { setText(params.get("filter") ?? ""); }, [params]);
  // All on / All off: the rows shown now (the filter narrows them), one save, Undo in the toast
  const filtered = shown.length !== mods.rows.length;
  const planOn = bulkPlan(shown, true);
  const planOff = bulkPlan(shown, false);
  const bulkTitle = (on: boolean) => {
    const p = on ? planOn : planOff;
    if (!loaded) return t("mods.list.loadProfileFirst");
    const n = p.change.length;
    const total = shown.length;
    const what = n
      ? on ? (filtered ? t("mods.list.bulkOnFiltered", { count: n, total }) : t("mods.list.bulkOnAll", { count: n, total }))
        : (filtered ? t("mods.list.bulkOffFiltered", { count: n, total }) : t("mods.list.bulkOffAll", { count: n, total }))
      : on ? (filtered ? t("mods.list.everyOnFiltered") : t("mods.list.everyOnAll"))
        : (filtered ? t("mods.list.everyOffFiltered") : t("mods.list.everyOffAll"));
    return p.left.length ? `${what} ${t("mods.list.cantSwitch", { count: p.left.length, why: [...new Set(p.left.map((l) => l.why))].join("; ") })}` : what;
  };
  // the toast's Undo runs on the list as it is when pressed (its profile, its save queue), never on this render's copy
  const here = useRef({ mods });
  here.current.mods = mods;
  useEffect(() => {
    const me = here.current;
    liveList = me;
    return () => { if (liveList === me) liveList = null; };
  }, []);
  /** All on / All off, and the selection's Switch on / Switch off: the same plan, one save, the same Undo. */
  const switchRows = (rows: ModRow[], on: boolean) => {
    const r = mods.setAll(rows, on);
    if (!r?.undo) return;
    const { plan, undo } = r;
    const n = plan.change.length;
    const left = plan.left.length ? t("mods.list.leftOut", { count: plan.left.length, why: [...new Set(plan.left.map((l) => l.why))].join("; ") }) : undefined;
    toast.ok(on ? t("mods.list.switchedOn", { count: n }) : t("mods.list.switchedOff", { count: n }), left, { actions: [{ label: t("common.undo"), onClick: async () => {
      const res = liveList ? await liveList.mods.undoAll(undo) : { ok: false as const, why: t("mods.list.listNotOpen") };
      if (!res.ok) toast.info(t("mods.list.cantUndo"), t("mods.list.cantUndoDetail", { why: `${res.why[0].toUpperCase()}${res.why.slice(1)}` }));
      else if (res.kept) toast.info(t("mods.list.undoKept"), t("mods.list.undoKeptDetail", { count: res.kept }));
    } }], ms: BULK_UNDO_MS });
  };
  const setAll = (on: boolean) => switchRows(shown, on);

  // ---- the selection (shell/selection.ts): one loaded profile's in one game, kept outside the list, so a hidden list
  // (a start's bar) gets it back; another profile loaded meanwhile (or one made again under the same id) has none ----
  const owner = ownerOf(pid, mods.profile);
  const { sel, note: selNote } = useOwnedSelection(game.id, owner);
  const order = shown.map((r) => r.key);
  const orderKey = order.join("\n");
  const read = !mods.loading && mods.rows.length > 0; // not read yet (or between two reads): nothing is let go
  useEffect(() => {
    pruneSelection(game.id, owner, orderKey ? orderKey.split("\n") : [], read, true);
  }, [orderKey, read, game.id, owner]);
  const picked = pickSelected(sel, shown);
  const selecting = picked.length > 0;
  const choose = (key: string, shift: boolean) => setSelection(game.id, owner, shift ? selectRange(sel, key, order) : toggleSelected(sel, key));
  const clearSel = () => setSelection(game.id, owner, NONE);
  // the list's own shortcuts: only for a key pressed on the list itself (its rows and their controls), never for one
  // that bubbles here through React from a portalled menu or dialog, nor in the filter or another field; an event they
  // do not handle goes on untouched
  const listEl = useRef<HTMLUListElement>(null);
  const keys = (e: KeyboardEvent) => {
    const el = e.target as HTMLElement;
    if (!listEl.current?.contains(el) || el.closest('[role="menu"], [role="dialog"]')) return;
    if (/^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName) || el.isContentEditable) return;
    if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.key === "a" || e.key === "A") && order.length) { e.preventDefault(); setSelection(game.id, owner, selectAll(order)); }
    else if (e.key === "Escape" && !e.ctrlKey && !e.metaKey && !e.altKey && selecting) { e.preventDefault(); clearSel(); }
  };
  const selOn = bulkPlan(picked, true);
  const selOff = bulkPlan(picked, false);
  const toRemove = picked.filter(removable);
  const skipRemove = picked.length - toRemove.length;
  const [removing, setRemoving] = useState<ModRow[] | null>(null);
  const check2 = useRemoveMany(removing ?? [], pid, !!removing);
  // "Remove its settings too": unchecked each time the dialog opens (the settings stay by default)
  const [rmSettings, setRmSettings] = useState(false);
  useEffect(() => { setRmSettings(false); }, [rm, removing]);
  // Profile-local controls end at the identity change, never at a later data refresh.
  useLayoutEffect(() => {
    setText(params.get("filter") ?? ""); setKind("");
    setSettings(null); setConfigs(null); setRm(null); setRemoving(null);
    setSource(null); setReplaceConfirm(null);
  }, [game.id, pid]);
  const skipLine = (p: ReturnType<typeof bulkPlan>) => (p.left.length ? ` ${t("mods.list.skipped", { count: p.left.length, why: [...new Set(p.left.map((l) => l.why))].join("; ") })}` : "");
  const selTitle = (on: boolean) => {
    const p = on ? selOn : selOff;
    if (!loaded) return t("mods.list.loadProfileFirst");
    const n = p.change.length;
    const total = picked.length;
    return `${n ? (on ? t("mods.list.selOn", { count: n, total }) : t("mods.list.selOff", { count: n, total })) : (on ? t("mods.list.selEveryOn") : t("mods.list.selEveryOff"))}${skipLine(p)}`;
  };
  const removeTitle = running ? t("mods.list.closeGameFirst") : `${t("mods.list.selRemove", { count: toRemove.length, total: picked.length })}${skipRemove ? ` ${t("mods.list.skipped", { count: skipRemove, why: notRemovableText() })}` : ""}`;

  const pick = async () => {
    try {
      const paths = await pickPaths({ multiple: true, filters: MOD_FILE_FILTERS, title: t("mods.list.addMods"), kind: "addMods" });
      if (paths?.length) await addMods(paths, game);
    } catch (e) { toast.error(e, t("mods.list.openFilesFailed")); }
  };

  const fixFound = async (r: ModRow) => {
    const f = r.found;
    if (!f || !pid) return;
    if (f.fix === "dropins" && f.kind) { const k = f.kind; mods.edit.update((p) => withDropin(p, k, true)); return; }
    if (f.fix !== "add") return;
    setBusy(r.key);
    try {
      const rep = await runTask<AddReport>(() => api.foundAdd(game.id, pid, f.path), { quiet: true });
      const skipped = rep.import.skipped[0];
      notifyAdd(rep);
      if (!rep.added.length && !rep.already.length && skipped) toast.error({ code: "refused", message: skipped.reason }, t("mods.list.libraryRefused", { name: f.name }));
      else toast.ok(t("mods.list.isOn", { name: f.name }), rep.applied ? t("mods.list.writtenToGame") : undefined);
    } catch (e) {
      toast.error(e, t("mods.list.addFailed", { name: f.name }));
    } finally {
      setBusy(null);
      void qc.invalidateQueries({ queryKey: ["found_scan"] });
      void qc.invalidateQueries({ queryKey: qk.profile(pid) });
      void qc.invalidateQueries({ queryKey: ["library_list"] });
    }
  };

  /** "Use the changed files" of a mod changed in its Library folder. */
  const takeChanged = async (r: ModRow) => {
    if (!r.changed) return;
    setBusy(r.key);
    try { await takeOver(qc, r.changed, r.name); } finally { setBusy(null); }
  };

  const menu = (r: ModRow): MenuItem[] => {
    if (r.found) {
      const f = r.found;
      return [
        ...(f.fix === "dropins" && f.kind ? [{ label: t("mods.list.loadDropins", { kind: KIND_SHORT[f.kind] }), onSelect: () => void fixFound(r) }] : []),
        ...(f.fix === "add" ? [{ label: t("mods.list.addToMyMods"), onSelect: () => void fixFound(r) }] : []),
        { label: t("mods.list.openFolder"), onSelect: () => api.appOpenPath(f.path, game.path).catch((e) => toast.error(e)) },
      ];
    }
    const versions = r.lib?.versions ?? [];
    const cur = r.entry?.version ?? versions[0]?.version;
    const sw: MenuItem[] = loaded && (r.entry || r.lib) ? [{ label: r.on ? t("mods.list.switchOff") : t("mods.list.switchOn"), onSelect: () => mods.toggle(r, !r.on) }] : [];
    const folder = versions.find((v) => v.version === cur)?.folder ?? versions[0]?.folder;
    return [
      ...(r.changed ? [{ label: changedFilesLabel(), disabled: running || !!busy, onSelect: () => void takeChanged(r) }] : []),
      ...sw,
      ...(canReplaceDll(r.kind, !!r.lib, !!r.entry && !!pid) ? [{ label: t("mods.list.replaceDllMenu"), disabled: !!busy || !api.isTauri(), onSelect: () => void replaceDll(r) }] : []),
      ...(r.kind && r.entry && SETTINGS_KINDS.includes(r.kind) ? [{ label: t("mods.list.settings"), onSelect: () => setSettings({ kind: r.kind!, id: r.id, name: r.name }) }] : []),
      ...(withConfig.has(r.id) ? [{ label: t("mods.list.configs"), onSelect: () => setConfigs(r.id) }] : []),
      ...(r.kind === "kiln" && r.on && cookable.has(r.id) ? [{ label: t("mods.list.cookAgain"), disabled: running,
        onSelect: () => void cookAgain(game.id, r.id, r.name).then(() => qc.invalidateQueries({ queryKey: ["cook_plan"] })) }] : []),
      ...otherVersions(r.lib, cur).map((v) => ({ label: t("mods.list.useVersion", { version: v.version }), disabled: !loaded, onSelect: () => mods.setVersion(r, v.version, v.hash) })),
      // an earlier version an update kept, with its settings (shell/rollback.ts)
      ...(r.kind && r.entry && pid ? rollbackTargets(r.lib, r.entry, pid).map((v) => ({ label: v.version === r.entry!.version ? t("mods.list.rollBackEarlier", { version: v.version }) : t("mods.list.rollBack", { version: v.version }), disabled: !!busy,
        onSelect: () => void rollBack(qc, game.id, pid, r.kind!, r.id, r.name, v, mods.edit.flush) })) : []),
      ...(folder ? [{ label: t("mods.list.openFolder"), onSelect: () => api.appOpenPath(folder).catch((e) => toast.error(e)) }] : []),
      ...(r.kind && r.lib ? [{ label: t("mods.list.updates"), onSelect: () => setSource({ kind: r.kind!, id: r.id, name: r.name }) }] : []),
      ...(versions.length > 1 ? versions.filter((v) => v.version !== r.entry?.version).map((v) => ({ label: t("mods.list.removeVersion", { version: v.version }), danger: true, disabled: running, onSelect: () => setRm({ row: r, version: v.version }) })) : []),
      { label: t("common.remove"), danger: true, disabled: running, onSelect: () => setRm({ row: r, version: null }) },
    ];
  };

  const foundStart = shown.findIndex((r) => r.found);
  return (
    <div className={"k-mods" + (mods.rows.length ? "" : " empty") + (selecting ? " selecting" : "")} onKeyDown={keys}>
      <SfSelfHostedNotice profile={mods.profile} found={mods.found.data} />
      <EmlRNotice found={mods.found.data} />
      <ShroudlinkNotice game={game} mods={mods} />
      <div className="k-mods-bar">
        {many && (
          <>
            <WellInput label={t("mods.list.filter")} compact value={text} onChange={(e) => setText(e.target.value)} onClear={() => setText("")} wellStyle={{ flex: "1 1 130px", maxWidth: 250 }}
              note={text || kind ? `${shown.length}/${mods.rows.length}` : undefined} />
            {kinds.length > 1 && (
              <Select aria-label={t("mods.list.kind")} className="k-mods-kind" value={kind} onChange={(v) => setKind(v as ModKind | "")}
                options={[{ value: "", label: t("mods.list.everyKind") }, ...kinds.map((k) => ({ value: k, label: KIND_SHORT[k] }))]} />
            )}
          </>
        )}
        {mods.rows.length > 0 && !mods.error && (
          <>
            <button type="button" className="k-link" disabled={!loaded || !planOn.change.length} title={bulkTitle(true)} onClick={() => setAll(true)}>
              {filtered ? t("mods.list.shownOn", { count: planOn.change.length }) : t("mods.list.allOn", { count: planOn.change.length })}
            </button>
            <button type="button" className="k-link" disabled={!loaded || !planOff.change.length} title={bulkTitle(false)} onClick={() => setAll(false)}>
              {filtered ? t("mods.list.shownOff", { count: planOff.change.length }) : t("mods.list.allOff", { count: planOff.change.length })}
            </button>
          </>
        )}
        <span className="k-spacer" />
        {mods.rows.length > 0 && <button type="button" className="k-link" disabled={checking} title={t("mods.list.checkUpdatesHint")}
          onClick={() => { setChecking(true); void checkNow(qc).finally(() => setChecking(false)); }}>{checking ? t("mods.list.checking") : t("mods.list.checkUpdates")}</button>}
        <FolderNotesButton gameId={game.id} />
        <RecentAddsButton />
        <Link className="k-link" to="/mods/order">{t("mods.tabs.order")}</Link>
        <Link className="k-link" to="/mods/engines">{t("mods.tabs.engines")}</Link>
        {nexus && <Link className="k-link" to="/catalog">{t("mods.list.catalog")}</Link>}
        <button type="button" className="k-link" disabled={!api.isTauri()} title={api.isTauri() ? t("mods.list.addModsHint") : t("mods.list.needsDesktop")} onClick={() => void pick()}>{t("mods.list.addMods")}</button>
      </div>
      <Modal open={!!replaceConfirm} title={t("mods.list.replaceDllConfirm")} onClose={() => setReplaceConfirm(null)}
        footer={<><Button variant="ghost" onClick={() => setReplaceConfirm(null)}>{t("common.cancel")}</Button><Button onClick={() => {
          const pending = replaceConfirm; setReplaceConfirm(null); void pending?.run();
        }}>{t("mods.list.replaceDllTitle")}</Button></>}><p>{replaceConfirm?.message}</p></Modal>
      {selecting && (
        <div className="k-mods-selbar" role="toolbar" aria-label={t("mods.list.selectedMods")}>
          <span className="k-mods-selbar-count" aria-live="polite">{t("mods.list.selectedCount", { count: picked.length })}</span>
          <button type="button" className="k-link" disabled={!loaded || !selOn.change.length} title={selTitle(true)} onClick={() => switchRows(picked, true)}>{t("mods.list.switchOn")}</button>
          <button type="button" className="k-link" disabled={!loaded || !selOff.change.length} title={selTitle(false)} onClick={() => switchRows(picked, false)}>{t("mods.list.switchOff")}</button>
          <button type="button" className="k-link k-tone-err" disabled={running || !toRemove.length} title={removeTitle} onClick={() => setRemoving(toRemove)}>{t("mods.list.removeMenu")}</button>
          <button type="button" className="k-link" onClick={clearSel} title="Esc">{t("mods.list.clear")}</button>
          {selNote && <span className="k-hint">{selNote}</span>}
        </div>
      )}
      {mods.error ? (
        <div className="k-mods-empty" role="alert">
          <p>{t("mods.list.notReadable")}</p>
          <p className="k-hint">{t("mods.list.notReadableHint")}</p>
        </div>
      ) : !mods.rows.length ? (
        <div className="k-mods-empty">
          <p>{t("mods.list.firstMod")}</p>
          <p className="k-hint">{nexus ? t("mods.list.firstModHintCatalog", { files: modFileWords() }) : t("mods.list.firstModHint", { files: modFileWords() })}</p>
        </div>
      ) : (
        <ul ref={listEl} className="k-mods-list" aria-label={t("mods.list.mods")}>
          {shown.map((r, i) => (
            <li key={r.key} className={"k-mod" + (r.on ? " on" : "") + (r.found ? " found" : "") + (i === foundStart && i > 0 ? " first-found" : "") + (sel.keys.includes(r.key) ? " picked" : "")} data-kind={r.kind ?? undefined}>
              <button type="button" role="checkbox" className="k-mod-check" aria-checked={sel.keys.includes(r.key)} aria-label={t("mods.list.select", { name: r.name })}
                title={t("mods.list.selectHint")}
                onClick={(e) => choose(r.key, e.shiftKey)}
                onKeyDown={(e) => { if (e.key === " " || e.key === "Enter") { e.preventDefault(); choose(r.key, e.shiftKey); } }}>
                {sel.keys.includes(r.key) && <Check aria-hidden strokeWidth={3} />}
              </button>
              {r.found ? <span className="k-mod-place" aria-hidden title={t("mods.list.inGameFolder")} /> : (
                <FlameToggle asSwitch on={r.on} label={r.name} disabled={!loaded} title={loaded ? undefined : t("mods.list.loadProfileFirst")} onChange={(on) => mods.toggle(r, on)} />
              )}
              <span className="k-mod-text">
                <span className="k-mod-name" title={r.name !== r.id ? t("mods.list.nameWithId", { name: r.name, id: r.id }) : r.name}>{r.name}</span>
                {(r.found || r.note || (r.lib && noticeable(statusOf(updates.data, r.kind, r.id), r.entry?.version))) && (
                  <span className="k-mod-note">
                    {r.lib && <UpdateBadge status={statusOf(updates.data, r.kind, r.id)} installed={r.entry?.version} />}
                    {r.found && <span className="k-mod-where">{t("mods.order.gameFolder")}</span>}
                    {r.note && <span className={`k-tone-${r.note.tone}`} title={r.note.title}>{r.note.text}</span>}
                    {r.note?.link && (
                      <button type="button" className="k-link" title={r.note.link.title}
                        onClick={() => { const url = r.note?.link?.url; if (url) api.appOpenUrl(url).catch((e) => toast.error(e)); }}>
                        {r.note.link.label}
                      </button>
                    )}
                    {r.changed && (
                      <button type="button" className="k-link" disabled={!!busy || running} onClick={() => void takeChanged(r)}
                        aria-label={t("mods.list.useChangedOf", { action: changedFilesLabel(), name: r.name })} title={t("mods.list.useChangedHint")}>
                        {busy === r.key ? t("mods.list.takingOver") : changedFilesLabel()}
                      </button>
                    )}
                    {r.found?.fix && (
                      <button type="button" className="k-link" disabled={!!busy || !loaded} onClick={() => void fixFound(r)}
                        aria-label={r.found.fix === "dropins" && r.found.kind ? t("mods.list.loadDropins", { kind: KIND_SHORT[r.found.kind] }) : t("mods.list.addNamed", { name: r.name })}
                        title={r.found.fix === "dropins" && r.found.kind ? t("mods.list.loadDropinsHint", { kind: KIND_SHORT[r.found.kind] }) : t("mods.list.addHint")}>
                        {busy === r.key ? t("mods.list.adding") : r.found.fix === "dropins" ? t("mods.list.loadThem") : t("mods.list.addIt")}
                      </button>
                    )}
                  </span>
                )}
              </span>
              <span className="k-mod-kind">{r.kind && <Badge tone={KIND_TONE[r.kind]} title={KIND_NAME[r.kind]}>{KIND_SHORT[r.kind]}</Badge>}</span>
              <span className="k-mono k-mod-ver" title={(r.lib?.versions.length ?? 0) > 1 ? t("mods.list.versionsInLibrary", { count: r.lib!.versions.length }) : r.version ?? undefined}>{r.version ?? ""}{replacedNote(r)}</span>
              <Menu key={pid} label={t("mods.list.moreFor", { name: r.name })} icon={<MoreHorizontal />} size="s" items={menu(r)} />
            </li>
          ))}
          {!shown.length && <li className="k-mods-none">{t("mods.list.noMatch")}</li>}
        </ul>
      )}
      <UpdateSourceModal target={source} onClose={() => setSource(null)} />
      <EngineSettingsModal target={settings} onClose={() => setSettings(null)} profileId={pid ?? ""} gameId={game.id} />
      <Modal open={!!configs} onClose={() => setConfigs(null)} title={t("mods.list.configs")} width={960} footer={<Button onClick={() => setConfigs(null)}>{t("common.done")}</Button>}>
        {configs && pid && <ConfigEditor target={{ profile: pid }} initial={configs} />}
      </Modal>
      <ConfirmDanger open={!!removing} onClose={() => setRemoving(null)} relaxable word={String(removing?.length ?? 0)} action={t("mods.list.removeCount", { count: removing?.length ?? 0 })}
        title={removing ? t("mods.list.removeManyTitle", { count: removing.length }) : ""} disabled={!check2.ready || running}
        message={removing && (
          <>
            <span>{!check2.ready ? t("mods.list.checkingUsers") : check2.error ? t("mods.list.checkUsersFailed", { error: check2.error }) : t("mods.list.filesLeave")}</span>
            <ul className="k-remove-list" aria-label={t("mods.list.modsToRemove")}>
              {removing.map((r) => {
                const o = check2.others.get(r.key) ?? [];
                return (
                  <li key={r.key}>{r.name}{!r.lib ? <span className="k-muted"> · {t("mods.list.filesGone")}</span>
                    : o.length ? <span className="k-muted"> · {t("mods.list.alsoListed", { count: o.length, names: o.join(", ") })}</span> : null}</li>
                );
              })}
            </ul>
            <SettingsCheck many checked={rmSettings} onChange={setRmSettings} />
          </>
        )}
        onConfirm={async () => {
          const rows = removing!;
          try {
            const r = await removeMany(qc, rows, check2.users, async () => { mods.edit.update((p) => withoutRows(p, rows)); return mods.edit.flush(); }, rmSettings);
            clearSel();
            if (r.settingsNotes.length) toast.info(t("shell.removeMod.settingsLeft"), r.settingsNotes.join(" "), { ms: 0 });
            if (!r.left.length) toast.ok(t("mods.list.removedMany", { count: r.removed.length }));
            else toast.info(r.removed.length ? t("mods.list.removedSome", { removed: r.removed.length, left: r.left.length }) : t("mods.list.nothingRemoved"),
              t("mods.list.leftInLibrary", { count: r.left.length, list: r.left.map((l) => t("mods.list.leftItem", { name: l.name, why: l.why })).join(" ") }), { ms: 0 });
          } catch (e) { toast.error(e); throw e; }
        }} />
      <ConfirmDanger open={!!rm} onClose={() => setRm(null)} relaxable word={rm?.row.id ?? ""} action={t("common.remove")}
        title={rm ? (rm.version ? t("mods.list.removeVersionTitle", { name: rm.row.name, version: rm.version }) : t("mods.list.removeTitle", { name: rm.row.name })) : ""} disabled={!check.ready || check.users.isFetching}
        message={<>{check.message}{wholeMod(rm) && <SettingsCheck checked={rmSettings} onChange={setRmSettings} />}</>}
        onConfirm={async () => {
          const target = rm!;
          try {
            const s = await removeMod(qc, target, check.users.data, async () => { mods.drop(target.row); return mods.edit.flush(); }, rmSettings);
            toast.ok(target.version ? t("mods.list.removedVersion", { name: target.row.name, version: target.version }) : t("mods.list.removed", { name: target.row.name }));
            const notes = settingsNotes(s);
            if (notes) toast.info(t("shell.removeMod.settingsLeft"), notes, { ms: 0 });
          } catch (e) { toast.error(e); void check.users.refetch(); throw e; }
        }} />
    </div>
  );
}

/** The remove dialog's "Remove its settings too" (unchecked: the settings stay, and come back with the mod). */
function SettingsCheck({ checked, onChange, many }: { checked: boolean; onChange: (on: boolean) => void; many?: boolean }) {
  const t = useT();
  return (
    <label className="k-remove-settings">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span>
        {t(many ? "shell.removeMod.settingsTooMany" : "shell.removeMod.settingsToo")}
        <small>{t(many ? "shell.removeMod.settingsTooManyHint" : "shell.removeMod.settingsTooHint")}</small>
      </span>
    </label>
  );
}
