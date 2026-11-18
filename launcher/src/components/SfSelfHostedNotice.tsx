import { Link } from "react-router-dom";
import type { FoundReport, Profile } from "../api/commands";
import { sfSelfHostedFile, sfSelfHostedText } from "../shell/sfSelfHosted";
import { useT } from "../i18n";
import { Banner } from "../ui";

export function SfSelfHostedNotice({ profile, found }: { profile: Profile | null; found?: FoundReport }) {
  const t = useT();
  const file = sfSelfHostedFile(profile, found);
  if (!file) return null;
  return <div style={{ flexShrink: 0, overflowWrap: "anywhere" }}><Banner tone="warn" title={t("sfSelfHostedNotice.title")} actions={file.toLowerCase() === "dinput8.dll"
    ? <Link className="k-link" to="/runtime">{t("sfSelfHostedNotice.manage")}</Link> : undefined}>
    {sfSelfHostedText(file)}
  </Banner></div>;
}
