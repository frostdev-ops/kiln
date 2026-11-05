// /servers/:id/logs: the live Console (the server's output; ServerConsolePage.tsx) and the log files: kiln.log, the
// game log, mod logs and crash reports of the server.
import { useParams, useSearchParams } from "react-router-dom";
import { Segmented } from "../../ui";
import { LogsFor } from "../LogsPage";
import { ServerConsole } from "./ServerConsolePage";

type View = "console" | "files";

export default function ServerLogsPage() {
  const { id = "" } = useParams();
  const [search, setSearch] = useSearchParams();
  const view: View = search.get("view") === "files" ? "files" : "console";
  return (
    <div className="k-stack" style={{ gap: 12 }}>
      <div className="k-row">
        <Segmented<View> aria-label="View" value={view} onChange={(v) => setSearch(v === "files" ? { view: "files" } : {}, { replace: true })}
          options={[{ id: "console", label: "Console" }, { id: "files", label: "Log files" }]} />
      </div>
      {view === "console" ? <ServerConsole serverId={id} /> : <LogsFor target={{ server: id }} embedded />}
    </div>
  );
}
