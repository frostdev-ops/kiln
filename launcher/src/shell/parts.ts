// The parts (bundle components) an install puts into a game: kiln_manage::install::resolve_components, for the
// install dialog and the Runtime page's installed-versus-available list. The backend decides; this mirrors it so the
// dialog can show what an update will add before it runs.
import type { BundlePart, InstallReceipt, RunSide, RuntimeBundle } from "../api/commands";

/** The bundle's parts this side can take. */
export function partsFor(b: RuntimeBundle, side: RunSide): BundlePart[] {
  return (b.parts ?? []).filter((p) => p.sides.includes(side));
}

/** An explicit selection and its available transitive requirements. The backend validates the full manifest. */
export function selectParts(b: RuntimeBundle, side: RunSide, want: string[]): string[] {
  const parts = partsFor(b, side);
  const out = [...want];
  for (let i = 0; i < out.length; i++) {
    const part = parts.find((p) => p.name === out[i]);
    if (!part) throw new Error(`component ${out[i]} is unavailable for the ${side} side`);
    for (const r of part.requires ?? []) if (!out.includes(r) && parts.some((p) => p.name === r)) out.push(r);
  }
  return [...new Set(out)].sort();
}

/** What an install of `b` with no explicit choice puts in: the defaults (first install); the installed parts the
 *  bundle has, plus every default not turned off (update, reinstall, rollback). */
export function resolveParts(b: RuntimeBundle, side: RunSide, receipt: InstallReceipt | null): string[] {
  const parts = partsFor(b, side);
  const defaults = parts.filter((p) => p.default).map((p) => p.name);
  if (!receipt || receipt.side !== side) return selectParts(b, side, defaults);
  const off = receipt.runtime.deselected ?? [];
  const want = receipt.runtime.components.filter((c) => !off.includes(c) && parts.some((p) => p.name === c));
  for (const d of defaults) if (!off.includes(d) && !want.includes(d)) want.push(d);
  return selectParts(b, side, want);
}

/** Defaults an explicit choice leaves out, plus earlier opt-outs not selected (even if now optional or absent). */
export function deselectedFor(b: RuntimeBundle, side: RunSide, chosen: string[], receipt: InstallReceipt | null): string[] {
  const parts = partsFor(b, side);
  const selected = selectParts(b, side, chosen);
  const before = receipt?.side === side ? receipt.runtime.deselected ?? [] : [];
  const off = parts.filter((p) => p.default && !selected.includes(p.name)).map((p) => p.name)
    .concat(before.filter((c) => !selected.includes(c)));
  return [...new Set(off)].sort();
}

export type PartState = "installed" | "missing" | "off" | "optional";

/** One row of the installed-versus-available list. */
export interface PartRow { name: string; state: PartState; part: BundlePart | null }

/** The parts of the installed bundle (`b`, when the launcher has it) against what the receipt says is in the game:
 *  installed, missing (a default not turned off or a required dependency), turned off, or optional. Parts the receipt
 *  names that `b` lacks (or every part, without `b`) show as installed. */
export function partRows(b: RuntimeBundle | null, side: RunSide, receipt: InstallReceipt): PartRow[] {
  const inGame = receipt.runtime.components;
  const off = receipt.runtime.deselected ?? [];
  const plan = b ? resolveParts(b, side, receipt) : [];
  const rows: PartRow[] = (b ? partsFor(b, side) : []).map((p) => ({
    name: p.name, part: p,
    state: inGame.includes(p.name) ? "installed" : plan.includes(p.name) ? "missing" : off.includes(p.name) ? "off" : "optional",
  }));
  for (const c of inGame) if (!rows.some((r) => r.name === c)) rows.push({ name: c, state: "installed", part: null });
  return rows;
}
