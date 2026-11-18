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
import { qk, useActiveProfile, useGame } from "../../api/hooks";
import { KIND_NAME, KIND_PLUGIN, KIND_SHORT, KIND_TONE, MOD_KINDS, NOT_RUNNING_KINDS } from "../../api/kinds";
import { fromGameFolder, type ModRow } from "../../shell/mods";
import { useMods } from "../../shell/useMods";
import { Badge, Button, FlameToggle, Menu, Segmented, Skeleton, errorText } from "../../ui";
import { ProfileAside, useActivation, useOpenSettings } from "../profiles/ProfileLayout";
import { rich, t, useT } from "../../i18n";

interface Step { id: string; title: string; hint: string; rows: { row: ModRow; num?: number; note?: string }[]; groups?: { kind: ModKind; rows: ModRow[] }[] }

function engineHint(kind: ModKind): string | undefined {
  switch (kind) {
    case "xhl": return t("mods.order.hintXhl");
    case "ember": return t("mods.order.hintEmber");
    default: return undefined;
  }
}

/** The steps of a start for one side, from the preview and the rows of the main list. */
export function steps(rows: ModRow[], preview: ResolvePreview | null, side: RunSide): { steps: Step[]; notLoading: ModRow[]; off: ModRow[] } {
  const on = rows.filter((r) => r.on && !r.found);
  const nat = (ph: NativePhase) => (preview?.native ?? []).filter((n) => n.phase === ph && n.enabled && !n.reason)
    .map((n) => on.find((r) => r.kind === "native" && r.id === n.id)).filter((r): r is ModRow => !!r);
  const kiln = (preview?.kiln.order ?? []).map((id) => on.find((r) => r.kind === "kiln" && r.id === id)).filter((r): r is ModRow => !!r);
  const world = (preview?.withWorld ?? []).map((id) => on.find((r) => r.kind === "kiln" && r.id === id)).filter((r): r is ModRow => !!r);
  const engines = MOD_KINDS.filter((k) => k !== "kiln" && k !== "native").map((kind) => ({ kind, rows: on.filter((r) => r.kind === kind && !r.wontRun && !NOT_RUNNING_KINDS.includes(kind)) }))
    .filter((g) => g.rows.length);
  // packages Kiln places in the game folder, which the game loads itself (no Kiln phase)
  const kilnPlaced = (preview?.native ?? []).filter(fromGameFolder)
    .map((n) => on.find((r) => r.kind === "native" && r.id === n.id)).filter((r): r is ModRow => !!r);
  const placed = new Set([...nat("dllmain"), ...nat("early"), ...nat("boot"), ...nat("world"), ...kiln, ...world, ...engines.flatMap((g) => g.rows), ...kilnPlaced]);
  const notLoading = on.filter((r) => !placed.has(r) && (!!preview || (r.kind !== "kiln" && r.kind !== "native")));
  const numbered = (list: ModRow[]) => list.map((row, i) => ({ row, num: i + 1 }));
  const s: Step[] = [
    { id: "dllmain", title: t("mods.order.dllmainTitle"), hint: t("mods.order.dllmainHint"), rows: numbered(nat("dllmain")) },
    { id: "early", title: t("mods.order.earlyTitle"), hint: t("mods.order.earlyHint"), rows: numbered(nat("early")) },
    { id: "plugins", title: t("mods.order.pluginsTitle"), hint: t("mods.order.pluginsHint"), rows: [], groups: engines },
    { id: "boot", title: t("mods.order.bootTitle"), hint: t("mods.order.bootHint"), rows: numbered(nat("boot")) },
    { id: "menu", title: side === "client" ? t("mods.order.menuTitleClient") : t("mods.order.menuTitleServer"), hint: t("mods.order.menuHint"), rows: numbered(kiln) },
    { id: "world", title: side === "client" ? t("mods.order.worldTitleClient") : t("mods.order.worldTitleServer"), hint: side === "client" ? t("mods.order.worldHintClient") : t("mods.order.worldHintServer"),
      rows: [...world.map((row) => ({ row })), ...numbered(nat("world"))] },
    { id: "folder", title: t("mods.order.folderTitle"), hint: t("mods.order.folderHint"),
      rows: [...kilnPlaced.map((row) => ({ row, note: t("mods.order.placedByKiln") })), ...rows.filter((r) => r.found?.state === "loads").map((row) => ({ row, note: t("mods.order.gameFolder") }))] },
  ];
  return { steps: s.filter((x) => x.rows.length || x.groups?.length), notLoading, off: rows.filter((r) => !r.on && !r.found) };
}

export default function LoadOrder() {
  const t = useT();
  const { game } = useGame();
  const nav = useNavigate();
  const mods = useMods(game);
  const active = useActiveProfile(game?.id);
  const [side, setSide] = useState<RunSide>("client");
  const act = useActivation();
  const openSettings = useOpenSettings();
  const pid = mods.profileId;
  const preview = useQuery({ queryKey: qk.resolve(pid ?? "", side, game?.id ?? null), queryFn: () => api.profileResolve(pid!, side, game?.id ?? null), enabled: !!pid });
  if (!game) return null;
  if (mods.loading) return <Skeleton lines={6} />;
  // where the game reads Kiln mods from (profile::render): players look for them in the game folder and find nothing
  const asideHint = (active.data?.pathMode ?? game.pathMode) === "copy" ? t("mods.order.asideHintCopy") : t("mods.order.asideHintLibrary");
  const { steps: list, notLoading, off } = steps(mods.rows, preview.data ?? null, side);
  const why = (r: ModRow) => {
    const rej = preview.data?.kiln.rejected.find((x) => x.id === r.id && r.kind === "kiln");
    return rej?.reason ?? r.note?.title ?? r.wontRun ?? r.note?.text ?? "";
  };
  // "Kiln plugins" or "<kind> mods", ", by <plugin>" when a plugin runs them, then the engine's note
  const groupLine = (kind: ModKind): ReactNode => {
    const name = kind === "plugin" ? t("mods.order.kilnPlugins") : t("mods.order.kindMods", { kind: KIND_NAME[kind] });
    const plugin = KIND_PLUGIN[kind];
    const label = plugin ? rich("mods.order.byPlugin", { group: name, plugin: <span className="k-mono">{plugin}</span> }) : name;
    const hint = engineHint(kind);
    return hint ? rich("mods.order.withHint", { group: <>{label}</>, hint }) : label;
  };
  const loads = list.reduce((n, s) => n + s.rows.length + (s.groups ?? []).reduce((m, g) => m + g.rows.length, 0), 0);
  const row = (r: ModRow, num?: number, note?: string): ReactNode => (
    <li key={r.key} className={"k-mod k-order-mod" + (r.on ? " on" : "")} data-kind={r.kind ?? undefined}>
      <span className="k-order-n" aria-hidden>{num ?? ""}</span>
      {r.found ? <span className="k-mod-place" aria-hidden title={t("mods.list.inGameFolder")} /> : <FlameToggle asSwitch on={r.on} label={r.name} onChange={(v) => mods.toggle(r, v)} />}
      <span className="k-mod-text">
        <span className="k-mod-name" title={r.id}>{r.name}</span>
        {note && <span className="k-mod-note"><span title={note}>{note}</span></span>}
      </span>
      <span className="k-mod-kind">{r.kind && <Badge tone={KIND_TONE[r.kind]} title={KIND_NAME[r.kind]}>{KIND_SHORT[r.kind]}</Badge>}</span>
      <span className="k-mono k-mod-ver">{r.version ?? ""}</span>
      <Menu label={t("mods.list.moreFor", { name: r.name })} icon={<MoreHorizontal />} size="s" items={[
        ...(r.found ? [] : [{ label: r.on ? t("mods.list.switchOff") : t("mods.list.switchOn"), onSelect: () => mods.toggle(r, !r.on) }]),
        ...(r.kind === "native" ? [{ label: t("mods.order.orderInPhase"), onSelect: () => nav("/mods/native") }] : []),
        { label: t("mods.order.showInList"), onSelect: () => nav("/") },
      ]} />
      {num !== undefined && <span className="k-sr-only">{t("mods.order.loadsNum", { num })}</span>}
    </li>
  );
  return (
    <>
      {preview.data?.warnings.map((w) => <p key={w} className="k-hint k-tone-warn">{w}</p>)}
      <div className="k-scroll k-stack" style={{ gap: 14, paddingBottom: 20 }}>
        <ol className="k-order-steps" aria-label={side === "client" ? t("mods.order.onClient") : t("mods.order.onServer")}>
          {list.map((s) => (
            <li key={s.id} className="k-order-step">
              <div className="k-order-step-head"><span className="k-order-step-title">{s.title}</span><span className="k-hint">{s.hint}</span></div>
              {s.rows.length > 0 && <ul className="k-mods-list k-order-list" aria-label={s.title}>{s.rows.map((x) => row(x.row, x.num, x.note))}</ul>}
              {s.groups?.map((g) => (
                <div key={g.kind} className="k-order-group">
                  <span className="k-hint">{groupLine(g.kind)}</span>
                  <ul className="k-mods-list k-order-list" aria-label={t("mods.order.kindMods", { kind: KIND_SHORT[g.kind] })}>{g.rows.map((r) => row(r))}</ul>
                </div>
              ))}
            </li>
          ))}
        </ol>
        {notLoading.length > 0 && (
          <section className="k-order-step" aria-label={t("mods.order.notLoadingTitle")}>
            <div className="k-order-step-head"><span className="k-order-step-title">{t("mods.order.notLoadingTitle")}</span><span className="k-hint">{t("mods.order.notLoadingHint")}</span></div>
            <ul className="k-mods-list k-order-list">{notLoading.map((r) => row(r, undefined, why(r)))}</ul>
          </section>
        )}
        {off.length > 0 && (
          <section className="k-order-step" aria-label={t("mods.order.offTitle")}>
            <div className="k-order-step-head"><span className="k-order-step-title">{t("mods.order.offTitle")}</span><span className="k-hint">{t("mods.order.offHint")}</span></div>
            <ul className="k-mods-list k-order-list">{off.map((r) => row(r))}</ul>
          </section>
        )}
        {!list.length && !notLoading.length && !off.length && <p className="k-hint">{t("mods.order.noMods")} <Link className="k-link" to="/">{t("mods.allMods")}</Link></p>}
      </div>
      <ProfileAside>
        <span className="k-label" style={{ fontSize: 16 }}>{t("mods.order.loadOrderFor")}</span>
        <div style={{ marginTop: -10 }}>
          <Segmented variant="side" aria-label={t("mods.order.loadOrderFor")} value={side} onChange={setSide} options={[{ id: "client", label: t("mods.order.client") }, { id: "server", label: t("mods.order.server") }]} />
        </div>
        {preview.isLoading ? <Skeleton lines={2} /> : preview.error ? <p className="k-error-text">{errorText(preview.error)}</p> : (
          <p style={{ fontSize: 19, lineHeight: 1.28, color: "var(--k-head)" }}>
            {side === "client" ? t("mods.order.loadsClient", { count: loads }) : t("mods.order.loadsServer", { count: loads })}{notLoading.length ? ` ${t("mods.order.notLoadingCount", { count: notLoading.length })}` : ""}
          </p>
        )}
        <p className="k-hint" style={{ fontSize: 16 }}>{asideHint}</p>
        <div className="k-row" style={{ marginTop: "auto", gap: 8 }}>
          {act.button}
          <Button onClick={openSettings}>{t("mods.foldersAndNotes")}</Button>
        </div>
      </ProfileAside>
    </>
  );
}
