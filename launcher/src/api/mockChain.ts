// Mock answers for chain_view / chain_set_late (browser mock mode; src/api/mock.ts registers them). The game has two
// [chain] entries (EMF's displaced proxy and XHL-Core); a profile's enabled dllmain natives are its [profile] entries.
// The "last session" saw XHL-Core hook ChatLogAdd before Kiln, ReShade hook over a ShroudForge target, and
// ShroudForge refuse SpawnNpc because FPS Unlock patched it first. Rules: the shipped "xhl-after-sf" (fires when the
// profile runs ShroudForge mods) and a warn on the chat overlap.
import type { ChainOverlap, ChainView, ChainViewEntry, CompatRuleResult, KilnError, LibraryEntry, Profile } from "./commands";

type Handler = (args: Record<string, unknown>) => unknown;
interface Deps {
  profiles: Map<string, Profile>;
  library: LibraryEntry[];
  reject: (code: KilnError["code"], message: string) => never;
}

const LIB = "Z:\\home\\player\\.local\\share\\kiln\\library\\native";
/** [chain] entries the player loads late (game-wide in chain.ini). */
const playerLate = new Set<string>();
const CHAIN: { path: string; engine: string | null }[] = [
  { path: "chain\\emf-dinput8.dll", engine: "emf" },
  { path: "chain\\xhl-core.dll", engine: null },
];
const file = (p: string) => p.split(/[\\/]/).pop() ?? p;
const eq = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

function profileEntries(p: Profile, library: LibraryEntry[]) {
  return p.mods.native.filter((m) => m.enabled).flatMap((m) => {
    const man = library.find((l) => l.kind === "native" && l.id === m.id)?.versions.find((v) => v.version === m.version)?.manifest as { phase?: string } | null;
    return man?.phase === "dllmain" ? [{ id: m.id, path: `${LIB}\\${m.id}\\${m.version}+${m.hash.slice(5, 13)}\\${m.id.replace(/-/g, "")}.dll` }] : [];
  });
}

function view(p: Profile, library: LibraryEntry[]): ChainView {
  const sf = (p.mods.shroudforge ?? []).some((m) => m.enabled);
  const rules: CompatRuleResult[] = [
    { id: "xhl-after-sf", source: "kiln", action: "late", fired: sf, applied: sf, targets: sf ? ["chain\\xhl-core.dll"] : [],
      message: "XHL-Core hooks functions ShroudForge needs. It loads after ShroudForge." },
    { id: "chat-overlap", source: "kiln", action: "warn", fired: true, applied: true, targets: [],
      message: "Two mods hook the chat log. If chat lines go missing, switch one of them off." },
    { id: "greeneye-zoom", source: "user", action: "disable", fired: false, applied: false, targets: [],
      message: "Zoom Out and FOV Tweak fight over the camera." },
  ];
  const ruleLate = (path: string) => rules.find((r) => r.applied && r.action === "late" && r.targets.some((t) => eq(t, path)));
  const late = p.late ?? [];
  const entries: ChainViewEntry[] = [
    ...CHAIN.map((c): ChainViewEntry => {
      const by = ruleLate(c.path) ? `rule:${ruleLate(c.path)!.id}` : playerLate.has(c.path.toLowerCase()) ? "player" : null;
      return { path: c.path, file: file(c.path), section: "chain", engine: c.engine, modId: null, late: !!by, lateBy: by, offBy: null, loader: c.engine === "emf" };
    }),
    ...profileEntries(p, library).map((e): ChainViewEntry => {
      const by = late.includes(e.id) ? "profile" : null;
      return { path: e.path, file: file(e.path), section: "profile", engine: null, modId: e.id, late: !!by, lateBy: by, offBy: null, loader: false };
    }),
  ];
  const at = (module: string) => entries.find((e) => eq(e.file, module)) ?? null;
  const lib = (id: string | null | undefined) => {
    const l = id ? library.find((x) => x.kind === "native" && x.id === id) : undefined;
    return l ? { kind: l.kind, id: l.id, name: l.name } : null;
  };
  const overlap = (symbol: string, rva: string, kind: ChainOverlap["kind"], module: string, owners: string[]): ChainOverlap => {
    const e = at(module);
    const late = !!e?.late;
    let fix: ChainOverlap["fix"] = "none";
    let hint: string;
    if (kind === "foreign-over") hint = `${module} hooked over ${owners.join(", ")} after the start. Switch one of them off, or ask its author.`;
    else if (!e) hint = `${module} is not in Kiln's chain (another loader loaded it). Add it to Kiln's chain to load it late, or use a compat rule.`;
    else if (late) hint = `${module} loads late already. Switch it or the other mod off, or add a compat rule.`;
    else { fix = "late"; hint = `Load ${e.file} after Kiln and ShroudForge, so ${owners.join(", ")} hook first and ${module} chains after them.`; }
    return { symbol, rva, kind, foreign: { module, offset: "0x2a0" }, owners, entry: e?.path ?? null, section: e?.section ?? null, library: lib(e?.modId), late, fix, hint };
  };
  const overlaps = [
    overlap("ChatLogAdd", "0x1a2b30", "foreign-before", "xhl-core.dll", ["kiln.chat", "kiln-emf"]),
    overlap("RenderFrame", "0x400000", "foreign-over", "reshade64.dll", ["kiln.ui"]),
    ...(entries.some((e) => e.modId === "fps-unlock") ? [overlap("SpawnNpc", "0x30aa00", "refused", "fpsunlock.dll", ["shroudforge"])] : []),
  ];
  return {
    entries, rules, ruleErrors: [], overlaps, active: p.id === "ember-run", current: p.id === "ember-run", error: null,
    session: { generated: "2026-11-09T11:42:00Z", phase: "world", kiln: "0.2.0-dev.21", worldChecked: true },
  };
}

export function chainMockHandlers({ profiles, library, reject }: Deps): Record<string, Handler> {
  const get = (id: unknown) => profiles.get(String(id ?? "")) ?? reject("not_found", "No such profile.");
  return {
    chain_view: (a) => view(get(a.profileId), library),
    chain_set_late: (a) => {
      const p = get(a.profileId);
      const path = String(a.path ?? "");
      const e = view(p, library).entries.find((x) => eq(x.path, path)) ?? reject("not_found", `${path} is not in this profile's chain`);
      const on = !!a.late;
      if (on && e.loader) reject("invalid_argument", `${e.file} is EMF's loader: it starts EMF itself, so it always loads first.`);
      if (e.section === "chain") {
        if (on) playerLate.add(e.path.toLowerCase());
        else playerLate.delete(e.path.toLowerCase());
      } else if (e.modId) {
        const late = new Set(p.late ?? []);
        if (on) late.add(e.modId);
        else late.delete(e.modId);
        profiles.set(p.id, { ...p, late: [...late], generation: p.generation + 1, updated: new Date().toISOString() });
      }
      return view(get(a.profileId), library);
    },
  };
}
