import type { GameInstall, Profile, ShroudlinkNotice } from "../api/commands";
import { t } from "../i18n";
// The English of the two sentences (the unit tests compare with them); shroudlinkNoticeText says them in the current
// language.
export const SHROUDLINK_NOTICE = "Shroudlink's MODS tab comes from its client addon, which this profile does not load. Your Shroudlink mods still run; switch the addon on to get the tab.";
export const SHROUDLINK_DOWNLOAD = "Get the client addon from the Shroudlink download and add it to the Library.";
export function shroudlinkNoticeText(game: Pick<GameInstall, "kind"> | null, profileId: string | null, profile: Profile | null, notice: ShroudlinkNotice | null | undefined): string | null {
  if (game?.kind !== "client" || !profileId || profile?.id !== profileId || !profile.mods.shroudlink?.some((m) => m.enabled) || !notice) return null;
  return notice.addons.length ? t("shell.shroudlinkNotice.notice") : t("shell.shroudlinkNotice.noticeDownload");
}
