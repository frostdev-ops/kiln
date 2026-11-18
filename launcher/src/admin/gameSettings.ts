// The game's world settings (ranges are the ones the game engine reported). Live values come from settings_get / settings_set (kiln.settings).
// `key`, `group` and `options` are identifiers (sent to the server, grouped by); `label` and `hint` are called at use (the
// current language), `groupLabel` / `optionLabel` show a group or an option.
import { t } from "../i18n";

export type SettingKind = "factor" | "bool" | "enum" | "minutes" | "int";
export interface SettingSpec {
  key: string;
  group: string;
  label: () => string;
  kind: SettingKind;
  min?: number;
  max?: number;
  options?: string[];
  hint?: () => string;
}
type Text = () => string;

const f = (key: string, group: string, label: Text, min: number, max: number, hint?: Text): SettingSpec => ({ key, group, label, kind: "factor", min, max, hint });
const b = (key: string, group: string, label: Text, hint?: Text): SettingSpec => ({ key, group, label, kind: "bool", hint });
const e = (key: string, group: string, label: Text, options: string[], hint?: Text): SettingSpec => ({ key, group, label, kind: "enum", options, hint });
/** stored in nanoseconds, edited in minutes */
const m = (key: string, group: string, label: Text, min: number, max: number, hint?: Text): SettingSpec => ({ key, group, label, kind: "minutes", min, max, hint });
const minutes: Text = () => t("admin.gameSettings.minutes");

export const GAME_SETTINGS: SettingSpec[] = [
  f("playerHealthFactor", "Player", () => t("admin.gameSettings.playerHealthFactor"), 0.25, 4),
  f("playerManaFactor", "Player", () => t("admin.gameSettings.playerManaFactor"), 0.25, 4),
  f("playerStaminaFactor", "Player", () => t("admin.gameSettings.playerStaminaFactor"), 0.25, 4),
  f("playerBodyHeatFactor", "Player", () => t("admin.gameSettings.playerBodyHeatFactor"), 0.5, 2, () => t("admin.gameSettings.playerBodyHeatFactorHint")),
  f("playerDivingTimeFactor", "Player", () => t("admin.gameSettings.playerDivingTimeFactor"), 0.5, 2),
  f("shroudTimeFactor", "Player", () => t("admin.gameSettings.shroudTimeFactor"), 0.5, 2),
  b("enableDurability", "Survival", () => t("admin.gameSettings.enableDurability")),
  b("enableStarvingDebuff", "Survival", () => t("admin.gameSettings.enableStarvingDebuff")),
  f("foodBuffDurationFactor", "Survival", () => t("admin.gameSettings.foodBuffDurationFactor"), 0.5, 2),
  m("fromHungerToStarving", "Survival", () => t("admin.gameSettings.fromHungerToStarving"), 5, 20, minutes),
  e("tombstoneMode", "Survival", () => t("admin.gameSettings.tombstoneMode"), ["AddBackpackMaterials", "Everything", "NoTombstone"], () => t("admin.gameSettings.tombstoneModeHint")),
  b("enableGliderTurbulences", "Survival", () => t("admin.gameSettings.enableGliderTurbulences")),
  e("weatherFrequency", "World", () => t("admin.gameSettings.weatherFrequency"), ["Disabled", "Rare", "Normal", "Often"]),
  e("randomSpawnerAmount", "World", () => t("admin.gameSettings.randomSpawnerAmount"), ["Few", "Normal", "Many", "Extreme", "Automatic"]),
  { key: "flameLevelFogRemoval", group: "World", label: () => t("admin.gameSettings.flameLevelFogRemoval"), kind: "int", min: 0, max: 20, hint: () => t("admin.gameSettings.flameLevelFogRemovalHint") },
  m("dayTimeDuration", "Time", () => t("admin.gameSettings.dayTimeDuration"), 2, 60, minutes),
  m("nightTimeDuration", "Time", () => t("admin.gameSettings.nightTimeDuration"), 2, 60, minutes),
  f("miningDamageFactor", "Rates", () => t("admin.gameSettings.miningDamageFactor"), 0.5, 2),
  f("plantGrowthSpeedFactor", "Rates", () => t("admin.gameSettings.plantGrowthSpeedFactor"), 0.25, 2),
  f("resourceDropStackAmountFactor", "Rates", () => t("admin.gameSettings.resourceDropStackAmountFactor"), 0.25, 2),
  f("factoryProductionSpeedFactor", "Rates", () => t("admin.gameSettings.factoryProductionSpeedFactor"), 0.25, 2),
  f("perkUpgradeRecyclingFactor", "Rates", () => t("admin.gameSettings.perkUpgradeRecyclingFactor"), 0, 1),
  f("perkCostFactor", "Rates", () => t("admin.gameSettings.perkCostFactor"), 0.25, 2),
  f("experienceCombatFactor", "Rates", () => t("admin.gameSettings.experienceCombatFactor"), 0.25, 2),
  f("experienceMiningFactor", "Rates", () => t("admin.gameSettings.experienceMiningFactor"), 0, 2),
  f("experienceExplorationQuestsFactor", "Rates", () => t("admin.gameSettings.experienceExplorationQuestsFactor"), 0.25, 2),
  e("aggroPoolAmount", "Enemies", () => t("admin.gameSettings.aggroPoolAmount"), ["Few", "Normal", "Many", "Extreme"]),
  f("enemyDamageFactor", "Enemies", () => t("admin.gameSettings.enemyDamageFactor"), 0.25, 5),
  f("enemyHealthFactor", "Enemies", () => t("admin.gameSettings.enemyHealthFactor"), 0.25, 4),
  f("enemyStaminaFactor", "Enemies", () => t("admin.gameSettings.enemyStaminaFactor"), 0.5, 2),
  f("enemyPerceptionRangeFactor", "Enemies", () => t("admin.gameSettings.enemyPerceptionRangeFactor"), 0.5, 2),
  f("bossDamageFactor", "Enemies", () => t("admin.gameSettings.bossDamageFactor"), 0.2, 5),
  f("bossHealthFactor", "Enemies", () => t("admin.gameSettings.bossHealthFactor"), 0.2, 5),
  f("threatBonus", "Enemies", () => t("admin.gameSettings.threatBonus"), 0.25, 4),
  b("pacifyAllEnemies", "Enemies", () => t("admin.gameSettings.pacifyAllEnemies"), () => t("admin.gameSettings.pacifyAllEnemiesHint")),
  e("curseModifier", "Enemies", () => t("admin.gameSettings.curseModifier"), ["Easy", "Normal", "Hard"]),
  e("tamingStartleRepercussion", "Other", () => t("admin.gameSettings.tamingStartleRepercussion"), ["KeepProgress", "LoseSomeProgress", "LoseAllProgress"]),
  e("fishingDifficulty", "Other", () => t("admin.gameSettings.fishingDifficulty"), ["VeryEasy", "Easy", "Normal", "Hard", "VeryHard"]),
];

/** A settings group's heading (`group` stays the identifier). */
export function groupLabel(group: string): string {
  switch (group) {
    case "Player": return t("admin.gameSettings.group.player");
    case "Survival": return t("admin.gameSettings.group.survival");
    case "World": return t("admin.gameSettings.group.world");
    case "Time": return t("admin.gameSettings.group.time");
    case "Rates": return t("admin.gameSettings.group.rates");
    case "Enemies": return t("admin.gameSettings.group.enemies");
    case "Other": return t("admin.gameSettings.group.others");
    default: return group;
  }
}

/** An enum value as shown ("VeryEasy" → "Very Easy"); a value the launcher does not know is spaced out
 * ("SomeValue" → "Some Value"), as before. */
export function optionLabel(o: string): string {
  switch (o) {
    case "AddBackpackMaterials": return t("admin.gameSettings.option.addBackpackMaterials");
    case "Everything": return t("admin.gameSettings.option.everything");
    case "NoTombstone": return t("admin.gameSettings.option.noTombstone");
    case "Disabled": return t("admin.gameSettings.option.disabled");
    case "Rare": return t("admin.gameSettings.option.rare");
    case "Normal": return t("admin.gameSettings.option.normal");
    case "Often": return t("admin.gameSettings.option.often");
    case "Few": return t("admin.gameSettings.option.fewAmount");
    case "Many": return t("admin.gameSettings.option.manyAmount");
    case "Extreme": return t("admin.gameSettings.option.extreme");
    case "Automatic": return t("admin.gameSettings.option.automatic");
    case "Easy": return t("admin.gameSettings.option.easy");
    case "Hard": return t("admin.gameSettings.option.hard");
    case "KeepProgress": return t("admin.gameSettings.option.keepProgress");
    case "LoseSomeProgress": return t("admin.gameSettings.option.loseSomeProgress");
    case "LoseAllProgress": return t("admin.gameSettings.option.loseAllProgress");
    case "VeryEasy": return t("admin.gameSettings.option.veryEasy");
    case "VeryHard": return t("admin.gameSettings.option.veryHard");
    default: return String(o).replace(/([a-z])([A-Z])/g, "$1 $2").replace(/^./, (c) => c.toUpperCase());
  }
}

export const NS_PER_MIN = 60e9;

/** Validates one game setting; the value to send, or an error. `current` lets an unknown enum
 * value the game reported stay as it is. */
export function checkSetting(spec: SettingSpec, v: unknown, current?: unknown): { value?: number | boolean | string; error?: string } {
  const { min = 0, max = 0 } = spec;
  const label = spec.label();
  switch (spec.kind) {
    case "bool":
      return typeof v === "boolean" ? { value: v } : { error: t("admin.gameSettings.errOnOff", { label }) };
    case "enum":
      return typeof v === "string" && ((spec.options ?? []).includes(v) || v === current) ? { value: v } : { error: t("admin.gameSettings.errOption", { label }) };
    case "minutes":
      return typeof v === "number" && Number.isInteger(v) && v >= min * NS_PER_MIN && v <= max * NS_PER_MIN ? { value: v } : { error: t("admin.gameSettings.errMinutes", { label, min, max }) };
    case "int":
      return typeof v === "number" && Number.isInteger(v) && v >= min && v <= max ? { value: v } : { error: t("admin.gameSettings.errRange", { label, min, max }) };
    default: {
      if (typeof v !== "number" || !Number.isFinite(v) || v < min || v > max) return { error: t("admin.gameSettings.errRange", { label, min, max }) };
      return { value: Math.round(v * 1000) / 1000 };
    }
  }
}

/** Display value: minutes for ns settings. */
export const shownSetting = (spec: SettingSpec, v: unknown) => (spec.kind === "minutes" && typeof v === "number" ? Math.round(v / NS_PER_MIN) : v);
