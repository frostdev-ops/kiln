// /about: the lockup, versions, the credits (CREDITS.md) and the launcher's own third-party pieces.
import * as api from "../api/commands";
import { useAppInfo, useDistribution } from "../api/hooks";
import { NexusVersionsLink, UpdateAboutLine } from "../components/UpdateSettings";
import { KeyValue, Lockup, Page, SplitPage } from "../ui";
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
  ["Grenze Gotisch, Alegreya Sans, Alegreya Sans SC, Inconsolata", "SIL Open Font License 1.1"],
  ["Lucide icons", "ISC"],
  ["React, TanStack Query, TanStack Virtual, zustand", "MIT"],
  ["Tauri", "MIT or Apache-2.0"],
];

export default function AboutPage() {
  const info = useAppInfo();
  const dist = useDistribution();
  const { rows, notes } = parseCredits(credits);
  const i = info.data;
  return (
    <Page fill>
      <SplitPage aside={<>
        <span className="k-label" style={{ fontSize: 16 }}>Version</span>
        <KeyValue rows={[
          ["Launcher", <span className="k-mono">{i?.version ?? "-"}</span>],
          ["Kiln", <span className="k-mono">{i?.kiln ?? "-"}</span>],
          i?.commit && ["Commit", <span className="k-mono">{i.commit}</span>],
          ["Platform", i ? `${i.platform} ${i.arch}` : "-"],
          // the Nexus Mods build does not update itself: where new versions are, nothing else (api/distribution.ts)
          dist === "nexus" ? ["New versions", <NexusVersionsLink />] : ["Updates", dist === "direct" ? <UpdateAboutLine /> : "-"],
          i?.mock && ["Backend", "Browser mock"],
        ]} />
        {i?.dataDir && <><span className="k-label" style={{ fontSize: 16, marginTop: 6 }}>Data</span><span className="k-mono" style={{ fontSize: 14.5, overflowWrap: "anywhere" }}>{i.dataDir}</span></>}
        <span className="k-label" style={{ fontSize: 16, marginTop: 6 }}>The launcher uses</span>
        {LAUNCHER.map(([a, b]) => <div key={a}><div style={{ fontSize: 16 }}>{a}</div><div className="k-hint">{b}</div></div>)}
      </>}>
        <header className="k-page-head">
          <div>
            <h1 className="k-page-title" style={{ lineHeight: 0, padding: "6px 0 10px" }}><Lockup height={56} title="Kiln" /></h1>
            <p className="k-page-sub">Mods for Enshrouded. Theme and design: frostdev.</p>
          </div>
        </header>
        <section className="k-card" style={{ flex: 1, minHeight: 0 }}>
          <header className="k-card-head"><div className="k-card-titles"><h2 className="k-card-title">Credits</h2><span className="k-card-sub">From CREDITS.md</span></div></header>
          <div className="k-scroll" style={{ paddingBottom: 16 }}>
            <div className="k-list">
              {rows.map((r) => (
                <div key={r.project} className="k-list-item" style={{ alignItems: "baseline", flexWrap: "wrap", rowGap: 0, padding: "8px 0" }}>
                  {r.url ? <button type="button" className="k-link" style={{ fontSize: 17 }} onClick={() => api.appOpenUrl(r.url!).catch(() => {})}>{r.project}</button> : <span className="k-item-title">{r.project}</span>}
                  <span className="k-muted" style={{ fontSize: 15 }}>{r.who}</span>
                  <span className="k-hint" style={{ flexBasis: "100%" }}>{r.what}</span>
                </div>
              ))}
            </div>
            {notes.map((n) => <p key={n} className="k-hint" style={{ marginTop: 10 }}>{n}</p>)}
          </div>
        </section>
      </SplitPage>
    </Page>
  );
}
