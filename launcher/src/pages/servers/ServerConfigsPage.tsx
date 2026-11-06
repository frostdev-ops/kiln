// /servers/:id/configs: the server's mod configs. While it runs: live, through Kiln.Admin (each change checked by Kiln
// and applied at once, except entries marked Restart). While it is stopped: the files (comments kept), applied at the
// next start.
import { useParams } from "react-router-dom";
import AdminConfigsPage from "../../admin/pages/AdminConfigsPage";
import { ConfigEditor } from "../../components/ConfigEditor";
import { Page, PageHeader } from "../../ui";
import { useServerStatus } from "./serverApi";

export default function ServerConfigsPage() {
  const { id = "" } = useParams();
  const st = useServerStatus(id);
  if (st.data?.state === "running") return <AdminConfigsPage />;
  return (
    <Page>
      <PageHeader title="Configs" sub="The server is stopped: changes go to the files and apply at the next start." />
      <ConfigEditor target={{ server: id }} />
    </Page>
  );
}
