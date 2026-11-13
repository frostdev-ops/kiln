// The main screen's list of every mod (shell/mods.ts): a switch, the name, the kind, the version, one short note when
// there is something to say, and a menu with what the profile tabs and the Library page offered for the mod (settings,
// configs, the version, its folder, remove). A mod found in the game folder is a row with its state and its fix, no
// switch. Above the list: the filter (with many mods), Notes (what the last activation said about mod folders, when it
// said something), Load order, Engines and Add mods. It scrolls inside the page.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, MoreHorizontal } from "lucide-react";
import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { Link } from "react-router-dom";
import * as api from "../../api/commands";
import type { AddReport, GameInstall, ModKind } from "../../api/commands";
import { qk, useNexusEnabled } from "../../api/hooks";
import { KIND_NAME, KIND_SHORT, KIND_TONE, SETTINGS_KINDS } from "../../api/kinds";
import { EngineSettingsModal } from "../../components/EngineSettings";
import { FolderNotesButton } from "../../components/FolderNotes";
import { ConfigEditor } from "../../components/ConfigEditor";
import { cookAgain } from "../../shell/cook";
import { useAddMods } from "../../shell/drop";
import { takeOver, USE_CHANGED } from "../../shell/libraryChanged";
import { MOD_FILE_FILTERS, MOD_FILE_WORDS } from "../../shell/modFiles";
import { bulkPlan, filterRows, kindsOf, type ModRow } from "../../shell/mods";
import { notifyAdd } from "../../shell/notices";
import { NOT_REMOVABLE, removable, removeMany, removeMod, useRemoveCheck, useRemoveMany, withoutRows, type RemoveTarget } from "../../shell/removeMod";
import { NONE, all as selectAll, ownerOf, pick as pickSelected, pruneSelection, range as selectRange, setSelection, toggle as toggleSelected, useOwnedSelection } from "../../shell/selection";
import type { useMods } from "../../shell/useMods";
import { withDropin } from "../profiles/dropins";
import { Badge, Button, ConfirmDanger, FlameToggle, Menu, Modal, Select, WellInput, pickPaths, runTask, toast, type MenuItem } from "../../ui";

/** More rows than this: the filter shows. */
const FILTER_FROM = 6;

type Mods = ReturnType<typeof useMods>;
/** How long the toast with a bulk action's Undo stays: longer than a plain toast's 8 s, since a player may first look
 *  at what changed (another page, the game folder notes) before deciding to undo many switches. */
const BULK_UNDO_MS = 20_000;
/** The mounted list (one at a time): what a bulk action's Undo acts through. */
let liveList: { mods: Mods } | null = null;

export function MainList({ game, mods, running }: { game: GameInstall; mods: Mods; running: boolean }) {
  const qc = useQueryClient();
  const nexus = useNexusEnabled();
  const addMods = useAddMods();
  const [text, setText] = useState("");
  const [kind, setKind] = useState<ModKind | "">("");
  const [settings, setSettings] = useState<{ kind: ModKind; id: string; name: string } | null>(null);
  const [configs, setConfigs] = useState<string | null>(null);
  const [rm, setRm] = useState<RemoveTarget | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const pid = mods.profileId;
  const cfg = useQuery({ queryKey: qk.configs({ profile: pid ?? "" }), queryFn: () => api.configList({ profile: pid! }), enabled: !!pid });
  const withConfig = new Set((cfg.data ?? []).map((f) => f.mod).filter(Boolean) as string[]);
  // the mods the launcher cooks for this game (models, animations, sounds made from the game): "Cook again" in their menu
  const cook = useQuery({ queryKey: ["cook_plan", game.id], queryFn: () => api.cookPlan(game.id) });
  const cookable = new Map((cook.data ?? []).map((j) => [j.id, j]));
  const kinds = kindsOf(mods.rows);
  const many = mods.rows.length > FILTER_FROM;
  const shown = filterRows(mods.rows, many ? text : "", many ? kind : "");
  const loaded = !!mods.profile;
  const check = useRemoveCheck(rm, pid);

  // All on / All off: the rows shown now (the filter narrows them), one save, Undo in the toast
  const filtered = shown.length !== mods.rows.length;
  const planOn = bulkPlan(shown, true);
  const planOff = bulkPlan(shown, false);
  const bulkTitle = (on: boolean) => {
    const p = on ? planOn : planOff;
    if (!loaded) return "Load a profile in Settings to switch mods";
    const n = p.change.length;
    const what = n ? `Switches ${on ? "on" : "off"} ${n} mod${n === 1 ? "" : "s"} of the ${shown.length}${filtered ? " the filter shows" : " in the list"}` : `Every mod ${filtered ? "the filter shows" : "in the list"} is ${on ? "on" : "off"} already`;
    return p.left.length ? `${what}. ${p.left.length} can't be switched: ${[...new Set(p.left.map((l) => l.why))].join("; ")}.` : `${what}.`;
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
    const left = plan.left.length ? `${plan.left.length} left out: ${[...new Set(plan.left.map((l) => l.why))].join("; ")}.` : undefined;
    toast.ok(`Switched ${n} mod${n === 1 ? "" : "s"} ${on ? "on" : "off"}`, left, { actions: [{ label: "Undo", onClick: async () => {
      const res = liveList ? await liveList.mods.undoAll(undo) : { ok: false as const, why: "the mods list is not open" };
      if (!res.ok) toast.info("Can't undo that", `${res.why[0].toUpperCase()}${res.why.slice(1)}. Switch the mods in the list instead.`);
      else if (res.kept) toast.info("Undo kept later changes", `${res.kept} mod${res.kept === 1 ? " was" : "s were"} switched since.`);
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
    const t = e.target as HTMLElement;
    if (!listEl.current?.contains(t) || t.closest('[role="menu"], [role="dialog"]')) return;
    if (/^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName) || t.isContentEditable) return;
    if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.key === "a" || e.key === "A") && order.length) { e.preventDefault(); setSelection(game.id, owner, selectAll(order)); }
    else if (e.key === "Escape" && !e.ctrlKey && !e.metaKey && !e.altKey && selecting) { e.preventDefault(); clearSel(); }
  };
  const selOn = bulkPlan(picked, true);
  const selOff = bulkPlan(picked, false);
  const toRemove = picked.filter(removable);
  const skipRemove = picked.length - toRemove.length;
  const [removing, setRemoving] = useState<ModRow[] | null>(null);
  const check2 = useRemoveMany(removing ?? [], pid, !!removing);
  const skipLine = (p: ReturnType<typeof bulkPlan>) => (p.left.length ? ` ${p.left.length} skipped: ${[...new Set(p.left.map((l) => l.why))].join("; ")}.` : "");
  const selTitle = (on: boolean) => {
    const p = on ? selOn : selOff;
    if (!loaded) return "Load a profile in Settings to switch mods";
    return `${p.change.length ? `Switches ${on ? "on" : "off"} ${p.change.length} of the ${picked.length} selected.` : `Every selected mod that has a switch is ${on ? "on" : "off"} already.`}${skipLine(p)}`;
  };
  const removeTitle = running ? "Close the game first" : `Removes ${toRemove.length} of the ${picked.length} selected.${skipRemove ? ` ${skipRemove} skipped: ${NOT_REMOVABLE}.` : ""}`;

  const pick = async () => {
    try {
      const paths = await pickPaths({ multiple: true, filters: MOD_FILE_FILTERS, title: "Add mods" });
      if (paths?.length) await addMods(paths, game);
    } catch (e) { toast.error(e, "Could not open files"); }
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
      if (!rep.added.length && !rep.already.length && skipped) toast.error({ code: "refused", message: skipped.reason }, `The Library did not take ${f.name}`);
      else toast.ok(`${f.name} is on`, rep.applied ? "Written to the game." : undefined);
    } catch (e) {
      toast.error(e, `Could not add ${f.name}`);
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
        ...(f.fix === "dropins" && f.kind ? [{ label: `Load the game-folder ${KIND_SHORT[f.kind]} mods`, onSelect: () => void fixFound(r) }] : []),
        ...(f.fix === "add" ? [{ label: "Add to my mods", onSelect: () => void fixFound(r) }] : []),
        { label: "Open folder", onSelect: () => api.appOpenPath(`${game.path}/${f.path}`).catch((e) => toast.error(e)) },
      ];
    }
    const versions = r.lib?.versions ?? [];
    const cur = r.entry?.version ?? versions[0]?.version;
    const sw: MenuItem[] = loaded && (r.entry || r.lib) ? [{ label: r.on ? "Switch off" : "Switch on", onSelect: () => mods.toggle(r, !r.on) }] : [];
    const folder = versions.find((v) => v.version === cur)?.folder ?? versions[0]?.folder;
    return [
      ...(r.changed ? [{ label: USE_CHANGED, disabled: running || !!busy, onSelect: () => void takeChanged(r) }] : []),
      ...sw,
      ...(r.kind && r.entry && SETTINGS_KINDS.includes(r.kind) ? [{ label: "Settings", onSelect: () => setSettings({ kind: r.kind!, id: r.id, name: r.name }) }] : []),
      ...(withConfig.has(r.id) ? [{ label: "Configs", onSelect: () => setConfigs(r.id) }] : []),
      ...(r.kind === "kiln" && r.on && cookable.has(r.id) ? [{ label: "Cook again", disabled: running,
        onSelect: () => void cookAgain(game.id, r.id, r.name).then(() => qc.invalidateQueries({ queryKey: ["cook_plan"] })) }] : []),
      ...versions.filter((v) => v.version !== cur).map((v) => ({ label: `Use ${v.version}`, disabled: !loaded, onSelect: () => mods.setVersion(r, v.version, v.hash) })),
      ...(folder ? [{ label: "Open folder", onSelect: () => api.appOpenPath(folder).catch((e) => toast.error(e)) }] : []),
      ...(versions.length > 1 ? versions.filter((v) => v.version !== r.entry?.version).map((v) => ({ label: `Remove ${v.version}`, danger: true, disabled: running, onSelect: () => setRm({ row: r, version: v.version }) })) : []),
      { label: "Remove", danger: true, disabled: running, onSelect: () => setRm({ row: r, version: null }) },
    ];
  };

  const foundStart = shown.findIndex((r) => r.found);
  return (
    <div className={"k-mods" + (mods.rows.length ? "" : " empty") + (selecting ? " selecting" : "")} onKeyDown={keys}>
      <div className="k-mods-bar">
        {many && (
          <>
            <WellInput label="Filter" compact value={text} onChange={(e) => setText(e.target.value)} wellStyle={{ flex: "1 1 130px", maxWidth: 250 }}
              note={text || kind ? `${shown.length}/${mods.rows.length}` : undefined} />
            {kinds.length > 1 && (
              <Select aria-label="Kind" className="k-mods-kind" value={kind} onChange={(v) => setKind(v as ModKind | "")}
                options={[{ value: "", label: "Every kind" }, ...kinds.map((k) => ({ value: k, label: KIND_SHORT[k] }))]} />
            )}
          </>
        )}
        {mods.rows.length > 0 && !mods.error && (
          <>
            <button type="button" className="k-link" disabled={!loaded || !planOn.change.length} title={bulkTitle(true)} onClick={() => setAll(true)}>
              {filtered ? "Shown" : "All"} on ({planOn.change.length})
            </button>
            <button type="button" className="k-link" disabled={!loaded || !planOff.change.length} title={bulkTitle(false)} onClick={() => setAll(false)}>
              {filtered ? "Shown" : "All"} off ({planOff.change.length})
            </button>
          </>
        )}
        <span className="k-spacer" />
        <FolderNotesButton gameId={game.id} />
        <Link className="k-link" to="/mods/order">Load order</Link>
        <Link className="k-link" to="/mods/engines">Engines</Link>
        {nexus && <Link className="k-link" to="/catalog">Catalog</Link>}
        <button type="button" className="k-link" disabled={!api.isTauri()} title={api.isTauri() ? "A zip, 7z, rar or DLL of any kind (a folder: drop it on the window)" : "Needs the desktop app: drop a file instead"} onClick={() => void pick()}>Add mods</button>
      </div>
      {selecting && (
        <div className="k-mods-selbar" role="toolbar" aria-label="Selected mods">
          <span className="k-mods-selbar-count" aria-live="polite">{picked.length} selected</span>
          <button type="button" className="k-link" disabled={!loaded || !selOn.change.length} title={selTitle(true)} onClick={() => switchRows(picked, true)}>Switch on</button>
          <button type="button" className="k-link" disabled={!loaded || !selOff.change.length} title={selTitle(false)} onClick={() => switchRows(picked, false)}>Switch off</button>
          <button type="button" className="k-link k-tone-err" disabled={running || !toRemove.length} title={removeTitle} onClick={() => setRemoving(toRemove)}>Remove…</button>
          <button type="button" className="k-link" onClick={clearSel} title="Esc">Clear</button>
          {selNote && <span className="k-hint">{selNote}</span>}
        </div>
      )}
      {mods.error ? (
        <div className="k-mods-empty" role="alert">
          <p>Your mods are not readable.</p>
          <p className="k-hint">Nothing was changed: Play still starts the game with the mods that were on. The reason is above.</p>
        </div>
      ) : !mods.rows.length ? (
        <div className="k-mods-empty">
          <p>Add your first mod</p>
          <p className="k-hint">Drop its {MOD_FILE_WORDS} anywhere on this window: Kiln, native, EMF, EML, ShroudForge, SET and the other loaders' mods all go here.{nexus ? " Or find one in the Catalog." : ""} It is on at the next Play.</p>
        </div>
      ) : (
        <ul ref={listEl} className="k-mods-list" aria-label="Mods">
          {shown.map((r, i) => (
            <li key={r.key} className={"k-mod" + (r.on ? " on" : "") + (r.found ? " found" : "") + (i === foundStart && i > 0 ? " first-found" : "") + (sel.keys.includes(r.key) ? " picked" : "")} data-kind={r.kind ?? undefined}>
              <button type="button" role="checkbox" className="k-mod-check" aria-checked={sel.keys.includes(r.key)} aria-label={`Select ${r.name}`}
                title="Select (Shift+click: a range, Ctrl+A: every mod shown, Esc: none)"
                onClick={(e) => choose(r.key, e.shiftKey)}
                onKeyDown={(e) => { if (e.key === " " || e.key === "Enter") { e.preventDefault(); choose(r.key, e.shiftKey); } }}>
                {sel.keys.includes(r.key) && <Check aria-hidden strokeWidth={3} />}
              </button>
              {r.found ? <span className="k-mod-place" aria-hidden title="In the game folder" /> : (
                <FlameToggle asSwitch on={r.on} label={r.name} disabled={!loaded} title={loaded ? undefined : "Load a profile in Settings to switch mods"} onChange={(on) => mods.toggle(r, on)} />
              )}
              <span className="k-mod-text">
                <span className="k-mod-name" title={r.name !== r.id ? `${r.name} (${r.id})` : r.name}>{r.name}</span>
                {(r.found || r.note) && (
                  <span className="k-mod-note">
                    {r.found && <span className="k-mod-where">Game folder</span>}
                    {r.note && <span className={`k-tone-${r.note.tone}`} title={r.note.title}>{r.note.text}</span>}
                    {r.changed && (
                      <button type="button" className="k-link" disabled={!!busy || running} onClick={() => void takeChanged(r)}
                        aria-label={`${USE_CHANGED} of ${r.name}`} title="Kiln takes the files in its folder over as the mod's files, with the name and info they give now">
                        {busy === r.key ? "Taking over…" : USE_CHANGED}
                      </button>
                    )}
                    {r.found?.fix && (
                      <button type="button" className="k-link" disabled={!!busy || !loaded} onClick={() => void fixFound(r)}
                        aria-label={r.found.fix === "dropins" && r.found.kind ? `Load the game-folder ${KIND_SHORT[r.found.kind]} mods` : `Add ${r.name} to my mods`}
                        title={r.found.fix === "dropins" && r.found.kind ? `Load every ${KIND_SHORT[r.found.kind]} mod in the game folder` : "Copy it into the library and switch it on"}>
                        {busy === r.key ? "Adding…" : r.found.fix === "dropins" ? "Load them" : "Add it"}
                      </button>
                    )}
                  </span>
                )}
              </span>
              <span className="k-mod-kind">{r.kind && <Badge tone={KIND_TONE[r.kind]} title={KIND_NAME[r.kind]}>{KIND_SHORT[r.kind]}</Badge>}</span>
              <span className="k-mono k-mod-ver" title={(r.lib?.versions.length ?? 0) > 1 ? `${r.lib!.versions.length} versions in the library` : r.version ?? undefined}>{r.version ?? ""}</span>
              <Menu label={`More for ${r.name}`} icon={<MoreHorizontal />} size="s" items={menu(r)} />
            </li>
          ))}
          {!shown.length && <li className="k-mods-none">No mod matches that.</li>}
        </ul>
      )}
      <EngineSettingsModal target={settings} onClose={() => setSettings(null)} profileId={pid ?? ""} gameId={game.id} />
      <Modal open={!!configs} onClose={() => setConfigs(null)} title="Configs" width={960} footer={<Button onClick={() => setConfigs(null)}>Done</Button>}>
        {configs && pid && <ConfigEditor target={{ profile: pid }} initial={configs} />}
      </Modal>
      <ConfirmDanger open={!!removing} onClose={() => setRemoving(null)} relaxable word={String(removing?.length ?? 0)} action={`Remove ${removing?.length ?? 0}`}
        title={removing ? `Remove ${removing.length} mod${removing.length === 1 ? "" : "s"}?` : ""} disabled={!check2.ready || running}
        message={removing && (
          <>
            <span>{!check2.ready ? "Checking what uses them." : check2.error ? `Kiln could not check what else uses some of them (${check2.error}). Remove stops for a mod something uses.` : "Their files leave the Library."}</span>
            <ul className="k-remove-list" aria-label="Mods to remove">
              {removing.map((r) => {
                const o = check2.others.get(r.key) ?? [];
                return (
                  <li key={r.key}>{r.name}{!r.lib ? <span className="k-muted"> · its files are gone already; it leaves your list</span>
                    : o.length ? <span className="k-muted"> · {o.join(", ")} {o.length === 1 ? "lists" : "list"} it too and will show it as missing</span> : null}</li>
                );
              })}
            </ul>
          </>
        )}
        onConfirm={async () => {
          const rows = removing!;
          try {
            const r = await removeMany(qc, rows, check2.users, async () => { mods.edit.update((p) => withoutRows(p, rows)); return mods.edit.flush(); });
            clearSel();
            if (!r.left.length) toast.ok(`Removed ${r.removed.length} mod${r.removed.length === 1 ? "" : "s"}`);
            else toast.info(r.removed.length ? `Removed ${r.removed.length}, ${r.left.length} left` : `Nothing removed`,
              `${r.left.map((l) => `${l.name}: ${l.why}`).join(" ")} ${r.left.length === 1 ? "It is" : "They are"} out of your list and still in the Library.`, { ms: 0 });
          } catch (e) { toast.error(e); throw e; }
        }} />
      <ConfirmDanger open={!!rm} onClose={() => setRm(null)} relaxable word={rm?.row.id ?? ""} action="Remove"
        title={rm ? `Remove ${rm.row.name}${rm.version ? ` ${rm.version}` : ""}?` : ""} message={check.message} disabled={!check.ready || check.users.isFetching}
        onConfirm={async () => {
          const t = rm!;
          try {
            await removeMod(qc, t, check.users.data, async () => { mods.drop(t.row); return mods.edit.flush(); });
            toast.ok(`Removed ${t.row.name}${t.version ? ` ${t.version}` : ""}`);
          } catch (e) { toast.error(e); void check.users.refetch(); throw e; }
        }} />
    </div>
  );
}
