import { describe, expect, test } from "bun:test";
import { emptyRegistryFixture, nativeShapedReport, registryFixture } from "../tests/fixtures/registryReport";
import { parseRegistryReport, reportFreshness, REPORT_LIMITS } from "../src/registry/report";
import de from "../src/i18n/de.json";
import enText from "../src/i18n/en.json";
import es from "../src/i18n/es.json";
import fr from "../src/i18n/fr.json";
import it from "../src/i18n/it.json";
import ja from "../src/i18n/ja.json";
import ko from "../src/i18n/ko.json";
import pl from "../src/i18n/pl.json";
import ptBR from "../src/i18n/pt-BR.json";
import ru from "../src/i18n/ru.json";
import zhCN from "../src/i18n/zh-CN.json";

const parse = (r: unknown) => parseRegistryReport(JSON.stringify(r));
describe("untrusted registry report", () => {
  test("reads the native format and ordered owners/operations", () => {
    const r = parse(registryFixture());
    expect(r.fields[0].baseOrigin).toBe("archive");
    expect(r.fields[0].writers.map(w => w.owner)).toEqual(["earlier", "stacker"]);
    expect(r.fields[0].writers[1].operation).toBe('{"mul":2}');
    expect(r.fields[0].winner).toBe("stacker");
    expect(r.fields[0].result).toBe("40");
    expect(r.baseOrigin).toBe("live");
  });
  test("empty is a valid report, not a missing file", () => expect(parse(emptyRegistryFixture()).empty).toBe(true));
  test("entry outcomes retain bounded Kiln provenance and distinguish pending intent from applied visibility", () => {
    const r = parse({ ...emptyRegistryFixture(),
      added: [{ registry: "items", id: "maker:logs", state: "pendingWorld", mod: "maker", source: "content/entries.json", like: "game:wood", base: "game:wood", address: "0xDEADBEEF" }],
      removed: [{ registry: "loot", id: "game:wood", state: "disabled", mod: "hide", source: "content/hide.json", reason: "hidden by hide" }],
      counts: { fields: 0, conflicts: 0, refused: 0, added: 1, removed: 1 },
    });
    expect(r.entries[0]).toMatchObject({ state: "pendingWorld", owner: "maker", source: "content/entries.json" });
    expect(r.entries[1]).toMatchObject({ state: "disabled", owner: "hide", reason: "hidden by hide" });
    expect(JSON.stringify(r)).not.toContain("DEADBEEF");
    const future = parse({ ...emptyRegistryFixture(), added: [{ registry: "items", id: "maker:logs", state: "future", mod: "x".repeat(10000) }] });
    expect(future.entries[0].state).toBe("");
    expect(future.entries[0].owner.length).toBeLessThanOrEqual(REPORT_LIMITS.text + 1);
  });
  test("refusals retain RestartRequired and Unproven reasons", () => {
    expect(parse(registryFixture()).refused.map(x => x.reason)).toEqual(registryFixture().refused.map(x => x.reason));
  });
  test("edits that wait for a restart are the refused rows with the token, counted over every row", () => {
    const r = parse(registryFixture());
    expect(r.restart.total).toBe(1);  // counts has no restartRequired here (an older Kiln): the rows it lists
    expect(r.restart.rows.map(x => x.owner)).toEqual(["late"]);
    expect(r.restart.rows[0].field).toBe("balance · levelCap");
    const counted = parse({ ...registryFixture(), counts: { ...registryFixture().counts, restartRequired: 7 } });
    expect(counted.restart.total).toBe(7);  // the native cap may leave rows out: the count says how many there are
    expect(counted.restart.rows.length).toBe(1);
    expect(parse(emptyRegistryFixture()).restart).toEqual({ total: 0, rows: [] });
    const raw = parse({ ...emptyRegistryFixture(), refused: [{ mod: "adv", source: "", stage: "alter", n: 0, registry: "items", id: "game:x", field: "secret.member", raw: true, reason: "RestartRequired: the base did not read secret.member at start" }],
      counts: { fields: 0, conflicts: 0, refused: 1, added: 0, removed: 0, restartRequired: 1 } });
    expect(raw.restart.rows[0].reason.startsWith("RestartRequired: ")).toBe(true);  // still counted, the raw path hidden
    expect(JSON.stringify(raw.restart)).not.toContain("secret.member");
    expect(() => parse({ ...registryFixture(), counts: { ...registryFixture().counts, restartRequired: -1 } })).toThrow();
  });
  test("does not upgrade unknown certainty to certain", () => {
    const r = registryFixture(); r.conflicts[0].certainty = "future";
    expect(parse(r).conflicts.map(x => x.certainty)).toEqual(["Unknown", "Best-effort drift"]);
  });
  test("journal naming is not certain observation", () => {
    const r = registryFixture(); r.fields[0].writers[0].certainty = "named";
    expect(parse(r).fields[0].writers[0].certainty).toBe("Named from journal");
  });
  test("archive-writer packages are named and drift rows are labelled best effort", () => {
    const r = registryFixture();
    r.fields[0].writers = [
      { kind: "archiveWriter", who: "shroudforge:alpha", op: 10, value: 10, certainty: "named" },
      { kind: "archiveWriter", who: "eml:bravo", op: 10, value: 10, certainty: "named" },
      { kind: "drift", who: "outside Kiln (best effort; suspects: xhl-core.dll)", op: 12, value: 12, certainty: "bestEffort" },
      ...r.fields[0].writers,
    ] as typeof r.fields[0].writers;
    const out = parse(r).fields[0].writers;
    expect(out.map(w => w.owner).slice(0, 3)).toEqual(["alpha (ShroudForge package)", "bravo (EML package)", "outside Kiln (best effort; suspects: xhl-core.dll)"]);
    expect(out.map(w => w.kind).slice(0, 3)).toEqual(["Wrote this entry before Kiln (archive)", "Wrote this entry before Kiln (archive)", "Changed outside Kiln"]);
    expect(out.map(w => w.certain)).toEqual([false, false, false, true, true]);
    expect(out[2].certainty).toBe("Best-effort drift");
    expect(out[3].kind).toBe("oldPack");
  });
  test("an archive-writer row claims no operation on the field: the journal names the entry", () => {
    const r = registryFixture();
    r.fields[0].writers = [
      { kind: "archiveWriter", who: "shroudforge:alpha", op: 10, value: 10, certainty: "named" },  // an older report's op is not shown either
      { kind: "archiveWriter", who: "eml:bravo", value: 10, certainty: "named" },
      ...r.fields[0].writers,
    ] as typeof r.fields[0].writers;
    const out = parse(r).fields[0].writers;
    expect(out[0].operation).toBe("Not known (kiln-eml's journal names the entry this package wrote, not this field)");
    expect(out[1].operation).toBe(out[0].operation);
    expect(out[1].value).toBe("10");
    expect(out[2].operation).not.toBe(out[0].operation);
  });
  test("an EML pass's journal names a field: the row says the field, still no operation (0.2.0-dev.31.18)", () => {
    const r = registryFixture();
    r.fields[0].writers = [
      { kind: "archiveWriter", who: "eml:bravo", value: 10, certainty: "named", named: "field" },
      { kind: "archiveWriter", who: "eml:alpha", value: 10, certainty: "named", named: "entry" },
      { kind: "archiveWriter", who: "shroudforge:old", value: 10, certainty: "named" },  // a report from before: the entry
      ...r.fields[0].writers,
    ] as typeof r.fields[0].writers;
    const out = parse(r).fields[0].writers;
    expect(out.map(w => w.kind).slice(0, 3)).toEqual(["Wrote this field before Kiln (archive)", "Wrote this entry before Kiln (archive)", "Wrote this entry before Kiln (archive)"]);
    expect(out[0].operation).toBe("Not known (kiln-eml's journal names this field as written by this package, not the value)");
    expect(out[1].operation).toBe("Not known (kiln-eml's journal names the entry this package wrote, not this field)");
    expect(out[0].owner).toBe("bravo (EML package)");
  });
  test("base.journal says whether the archive writer's packages are named", () => {
    const read = parse({ ...registryFixture(), base: { origin: "live", writer: "kiln-eml", journal: { state: "read", packages: 2, entries: 3, truncated: false, text: "x" } } });
    expect(read.journal).toBe("Archive-writer packages named from kiln-eml's change journal: 2 packages, 3 entries (a ShroudForge pass names items, drops and balance per entry; an EML pass names the fields its mods wrote, on recipes and loot by their element).");
    const truncated = parse({ ...registryFixture(), base: { origin: "live", writer: "kiln-eml", journal: { state: "read", packages: 1, entries: 1, truncated: true } } });
    expect(truncated.journal).toContain("truncated");
    const none = parse({ ...registryFixture(), base: { origin: "live", writer: "shroudforge-hosted", journal: { state: "none", packages: 0, entries: 0, truncated: false, text: "hosted ShroudForge keeps no change journal" } } });
    expect(none.journal).toBe("Archive-writer packages are not named in this start: hosted ShroudForge keeps no change journal");
    expect(parse({ ...registryFixture(), base: { origin: "live", writer: "none", journal: { state: "none", packages: 0, entries: 0, truncated: false } } }).journal).toBe("");
    expect(parse(registryFixture()).journal).toBe("");  // a report from an older Kiln has no journal
    const stale = parse({ ...registryFixture(), base: { origin: "live", writer: "kiln-eml", journal: { state: "stale", packages: 0, entries: 0, truncated: false, text: "left by an earlier start" } } });
    expect(stale.journal).toBe("Archive-writer packages are not named in this start: left by an earlier start");
  });
  test.each(["null", "[]", "{}", "{", '{"format":2}'])("rejects invalid or unsupported document %s", text => {
    expect(() => parseRegistryReport(text)).toThrow();
  });
  test("rejects oversized input before JSON parsing", () => expect(() => parseRegistryReport(" ".repeat(REPORT_LIMITS.bytes + 1))).toThrow("8 MiB"));
  test("an oversized report says where to read it in full", () => expect(() => parseRegistryReport(" ".repeat(REPORT_LIMITS.bytes + 1))).toThrow("kiln/state/registry.json"));
  test("a native-shaped 5,000-field report (about 1.4 MiB) parses", () => {
    const text = JSON.stringify(nativeShapedReport(5000));
    expect(text.length).toBeGreaterThan(1024 * 1024);
    const out = parseRegistryReport(text);
    expect(out.fields.length).toBe(REPORT_LIMITS.rows);
    expect(out.omitted).toBe(5000 - REPORT_LIMITS.rows);
  });
  test("a native-shaped report at the native field cap (20,000 rows) parses and counts what is left out", () => {
    const text = JSON.stringify(nativeShapedReport(20_000, 1, 26_000));
    expect(text.length).toBeLessThanOrEqual(REPORT_LIMITS.bytes);
    const out = parseRegistryReport(text);
    expect(out.fields.length).toBe(REPORT_LIMITS.rows);
    expect(out.omitted).toBe(26_000 - REPORT_LIMITS.rows);
  });
  test("hostile structure within the byte cap stays bounded", () => {
    expect(() => parseRegistryReport("[" + "[],".repeat(1_000_000) + "[]]")).toThrow("structure limit");
  });
  test("caps nesting before JSON parsing", () => expect(() => parseRegistryReport("[".repeat(70) + "0" + "]".repeat(70))).toThrow("nesting"));
  test("ignores unknown members without rendering them", () => {
    expect(JSON.stringify(parse({ ...registryFixture(), offset: "0xDEADBEEF", gameFunction: "SecretFunction" }))).not.toContain("SecretFunction");
  });
  test("refuses malformed rows instead of silently showing an empty report", () => {
    const r = { ...registryFixture(), fields: [null] };
    expect(() => parse(r)).toThrow();
  });
  test("bounds rows, writers and text and discloses omissions", () => {
    const r = registryFixture();
    r.fields = Array.from({ length: REPORT_LIMITS.rows + 1 }, () => ({ ...r.fields[0] }));
    r.fields[0].writers = Array.from({ length: 80 }, (_, n) => ({ ...r.fields[0].writers[0], mod: `writer${n}` }));
    r.refused[0].reason = "x".repeat(10000);
    const out = parse(r);
    expect(out.fields.length).toBe(REPORT_LIMITS.rows);
    expect(out.fields[0].writers.length).toBe(REPORT_LIMITS.writers);
    expect(out.fields[0].writers.at(-1)?.owner).toBe("writer79");
    expect(out.fields[0].writersOmitted).toBe(16);
    expect(out.refused[0].reason.length).toBeLessThanOrEqual(REPORT_LIMITS.text + 1);
    expect(out.omitted).toBeGreaterThan(0);
  });
  test("native truncation and list omission counts are visible", () => {
    const r = registryFixture();
    const field = { ...r.fields[0], writersLeftOut: 4, resultEntriesLeftOut: 6, base: { ...r.fields[0].base, entriesLeftOut: 3 } };
    const out = parse({ ...r, fields: [field], truncated: { ...r.truncated, fields: 20 } });
    expect(out.omitted).toBe(20);
    expect(out.fields[0].writersOmitted).toBe(4);
    expect(out.fields[0].result).toContain("6 omitted");
    expect(out.fields[0].base).toContain("3 omitted");
  });
  test("writer chains cut by native or by this view are counted for the report banner", () => {
    const r = registryFixture();
    const out = parse({ ...r, truncated: { ...r.truncated, writers: 7 } });
    expect(out.omitted).toBe(0);
    expect(out.writersOmitted).toBe(7);
    const many = { ...r.fields[0], writers: Array.from({ length: 70 }, (_, n) => ({ ...r.fields[0].writers[0], mod: `w${n}` })) };
    expect(parse({ ...r, fields: [many], truncated: { ...r.truncated, writers: 7 } }).writersOmitted).toBe(13);
  });
  test("a list-element writer keeps the element it changed; an element the lock does not know is hidden", () => {
    const r = registryFixture();
    const row = { ...r.fields[0], registry: "drops", id: "game:fixture_drops", field: "entries", writers: [
      { kind: "registry", mod: "loot", stage: "alter", n: 0, element: "entries[3].chance", op: { set: 0.5 }, value: 0.5, certainty: "certain" },
      { kind: "registry", mod: "odd", stage: "alter", n: 1, element: "SecretList[2].member", op: { set: 1 }, value: 1, certainty: "certain" },
      { kind: "registry", mod: "plain", stage: "alter", n: 2, op: 3, value: 3, certainty: "certain" },
    ] };
    const out = parse({ ...r, fields: [row] });
    expect(out.fields[0].writers.map(w => w.element)).toEqual(["entries[3].chance", "element details hidden", ""]);
    expect(JSON.stringify(out)).not.toContain("SecretList");
  });
  test("raw paths and native offsets never enter the view model", () => {
    const r = registryFixture();
    const field = { ...r.fields[0], raw: true, field: "SecretSetup.Offset" };
    const out = parse({ ...r, fields: [field], refused: [{ ...r.refused[0], reason: "Unmapped at RVA 0xDEADBEEF" }] });
    expect(JSON.stringify(out)).not.toContain("SecretSetup");
    expect(JSON.stringify(out)).not.toContain("DEADBEEF");
    expect(out.fields[0].field).toBe("Advanced field (details hidden)");
  });
  test("raw conflict/refusal paths stay hidden even without a retained field row", () => {
    const r = registryFixture();
    const out = parse({ ...r, fields: [],
      conflicts: [{ ...r.conflicts[0], field: "items[game:fixture_stack].SecretSetup.Offset", raw: true, text: "SecretSetup.Offset changed" }],
      refused: [{ ...r.refused[0], registry: "items", field: "SecretSetup.Offset", raw: true, reason: "Unproven: SecretSetup.Offset (mapping missing)" }],
    });
    expect(JSON.stringify(out)).not.toContain("SecretSetup");
  });
  test("live raw bind refusals hide repeated member paths while ordinary unknown-field reasons remain readable", () => {
    const r = registryFixture();
    const out = parse({ ...r, refused: [
      { mod: "advanced-mod", source: "content/raw.json", stage: "alter", n: 9,
        registry: "items", id: "game:fixture_stack", field: "Missing.Member", raw: true,
        reason: "items[game:fixture_stack].raw:Missing.Member: the member Missing.Member is not a writable member on this game build; this edit was not applied" },
      { mod: "advanced-mod", source: "content/raw.json", stage: "alter", n: 10,
        registry: "items", id: "game:fixture_stack", field: "maxStackSize", raw: true,
        reason: "items[game:fixture_stack].raw:maxStackSize: the member maxStackSize is not a writable member on this game build; this edit was not applied" },
      { mod: "typo", source: "content/typo.json", stage: "alter", n: 11,
        registry: "items", id: "game:fixture_stack", field: "maxStak",
        reason: "items has no field \"maxStak\"" },
    ] });
    expect(out.refused.slice(0, 2).map(row => row.field)).toEqual([
      "Advanced field (details hidden)", "Advanced field (details hidden)",
    ]);
    expect(JSON.stringify(out.refused)).not.toContain("Missing.Member");
    expect(JSON.stringify(out.refused)).not.toContain("maxStackSize");
    expect(out.refused[2].reason).toBe("items has no field \"maxStak\"");
    expect(out.refused[2].field).toContain("maxStak");
  });
  test("a refusal of a misspelled Kiln field keeps its native reason and is not called Advanced", () => {
    const r = registryFixture();
    const out = parse({ ...r, refused: [{ mod: "typo", source: "content/a.json", stage: "alter", n: 0, registry: "items", id: "game:fixture_stack",
      field: "maxStak", reason: "items has no field \"maxStak\"" }] });
    expect(out.refused[0].reason).toBe("items has no field \"maxStak\"");
    expect(out.refused[0].field).toContain("maxStak");
    expect(out.refused[0].field).toContain("Field unknown to this launcher");
    expect(JSON.stringify(out.refused)).not.toContain("Advanced");
  });
  test("a field or conflict a newer Kiln names is labelled unknown to this launcher, not Advanced", () => {
    const r = registryFixture();
    const out = parse({ ...r, fields: [{ ...r.fields[0], field: "futureField" }],
      conflicts: [{ ...r.conflicts[0], field: "items[game:fixture_stack].futureField", text: "stacker composes on earlier" }] });
    expect(out.fields[0].field).toBe("futureField (Field unknown to this launcher)");
    expect(out.conflicts[0].field).toBe("items[game:fixture_stack].futureField (Field unknown to this launcher)");
    expect(out.conflicts[0].text).toBe("stacker composes on earlier");
  });
  test("a refusal on a Kiln element path keeps its reason verbatim", () => {
    const r = registryFixture();
    const reason = "Unproven: drops.entries[2].chance is not proven for this build (no verified mapping); this edit was not applied";
    const out = parse({ ...r, refused: [{ mod: "loot", source: "", stage: "alter", n: 0, registry: "drops", id: "game:fixture_drops", field: "entries[2].chance", reason }] });
    expect(out.refused[0].reason).toBe(reason);
    expect(out.refused[0].field).toBe("drops · game:fixture_drops · entries[2].chance");
  });
  test("a refusal the runtime marks raw stays hidden", () => {
    const r = registryFixture();
    const out = parse({ ...r, refused: [{ mod: "adv", source: "", stage: "alter", n: 0, registry: "items", field: "SecretSetup.Member", raw: true,
      reason: "game:x: no entry" }] });
    expect(JSON.stringify(out.refused)).not.toContain("SecretSetup");
    expect(out.refused[0].field).toBe("Advanced field (details hidden)");
  });
  test("a Kiln field's conflict text never names a raw path a mod wrote", () => {
    const r = registryFixture();
    const out = parse({ ...r, conflicts: [
      { ...r.conflicts[0], kind: "overridden", text: "items[game:fixture_stack].maxStack: b sets 5 over a's raw path SecretDesc.m_stackSize (one place in the game on two scales; the later value wins)" },
      { ...r.conflicts[0], kind: "exclusive", text: "Conflict (one place, two scales): items[game:fixture_stack].maxStack and raw path SecretDesc.m_stackSize are one place in the game, which Kiln converts between them; a writes raw path SecretDesc.m_stackSize there and no conversion of a relative change is known here: b's relative edit is refused (a value would win)" },
    ] });
    expect(JSON.stringify(out.conflicts)).not.toContain("SecretDesc");
    expect(out.conflicts[0].text).toContain("raw path (details hidden)");
    expect(out.conflicts[1].text).toContain("items[game:fixture_stack].maxStack and raw path (details hidden) are one place");
  });
  test("a Kiln field's refusal reason never names a raw path a mod wrote", () => {
    const r = registryFixture();
    const out = parse({ ...r, refused: [
      { ...r.refused[1], reason: "Conflict (one place, two scales): items[game:fixture_stack].maxStack and raw path SecretDesc.m_stackSize are one place" },
      { ...r.refused[1], field: "", reason: "game:x: raw path \"SecretDesc.Member\" does not resolve on this game build (no member)" },
      { ...r.refused[1], field: "", reason: "items[game:x].raw:secretdesc.member: needs a restart; this edit was not applied" },
      { ...r.refused[1], field: "", reason: "Conflict (exclusive): a also writes items[game:x].SecretDesc.Member (raw); the later edit is refused" },
    ] });
    expect(JSON.stringify(out.refused).toLowerCase()).not.toContain("secretdesc");
    expect(out.refused[0].reason).toContain("items[game:fixture_stack].maxStack");
  });
  test("field rows carry stable keys by registry, id and field (not by position)", () => {
    const r = registryFixture();
    const a = { ...r.fields[0], id: "game:a" }, b = { ...r.fields[0], id: "game:b" };
    const one = parse({ ...r, fields: [a, b, b] }), two = parse({ ...r, fields: [b, a] });
    expect(one.fields.map(f => f.key)).toEqual(["items|game:a|maxStack|0", "items|game:b|maxStack|0", "items|game:b|maxStack|1"]);
    expect(two.fields[1].key).toBe(one.fields[0].key);
  });
  test("zero-width and bidi marks are removed and truncation never splits a surrogate pair", () => {
    const r = registryFixture();
    const out = parse({ ...r, refused: [{ ...r.refused[0], mod: "ev\u200bil\u200e\u200f\u061c\u2060\ufeff", reason: "x".repeat(REPORT_LIMITS.text - 1) + "😀tail" }] });
    expect(out.refused[0].owner).toBe("evil");
    expect(out.refused[0].reason).not.toMatch(/[\ud800-\udbff](?![\udc00-\udfff])/);
  });
  test("record members hidden from a value are marked, not silently dropped", () => {
    const r = registryFixture();
    const out = parse({ ...r, fields: [{ ...r.fields[0], result: { offset: 3, item: "game:x" } }] });
    expect(out.fields[0].result).toContain("game:x");
    expect(out.fields[0].result).toContain("1 hidden");
  });
  test("HTML is kept as plain text, never interpreted", () => {
    const r = registryFixture(); r.refused[0].reason = '<img src=x onerror="alert(1)">';
    expect(parse(r).refused[0].reason).toBe(r.refused[0].reason);
  });
  test("deep or wide values are bounded, not recursively stringified wholesale", () => {
    const r = registryFixture();
    const out = parse({ ...r, fields: [{ ...r.fields[0], result: Array(500).fill("value") }] });
    expect(out.fields[0].result.length).toBeLessThan(2000);
    expect(out.fields[0].result).toContain("omitted");
  });
});
describe("snapshot freshness", () => {
  const now = Date.now();
  const written = new Date(now - 1000).toISOString();
  test("stopped game is stale", () => expect(reportFreshness(written, { running: false }, now)).toContain("not running"));
  test("report from a previous process start is stale", () => expect(reportFreshness(written, { running: true, started: new Date(now).toISOString() }, now)).toContain("current game start"));
  test("old snapshot is stale even when the game is running", () => expect(reportFreshness(new Date(now - 86400001).toISOString(), { running: true }, now)).toContain("24 hours"));
  test("future clock is not presented as fresh", () => expect(reportFreshness(new Date(now + 600000).toISOString(), { running: true }, now)).not.toBeNull());
  test("unknown process state is explicit", () => expect(reportFreshness(written, undefined, now)).toContain("cannot be verified"));
  test("invalid process-start timestamp cannot establish freshness", () => expect(reportFreshness(written, { running: true, started: "invalid" }, now)).toContain("cannot be verified"));
  test("snapshot from this start is not stale", () => expect(reportFreshness(written, { running: true, started: new Date(now - 2000).toISOString() }, now)).toBeNull());
});

// Review (registry-eml-journal): the Conflicts page's archive-writer texts are in every locale with English's placeholders,
// the EML pass's field-level ones included, and no locale still says a recipe or loot entry is never named (an EML pass
// names them by their element).
test("every locale has the archive-writer texts, the EML pass's field-level ones too, with English's placeholders", () => {
  const keys = ["journal.read", "journal.truncated", "archiveUnattributed", "archiveOp", "archiveOpField", "kind.archiveWriter", "kind.archiveWriterField", "certaintyHint"];
  const at = (o: unknown, k: string) => k.split(".").reduce<unknown>((x, p) => (x as Record<string, unknown> | undefined)?.[p], (o as Record<string, unknown>).registry);
  const ph = (s: unknown) => (String(s).match(/\{[a-z]+\}/gi) ?? []).sort().join();
  const old: Record<string, string> = { de: "Rezept- oder Beute-Eintrag wird nicht benannt", es: "no se nombra ninguna entrada de receta", fr: "recette ou de butin n'est pas nommée",
    it: "ricetta o di bottino non viene indicata", ja: "レシピやルートのエントリは特定されません", ko: "레시피나 전리품 항목은 확인하지 않습니다", pl: "wpis receptury ani łupu nie jest nazywany",
    ptBR: "receita ou de saque não é nomeada", ru: "запись рецепта или добычи не называется", zhCN: "不指认配方或战利品条目" };
  for (const [name, loc] of Object.entries({ de, es, fr, it, ja, ko, pl, ptBR, ru, zhCN })) {
    for (const k of keys) {
      expect(typeof at(loc, k), `${name} ${k}`).toBe("string");
      expect(ph(at(loc, k)), `${name} ${k}`).toBe(ph(at(enText, k)));
    }
    expect(String(at(loc, "journal.read")).includes(old[name]), `${name}: journal.read says recipes and loot are never named`).toBe(false);
  }
});
