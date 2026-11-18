import { useQuery } from "@tanstack/react-query";
import * as api from "../api/commands";
import { usePoll } from "../api/hooks";
import { useT } from "../i18n";
import type { useMods } from "../shell/useMods";
import { shroudlinkNoticeText } from "../shell/shroudlinkNotice";
import { ownerOf } from "../shell/selection";
import { Banner, Button } from "../ui";
import { DeckButton } from "../deck/ui";

export function ShroudlinkNotice({ game, mods, deck = false }: { game: api.GameInstall | null; mods: ReturnType<typeof useMods>; deck?: boolean }) {
  const t = useT();
  const profile = mods.profile;
  const owner = ownerOf(mods.profileId, profile);
  const enabled = game?.kind === "client" && !!owner && !!profile?.mods.shroudlink?.some((m) => m.enabled);
  const query = useQuery({
    queryKey: ["shroudlink_notice", game?.id, owner, profile, mods.rows.map((r) => r.lib)],
    queryFn: () => api.shroudlinkNotice(profile!, game!.id), enabled, refetchInterval: usePoll(4000),
  });
  const text = shroudlinkNoticeText(game, mods.profileId, profile, query.data);
  if (!text) return null;
  // The existing list toggle adds an absent entry, or enables the selected Library version.
  const addon = query.data?.addons.find((a) => mods.rows.some((r) => r.kind === "native" && r.id === a.id));
  const row = addon && mods.rows.find((r) => r.kind === "native" && r.id === addon.id);
  const Action = deck ? DeckButton : Button;
  return <Banner title={t("shroudlinkNotice.title")} actions={row && addon ? <Action id="shroudlink-addon-on" onClick={() => {
    mods.edit.update((p) => {
      const entry = { id: addon.id, version: addon.version, hash: addon.hash, enabled: true };
      const list = p.mods.native ?? [];
      p.mods.native = list.some((m) => m.id === addon.id) ? list.map((m) => m.id === addon.id ? entry : m) : [...list, entry];
    });
  }}>{t("shroudlinkNotice.switchOn")}</Action> : undefined}>{text}</Banner>;
}
