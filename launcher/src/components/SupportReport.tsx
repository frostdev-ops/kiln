// "Support report": one zip with what a maintainer needs when mods don't load (report_save), and a few lines to paste
// into a post (report_summary). Both are redacted by kiln_manage::report: names and paths never leave the machine.
// Used by the Logs page (a game or a local server; at the top of every tab), where Play's problem banners point.
// docs/kiln/launcher/support-report.md
import { useState } from "react";
import * as api from "../api/commands";
import type { LogTarget } from "../api/commands";
import { isTauri } from "../api/commands";
import { t, useT } from "../i18n";
import { Button, fmtBytes, toast } from "../ui";

/** `kiln-report-2026-11-10.zip` (the crate's report::file_name, in local time). */
export function reportFileName(d = new Date()): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `kiln-report-${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}.zip`;
}

async function choosePath(): Promise<string | null> {
  if (!isTauri()) return reportFileName(); // mock mode: no dialog
  const { save } = await import("@tauri-apps/plugin-dialog");
  return save({ title: t("supportReport.dialogTitle"), defaultPath: reportFileName(), filters: [{ name: "Zip", extensions: ["zip"] }] });
}

/** The block: one sentence, "Save report...", "Copy summary". `target` null: the system and the launcher only. */
export function SupportReport({ target, compact }: { target: LogTarget | null; compact?: boolean }) {
  const t = useT();
  const [busy, setBusy] = useState<"save" | "copy" | null>(null);
  const save = async () => {
    try {
      const path = await choosePath();
      if (!path) return;
      setBusy("save");
      const r = await api.reportSave(target, path);
      const problems = r.findings.filter((f) => f.level === "problem").length;
      const folder = r.path.replace(/[\\/][^\\/]*$/, "");
      toast.ok(t("supportReport.saved"), [
        t("supportReport.savedFile", { file: r.path.split(/[\\/]/).pop(), size: fmtBytes(r.size) }),
        problems ? t("supportReport.problems", { count: problems }) : null,
        t("supportReport.attach"),
      ].filter(Boolean).join(" "),
        { actions: isTauri() && folder && folder !== r.path ? [{ label: t("supportReport.openFolder"), onClick: () => { api.appOpenPath(folder).catch((e) => toast.error(e)); } }] : [] });
    } catch (e) {
      toast.error(e, t("supportReport.notSaved"));
    } finally {
      setBusy(null);
    }
  };
  const copy = async () => {
    setBusy("copy");
    try {
      const text = await api.reportSummary(target);
      if (!navigator.clipboard) throw new Error(t("supportReport.noClipboard"));
      await navigator.clipboard.writeText(text);
      toast.ok(t("supportReport.copied"), t("supportReport.copiedDetail"));
    } catch (e) {
      toast.error(e, t("supportReport.copyFailed"));
    } finally {
      setBusy(null);
    }
  };
  return (
    <div className="k-list-item" data-testid="support-report" style={{ flexWrap: "wrap", rowGap: 6, padding: compact ? "6px 0" : "10px 0" }}>
      <span className="k-word-sm">{t("supportReport.title")}</span>
      <span className="k-hint" style={{ flex: "1 1 260px" }}>
        {t("supportReport.hint")}
      </span>
      <Button size="s" loading={busy === "save"} disabled={!!busy} onClick={save}>{t("supportReport.save")}</Button>
      <Button size="s" variant="ghost" loading={busy === "copy"} disabled={!!busy} onClick={copy}>{t("supportReport.copy")}</Button>
    </div>
  );
}
