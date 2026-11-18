import { useNavigate } from "react-router-dom";
import type { PlayControl } from "./play";
import { Banner, Button } from "../ui";
import { DeckButton } from "../deck/ui";
import { stopFilter, stopSentence, stopShowsReason } from "./stoppedStart";
import { useT } from "../i18n";

export function StoppedStartNotice({ ctl, deck = false }: { ctl: PlayControl; deck?: boolean }) {
  const t = useT();
  const nav = useNavigate();
  const s = ctl.stoppedStart;
  if (!s) return null;
  const review = () => nav(`${deck ? "/deck/mods" : "/"}?filter=${encodeURIComponent(stopFilter(s.mods))}`);
  const body = <><p>{stopSentence(s)}</p>{stopShowsReason(s) && <p>{s.reason}</p>}<p>{s.advice}</p></>;
  return <section role="region" aria-label={t("shell.play.stoppedStart")} className="k-stop-notice" tabIndex={deck ? undefined : 0} style={{ overflowWrap: "anywhere", flexShrink: 0, maxHeight: deck ? undefined : "min(280px, 42vh)", overflowY: deck ? undefined : "auto" }}>
    {deck ? <><p role="status">{t("shell.play.stoppedStart")}</p>{body}<div className="deck-home-actions">
      {s.mods.length > 0 && <DeckButton id="stop-mods" onClick={review}>{t("shell.stoppedStart.review")}</DeckButton>}
      <DeckButton id="stop-dismiss" onClick={ctl.dismissStop}>{t("common.dismiss")}</DeckButton>
    </div></> : <Banner tone="warn" title={t("shell.play.stoppedStart")} actions={<>
      {s.mods.length > 0 && <Button size="s" onClick={review}>{t("shell.stoppedStart.review")}</Button>}
      <Button size="s" onClick={ctl.dismissStop}>{t("common.dismiss")}</Button>
    </>}>{body}</Banner>}
  </section>;
}
