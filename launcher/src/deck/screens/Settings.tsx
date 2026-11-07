// Deck Settings: the desktop UI (now, and which UI to start in), button glyphs, launcher updates, fullscreen, Quit.
// The last line names what the Deck detection saw (for testers' reports).
// The Nexus Mods build (api/distribution.ts) has no launcher updates: its row is "New versions: Nexus Mods" (A opens
// the Kiln page there in the browser), and no update command is called.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "../../api/commands";
import type { UiMode, UpdateStatus } from "../../api/commands";
import { NEXUS_PAGE } from "../../api/distribution";
import { useAppInfo, useDistribution, useSettings } from "../../api/hooks";
import { runTask, toast } from "../../ui";
import { QuitDialog } from "../dialogs";
import { useGlyphPref, type GlyphPref } from "../glyphs";
import { usePads } from "../input";
import { saveSettings, saveUiMode, switchUi } from "../mode";
import { Row, Screen, Word } from "../ui";

const MODES: { id: UiMode; word: string }[] = [{ id: "auto", word: "Auto" }, { id: "deck", word: "Deck mode" }, { id: "desktop", word: "Desktop" }];
const GLYPHS: { id: GlyphPref; word: string }[] = [{ id: "auto", word: "Auto" }, { id: "deck", word: "Steam Deck" }, { id: "xbox", word: "Xbox" }];
const next = <T,>(list: { id: T }[], cur: T) => list[(list.findIndex((x) => x.id === cur) + 1) % list.length].id;

export default function Settings() {
  const qc = useQueryClient();
  const nav = useNavigate();
  const info = useAppInfo();
  const settings = useSettings();
  const glyph = useGlyphPref();
  const pads = usePads();
  const [quit, setQuit] = useState(false);
  const [full, setFull] = useState<boolean | null>(null);
  const [upBusy, setUpBusy] = useState(false);
  const updateLock = useRef(false);
  const dist = useDistribution();
  const selfUpdates = dist === "direct";
  const up = useQuery({ queryKey: ["update_status"], queryFn: api.updateStatus, retry: false, enabled: selfUpdates });
  useEffect(() => {
    if (!selfUpdates) return;
    let off: (() => void) | undefined, live = true;
    api.onKilnEvent("kiln://update", (s: UpdateStatus) => qc.setQueryData(["update_status"], s)).then((f) => (live ? (off = f) : f()));
    return () => { live = false; off?.(); };
  }, [qc, selfUpdates]);
  const mode: UiMode = settings.data?.uiMode === "deck" || settings.data?.uiMode === "desktop" ? settings.data.uiMode : "auto";
  const d = info.data?.deck;
  const u = up.data;
  const check = async () => {
    try { qc.setQueryData(["update_status"], await api.updateCheck()); } catch (e) { toast.error(e); }
  };
  const install = async () => {
    try { qc.setQueryData(["update_status"], await runTask<UpdateStatus>(() => api.updateInstall(), { failed: "Update failed" })); } catch { /* toasted */ }
  };
  const restart = () => api.updateRestart().catch((e) => toast.error(e));
  const update = async () => {
    if (!selfUpdates || updateLock.current) return;
    updateLock.current = true;
    setUpBusy(true);
    try { await (u?.ready ? restart() : u?.available && u.install.inPlace ? install() : check()); }
    finally { updateLock.current = false; setUpBusy(false); }
  };
  let upWord = "", upTone: "ok" | "warn" | "info" | "idle" | "ember" = "idle";
  if (u) {
    if (u.ready) { upWord = `${u.ready.version} ready`; upTone = "ok"; }
    else if (u.phase === "downloading") { upWord = "Downloading"; upTone = "ember"; }
    else if (u.phase === "checking") { upWord = "Checking"; upTone = "ember"; }
    else if (u.available) { upWord = `${u.available.version} available`; upTone = "info"; }
    else if (u.phase === "failed") { upWord = "Check failed"; upTone = "warn"; }
    else if (u.checked) { upWord = "Up to date"; upTone = "ok"; }
  }
  const pad = Object.values(pads.native)[0]?.name ?? pads.browser[0] ?? null;
  return (
    <Screen name="settings" title="Settings">
      <div className="deck-list deck-scroll">
        <Row id="set-desktop" title="Desktop UI" sub="Mouse and keyboard, every page" a="Switch" onClick={() => switchUi(nav, "desktop")} />
        <Row id="set-start" title="Start in" a="Change" end={<Word tone="ember">{MODES.find((m) => m.id === mode)?.word}</Word>}
          sub={mode === "auto" ? (d?.detected ? "Deck mode on this device" : "Desktop on this device") : undefined}
          onClick={() => void saveUiMode(qc, (cur) => next(MODES, cur))} />
        <Row id="set-fire" title="Animated fire" a="Change" sub={info.data?.noWebgl ? "Off for this start: KILN_LAUNCHER_NO_WEBGL" : undefined}
          disabled={!!info.data?.noWebgl || !settings.data}
          end={<Word tone="ember">{!info.data?.noWebgl && settings.data?.animatedFire !== false ? "On" : "Off"}</Word>}
          onClick={() => void saveSettings(qc, (cur) => ({ animatedFire: cur.animatedFire === false }))} />
        <Row id="set-glyphs" title="Button glyphs" a="Change" end={<Word tone="ember">{GLYPHS.find((g) => g.id === glyph.pref)?.word}</Word>}
          onClick={() => glyph.set(next(GLYPHS, glyph.pref))} />
        {api.isTauri() && !d?.gamescope && (
          <Row id="set-full" title="Fullscreen" a="Change" end={<Word tone="ember">{full === false ? "Off" : "On"}</Word>}
            onClick={() => api.deckFullscreen(full === false).then(setFull).catch((e) => toast.error(e))} />
        )}
        {selfUpdates && (
          <Row id="set-update" title="Launcher updates" a={u?.ready ? "Restart" : u?.available && u.install.inPlace ? "Install" : "Check"}
            sub={u ? `Kiln Launcher ${u.current}` : undefined} end={upWord ? <Word tone={upTone}>{upWord}</Word> : undefined}
            disabled={upBusy || u?.phase === "checking" || u?.phase === "downloading"}
            onClick={() => void update()} />
        )}
        {dist === "nexus" && (
          <Row id="set-versions" title="New versions" a="Open" sub={info.data ? `Kiln Launcher ${info.data.version}` : undefined}
            end={<Word tone="info">Nexus Mods</Word>} onClick={() => void api.appOpenUrl(NEXUS_PAGE).catch((e) => toast.error(e))} />
        )}
        <Row id="set-quit" title="Quit" a="Quit" onClick={() => setQuit(true)} />
      </div>
      <p className="deck-foot-note" data-testid="deck-detection">
        {info.data ? `Launcher ${info.data.version} · Kiln ${info.data.kiln}` : ""}
        {d ? ` · ${d.forced ? `--${d.forced}` : d.detected ? d.signals.join(", ") : "no Steam Deck found"}` : ""}
        {pad ? ` · ${pad}` : ""}
      </p>
      {quit && <QuitDialog onClose={() => setQuit(false)} />}
    </Screen>
  );
}
