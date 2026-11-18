import type { FoundReport, Profile } from "../api/commands";
import { t } from "../i18n";

export function sfSelfHostedText(file: string): string {
  return t("shell.sfSelfHosted.text", { file });
}

export function sfSelfHostedFile(profile: Profile | null | undefined, found: FoundReport | null | undefined): string | null {
  if (!profile?.mods.shroudforge?.length) return null;
  return found?.loaders.find((l) => l.engine === "shroudforge")?.file ?? null;
}
