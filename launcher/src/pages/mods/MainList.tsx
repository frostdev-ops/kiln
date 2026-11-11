// The main screen's list of every mod (shell/mods.ts): a switch, the name, the kind, the version, one short note when
// there is something to say, and a menu with what the profile tabs and the Library page offered for the mod (settings,
// configs, the version, its folder, remove). A mod found in the game folder is a row with its state and its fix, no
// switch. Above the list: the filter (with many mods), Load order, Engines and Add mods. It scrolls inside the page.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { MoreHorizontal } from "lucide-react";
import { useState } from "react";
import { Link } from "react-router-dom";
import * as api from "../../api/commands";
import type { AddReport, GameInstall, ModKind } from "../../api/commands";
import { qk, useNexusEnabled } from "../../api/hooks";
import { KIND_NAME, KIND_SHORT, KIND_TONE, SETTINGS_KINDS } from "../../api/kinds";
import { EngineSettingsModal } from "../../components/EngineSettings";
import { ConfigEditor } from "../../components/ConfigEditor";
import { useAddMods } from "../../shell/drop";
import { filterRows, kindsOf, type ModRow } from "../../shell/mods";
import { notifyAdd } from "../../shell/notices";
import { removeMod, useRemoveCheck, type RemoveTarget } from "../../shell/removeMod";
import type { useMods } from "../../shell/useMods";
import { withDropin } from "../profiles/dropins";
import { Badge, Button, ConfirmDanger, FlameToggle, Menu, Modal, Select, WellInput, pickPaths, runTask, toast, type MenuItem } from "../../ui";

/** More rows than this: the filter shows. */
const FILTER_FROM = 6;

type Mods = ReturnType<typeof useMods>;

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
  const kinds = kindsOf(mods.rows);
  const many = mods.rows.length > FILTER_FROM;
  const shown = filterRows(mods.rows, many ? text : "", many ? kind : "");
  const loaded = !!mods.profile;
  const check = useRemoveCheck(rm, pid);

  const pick = async () => {
    try {
      const paths = await pickPaths({ multiple: true, filters: [{ name: "Mods", extensions: ["zip", "dll"] }], title: "Add mods" });
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
      ...sw,
      ...(r.kind && r.entry && SETTINGS_KINDS.includes(r.kind) ? [{ label: "Settings", onSelect: () => setSettings({ kind: r.kind!, id: r.id, name: r.name }) }] : []),
      ...(withConfig.has(r.id) ? [{ label: "Configs", onSelect: () => setConfigs(r.id) }] : []),
      ...versions.filter((v) => v.version !== cur).map((v) => ({ label: `Use ${v.version}`, disabled: !loaded, onSelect: () => mods.setVersion(r, v.version, v.hash) })),
      ...(folder ? [{ label: "Open folder", onSelect: () => api.appOpenPath(folder).catch((e) => toast.error(e)) }] : []),
      ...(versions.length > 1 ? versions.filter((v) => v.version !== r.entry?.version).map((v) => ({ label: `Remove ${v.version}`, danger: true, disabled: running, onSelect: () => setRm({ row: r, version: v.version }) })) : []),
      { label: "Remove", danger: true, disabled: running, onSelect: () => setRm({ row: r, version: null }) },
    ];
  };

  const foundStart = shown.findIndex((r) => r.found);
  return (
    <div className={"k-mods" + (mods.rows.length ? "" : " empty")}>
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
        <span className="k-spacer" />
        <Link className="k-link" to="/mods/order">Load order</Link>
        <Link className="k-link" to="/mods/engines">Engines</Link>
        {nexus && <Link className="k-link" to="/catalog">Catalog</Link>}
        <button type="button" className="k-link" disabled={!api.isTauri()} title={api.isTauri() ? "Zip, folder or DLL of any kind" : "Needs the desktop app: drop a file instead"} onClick={() => void pick()}>Add mods</button>
      </div>
      {mods.error ? (
        <div className="k-mods-empty" role="alert">
          <p>Your mods are not readable.</p>
          <p className="k-hint">Nothing was changed: Play still starts the game with the mods that were on. The reason is above.</p>
        </div>
      ) : !mods.rows.length ? (
        <div className="k-mods-empty">
          <p>Add your first mod</p>
          <p className="k-hint">Drop its zip, folder or DLL anywhere on this window: Kiln, native, EMF, EML, ShroudForge, SET and the other loaders' mods all go here.{nexus ? " Or find one in the Catalog." : ""} It is on at the next Play.</p>
        </div>
      ) : (
        <ul className="k-mods-list" aria-label="Mods">
          {shown.map((r, i) => (
            <li key={r.key} className={"k-mod" + (r.on ? " on" : "") + (r.found ? " found" : "") + (i === foundStart && i > 0 ? " first-found" : "")} data-kind={r.kind ?? undefined}>
              {r.found ? <span className="k-mod-place" aria-hidden title="In the game folder" /> : (
                <FlameToggle asSwitch on={r.on} label={r.name} disabled={!loaded} title={loaded ? undefined : "Load a profile in Settings to switch mods"} onChange={(on) => mods.toggle(r, on)} />
              )}
              <span className="k-mod-text">
                <span className="k-mod-name" title={r.name !== r.id ? `${r.name} (${r.id})` : r.name}>{r.name}</span>
                {(r.found || r.note) && (
                  <span className="k-mod-note">
                    {r.found && <span className="k-mod-where">Game folder</span>}
                    {r.note && <span className={`k-tone-${r.note.tone}`} title={r.note.title}>{r.note.text}</span>}
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
