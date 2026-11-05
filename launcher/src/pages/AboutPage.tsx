// /about: versions, credits (CREDITS.md), the launcher's own third-party pieces.
import * as api from "../api/commands";
import { useAppInfo } from "../api/hooks";
import { Card, KeyValue, Mark, Page } from "../ui";
import credits from "../../../CREDITS.md?raw";

interface Credit { project: string; url: string | null; who: string; what: string }

/** The table rows of CREDITS.md, markdown links reduced to text (+ the first https URL). */
function parseCredits(md: string): { rows: Credit[]; notes: string[] } {
  const rows: Credit[] = [];
  const notes: string[] = [];
  const text = (s: string) => s.replace(/\[([^\]]+)\]\([^)]+\)/g, "$1").replace(/`([^`]+)`/g, "$1").trim();
  for (const line of md.split("\n")) {
    const t = line.trim();
    if (t.startsWith("|") && !/^\|\s*-/.test(t) && !/^\|\s*Project\s*\|/.test(t)) {
      const cells = t.split("|").slice(1, -1);
      if (cells.length >= 3) rows.push({ project: text(cells[0]), url: /\((https:[^)]+)\)/.exec(cells[0])?.[1] ?? null, who: text(cells[1]), what: text(cells[2]) });
    } else if (t && !t.startsWith("#") && !t.startsWith("|") && !t.startsWith("Kiln builds on")) notes.push(text(t));
  }
  return { rows, notes };
}

const LAUNCHER = [
  ["Fraunces, Inter, JetBrains Mono", "SIL Open Font License 1.1"],
  ["Lucide icons", "ISC"],
  ["React, TanStack Query, TanStack Virtual, zustand", "MIT"],
  ["Tauri", "MIT / Apache-2.0"],
];

export default function AboutPage() {
  const info = useAppInfo();
  const { rows, notes } = parseCredits(credits);
  const i = info.data;
  return (
    <Page>
      <section className="k-hero">
        <Mark size={64} />
        <div className="k-hero-main">
          <h1 className="k-hero-title">Kiln</h1>
          <span className="k-muted">Mods for Enshrouded. Theme and design: frostdev.</span>
        </div>
      </section>
      <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
        <Card title="Version">
          <KeyValue rows={[
            ["Launcher", i?.version ?? "-"],
            ["Kiln", i?.kiln ?? "-"],
            i?.commit && ["Commit", <span className="k-mono">{i.commit}</span>],
            ["Platform", i ? `${i.platform} ${i.arch}` : "-"],
            ["Data", <span className="k-mono" style={{ fontSize: 12 }}>{i?.dataDir ?? "-"}</span>],
            i?.mock && ["Backend", "browser mock"],
          ]} />
        </Card>
        <Card title="Launcher uses">
          <KeyValue rows={LAUNCHER.map(([a, b]) => [a, <span className="k-muted">{b}</span>])} />
        </Card>
      </div>
      <Card title="Credits">
        <div className="k-list" style={{ margin: "0 -16px" }}>
          {rows.map((r) => (
            <div key={r.project} className="k-list-item" style={{ alignItems: "flex-start" }}>
              <span className="k-stack" style={{ gap: 2, flex: 1, minWidth: 0 }}>
                <span className="k-row" style={{ gap: 8 }}>
                  {r.url ? <button type="button" className="k-link k-item-title" onClick={() => api.appOpenUrl(r.url!).catch(() => {})}>{r.project}</button> : <span className="k-item-title">{r.project}</span>}
                  <span className="k-muted" style={{ fontSize: 12 }}>{r.who}</span>
                </span>
                <span className="k-hint">{r.what}</span>
              </span>
            </div>
          ))}
        </div>
        {notes.map((n) => <span key={n} className="k-hint">{n}</span>)}
      </Card>
    </Page>
  );
}
