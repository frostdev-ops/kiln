// /servers/:id/overview: the live console with the Kiln command line, and an aside with the step time (tick stats)
// and the server's facts: steps per second, run by, port, the admin endpoint, policy, game version.
import { useQuery } from "@tanstack/react-query";
import { useParams } from "react-router-dom";
import * as api from "../../api/commands";
import { KeyValue } from "../../ui";
import { StepChart, useTicks } from "../ticks";
import { ServerConsole } from "./ServerConsole";
import { runnerLabel, sk, useServer, useServerStatus } from "./serverApi";

export default function ServerOverviewPage() {
  const { id = "" } = useParams();
  const srv = useServer(id);
  const st = useServerStatus(id);
  const query = useQuery({ queryKey: sk.query(id), queryFn: () => api.serverQuery(id), enabled: st.data?.state === "running", refetchInterval: 15000, retry: false });
  const { ticks } = useTicks({ server: id });
  const last = ticks[ticks.length - 1];
  const s = srv.data;
  const status = st.data;
  const T: Record<string, string> = { tcp: "KAP/1 TCP", file: "File bridge", ssh: "SSH tunnel" };
  return (
    <div className="k-overview">
      <ServerConsole serverId={id} />
      <aside className="k-overview-aside" aria-label="Server facts">
        <StepChart ticks={ticks} />
        <KeyValue rows={[
          last && ["Steps/s", <span className="k-mono">{last.rate.toFixed(2)}</span>],
          last && ["Max step", <span className="k-mono">{last.step_ms.max.toFixed(1)} ms</span>],
          s && ["Run by", runnerLabel[s.runner ?? "kilnctl"]],
          s && ["Port", <span className="k-mono">UDP {s.queryPort}</span>],
          ["Admin", status?.endpoint ? <span className="k-mono">{T[(status.endpoint as { transport?: string }).transport ?? "tcp"] ?? "KAP/1 TCP"}</span> : <span className="k-muted">{status?.pid ? "No endpoint" : "Off"}</span>],
          s && ["Policy", <span className="k-mono">{s.policy}</span>],
          query.data?.version && ["Game", <span className="k-mono">{query.data.version}</span>],
          status?.kiln && ["Kiln", <span className="k-mono">{status.kiln}</span>],
          status?.pid && ["Process", <span className="k-mono">pid {status.pid}</span>],
        ]} />
      </aside>
    </div>
  );
}
