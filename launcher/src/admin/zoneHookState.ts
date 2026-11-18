import { t } from "../i18n";
import type { GameplayStatus } from "./types";

export function zoneHookNotices(status?: GameplayStatus): string[] {
  if (!status?.available) return [];
  const notices: string[] = [];
  for (const [group, flag] of [["build", "buildHooks"], ["terraform", "terraformHook"], ["spawn", "spawnHook"]] as const) {
    const row = status.hookGroups?.[group];
    if (row?.state === "installed" || (!row || row.state === "unknown") && status[flag]) continue;
    const label = group === "build" ? t("admin.zoneHooks.build") : group === "terraform" ? t("admin.zoneHooks.terraform") : t("admin.zoneHooks.spawn");
    notices.push(row?.state === "idle" ? t("admin.zoneHooks.idle", { label }) : row?.state === "waiting" ? t("admin.zoneHooks.waiting", { label, reason: row.reason }) :
      row?.state === "refused" ? t("admin.zoneHooks.refused", { label, reason: row.reason.replace(/^refused:\s*/i, "") }) : t("admin.zoneHooks.unknown", { label }));
  }
  // CraftRecipe observes completed recipes. Craft protection, like opening and looting, uses Pre interception.
  if (status.intercept === false) notices.push(t("admin.zoneHooks.noIntercept"));
  return notices;
}
