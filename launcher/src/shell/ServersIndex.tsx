// /servers without a servers page (src/pages/servers/ServersPage.tsx): opens the first
// server, or says there is none.
import { useQuery } from "@tanstack/react-query";
import { Server } from "lucide-react";
import { Navigate } from "react-router-dom";
import { serverList } from "../api/commands";
import { EmptyState, Page, Skeleton, errorText } from "../ui";
import { useT } from "../i18n";

export function ServersIndex() {
  const t = useT();
  const q = useQuery({ queryKey: ["server_list"], queryFn: serverList });
  if (q.isLoading) return <Page><Skeleton /></Page>;
  if (q.data?.length) return <Navigate to={`/servers/${q.data[0].id}/overview`} replace />;
  return (
    <Page>
      <EmptyState icon={<Server />} title={t("shell.serversIndex.none")} hint={q.error ? errorText(q.error) : t("shell.serversIndex.hint")} />
    </Page>
  );
}
