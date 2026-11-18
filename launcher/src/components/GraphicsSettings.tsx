// Settings, Graphics (dev.29.18): Kiln's own graphics tools in the game, the "graphics" section of the game's
// kiln/kiln.json (frame cap, DLSS preset and render scale, readout). The same document, query and mutations as the
// kiln.json card (pages/SettingsPage.tsx, which leaves these entries out); the game reads the file again while it runs.
// Under the form: the tools in the game folder next to which Kiln's frame cap and DLSS settings step aside
// (GameStatus.graphicsStepAside, kiln_manage::game::graphics_step_aside).
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import * as api from "../api/commands";
import type { ConfigDocument, GraphicsStepAside, Json, SchemaSnapshot } from "../api/commands";
import { qk, useGame, useGameStatus } from "../api/hooks";
import { t as tr, useT } from "../i18n";
import { Banner, Card, EmptyState, SchemaForm, Skeleton, errorText, toast } from "../ui";

/** The kiln.json section the Graphics card edits. */
export const GRAPHICS_SECTION = "graphics";

/** A kiln.json entry of the Graphics card (`graphics.*`). */
export const isGraphicsPath = (path: string) => path.startsWith(GRAPHICS_SECTION + ".");

/** The Graphics card's form: the `graphics.*` entries alone, no section head (the card's title says it). */
export function graphicsSchema(schema: SchemaSnapshot): SchemaSnapshot {
  return { ...schema, sections: [], entries: schema.entries.filter((e) => isGraphicsPath(e.path)) };
}

/** The card's field labels by kiln.json path (SchemaForm's `labels`): the key names ("fpsCap", "dlssScale") do not read as
 *  words. Every `graphics.*` entry of the snapshot has one (unit/graphics-settings.test.tsx). */
export function graphicsLabels(): Record<string, string> {
  return {
    "graphics.fpsCap": tr("settings.graphics.labels.fpsCap"),
    "graphics.dlssPreset": tr("settings.graphics.labels.dlssPreset"),
    "graphics.dlssScale": tr("settings.graphics.labels.dlssScale"),
    "graphics.dlssRenderScale": tr("settings.graphics.labels.dlssRenderScale"),
    "graphics.dlssQualityScale": tr("settings.graphics.labels.dlssQualityScale"),
    "graphics.dlssBalancedScale": tr("settings.graphics.labels.dlssBalancedScale"),
    "graphics.dlssPerformanceScale": tr("settings.graphics.labels.dlssPerformanceScale"),
    "graphics.dlssUltraPerformanceScale": tr("settings.graphics.labels.dlssUltraPerformanceScale"),
    "graphics.readout": tr("settings.graphics.labels.readout"),
  };
}

/** One line for a tool Kiln's graphics tools step aside for: what it is (naming the file) and what steps aside. */
export function graphicsAsideText(a: GraphicsStepAside): string {
  const tools: Record<string, string> = {
    optiscaler: tr("settings.graphics.aside.optiscaler", { file: a.file }),
    streamline: tr("settings.graphics.aside.streamline", { file: a.file }),
    vkfg: tr("settings.graphics.aside.vkfg", { file: a.file }),
    dlsstweaks: tr("settings.graphics.aside.dlsstweaks", { file: a.file }),
    nvngx: tr("settings.graphics.aside.nvngx", { file: a.file }),
  };
  const tool = tools[a.kind] ?? tr("settings.graphics.aside.unknown", { file: a.file });
  const effect = a.frameCap ? tr("settings.graphics.aside.frameCap") : tr("settings.graphics.aside.dlss");
  return tr("settings.graphics.aside.line", { tool, effect });
}

export function GraphicsCard() {
  const t = useT();
  const qc = useQueryClient();
  const { game } = useGame();
  const status = useGameStatus(game?.id);
  const key = qk.kilnJson(game?.id ?? "");
  const doc = useQuery({ queryKey: key, queryFn: () => api.configKilnRead(game!.id), enabled: !!game });
  const onDoc = (d: ConfigDocument) => qc.setQueryData(key, d);
  const set = useMutation({ mutationFn: (v: { path: string; value: Json }) => api.configKilnSet(game!.id, v.path, v.value), onSuccess: onDoc, onError: (e) => toast.error(e) });
  const reset = useMutation({ mutationFn: (path: string) => api.configKilnReset(game!.id, path), onSuccess: onDoc, onError: (e) => toast.error(e) });
  // a dedicated server draws no frames: the card is for a client
  if (!game || game.kind !== "client") return null;
  const installed = status.data?.runtime.installed;
  const aside = status.data?.graphicsStepAside ?? [];
  const schema = doc.data?.schema ? graphicsSchema(doc.data.schema) : null;
  return (
    <Card id="graphics" title={t("settings.graphics.title")} sub={t("settings.graphics.sub")} aria-label={t("settings.graphics.title")}>
      {status.data && !installed ? <EmptyState title={t("settings.kilnJson.notInstalled")} hint={t("settings.kilnJson.notInstalledHint")} /> :
        doc.isLoading ? <Skeleton lines={4} /> : doc.error ? <EmptyState title={t("settings.kilnJson.unavailable")} hint={errorText(doc.error)} /> : schema && (
          <SchemaForm schema={schema} values={doc.data!.values} errors={doc.data!.errors.filter((e) => isGraphicsPath(e.path))} disabled={set.isPending || reset.isPending} labels={graphicsLabels()}
            onChange={(path, value) => set.mutate({ path, value })} onReset={(path) => reset.mutate(path)} />
        )}
      {aside.length > 0 && (
        <div className="k-stack" style={{ gap: 6 }}>
          {aside.map((a) => (
            <Banner key={`${a.kind}-${a.file}`} tone="info">
              <span data-testid="graphics-step-aside">{graphicsAsideText(a)}</span>
            </Banner>
          ))}
        </div>
      )}
    </Card>
  );
}
