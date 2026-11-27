// Settings > General: the launcher's language and Kiln's in the game, the fire, Steam Deck, the app menu entry.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { RefreshCw } from "lucide-react";
import { useNavigate } from "react-router-dom";
import * as api from "../../../api/commands";
import type { Json, UiMode } from "../../../api/commands";
import { qk, useAppInfo, useGame, useGameStatus, useSettings } from "../../../api/hooks";
import { GAME_AUTO, GAME_LANGUAGES } from "../../../components/LanguageSettings";
import { saveSettings, saveUiMode, switchUi, uiModeOf } from "../../../deck/mode";
import { languageName, languages, useT } from "../../../i18n";
import { applyLanguage, userLocalesKey, useUserLocales } from "../../../i18n/LanguageSync";
import { Button, IconButton, PathText, Segmented, Select, Toggle, getPath, toast } from "../../../ui";
import { Group, Hi, SettingRow, useSettingsView } from "../parts";
import { setFestive, useFestiveSwitch } from "../../../shell/season";

export function GeneralSection() {
  return (
    <>
      <LanguageGroup />
      <LookGroup />
      <DeckGroup />
      <ComputerGroup />
    </>
  );
}

/** The launcher's language (LauncherSettings.language, applied at once: src/i18n) and the language of Kiln's own texts in
 *  the game (kiln.json `ui.language`: "auto" follows the game's text language; components/LanguageSettings.tsx). */
function LanguageGroup() {
  const t = useT();
  const qc = useQueryClient();
  const s = useSettings();
  const files = useUserLocales();
  const v = useSettingsView();
  const { game } = useGame();
  const status = useGameStatus(game?.id);
  const key = qk.kilnJson(game?.id ?? "");
  const doc = useQuery({ queryKey: key, queryFn: () => api.configKilnRead(game!.id), enabled: !!game });
  const setGame = useMutation({
    mutationFn: (v: string) => api.configKilnSet(game!.id, "ui.language", v),
    onSuccess: (d) => qc.setQueryData(key, d),
    onError: (e) => toast.error(e),
  });

  const pref = s.data?.language || "system";
  const launcherOptions = [
    { value: "system", label: t("languageSettings.system") },
    ...languages().map((l) => ({ value: l.tag, label: l.name })),
  ];
  if (!launcherOptions.some((o) => o.value.toLowerCase() === pref.toLowerCase())) launcherOptions.push({ value: pref, label: pref });
  const choose = (v: string) => {
    applyLanguage(v);
    void saveSettings(qc, () => ({ language: v === "system" ? null : v }));
  };

  const installed = status.data?.runtime.installed;
  const raw = doc.data ? getPath(doc.data.values as Json, "ui.language") : undefined;
  const ingame = typeof raw === "string" && raw ? raw : GAME_AUTO;
  const gameOptions = [{ value: GAME_AUTO, label: t("languageSettings.gameAuto") }, ...GAME_LANGUAGES.map((tag) => ({ value: tag, label: languageName(tag) }))];
  if (!gameOptions.some((o) => o.value.toLowerCase() === ingame.toLowerCase())) gameOptions.push({ value: ingame, label: ingame });
  const gameBlocked = !game ? t("languageSettings.gameNoGame") : status.data && !installed ? t("languageSettings.gameNoKiln") : null;
  const item = v.items.get("language.launcher");
  // a translation file that did not read says so under the row, More or not
  const fileErrors = files.data?.errors ?? [];

  return (
    <Group id="language" items={["language.launcher", "language.game"]}>
      <SettingRow id="language.launcher" htmlFor="st-lang"
        hint={fileErrors.length ? <>
          {item?.hint && <Hi text={item.hint} />}
          {fileErrors.map((e) => <span key={e.file} className="k-error-text" style={{ display: "block" }}>{t("languageSettings.filesBad", e.file, e.message)}</span>)}
        </> : undefined}
        control={<Select id="st-lang" value={launcherOptions.find((o) => o.value.toLowerCase() === pref.toLowerCase())?.value ?? pref}
          disabled={!s.data} onChange={choose} options={launcherOptions} />}
        moreExtra={files.data && (
          <span className="k-row" style={{ gap: 6, flexWrap: "wrap" }} data-testid="language-files">
            <span>{t("languageSettings.files")}</span>
            <PathText path={files.data.dir} />
            <IconButton size="s" label={t("languageSettings.filesReload")} icon={<RefreshCw />}
              onClick={() => void qc.fetchQuery({ queryKey: userLocalesKey, staleTime: 0 }).then(
                (r) => toast.ok(t("languageSettings.filesRead", { count: (r as { tags: string[] }).tags.length })),
                (e) => toast.error(e))} />
          </span>
        )} />
      <SettingRow id="language.game" htmlFor="st-lang-game" tag={t("settings.tag.thisGame")} hint={gameBlocked ?? undefined}
        control={<Select id="st-lang-game" value={gameOptions.find((o) => o.value.toLowerCase() === ingame.toLowerCase())?.value ?? ingame}
          disabled={!!gameBlocked || !doc.data || setGame.isPending} onChange={(v) => setGame.mutate(v)} options={gameOptions} />} />
    </Group>
  );
}

/** The fire on Play. Off (or KILN_LAUNCHER_NO_WEBGL=1 for one start) draws the plain Play button, the same one a machine
 *  without WebGL gets. And during the festive season (shell/season.ts) its look, on unless the player switched it off
 *  (kept in local storage for the season). */
function LookGroup() {
  const qc = useQueryClient();
  const s = useSettings();
  const info = useAppInfo();
  const forced = !!info.data?.noWebgl;
  const festive = useFestiveSwitch();
  return (
    <Group id="look" items={festive.offered ? ["fire", "festive"] : ["fire"]}>
      <SettingRow id="fire" htmlFor="st-fire"
        control={<Toggle id="st-fire" checked={!forced && s.data?.animatedFire !== false} disabled={forced || !s.data}
          onChange={(on) => void saveSettings(qc, () => ({ animatedFire: on }))} />} />
      {festive.offered && (
        <SettingRow id="festive" htmlFor="st-festive"
          control={<Toggle id="st-festive" checked={festive.on} onChange={(on) => setFestive(on)} />} />
      )}
    </Group>
  );
}

/** The Steam Deck UI: start in it (auto / always / never), open it now (deck/mode.tsx). */
function DeckGroup() {
  const t = useT();
  const qc = useQueryClient();
  const nav = useNavigate();
  const s = useSettings();
  const mode: UiMode = uiModeOf(s.data);
  return (
    <Group id="deck" items={["deck.startIn"]}
      actions={<Button size="s" onClick={() => switchUi(nav, "deck")}>{t("deck.modeCard.open")}</Button>}>
      {s.data && (
        <SettingRow id="deck.startIn"
          control={<Segmented<UiMode> aria-label={t("deck.modeCard.startIn")} value={mode} onChange={(v) => void saveUiMode(qc, v)}
            options={[{ id: "auto", label: t("deck.modeCard.auto") }, { id: "deck", label: t("deck.modeCard.deck") }, { id: "desktop", label: t("deck.modeCard.desktop") }]} />} />
      )}
    </Group>
  );
}

/** Linux AppImage: the app menu entry Kiln writes so taskbars and docks show its icon (desktop.rs). */
function ComputerGroup() {
  const t = useT();
  const qc = useQueryClient();
  const info = useAppInfo();
  const desktop = info.data?.desktop;
  if (!desktop) return null;
  const setDesktop = (on: boolean) => api.appDesktopSet(on).then((next) => {
    qc.setQueryData(qk.appInfo, (cur: api.AppInfo | undefined) => (cur ? { ...cur, desktop: next } : cur));
    toast.ok(on ? t("settings.appearance.desktopOn") : t("settings.appearance.desktopOff"));
  }).catch((e) => toast.error(e));
  return (
    <Group id="computer" items={["desktop"]}>
      <SettingRow id="desktop" htmlFor="st-desktop"
        control={<Toggle id="st-desktop" checked={desktop.on && desktop.installed} onChange={(on) => void setDesktop(on)} />} />
    </Group>
  );
}
