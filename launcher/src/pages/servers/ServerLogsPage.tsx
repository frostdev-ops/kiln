// /servers/:id/logs: the server's log files: kiln.log, the game log, proxy.log, mod logs. The live console is on the
// Overview; crash reports and tick stats have their own entries in the server's list.
import { useParams } from "react-router-dom";
import { LogsFor } from "../LogsPage";

export default function ServerLogsPage() {
  const { id = "" } = useParams();
  return <LogsFor target={{ server: id }} embedded tabs={["kiln", "game", "proxy", "mods"]} />;
}
