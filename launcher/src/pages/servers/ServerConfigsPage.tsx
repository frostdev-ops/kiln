// /servers/:id/configs: the server's mod configs. While it runs: live, through Kiln.Admin (each change checked by Kiln
// and applied at once, except entries marked Restart). While it is stopped: the files (comments kept), applied at the
// next start.
import { useParams } from "react-router-dom";
import AdminConfigsPage from "../../admin/pages/AdminConfigsPage";
import { ConfigEditor } from "../../components/ConfigEditor";
import { Page, PageHeader } from "../../ui";
import { useServerStatus } from "./serverApi";
import { useT } from "../../i18n";

export default function ServerConfigsPage() {
  const t = useT();
  const { id = "" } = useParams();
  const st = useServerStatus(id);
  if (st.data?.state === "running") return <AdminConfigsPage />;
  return (
    <Page>
      <PageHeader title={t("servers.configs.title")} sub={t("servers.configs.sub")} />
      <ConfigEditor target={{ server: id }} />
    </Page>
  );
}
