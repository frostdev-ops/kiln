// /servers/:id/ticks: the server's tick stats (kiln/logs/tick-stats.jsonl): step time, rate, the per-minute table.
import { useParams } from "react-router-dom";
import { TickStats } from "../ticks";

export default function ServerTicksPage() {
  const { id = "" } = useParams();
  return <div className="k-page k-page-fill" style={{ padding: "0 0 18px" }}><TickStats target={{ server: id }} fill /></div>;
}
