// /mods/order: every mod that is on, of every kind, in the order a start brings them up (docs/kiln/architecture.md
// "Boot", native-mods.md "Phases"): native mods in the proxy (dllmain), when Kiln starts (early), the plugins (Kiln's
// own and the ones that run other loaders' mods, each starting its own), native mods after them (boot), Kiln mods at
// the main menu in the order Kiln resolves, then what starts with the player's own world. Then what is on but will not
// load, and what is off. A client or a server side. Native order inside a phase is set on Native and DLLs.
import { useQuery } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { MoreHorizontal } from "lucide-react";
import { Link, useNavigate } from "react-router-dom";
import * as api from "../../api/commands";
import type { ModKind, NativePhase, ResolvePreview, RunSide } from "../../api/commands";
import { qk, useGame } from "../../api/hooks";
import { KIND_NAME, KIND_PLUGIN, KIND_SHORT, KIND_TONE, MOD_KINDS, NOT_RUNNING_KINDS } from "../../api/kinds";
import type { ModRow } from "../../shell/mods";
import { useMods } from "../../shell/useMods";
import { Badge, Button, FlameToggle, Menu, Segmented, Skeleton, errorText } from "../../ui";
import { ProfileAside, useActivation, useOpenSettings } from "../profiles/ProfileLayout";

interface Step { id: string; title: string; hint: string; rows: { row: ModRow; num?: number; note?: string }[]; groups?: { kind: ModKind; rows: ModRow[] }[] }

const ENGINE_HINT: Partial<Record<ModKind, string>> = {
  xhl: "XHL-Core, installed separately, loads these; Kiln never runs their DLLs.",
  ember: "Kept, but they do not run yet: Ember has published no mod ABI.",
};

/** The steps of a start for one side, from the preview and the rows of the main list. */
export function steps(rows: ModRow[], preview: ResolvePreview | null, side: RunSide): { steps: Step[]; notLoading: ModRow[]; off: ModRow[] } {
  const on = rows.filter((r) => r.on && !r.found);
  const nat = (ph: NativePhase) => (preview?.native ?? []).filter((n) => n.phase === ph && n.enabled && !n.reason)
    .map((n) => on.find((r) => r.kind === "native" && r.id === n.id)).filter((r): r is ModRow => !!r);
  const kiln = (preview?.kiln.order ?? []).map((id) => on.find((r) => r.kind === "kiln" && r.id === id)).filter((r): r is ModRow => !!r);
  const world = (preview?.withWorld ?? []).map((id) => on.find((r) => r.kind === "kiln" && r.id === id)).filter((r): r is ModRow => !!r);
  const engines = MOD_KINDS.filter((k) => k !== "kiln" && k !== "native").map((kind) => ({ kind, rows: on.filter((r) => r.kind === kind && !r.wontRun && !NOT_RUNNING_KINDS.includes(kind)) }))
    .filter((g) => g.rows.length);
  const placed = new Set([...nat("dllmain"), ...nat("early"), ...nat("boot"), ...nat("world"), ...kiln, ...world, ...engines.flatMap((g) => g.rows)]);
  const notLoading = on.filter((r) => !placed.has(r) && (!!preview || (r.kind !== "kiln" && r.kind !== "native")));
  const numbered = (list: ModRow[]) => list.map((row, i) => ({ row, num: i + 1 }));
  const s: Step[] = [
    { id: "dllmain", title: "In the proxy, before Kiln", hint: "Native mods of the dllmain phase, with the DLLs chain.ini loads.", rows: numbered(nat("dllmain")) },
    { id: "early", title: "As Kiln starts", hint: "Native mods of the early phase. They run in safe idle too.", rows: numbered(nat("early")) },
    { id: "plugins", title: "Kiln's plugins", hint: "Kiln plugins, and the plugins that run other loaders' mods; each starts its own mods.", rows: [], groups: engines },
    { id: "boot", title: "After the plugins", hint: "Native mods of the boot phase.", rows: numbered(nat("boot")) },
    { id: "menu", title: side === "client" ? "At the main menu" : "When the server world starts", hint: "Kiln mods, in the order Kiln resolves from what each needs and comes after.", rows: numbered(kiln) },
    { id: "world", title: side === "client" ? "With your world" : "At the first world", hint: side === "client" ? "When you play alone or friends join you: Kiln mods for the server side, and native mods of the world phase." : "Native mods of the world phase.",
      rows: [...world.map((row) => ({ row })), ...numbered(nat("world"))] },
    { id: "folder", title: "From the game folder", hint: "Mods put there by hand, which load by their kind's rules; Kiln did not place them.",
      rows: rows.filter((r) => r.found?.state === "loads").map((row) => ({ row, note: "Game folder" })) },
  ];
  return { steps: s.filter((x) => x.rows.length || x.groups?.length), notLoading, off: rows.filter((r) => !r.on && !r.found) };
}

export default function LoadOrder() {
  const { game } = useGame();
  const nav = useNavigate();
  const mods = useMods(game);
  const [side, setSide] = useState<RunSide>("client");
  const act = useActivation();
  const openSettings = useOpenSettings();
  const pid = mods.profileId;
  const preview = useQuery({ queryKey: qk.resolve(pid ?? "", side, game?.id ?? null), queryFn: () => api.profileResolve(pid!, side, game?.id ?? null), enabled: !!pid });
  if (!game) return null;
  if (mods.loading) return <Skeleton lines={6} />;
  const { steps: list, notLoading, off } = steps(mods.rows, preview.data ?? null, side);
  const why = (r: ModRow) => {
    const rej = preview.data?.kiln.rejected.find((x) => x.id === r.id && r.kind === "kiln");
    return rej?.reason ?? r.note?.title ?? r.wontRun ?? r.note?.text ?? "";
  };
  const loads = list.reduce((n, s) => n + s.rows.length + (s.groups ?? []).reduce((m, g) => m + g.rows.length, 0), 0);
  const row = (r: ModRow, num?: number, note?: string): ReactNode => (
    <li key={r.key} className={"k-mod k-order-mod" + (r.on ? " on" : "")} data-kind={r.kind ?? undefined}>
      <span className="k-order-n" aria-hidden>{num ?? ""}</span>
      {r.found ? <span className="k-mod-place" aria-hidden title="In the game folder" /> : <FlameToggle asSwitch on={r.on} label={r.name} onChange={(v) => mods.toggle(r, v)} />}
      <span className="k-mod-text">
        <span className="k-mod-name" title={r.id}>{r.name}</span>
        {note && <span className="k-mod-note"><span title={note}>{note}</span></span>}
      </span>
      <span className="k-mod-kind">{r.kind && <Badge tone={KIND_TONE[r.kind]} title={KIND_NAME[r.kind]}>{KIND_SHORT[r.kind]}</Badge>}</span>
      <span className="k-mono k-mod-ver">{r.version ?? ""}</span>
      <Menu label={`More for ${r.name}`} icon={<MoreHorizontal />} size="s" items={[
        ...(r.found ? [] : [{ label: r.on ? "Switch off" : "Switch on", onSelect: () => mods.toggle(r, !r.on) }]),
        ...(r.kind === "native" ? [{ label: "Order in its phase", onSelect: () => nav("/mods/native") }] : []),
        { label: "Show in the list", onSelect: () => nav("/") },
      ]} />
      {num !== undefined && <span className="k-sr-only">loads {num}.</span>}
    </li>
  );
  return (
    <>
      {preview.data?.warnings.map((w) => <p key={w} className="k-hint k-tone-warn">{w}</p>)}
      <div className="k-scroll k-stack" style={{ gap: 14, paddingBottom: 20 }}>
        <ol className="k-order-steps" aria-label={`Load order on a ${side}`}>
          {list.map((s) => (
            <li key={s.id} className="k-order-step">
              <div className="k-order-step-head"><span className="k-order-step-title">{s.title}</span><span className="k-hint">{s.hint}</span></div>
              {s.rows.length > 0 && <ul className="k-mods-list k-order-list" aria-label={s.title}>{s.rows.map((x) => row(x.row, x.num, x.note))}</ul>}
              {s.groups?.map((g) => (
                <div key={g.kind} className="k-order-group">
                  <span className="k-hint">{g.kind === "plugin" ? "Kiln plugins" : `${KIND_NAME[g.kind]} mods`}{KIND_PLUGIN[g.kind] ? <>, by <span className="k-mono">{KIND_PLUGIN[g.kind]}</span></> : ""}{ENGINE_HINT[g.kind] ? `. ${ENGINE_HINT[g.kind]}` : ""}</span>
                  <ul className="k-mods-list k-order-list" aria-label={`${KIND_SHORT[g.kind]} mods`}>{g.rows.map((r) => row(r))}</ul>
                </div>
              ))}
            </li>
          ))}
        </ol>
        {notLoading.length > 0 && (
          <section className="k-order-step" aria-label="On, but not loading">
            <div className="k-order-step-head"><span className="k-order-step-title">On, but not loading</span><span className="k-hint">Each says why.</span></div>
            <ul className="k-mods-list k-order-list">{notLoading.map((r) => row(r, undefined, why(r)))}</ul>
          </section>
        )}
        {off.length > 0 && (
          <section className="k-order-step" aria-label="Off">
            <div className="k-order-step-head"><span className="k-order-step-title">Off</span><span className="k-hint">Light one and it takes its place above.</span></div>
            <ul className="k-mods-list k-order-list">{off.map((r) => row(r))}</ul>
          </section>
        )}
        {!list.length && !notLoading.length && !off.length && <p className="k-hint">No mods yet. <Link className="k-link" to="/">All mods</Link></p>}
      </div>
      <ProfileAside>
        <span className="k-label" style={{ fontSize: 16 }}>Load order for</span>
        <div style={{ marginTop: -10 }}>
          <Segmented variant="side" aria-label="Load order for" value={side} onChange={setSide} options={[{ id: "client", label: "Client" }, { id: "server", label: "Server" }]} />
        </div>
        {preview.isLoading ? <Skeleton lines={2} /> : preview.error ? <p className="k-error-text">{errorText(preview.error)}</p> : (
          <p style={{ fontSize: 19, lineHeight: 1.28, color: "var(--k-head)" }}>
            {loads} mod{loads === 1 ? "" : "s"} load on a {side}.{notLoading.length ? ` ${notLoading.length} on but not loading.` : ""}
          </p>
        )}
        <p className="k-hint" style={{ fontSize: 16 }}>Mods of every kind, as a start brings them up. Native mods keep their order inside a phase: drag them on Native and DLLs.</p>
        <div className="k-row" style={{ marginTop: "auto", gap: 8 }}>
          {act.button}
          <Button onClick={openSettings}>Folders and notes</Button>
        </div>
      </ProfileAside>
    </>
  );
}
