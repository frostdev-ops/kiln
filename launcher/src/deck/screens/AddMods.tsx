// Deck Add mods: no drag and drop with a controller, so this lists the mods the library recognises in Downloads
// (deck_browse), and browses other folders (SD card, Desktop, Home) without a file dialog. A on a mod: library_add
// imports it, switches it on in the mods list ("My mods" for a first-time player) and writes the game's profile.json;
// a modpack zip asks for consent first. A on a folder opens it; B goes back up.
import { useQueryClient } from "@tanstack/react-query";
import { useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "../../api/commands";
import type { AddReport, DeckFoundMod } from "../../api/commands";
import { invalidateGame, qk, useActiveProfile, useGame, useGameStatus, useProfile } from "../../api/hooks";
import { leftOutLine, rememberAdd, summarize } from "../../shell/importResult";
import { runTask, toast } from "../../ui";
import { notifyAdd } from "../../shell/notices";
import { FolderIcon, ModpackDialog, useBrowse } from "../dialogs";
import { DeckButton, Row, Screen, Word } from "../ui";
import { KIND_WORD, ago, size } from "../status";
import type { DeckAction } from "../layers";
import { t, useT } from "../../i18n";

/** A zip or folder holding one mod: that mod. */
const one = (m: DeckFoundMod) => (m.detected.kind === "unknown" && m.detected.items.length === 1 ? m.detected.items[0] : m.detected);

/** The mod's own name ("Lantern Glow"), else its id made readable, never just the file name again. */
function modName(m: DeckFoundMod): string {
  const d = m.detected.kind === "unknown" && m.detected.items.length === 1 ? m.detected.items[0] : m.detected;
  if (m.detected.kind === "unknown" && m.detected.items.length > 1) return t("deck.addMods.fileMods", { file: m.file, count: m.detected.items.length });
  if (d.name && d.name !== m.file && !/\.(zip|7z|rar|dll)$/i.test(d.name)) return d.name;
  return d.id ? d.id.replace(/[-_.]+/g, " ").replace(/\b\w/g, (c) => c.toUpperCase()) : m.file;
}

export default function AddMods() {
  const t = useT();
  const qc = useQueryClient();
  const nav = useNavigate();
  const { game } = useGame();
  const status = useGameStatus(game?.id);
  const active = useActiveProfile(game?.id);
  const profile = useProfile(active.data?.profileId);
  const scope = useRef<HTMLElement | null>(null);
  const b = useBrowse(null, scope);
  const [busy, setBusy] = useState<string | null>(null);
  const [pack, setPack] = useState<string | null>(null);
  const [places, setPlaces] = useState(false);
  const f = b.q.data;
  const installed = !!status.data?.runtime.installed;
  const inProfile = (m: DeckFoundMod) => {
    const d = one(m);
    const p = profile.data;
    if (!p || !d.id) return false;
    const kind = d.kind === "bare-dll" ? "native" : d.kind;
    return kind in p.mods && p.mods[kind as api.ModKind].some((x) => x.id === d.id && x.enabled && (!d.version || x.version === d.version || d.version === "0.0.0"));
  };

  const add = async (m: DeckFoundMod) => {
    if (busy) return;
    if (!game) { toast.info(t("deck.addMods.noGame")); return; }
    if (m.detected.kind === "modpack") { setPack(m.path); return; }
    if (!installed) { toast.info(t("deck.addMods.installFirst")); return; }
    setBusy(m.path);
    try {
      const r = await runTask<AddReport>(() => api.libraryAdd(game.id, [m.path]), { failed: t("deck.addMods.notAdded") });
      qc.invalidateQueries({ queryKey: ["library_list"] });
      qc.invalidateQueries({ queryKey: ["profile_resolve"] });
      if (r.profileId) qc.invalidateQueries({ queryKey: qk.profile(r.profileId) });
      invalidateGame(qc, game.id);
      notifyAdd(r); // what writing the profile again kept in the game folder, and what else changed in it
      // kept for "Last adds" (the desktop's main list, after the Deck's toast is gone)
      rememberAdd({ heading: r.added.length ? t("deck.addMods.addedOn") : r.already.length ? t("deck.addMods.alreadyOn") : r.libraryOnly ? t("deck.addMods.addedOff") : t("deck.addMods.nothingAdded"),
        summary: summarize(r.import), libraryOnly: r.libraryOnly });
      if (r.modpacks.length) setPack(r.modpacks[0]);
      else if (r.added.length) toast.ok(r.added.length === 1 ? t("deck.addMods.addedOne", { name: r.added[0].name }) : t("deck.addMods.addedMany", { count: r.added.length }),
        r.missing.length ? t("deck.addMods.needs", { ids: r.missing.map((x) => x.id).join(", ") }) : undefined);
      else if (r.already.length) toast.info(r.already.length === 1 ? t("deck.addMods.alreadyOne", { name: r.already[0].name }) : t("deck.addMods.alreadyMany", { count: r.already.length }));
      else if (r.libraryOnly && (r.import.imported.length || r.import.existing.length)) toast.info(t("deck.addMods.addedOff"), t("deck.addMods.noProfileLoaded"));
      else toast.info(t("deck.addMods.nothingToAdd"), leftOutLine(summarize(r.import)));
    } catch { /* toasted */ } finally { setBusy(null); }
  };

  const actions: DeckAction[] = [{ button: "y", label: t("deck.shared.places"), run: () => setPlaces((x) => !x) }];
  if (f?.parent) actions.unshift({ button: "x", label: t("deck.shared.up"), run: () => void b.up() });
  const back = b.depth > 0 ? () => void b.up() : undefined;
  const say = !f ? (b.q.error ? api.toKilnError(b.q.error).message : t("deck.shared.reading"))
    : f.mods.length ? t("deck.addMods.modsHere", { count: f.mods.length }) : t("deck.addMods.noModsHere");

  return (
    <Screen name="add" title={f?.name ?? t("deck.app.tabs.addMods")} say={say} actions={actions} back={back} backLabel={back ? t("deck.shared.up") : undefined}>
      <div ref={(el) => { scope.current = el?.closest<HTMLElement>("[data-deck-screen]") ?? el; }} className="deck-add">
        {!installed && status.data && game && (
          <p className="deck-line"><Word tone="warn">{t("deck.shared.kilnNotInstalled")}</Word> <DeckButton id="go-kiln" onClick={() => nav("/deck/kiln")}>{t("deck.shared.installKiln")}</DeckButton></p>
        )}
        {(places || !f?.mods.length) && f && (
          <div className="deck-places" role="group" aria-label={t("deck.shared.places")}>
            {f.places.filter((p) => p.kind !== "games").map((p) => (
              <DeckButton key={p.path} id={`place-${p.kind}-${p.name}`} className={p.path === f.dir ? "on" : ""} a={t("common.open")} onClick={() => { b.jump(p.path); setPlaces(false); }}>{p.name}</DeckButton>
            ))}
          </div>
        )}
        {f && <p className="deck-path deck-mono">{f.dir}</p>}
        <div className="deck-list deck-scroll" role="list" aria-label={t("deck.addMods.listAria")}>
          {f?.mods.map((m) => {
            const there = inProfile(m);
            return (
              <Row key={m.path} id={`file-${m.file}`} a={m.detected.kind === "modpack" ? t("common.install") : there ? t("deck.shared.addAgain") : t("common.add")} disabled={!!busy}
                title={modName(m)} sub={`${KIND_WORD[one(m).kind]}${one(m).version && one(m).version !== "0.0.0" ? ` ${one(m).version}` : ""} · ${m.file}${m.size ? ` · ${size(m.size)}` : ""}${m.modified ? ` · ${ago(m.modified)}` : ""}`}
                end={busy === m.path ? <Word tone="ember">{t("deck.shared.adding")}</Word> : there ? <Word tone="ok">{t("deck.shared.on")}</Word> : undefined}
                onClick={() => void add(m)} />
            );
          })}
          {f?.folders.map((d) => <Row key={d.path} id={`dir-${d.name}`} path={d.path} icon={<FolderIcon />} title={d.name} dim a={t("common.open")} onClick={() => b.open(d.path)} />)}
        </div>
      </div>
      {pack && game && <ModpackDialog game={game} path={pack} onClose={() => setPack(null)} />}
    </Screen>
  );
}
