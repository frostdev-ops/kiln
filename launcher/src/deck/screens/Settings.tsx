// Deck Settings: the desktop UI (now, and which UI to start in), button glyphs, launcher updates, fullscreen, the loaded
// profile (a background feature: the Mods screen shows its switches), the notes about mod folders (the switch, and
// the last activation's notes to read again: components/FolderNotes.tsx), the data folder (../DataFolder.tsx), Quit.
// The last line names what the Deck detection saw (for testers' reports).
// (dev.29) Channel: Release or Beta (test builds); A on it switches (DeckSwitchDialog: what happens, the build installed
// now, Restart now), both ways; a test build says so in the row.
// The Nexus Mods build (api/distribution.ts) has no launcher updates: its row is "New versions: Nexus Mods" (A opens
// the Kiln page there in the browser) and "Test builds" (A opens the download page), and no update command is called.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "../../api/commands";
import type { UiMode, UpdateChannel, UpdateStatus } from "../../api/commands";
import { RELEASE_CHANNEL, acceptedRestore, buildLabel, channelName, channelWord, restoreWords, switchKind, switchWords } from "../../api/channel";
import { DIRECT_DOWNLOAD_PAGE, NEXUS_PAGE } from "../../api/distribution";
import { runSwitch } from "../../shell/ChannelSwitch";
import { useActiveProfile, useAppInfo, useDistribution, useGame, useGameStatus, useNexusEnabled, useProfiles, useSettings } from "../../api/hooks";
import * as nx from "../../api/nexus";
import { useNexusAccount } from "../../shell/catalogInstall";
import { runTask, toast } from "../../ui";
import { DeckDataFolderRow } from "../DataFolder";
import { otherFolderProfile } from "../../shell/dataFolderText";
import { ProfileDialog, QuitDialog } from "../dialogs";
import { useGlyphPref, type GlyphPref } from "../glyphs";
import { usePads } from "../input";
import { saveSettings, saveUiMode, switchUi } from "../mode";
import { DeckButton, Dialog, Row, Screen, Word } from "../ui";
import { FolderNotesList, useGameFolderNotes } from "../../components/FolderNotes";
import { folderNotesCount } from "../../shell/notices";
import { t, useT } from "../../i18n";

const MODES: { id: UiMode }[] = [{ id: "auto" }, { id: "deck" }, { id: "desktop" }];
const GLYPHS: { id: GlyphPref }[] = [{ id: "auto" }, { id: "deck" }, { id: "xbox" }];

function modeWord(m: UiMode): string {
  switch (m) {
    case "auto": return t("deck.settings.auto");
    case "deck": return t("deck.settings.deckMode");
    case "desktop": return t("deck.settings.desktop");
  }
}

function glyphWord(g: GlyphPref): string {
  switch (g) {
    case "auto": return t("deck.settings.auto");
    case "deck": return t("deck.settings.glyphDeck");
    case "xbox": return t("deck.settings.glyphXbox");
  }
}
const next = <T,>(list: { id: T }[], cur: T) => list[(list.findIndex((x) => x.id === cur) + 1) % list.length].id;

export default function Settings() {
  const t = useT();
  const qc = useQueryClient();
  const nav = useNavigate();
  const info = useAppInfo();
  const settings = useSettings();
  const glyph = useGlyphPref();
  const pads = usePads();
  const [quit, setQuit] = useState(false);
  const [profiles, setProfiles] = useState(false);
  const { game } = useGame();
  const gameStatus = useGameStatus(game?.id);
  const active = useActiveProfile(game?.id);
  const list = useProfiles();
  const loadedName = list.data?.find((p) => p.id === active.data?.profileId)?.name ?? (active.isSuccess ? t("deck.dialogs.noProfile") : "");
  const notes = useGameFolderNotes(game?.id);
  const noteCount = folderNotesCount(notes);
  const [notesOpen, setNotesOpen] = useState(false);
  const [full, setFull] = useState<boolean | null>(null);
  const [upBusy, setUpBusy] = useState(false);
  const [switchTo, setSwitchTo] = useState<UpdateChannel | null>(null);
  const [nexusBusy, setNexusBusy] = useState(false);
  const nexus = useNexusAccount();
  const nexusEnabled = useNexusEnabled();
  const nexusDisconnect = !!(nexus.data?.signedIn || nexus.data?.error);
  const nexusAccount = async () => {
    if (nexusBusy) return;
    setNexusBusy(true);
    try {
      const account = nexusDisconnect ? await nx.nexusSignOut() : await runTask<nx.NexusAccount>(nx.nexusSignIn, { failed: t("deck.settings.notSignedIn") });
      qc.setQueryData(nx.nk.account, account);
      for (const queryKey of [["nexus_search"], ["nexus_mod"], nx.nk.installed]) qc.invalidateQueries({ queryKey });
    } catch (e) { if (nexusDisconnect) toast.error(e); }
    finally { setNexusBusy(false); }
  };
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
    try { qc.setQueryData(["update_status"], await runTask<UpdateStatus>(() => api.updateInstall(), { failed: t("deck.settings.updateFailed") })); } catch { /* toasted */ }
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
    if (u.ready) { upWord = t("deck.settings.ready", { version: buildLabel(u.ready.version) }); upTone = "ok"; }
    else if (u.phase === "downloading") { upWord = t("deck.settings.downloading"); upTone = "ember"; }
    else if (u.phase === "checking") { upWord = t("deck.shared.checking"); upTone = "ember"; }
    else if (u.available) { upWord = t("deck.settings.available", { version: buildLabel(u.available.version) }); upTone = "info"; }
    else if (u.phase === "failed") { upWord = t("deck.settings.checkFailed"); upTone = "warn"; }
    else if (u.checked) { upWord = t("deck.settings.upToDate"); upTone = "ok"; }
  }
  const pad = Object.values(pads.native)[0]?.name ?? pads.browser[0] ?? null;
  // the channel row: a test build goes back to the release; a release build to the beta (or, following the beta
  // already, back to following the release)
  const onBeta = u?.buildChannel === "beta";
  const channelTarget: UpdateChannel = onBeta || u?.channel === "beta" ? RELEASE_CHANNEL : "beta";
  const changeChannel = () => {
    if (!u) return;
    if (switchKind(u, channelTarget)) setSwitchTo(channelTarget);
    else void saveSettings(qc, () => ({ updateChannel: channelTarget })).then(() => api.updateStatus().then((x) => qc.setQueryData(["update_status"], x)).catch(() => {}));
  };
  return (
    <Screen name="settings" title={t("deck.app.tabs.settings")}>
      <div className="deck-list deck-scroll">
        <Row id="set-desktop" title={t("deck.shared.desktopUi")} sub={t("deck.settings.desktopSub")} a={t("deck.settings.switch")} onClick={() => switchUi(nav, "desktop")} />
        <Row id="set-start" title={t("deck.settings.startIn")} a={t("deck.shared.change")} end={<Word tone="ember">{modeWord(mode)}</Word>}
          sub={mode === "auto" ? (d?.detected ? t("deck.settings.deckOnDevice") : t("deck.settings.desktopOnDevice")) : undefined}
          onClick={() => void saveUiMode(qc, (cur) => next(MODES, cur))} />
        <Row id="set-fire" title={t("deck.settings.fire")} a={t("deck.shared.change")} sub={info.data?.noWebgl ? (info.data.render?.mode === "software" ? t("deck.settings.fireSoftware") : t("deck.settings.fireNoWebgl")) : undefined}
          disabled={!!info.data?.noWebgl || !settings.data}
          end={<Word tone="ember">{!info.data?.noWebgl && settings.data?.animatedFire !== false ? t("deck.shared.on") : t("deck.shared.off")}</Word>}
          onClick={() => void saveSettings(qc, (cur) => ({ animatedFire: cur.animatedFire === false }))} />
        <Row id="set-glyphs" title={t("deck.settings.glyphs")} a={t("deck.shared.change")} end={<Word tone="ember">{GLYPHS.some((g) => g.id === glyph.pref) ? glyphWord(glyph.pref) : undefined}</Word>}
          onClick={() => glyph.set(next(GLYPHS, glyph.pref))} />
        {api.isTauri() && !d?.gamescope && (
          <Row id="set-full" title={t("deck.settings.fullscreen")} a={t("deck.shared.change")} end={<Word tone="ember">{full === false ? t("deck.shared.off") : t("deck.shared.on")}</Word>}
            onClick={() => api.deckFullscreen(full === false).then(setFull).catch((e) => toast.error(e))} />
        )}
        {selfUpdates && (
          <Row id="set-update" title={t("deck.settings.updates")} a={u?.ready ? t("deck.dataFolder.restart") : u?.available && u.install.inPlace ? t("common.install") : t("deck.settings.check")}
            sub={u ? t("deck.settings.launcherVersion", { version: u.current }) : undefined} end={upWord ? <Word tone={upTone}>{upWord}</Word> : undefined}
            disabled={upBusy || u?.phase === "checking" || u?.phase === "downloading"}
            onClick={() => void update()} />
        )}
        {selfUpdates && u && (
          <Row id="set-channel" title={t("deck.settings.channel")} a={onBeta ? t("deck.settings.switchBack") : t("deck.shared.change")}
            sub={onBeta ? t("deck.shared.testBuild", { beta: u.beta ?? "", current: u.current }) : channelName(u.channel)}
            end={<Word tone={onBeta ? "warn" : "ember"}>{onBeta ? t("deck.shared.beta", { beta: u.beta ?? "" }) : channelWord(u.channel)}</Word>}
            disabled={upBusy || u.phase === "downloading"} onClick={changeChannel} />
        )}
        {dist === "nexus" && (
          <Row id="set-versions" title={t("deck.settings.newVersions")} a={t("common.open")} sub={info.data ? t("deck.settings.launcherVersion", { version: info.data.version }) : undefined}
            end={<Word tone="info">Nexus Mods</Word>} onClick={() => void api.appOpenUrl(NEXUS_PAGE).catch((e) => toast.error(e))} />
        )}
        {dist === "nexus" && (
          <Row id="set-test-builds" title={t("deck.settings.testBuilds")} a={t("common.open")} sub={t("deck.settings.testBuildsSub")}
            end={<Word tone="info">{t("deck.settings.downloadPage")}</Word>} onClick={() => void api.appOpenUrl(DIRECT_DOWNLOAD_PAGE).catch((e) => toast.error(e))} />
        )}
        {nexusEnabled && <Row id="set-nexus" title="Nexus Mods" a={nexusDisconnect ? t("deck.settings.signOut") : t("deck.settings.signIn")}
          disabled={nexusBusy || !nexus.data || (!nexus.data.configured && !nexusDisconnect)}
          sub={nexus.data?.error?.message ?? (nexus.data?.configured === false ? t("deck.settings.signInUnavailable") : undefined)}
          end={<Word tone={nexus.data?.signedIn ? "ok" : "idle"}>{nexus.data?.signedIn ? nexus.data.name ?? t("deck.settings.signedIn") : t("deck.settings.notSignedIn")}</Word>}
          onClick={() => void nexusAccount()} />}
        {game && gameStatus.data?.runtime.installed && (
          <Row id="set-profile" title={t("deck.dialogs.profile")} a={t("deck.shared.change")}
            sub={gameStatus.data?.otherFolderProfile ? otherFolderProfile() : t("deck.settings.profileSub")}
            end={gameStatus.data?.otherFolderProfile ? <Word tone="warn">{t("deck.settings.otherFolder")}</Word> : <Word tone="ember">{loadedName}</Word>} onClick={() => setProfiles(true)} />
        )}
        <Row id="set-notes-switch" title={t("deck.settings.notesSwitch")} a={t("deck.shared.change")} disabled={!settings.data}
          sub={t("deck.settings.notesSwitchSub")}
          end={<Word tone="ember">{settings.data?.modFolderNotes !== false ? t("deck.shared.on") : t("deck.shared.off")}</Word>}
          onClick={() => void saveSettings(qc, (cur) => ({ modFolderNotes: cur.modFolderNotes === false }))} />
        {game && (
          <Row id="set-notes" title={t("deck.settings.modNotes")} a={t("deck.shared.read")} disabled={!noteCount} sub={t("deck.settings.modNotesSub")}
            end={<Word tone={noteCount ? "info" : "idle"}>{String(noteCount)}</Word>} onClick={() => setNotesOpen(true)} />
        )}
        <DeckDataFolderRow />
        <Row id="set-quit" title={t("deck.dialogs.quit")} a={t("deck.dialogs.quit")} onClick={() => setQuit(true)} />
      </div>
      <p className="deck-foot-note" data-testid="deck-detection">
        {info.data ? t("deck.settings.footVersions", { launcher: info.data.version, kiln: info.data.kiln }) : ""}
        {d ? ` · ${d.forced ? `--${d.forced}` : d.detected ? d.signals.join(", ") : t("deck.settings.noDeckFound")}` : ""}
        {pad ? ` · ${pad}` : ""}
      </p>
      {quit && <QuitDialog onClose={() => setQuit(false)} />}
      {switchTo && u && switchKind(u, switchTo) && <DeckSwitchDialog s={u} target={switchTo} onClose={() => setSwitchTo(null)} />}
      {profiles && game && <ProfileDialog game={game} onClose={() => setProfiles(false)} />}
      {notesOpen && (
        <Dialog name="folder-notes" title={t("deck.settings.notesTitle")} wide onClose={() => setNotesOpen(false)} backLabel={t("common.close")}
          buttons={<DeckButton primary onClick={() => setNotesOpen(false)}>{t("common.ok")}</DeckButton>}>
          <div className="deck-scroll" style={{ overflowWrap: "anywhere" }}><FolderNotesList notes={notes} /></div>
        </Dialog>
      )}
    </Screen>
  );
}

/** (dev.29) A channel switch on the Deck: what happens (worlds, multiplayer), the build installed now, Restart now. A
 *  switch back with a "before beta" copy offers to put the profiles and settings from before the beta back. */
function DeckSwitchDialog({ s, target, onClose }: { s: UpdateStatus; target: UpdateChannel; onClose: () => void }) {
  const t = useT();
  const qc = useQueryClient();
  const kind = switchKind(s, target)!;
  const w = switchWords(kind, s.ready?.version);
  const [busy, setBusy] = useState(false);
  const ready = s.phase === "ready" && s.switching === target && s.ready;
  const downloading = busy || (s.phase === "downloading" && s.switching === target);
  const go = async (restore: boolean) => {
    if (busy) return;
    setBusy(true);
    try {
      const next = await runSwitch(qc, s, target, restore);
      if (next) qc.setQueryData(["update_status"], next);
    } finally { setBusy(false); }
  };
  const r = kind === "back" && s.beforeBeta && s.install.inPlace ? restoreWords(s.beforeBeta.taken) : null;
  const buttons = ready
    ? <><DeckButton primary onClick={() => api.updateRestart().catch((e) => toast.error(e))}>{t("deck.settings.restartNow")}</DeckButton><DeckButton onClick={onClose}>{t("deck.settings.later")}</DeckButton></>
    : <>
      <DeckButton primary disabled={downloading || s.phase === "ready"} onClick={() => void go(false)}>{s.install.inPlace ? w.confirm : t("deck.settings.openDownloadPage")}</DeckButton>
      {r && <DeckButton disabled={downloading || s.phase === "ready"} onClick={() => void go(true)}>{t("deck.settings.switchBackRestore")}</DeckButton>}
    </>;
  return (
    <Dialog name="channel-switch" title={ready ? t("deck.settings.readyTitle", { version: buildLabel(s.ready!.version) }) : w.title} wide onClose={onClose} locked={downloading} buttons={buttons}>
      <div className="deck-scroll" data-testid={`deck-switch-${kind}`}>
        {ready && <p>{acceptedRestore(s) ? t("deck.settings.restoreGoes") : t("deck.settings.settingsStay")}</p>}
        {!!s.beforeBetaWarning?.length && <p>{t("deck.settings.noCopy", { warnings: s.beforeBetaWarning.join("; ") })}</p>}
        {w.say.map((line) => <p key={line}>{line}</p>)}
        {!ready && r && <p className="deck-note">{r.hint}</p>}
        {downloading && <p className="deck-note">{t("deck.settings.downloadingDots")}</p>}
      </div>
    </Dialog>
  );
}
