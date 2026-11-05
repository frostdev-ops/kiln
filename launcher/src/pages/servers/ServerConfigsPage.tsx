// /servers/:id/configs: the server's mod configs. Offline edits through the JSONC editor;
// a running server's configs are edited live on the admin Configs page.
import { useParams } from "react-router-dom";
import { ConfigEditor } from "../../components/ConfigEditor";

export default function ServerConfigsPage() {
  const { id = "" } = useParams();
  return <ConfigEditor target={{ server: id }} />;
}
