// The game's world settings (ranges are the ones the game engine reported). Live values come from settings_get / settings_set (kiln.settings).

export type SettingKind = "factor" | "bool" | "enum" | "minutes" | "int";
export interface SettingSpec {
  key: string;
  group: string;
  label: string;
  kind: SettingKind;
  min?: number;
  max?: number;
  options?: string[];
  hint?: string;
}

const f = (key: string, group: string, label: string, min: number, max: number, hint = ""): SettingSpec => ({ key, group, label, kind: "factor", min, max, hint });
const b = (key: string, group: string, label: string, hint = ""): SettingSpec => ({ key, group, label, kind: "bool", hint });
const e = (key: string, group: string, label: string, options: string[], hint = ""): SettingSpec => ({ key, group, label, kind: "enum", options, hint });
/** stored in nanoseconds, edited in minutes */
const m = (key: string, group: string, label: string, min: number, max: number, hint = ""): SettingSpec => ({ key, group, label, kind: "minutes", min, max, hint });

export const GAME_SETTINGS: SettingSpec[] = [
  f("playerHealthFactor", "Player", "Health", 0.25, 4),
  f("playerManaFactor", "Player", "Mana", 0.25, 4),
  f("playerStaminaFactor", "Player", "Stamina", 0.25, 4),
  f("playerBodyHeatFactor", "Player", "Body heat", 0.5, 2, "How long you stay warm in the cold"),
  f("playerDivingTimeFactor", "Player", "Diving time", 0.5, 2),
  f("shroudTimeFactor", "Player", "Time in the Shroud", 0.5, 2),
  b("enableDurability", "Survival", "Item durability"),
  b("enableStarvingDebuff", "Survival", "Starving debuff"),
  f("foodBuffDurationFactor", "Survival", "Food buff duration", 0.5, 2),
  m("fromHungerToStarving", "Survival", "Hunger to starving", 5, 20, "Minutes"),
  e("tombstoneMode", "Survival", "On death", ["AddBackpackMaterials", "Everything", "NoTombstone"], "What goes into your tombstone"),
  b("enableGliderTurbulences", "Survival", "Glider turbulence"),
  e("weatherFrequency", "World", "Weather", ["Disabled", "Rare", "Normal", "Often"]),
  e("randomSpawnerAmount", "World", "Random enemy spawns", ["Few", "Normal", "Many", "Extreme", "Automatic"]),
  { key: "flameLevelFogRemoval", group: "World", label: "Fog removal flame level", kind: "int", min: 0, max: 20, hint: "Flame level at which Shroud fog lifts" },
  m("dayTimeDuration", "Time", "Day length", 2, 60, "Minutes"),
  m("nightTimeDuration", "Time", "Night length", 2, 60, "Minutes"),
  f("miningDamageFactor", "Rates", "Mining damage", 0.5, 2),
  f("plantGrowthSpeedFactor", "Rates", "Plant growth", 0.25, 2),
  f("resourceDropStackAmountFactor", "Rates", "Resource drops", 0.25, 2),
  f("factoryProductionSpeedFactor", "Rates", "Crafting station speed", 0.25, 2),
  f("perkUpgradeRecyclingFactor", "Rates", "Perk recycling refund", 0, 1),
  f("perkCostFactor", "Rates", "Perk cost", 0.25, 2),
  f("experienceCombatFactor", "Rates", "Combat XP", 0.25, 2),
  f("experienceMiningFactor", "Rates", "Mining XP", 0, 2),
  f("experienceExplorationQuestsFactor", "Rates", "Exploration & quest XP", 0.25, 2),
  e("aggroPoolAmount", "Enemies", "Simultaneous attackers", ["Few", "Normal", "Many", "Extreme"]),
  f("enemyDamageFactor", "Enemies", "Enemy damage", 0.25, 5),
  f("enemyHealthFactor", "Enemies", "Enemy health", 0.25, 4),
  f("enemyStaminaFactor", "Enemies", "Enemy stamina", 0.5, 2),
  f("enemyPerceptionRangeFactor", "Enemies", "Enemy perception", 0.5, 2),
  f("bossDamageFactor", "Enemies", "Boss damage", 0.2, 5),
  f("bossHealthFactor", "Enemies", "Boss health", 0.2, 5),
  f("threatBonus", "Enemies", "Threat bonus", 0.25, 4),
  b("pacifyAllEnemies", "Enemies", "Pacify all enemies", "Enemies only fight back"),
  e("curseModifier", "Enemies", "Curse strength", ["Easy", "Normal", "Hard"]),
  e("tamingStartleRepercussion", "Other", "Startling a tame", ["KeepProgress", "LoseSomeProgress", "LoseAllProgress"]),
  e("fishingDifficulty", "Other", "Fishing difficulty", ["VeryEasy", "Easy", "Normal", "Hard", "VeryHard"]),
];

export const NS_PER_MIN = 60e9;

/** Validates one game setting; the value to send, or an error. `current` lets an unknown enum
 * value the game reported stay as it is. */
export function checkSetting(spec: SettingSpec, v: unknown, current?: unknown): { value?: number | boolean | string; error?: string } {
  const { label, min = 0, max = 0 } = spec;
  switch (spec.kind) {
    case "bool":
      return typeof v === "boolean" ? { value: v } : { error: `${label}: on or off.` };
    case "enum":
      return typeof v === "string" && ((spec.options ?? []).includes(v) || v === current) ? { value: v } : { error: `${label}: unknown option.` };
    case "minutes":
      return typeof v === "number" && Number.isInteger(v) && v >= min * NS_PER_MIN && v <= max * NS_PER_MIN ? { value: v } : { error: `${label}: ${min} to ${max} minutes.` };
    case "int":
      return typeof v === "number" && Number.isInteger(v) && v >= min && v <= max ? { value: v } : { error: `${label}: ${min} to ${max}.` };
    default: {
      if (typeof v !== "number" || !Number.isFinite(v) || v < min || v > max) return { error: `${label}: ${min} to ${max}.` };
      return { value: Math.round(v * 1000) / 1000 };
    }
  }
}

/** Display value: minutes for ns settings. */
export const shownSetting = (spec: SettingSpec, v: unknown) => (spec.kind === "minutes" && typeof v === "number" ? Math.round(v / NS_PER_MIN) : v);
