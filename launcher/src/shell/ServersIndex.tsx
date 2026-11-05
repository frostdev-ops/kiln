// /servers without a servers page (src/pages/servers/ServersPage.tsx): opens the first
// server, or says there is none.
import { useQuery } from "@tanstack/react-query";
import { Server } from "lucide-react";
import { Navigate } from "react-router-dom";
import { serverList } from "../api/commands";
import { EmptyState, Page, Skeleton, errorText } from "../ui";

export function ServersIndex() {
  const q = useQuery({ queryKey: ["server_list"], queryFn: serverList });
  if (q.isLoading) return <Page><Skeleton /></Page>;
  if (q.data?.length) return <Navigate to={`/servers/${q.data[0].id}/overview`} replace />;
  return (
    <Page>
      <EmptyState icon={<Server />} title="No servers" hint={q.error ? errorText(q.error) : "Local and remote dedicated servers show here."} />
    </Page>
  );
}
