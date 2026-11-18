// Settings, Language: the launcher's language (LauncherSettings.language, applied at once: src/i18n) and the language of
// Kiln's own texts in the game (kiln.json `ui.language`: "auto" follows the game's text language, else a tag; the
// runtime's locales/<tag>.json answer it). docs/kiln/translating.md.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { RefreshCw } from "lucide-react";
import * as api from "../api/commands";
import type { Json } from "../api/commands";
import { qk, useGame, useGameStatus, useSettings } from "../api/hooks";
import { saveSettings } from "../deck/mode";
import { languageName, languages, useT } from "../i18n";
import { applyLanguage, userLocalesKey, useUserLocales } from "../i18n/LanguageSync";
import { Card, Field, IconButton, PathText, Select, getPath, toast } from "../ui";

/** The game's text languages Kiln's in-game texts may be asked for (Kiln.Ui GameLanguage; the in-game side falls back
 * to English for one it has no file for). */
export const GAME_LANGUAGES = [
  "en", "de", "fr", "es", "es-MX", "it", "pl", "pt-BR", "pt-PT", "ru", "uk", "tr", "nl", "cs", "hu", "ja", "ko", "th",
  "zh-CN", "zh-TW",
] as const;

/** The kiln.json value for "follow the game's language". */
export const GAME_AUTO = "auto";

export function LanguageCard() {
  const t = useT();
  const qc = useQueryClient();
  const s = useSettings();
  const files = useUserLocales();
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

  return (
    <Card title={t("languageSettings.title")}>
      <Field label={t("languageSettings.launcher")} htmlFor="st-lang" hint={t("languageSettings.launcherHint")}>
        <Select id="st-lang" value={launcherOptions.find((o) => o.value.toLowerCase() === pref.toLowerCase())?.value ?? pref}
          disabled={!s.data} onChange={choose} options={launcherOptions} />
      </Field>
      {files.data && (
        <div className="k-row k-hint" style={{ gap: 6, flexWrap: "wrap" }}>
          <span>{t("languageSettings.files")}</span>
          <PathText path={files.data.dir} />
          <IconButton size="s" label={t("languageSettings.filesReload")} icon={<RefreshCw />}
            onClick={() => void qc.fetchQuery({ queryKey: userLocalesKey, staleTime: 0 }).then(
              (r) => toast.ok(t("languageSettings.filesRead", { count: (r as { tags: string[] }).tags.length })),
              (e) => toast.error(e))} />
          {files.data.errors.map((e) => <span key={e.file} className="k-error-text">{t("languageSettings.filesBad", e.file, e.message)}</span>)}
        </div>
      )}
      <Field label={t("languageSettings.game")} htmlFor="st-lang-game" hint={gameBlocked ?? t("languageSettings.gameHint")}>
        <Select id="st-lang-game" value={gameOptions.find((o) => o.value.toLowerCase() === ingame.toLowerCase())?.value ?? ingame}
          disabled={!!gameBlocked || !doc.data || setGame.isPending} onChange={(v) => setGame.mutate(v)} options={gameOptions} />
      </Field>
    </Card>
  );
}
