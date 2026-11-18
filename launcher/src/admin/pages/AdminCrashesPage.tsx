// /servers/:id/admin/crashes: Kiln's crash reports (kiln/crash-reports: the faulting mod, the
// exception, the mod list and the log around it) through crash_reports; the same folder while the server is stopped
// through the launcher's logs_crash_reports.
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useAdminQuery, useAdminState, useHasOp } from "../../api/admin";
import { logsCrashReports } from "../../api/commands";
import { useT } from "../../i18n";
import { useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import type { CrashReportInfo } from "../types";
import { Badge, Banner, Button, Card, EmptyState, Page, PageHeader, TextInput, fmtAgo, fmtBytes, fmtTime } from "../ui";

function CrashesPage() {
  const t = useT();
  const id = useServerId();
  const st = useAdminState();
  const has = useHasOp("crash_reports");
  const live = useAdminQuery<CrashReportInfo[]>("crash_reports", {}, { refetchInterval: 20_000 });
  // stopped (or an older Kiln.Admin): the launcher reads the folder itself
  const disk = useQuery({ queryKey: ["logs_crash_reports", id], queryFn: () => logsCrashReports({ server: id }), enabled: !st.session || !has, retry: false });
  const viaOp = !!st.session && has;
  const list: CrashReportInfo[] = viaOp ? live.data ?? [] : (disk.data ?? []).map((c) => ({ name: c.name, bytes: c.size, time: c.created }));
  const [open, setOpen] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  const cur = open ?? list[0]?.name ?? null;
  const report = useAdminQuery<{ name: string; text: string }>("crash_reports", { name: cur ?? "" }, { enabled: !!cur && viaOp && !!live.data?.some((c) => c.name === cur), staleTime: Infinity });
  const since = st.connectedAt;
  const text = report.data?.text ?? "";
  const shown = filter ? text.split("\n").filter((l) => l.toLowerCase().includes(filter.toLowerCase())).join("\n") : text;
  return (
    <Page>
      <PageHeader title={t("admin.crashes.title")} sub={t("admin.crashes.sub")} />
      <OfflineNote />
      {!has && st.session ? <Banner tone="ember" title={t("admin.common.needsNewer")}>{t("admin.crashes.noOp")}</Banner> : null}
      <div className="ka-split list-left">
        <Card title={t("admin.crashes.reports")} sub={t("admin.crashes.reportsSub", { n: list.length })} flush>
          <div className="ka-pick" style={{ padding: 6 }}>
            {list.map((c) => (
              <button key={c.name} className={c.name === cur ? "active" : ""} onClick={() => setOpen(c.name)}>
                <span className="k-mono" style={{ fontSize: 12 }}>{c.name.replace(/^crash-/, "").replace(/\.txt$/, "")}</span>
                {since && Date.parse(c.time) > since - 60_000 ? <Badge tone="warn">{t("admin.crashes.new")}</Badge> : null}
                <small>{fmtAgo(c.time)}</small>
              </button>
            ))}
            {!list.length ? <EmptyState title={t("admin.crashes.empty")} hint={t("admin.crashes.emptyHint")} /> : null}
            {live.error && has ? <div className="k-error-text" style={{ padding: 8 }}>{live.error.message}</div> : null}
            {disk.error && (!has || !st.session) ? <div className="k-hint" style={{ padding: 8 }}>{disk.error.message}</div> : null}
          </div>
        </Card>
        {cur ? (
          <Card title={<span className="k-mono" style={{ fontSize: 13 }}>{cur}</span>} sub={(() => { const c = list.find((x) => x.name === cur); return c ? `${fmtTime(c.time)} · ${fmtBytes(c.bytes)}` : undefined; })()}
            actions={<>
              <TextInput aria-label={t("admin.crashes.filterLabel")} placeholder={t("admin.crashes.filterPlaceholder")} value={filter} onChange={(e) => setFilter(e.target.value)} style={{ width: 180 }} />
              <Button size="s" variant="ghost" disabled={!text} onClick={() => navigator.clipboard?.writeText(text)}>{t("common.copy")}</Button>
            </>}>
            {report.error ? <Banner tone="warn">{report.error.message}</Banner> : null}
            {!has ? <div className="k-muted">{t("admin.crashes.openOnServer", { name: cur })}</div> : null}
            {has && !report.data && !report.error ? <div className="k-muted">{t("admin.players.reading")}</div> : null}
            {report.data ? <pre className="ka-pre">{shown}</pre> : null}
            {report.data && text.length >= 200_000 ? <div className="k-hint">{t("admin.crashes.first200")}</div> : null}
          </Card>
        ) : <Card><EmptyState title={t("admin.crashes.pick")} /></Card>}
      </div>
    </Page>
  );
}

export default withAdmin(CrashesPage);
