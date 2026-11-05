// SPDX-License-Identifier: MIT
// Headless unit tests of src/core (no VS Code): mocha --ui tdd out/test/unit/*.test.js
import * as assert from "node:assert/strict";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { Catalog, describeEntry, locateCatalog, stringAt } from "../../src/core/catalog";
import { sendControl } from "../../src/core/control";
import { blankNonCode, findTests } from "../../src/core/csharpTests";
import { globToRegExp, matchFiles } from "../../src/core/glob";
import { FALLBACK_IDREFS, IdRefIndex } from "../../src/core/idrefs";
import { codePart, ecallAt, fnv1a32, guidBytes, hex32, ImpactData, scanSymbols, wordAt } from "../../src/core/impactData";
import { byFile, parseLintReport, rangeOf } from "../../src/core/lintReport";
import { findLinks, parseLogLine, winToPosix } from "../../src/core/logLine";
import { defaultModId, isKilnProject, modFromDir } from "../../src/core/project";
import { discoverKilnctl, discoverLabs, protectedReason, readTomlTop } from "../../src/core/servers";
import { byteToIndex, indexToByte, lineColToIndex } from "../../src/core/utf8";

// out/test/unit/x.js -> tools/vscode
const EXT = path.resolve(__dirname, "../../..");
const REPO = path.resolve(EXT, "../..");
const FIX = path.join(EXT, "test", "fixtures");
const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), "kiln-vscode-"));

suite("utf8", () => {
  test("byte offsets <-> string indices with multi-byte characters", () => {
    const t = "aé🔥b";
    assert.equal(byteToIndex(t, 0), 0);
    assert.equal(byteToIndex(t, 1), 1);
    assert.equal(byteToIndex(t, 3), 2); // after é (2 bytes)
    assert.equal(byteToIndex(t, 7), 4); // after the emoji (4 bytes, 2 UTF-16 units)
    assert.equal(byteToIndex(t, 8), 5);
    assert.equal(byteToIndex(t, 99), t.length);
    assert.equal(indexToByte(t, 4), 7);
  });
  test("1-based line/col in code points", () => {
    const t = "x\n🔥ab\n";
    assert.equal(lineColToIndex(t, 2, 1), 2);
    assert.equal(lineColToIndex(t, 2, 2), 4); // past the emoji's two UTF-16 units
    assert.equal(lineColToIndex(t, 2, 3), 5);
  });
});

suite("lint report", () => {
  const text = fs.readFileSync(path.join(FIX, "lint", "kiln.mod.json"), "utf8");
  const rep = parseLintReport("warning: something on stdout first\n" + fs.readFileSync(path.join(FIX, "lint", "report.json"), "utf8"));
  test("parses the agreed shape", () => {
    assert.equal(rep.format, 1);
    assert.equal(rep.diagnostics.length, 2);
    assert.equal(rep.summary?.errors, 1);
    assert.equal(rep.diagnostics[0].rule, "unknown-key");
  });
  test("byte spans land on the right text after non-ASCII lines", () => {
    const r = rangeOf(text, rep.diagnostics[0]);
    assert.equal(text.slice(r.start, r.end), '"dependecies"');
    const v = rangeOf(text, rep.diagnostics[1]);
    assert.equal(text.slice(v.start, v.end), "2");
  });
  test("line/col fallback agrees with the span", () => {
    const d = { ...rep.diagnostics[0], span: undefined };
    const r = rangeOf(text, d);
    assert.equal(text.slice(r.start, r.end), '"dependecies"');
  });
  test("fix edits use spans too", () => {
    const e = rep.diagnostics[0].fixes![0].edits[0];
    const r = rangeOf(text, { span: e.span });
    assert.equal(text.slice(0, r.start) + e.newText + text.slice(r.end), text.replace("dependecies", "dependencies"));
  });
  test("grouping and rejection of other formats", () => {
    assert.equal(byFile(rep).get("/FIXTURE/kiln.mod.json")!.length, 2);
    assert.throws(() => parseLintReport('{"format":2,"diagnostics":[]}'), /format 2/);
    assert.throws(() => parseLintReport("nothing"), /no JSON/);
  });
});

suite("lint report: the shared fixture", () => {
  const raw = fs.readFileSync(path.join(REPO, "schema", "fixtures", "lint", "output.json"), "utf8").replaceAll("<repo>", REPO);
  const rep = parseLintReport(raw);
  test("every diagnostic in a readable file: the byte span and line/col name the same text", () => {
    let checked = 0;
    for (const d of rep.diagnostics) {
      if (!d.span || !d.line || !fs.existsSync(d.file) || !fs.statSync(d.file).isFile()) continue;
      const text = fs.readFileSync(d.file, "utf8");
      const a = rangeOf(text, d);
      const b = rangeOf(text, { ...d, span: undefined });
      assert.deepEqual([a.start, a.end], [b.start, b.end], `${d.code} in ${d.file}`);
      checked++;
    }
    assert.ok(checked > 0, "the fixture has diagnostics with spans");
  });
  test("every severity and fix edit is usable", () => {
    for (const d of rep.diagnostics) {
      assert.ok(["error", "warning", "info", "hint"].includes(d.severity));
      for (const fix of d.fixes ?? []) for (const e of fix.edits) assert.ok(e.span && typeof e.newText === "string", `${d.code}: ${fix.title}`);
    }
  });
});

suite("kiln.log lines", () => {
  test("parse", () => {
    const l = parseLogLine("[E 00:50:29.622] [mod:api-test] config api-test: broken");
    assert.deepEqual(l, { level: "E", time: "00:50:29.622", tag: "mod:api-test", text: "config api-test: broken" });
    assert.equal(parseLogLine("   at X.Y()"), undefined);
  });
  test("links: mod tag, Wine path, stack frame, POSIX path with line, test name", () => {
    const a = "[E 00:50:29.622] [mod:api-test] config api-test: Z:\\home\\user\\srv\\lab\\kiln\\config\\api-test.json is not valid JSON";
    const la = findLinks(a);
    assert.deepEqual(la.map((x) => x.target), [
      { kind: "mod", id: "api-test" },
      { kind: "file", path: "/home/user/srv/lab/kiln/config/api-test.json" },
    ]);
    const s = "   at ChatTest.ChatTestMod.<>c.<CommandChecks>b__9_9(CommandContext _) in /home/user/kiln/mods/tests/chat-test/ChatTestMod.cs:line 116";
    const ls = findLinks(s);
    assert.equal(ls.length, 1);
    assert.deepEqual(ls[0].target, { kind: "file", path: "/home/user/kiln/mods/tests/chat-test/ChatTestMod.cs", line: 116 });
    assert.equal(s.slice(ls[0].start, ls[0].end), "/home/user/kiln/mods/tests/chat-test/ChatTestMod.cs:line 116");
    const w = findLinks("   at A.B() in Z:\\home\\x\\Mod.cs:line 7");
    assert.deepEqual(w[0].target, { kind: "file", path: "/home/x/Mod.cs", line: 7 });
    const p = findLinks("[I 00:00:00.000] [host] wrote /home/x/kiln/logs/tests/a.json:12:3.");
    assert.deepEqual(p.find((x) => x.target.kind === "file")!.target, { kind: "file", path: "/home/x/kiln/logs/tests/a.json", line: 12, col: 3 });
    const t = findLinks("[I 06:28:28.400] [mod:sample-tests] PASS SampleTestsMod.Imc_round_trip (97.5 ms)");
    assert.deepEqual(t.find((x) => x.target.kind === "test")!.target, { kind: "test", mod: "sample-tests", name: "SampleTestsMod.Imc_round_trip" });
    const f = findLinks("[E 06:28:28.400] [mod:x] FAIL MyTests.Something (timeout): did not finish within 10 s");
    assert.deepEqual(f.find((x) => x.target.kind === "test")!.target, { kind: "test", mod: "x", name: "MyTests.Something" });
  });
  test("Wine paths", () => {
    assert.equal(winToPosix("Z:\\a\\b.txt"), "/a/b.txt");
    assert.equal(winToPosix("C:\\x"), "C:/x");
    assert.equal(winToPosix("/already"), "/already");
  });
});

suite("C# test discovery", () => {
  test("finds [KilnTest] methods with Kiln.Testing's names", () => {
    const src = `
using Kiln.Testing;
namespace N;
// [KilnTest] in a comment is not a test
public sealed class MyTests
{
    [KilnTest]
    public void Mod_is_loaded() => Assert.True(Mods.IsLoaded("my-mod"));

    [KilnTest(TimeoutSeconds = 10)]
    public async Task Something_happens(TestContext t) { var s = "[KilnTest] public void Fake()"; await t.Ticks(3); }

    [KilnTest(Skip = "needs a player")]
    public void Later() { }

    [KilnTest("custom name")]
    [System.Obsolete]
    public static ValueTask<int> Named() => default;

    [KilnTest(Name = "other \\"quoted\\"", Order = 2)] public void Prop() { }

    public sealed class Inner { [KilnTest] public void Deep() { } }
    record R(int X);
    [KilnTest] public void AfterRecord() { }
}`;
    const tests = findTests(src);
    assert.deepEqual(
      tests.map((t) => [t.name, t.className, t.method, t.skip ?? null]),
      [
        ["MyTests.Mod_is_loaded", "MyTests", "Mod_is_loaded", null],
        ["MyTests.Something_happens", "MyTests", "Something_happens", null],
        ["MyTests.Later", "MyTests", "Later", "needs a player"],
        ["custom name", "MyTests", "Named", null],
        ['other \\"quoted\\"', "MyTests", "Prop", null],
        ["Inner.Deep", "Inner", "Deep", null],
        ["MyTests.AfterRecord", "MyTests", "AfterRecord", null],
      ],
    );
    const t0 = tests[0];
    assert.equal(src.split("\n")[t0.methodLine].slice(t0.methodCol, t0.methodCol + t0.method.length), "Mod_is_loaded");
  });
  test("blanking keeps offsets", () => {
    const s = 'a "b;c" // d\n/* e */ f';
    const b = blankNonCode(s);
    assert.equal(b.length, s.length);
    assert.equal(b.indexOf("f"), s.indexOf("f"));
    assert.ok(!b.includes(";"));
  });
  test("the repository's SampleTests", () => {
    const file = path.join(REPO, "mods", "samples", "SampleTests", "SampleTestsMod.cs");
    if (!fs.existsSync(file)) return;
    const tests = findTests(fs.readFileSync(file, "utf8"));
    assert.ok(tests.length >= 10, `found ${tests.length}`);
    assert.ok(tests.some((t) => t.name === "SampleTestsMod.Imc_round_trip"));
    assert.ok(tests.some((t) => t.skip === "shows how a skipped test is reported"));
  });
});

suite("catalog", () => {
  const c = Catalog.load(path.join(FIX, "catalog", "client.json"));
  test("resolve by name, case, GUID, id and prefixed forms", () => {
    assert.equal(c.resolve("Test_Sword_T2")?.entry.displayName, "Test Sword T2");
    assert.equal(c.resolve("test_sword_t2")?.entry.name, "Test_Sword_T2");
    assert.equal(c.resolve("11111111-2222-4333-8444-555555555555")?.section, "items");
    assert.equal(c.resolve("0xBEEF")?.entry.name, "Test_Sword_T2");
    assert.equal(c.resolve("item:Material_Test_Resin.maxStack")?.entry.name, "Material_Test_Resin");
    assert.equal(c.resolve("template:Explosion_Test_1")?.section, "templates");
    assert.equal(c.resolve("Test_Sword_T2", ["templates"]), undefined);
    assert.equal(c.resolve("Nope"), undefined);
  });
  test("search and icons", () => {
    assert.deepEqual(c.search("test", ["items"]).map((h) => h.entry.name).slice(0, 1), ["Material_Test_Resin"]);
    assert.equal(c.search("resin")[0].entry.name, "Material_Test_Resin");
    assert.ok(c.iconPath(c.resolve("Test_Sword_T2")!.entry)!.endsWith("Test_Sword_T2_icon.webp"));
    assert.equal(c.iconPath(c.resolve("Material_Test_Twigs")!.entry), undefined);
    assert.match(describeEntry("items", c.resolve("Test_Sword_T2")!.entry).join("\n"), /tier 2/);
  });
  test("locate: a folder prefers client.json; a file is taken as is", () => {
    assert.equal(locateCatalog(path.join(FIX, "catalog")), path.join(FIX, "catalog", "client.json"));
    const d = tmp();
    fs.writeFileSync(path.join(d, "server.json"), "{}");
    assert.equal(locateCatalog(d), path.join(d, "server.json"));
  });
  test("the catalog fixture (crates/kiln-manage/src/catalog/testdata)", function () {
    const f = path.join(REPO, "crates", "kiln-manage", "src", "catalog", "testdata", "client.json");
    if (!fs.existsSync(f)) this.skip();
    const k = Catalog.load(f);
    const blade = k.resolve("Test_Frost_Blade", ["items"])!;
    assert.equal(blade.entry.displayName, "Frost Blade");
    assert.ok(k.iconPath(blade.entry)?.endsWith("Test_Frost_Blade_icon.webp"));
    assert.equal(k.resolve("0x0e0e0001", ["buffs"])?.entry.displayName, "Chilled"); // data packs key buffs by id
    assert.equal(k.resolve("0x10101001", ["loca"])?.entry.displayName, "Frost Blade"); // loca: id + text
    assert.ok(k.entries("drops").length >= 1 && k.entries("resources").length >= 1); // keyed by GUID
    for (const s of k.sectionNames()) assert.ok(k.entries(s).every((e) => typeof e.name === "string"), s);
  });
  test("the string literal under the cursor", () => {
    const l = '{ "guid": "6a7d", "base": "Test_Frost_Blade", "x": "a\\"b" }';
    const at = (needle: string, d: number) => stringAt(l, l.indexOf(needle) + d);
    assert.deepEqual(at("Test_Frost_Blade", 3), { start: l.indexOf("Test_Frost_Blade"), end: l.indexOf("Test_Frost_Blade") + 16 });
    assert.equal(l.slice(at("base", 1)!.start, at("base", 1)!.end), "base");
    assert.equal(l.slice(at('a\\"b', 1)!.start, at('a\\"b', 1)!.end), 'a\\"b');
    assert.equal(stringAt(l, l.indexOf(":")), undefined);
    assert.deepEqual(stringAt('"item": "', 9), { start: 9, end: 9 }); // unclosed: runs to the end
  });
  test("rejects other kinds", () => {
    const d = tmp();
    fs.writeFileSync(path.join(d, "client.json"), JSON.stringify({ format: 1, kind: "other", sections: {} }));
    assert.throws(() => Catalog.load(path.join(d, "client.json")), /kind/);
  });
});

suite("glob and idrefs", () => {
  test("schema/idrefs.json: every plain row matches a value at its own pointer", () => {
    const table = JSON.parse(fs.readFileSync(path.join(REPO, "schema", "idrefs.json"), "utf8"));
    const ix = new IdRefIndex(table);
    assert.equal(ix.size, table.refs.length);
    let checked = 0;
    for (const r of table.refs as { files: string[]; pointer: string; section: string }[]) {
      const glob = r.files[0];
      if (r.files.length !== 1 || /[{}!?[\]]/.test(glob)) continue;
      const file = glob.replace(/^\*\*\//, "/m/").replace(/\*\*\//g, "").replace(/\*/g, "x");
      const isKey = r.pointer.endsWith("~");
      const at = (isKey ? r.pointer.slice(0, -1) : r.pointer).split("/").slice(1).map((p) => (p === "*" ? "x" : p.replace(/~1/g, "/").replace(/~0/g, "~")));
      assert.ok(ix.match([file], at, isKey), `${glob} ${r.pointer}`);
      checked++;
    }
    assert.ok(checked > 50, `${checked} rows checked`);
  });
  test("globs", () => {
    assert.ok(globToRegExp("**/kiln.mod.json").test("/a/b/kiln.mod.json"));
    assert.ok(globToRegExp("**/kiln.mod.json").test("kiln.mod.json"));
    assert.ok(!globToRegExp("**/kiln.mod.json").test("/a/xkiln.mod.json"));
    assert.ok(globToRegExp("**/content/**/*.json").test("mods/x/content/a.json"));
    assert.ok(globToRegExp("**/content/**/*.json").test("/m/content/sub/a.json"));
    assert.ok(globToRegExp("**/{a,b}.json").test("x/b.json"));
    assert.ok(matchFiles(["**/native.json", "!**/kiln/config/native.json"], ["/g/mods/n/native.json"]));
    assert.ok(!matchFiles(["**/native.json", "!**/kiln/config/native.json"], ["/g/kiln/config/native.json"]));
  });
  test("idrefs pointers: values, keys, keywords", () => {
    const ix = new IdRefIndex(FALLBACK_IDREFS);
    const f = ["/m/content/pack.json"];
    assert.equal(ix.match(f, ["items", "add", 0, "base"], false)?.section, "items");
    assert.equal(ix.match(f, ["items", "Test_Sword_T2"], true)?.section, "items");
    assert.equal(ix.match(f, ["items", "add"], true), undefined); // the keyword
    assert.equal(ix.match(f, ["recipes", "add", 3, "inputs", 1, "item"], false)?.section, "items");
    assert.equal(ix.match(f, ["templates", "Explosion_Test_1"], true)?.section, "templates");
    assert.equal(ix.match(["/m/assets/assets.json"], ["items", "add", 0, "base"], false), undefined);
    const custom = new IdRefIndex({ format: 1, refs: [{ files: ["**/x.json"], pointer: "/a~1b/*/c~0d", section: "items" }] });
    assert.equal(custom.match(["/x.json"], ["a/b", 2, "c~d"], false)?.section, "items");
  });
});

suite("impact data", () => {
  const index = JSON.parse(fs.readFileSync(path.join(EXT, "data", "impact", "index.json"), "utf8"));
  const d = new ImpactData(index);
  test("hashes as the game computes them", () => {
    assert.equal(hex32(fnv1a32("ApplyDamage")), "0xb04d0e7a");
    assert.equal(hex32(fnv1a32("kiln.Log")), "0xc0b137a7");
    assert.equal(hex32(fnv1a32("kiln.Signal")), "0x38b05baf");
    assert.equal(hex32(fnv1a32(guidBytes("aad149f6-5485-4607-875e-62722822c77f"))), "0xa1bcb09a");
  });
  test("natives by name, node name and hash; mod natives", () => {
    assert.equal(d.native("applyDamage")?.argc, 25);
    assert.equal(d.native("ApplyDamage")?.name, "applyDamage");
    assert.equal(d.native("0xb04d0e7a")?.name, "applyDamage");
    assert.equal(d.native("kiln.Signal")?.origin, "kiln");
    const m = d.withModNatives({ natives: [{ name: "impactsample.Ping", argc: 1, args: [{ name: "entity", types: ["ecs.EntityId"], dir: "in" }] }] });
    assert.equal(m.native("impactsample.Ping")?.hash, hex32(fnv1a32("impactsample.Ping")));
    assert.equal(d.native("impactsample.Ping"), undefined);
    assert.ok(d.types.get("float32"));
    assert.ok(d.config("Damage") || d.index.configs.length > 0);
  });
  test("line analysis", () => {
    const line = "tick:   ecall changeAttributeValue($health, $add, $old, $target, _, $amount) site heal ; c";
    const at = ecallAt(line, line.indexOf("$old") + 1)!;
    assert.deepEqual([at.native, at.arg], ["changeAttributeValue", 2]);
    assert.equal(ecallAt(line, line.indexOf("site")), undefined);
    assert.equal(wordAt(line, line.indexOf("$add") + 2)?.text, "$add");
    assert.equal(wordAt(line, 1)?.text, "tick");
    assert.equal(codePart(line).includes("; c"), false);
    const nested = "ecall f($a, (1, 2), $b";
    assert.equal(ecallAt(nested, nested.length)!.arg, 2);
  });
  test("symbols of the ImpactSample program", () => {
    const file = path.join(REPO, "mods", "samples", "ImpactSample", "impact", "heal_over_time.kimp");
    if (!fs.existsSync(file)) return;
    const s = scanSymbols(fs.readFileSync(file, "utf8"));
    assert.equal(s.guid, "aad149f6-5485-4607-875e-62722822c77f");
    assert.equal(s.vars.get("amount")?.type, "sint32");
    assert.ok(s.labels.has("tick") && s.labels.has("wait") && s.labels.has("again"));
    assert.equal(s.labels.get("tick")?.section, "code");
  });
});

suite("control folder", () => {
  test("round trip with a responder that behaves like Kiln.Host", async () => {
    const srv = tmp();
    const ctl = path.join(srv, "kiln", "control");
    fs.mkdirSync(ctl, { recursive: true });
    const seen: string[] = [];
    const t = setInterval(() => {
      for (const f of fs.readdirSync(ctl).filter((x) => x.endsWith(".cmd"))) {
        const cmd = fs.readFileSync(path.join(ctl, f), "utf8").trim();
        fs.rmSync(path.join(ctl, f));
        seen.push(cmd);
        const name = f.slice(0, -4);
        const res = { command: cmd, ok: true, message: `did ${cmd}`, data: { x: 1 }, time: new Date().toISOString() };
        fs.writeFileSync(path.join(ctl, `${name}.result.json.tmp`), JSON.stringify(res));
        fs.renameSync(path.join(ctl, `${name}.result.json.tmp`), path.join(ctl, `${name}.result.json`));
      }
    }, 20);
    try {
      const r = await sendControl(srv, "reload my-mod", { pollMs: 10, timeoutMs: 3000 });
      assert.equal(r.message, "did reload my-mod");
      assert.deepEqual(seen, ["reload my-mod"]);
      assert.deepEqual(fs.readdirSync(ctl), []); // result consumed, nothing left
    } finally {
      clearInterval(t);
    }
  });
  test("nobody listening: not_picked_up, and the .cmd is removed", async () => {
    const srv = tmp();
    fs.mkdirSync(path.join(srv, "kiln"));
    await assert.rejects(sendControl(srv, "mods", { timeoutMs: 200, pollMs: 20 }), (e: Error & { code?: string }) => e.code === "not_picked_up");
    assert.deepEqual(fs.readdirSync(path.join(srv, "kiln", "control")), []);
  });
  test("no kiln folder", async () => {
    await assert.rejects(sendControl(tmp(), "mods"), /no kiln/);
  });
});

suite("servers", () => {
  test("Steam libraries are read-only", () => {
    assert.ok(protectedReason("/home/x/.local/share/Steam/steamapps/common/EnshroudedServer"));
    assert.ok(protectedReason("/mnt/g/SteamLibrary/steamapps/compatdata/1203620/pfx"));
    assert.equal(protectedReason("/home/x/srv/enshrouded/labs/mylab"), undefined);
  });
  test("kilnctl instance files", () => {
    assert.deepEqual(readTomlTop('# c\ndir = "/srv/a" # x\nquery_port = 15637\nwine = \'ge\'\n[kiln]\nsource = "/nope"\n'), {
      dir: "/srv/a",
      query_port: "15637",
      wine: "ge",
    });
    const d = tmp();
    fs.writeFileSync(path.join(d, "one.toml"), 'dir = "/srv/one"\nprefix = "/srv/one.pfx"\n');
    fs.writeFileSync(path.join(d, "two.toml"), 'dir = "/srv/two"\n');
    fs.writeFileSync(path.join(d, "bad.toml"), "nothing = 1\n");
    const s = discoverKilnctl(d);
    assert.deepEqual(s.map((x) => [x.name, x.dir, x.prefix, x.kind]), [
      ["one", "/srv/one", "/srv/one.pfx", "kilnctl"],
      ["two", "/srv/two", "/srv/two.pfx", "kilnctl"],
    ]);
  });
  test("labs", () => {
    const d = tmp();
    for (const n of ["a", "b.pfx", "c"]) fs.mkdirSync(path.join(d, n));
    fs.writeFileSync(path.join(d, "a", "enshrouded_server.exe"), "");
    assert.deepEqual(discoverLabs(d).map((x) => [x.name, x.prefix]), [["a", path.join(d, "a.pfx")]]);
  });
});

suite("projects", () => {
  test("mod ids", () => {
    assert.equal(defaultModId("My Cool.Mod_1"), "my-cool.mod_1");
    assert.ok(isKilnProject('<Project Sdk="Kiln.Sdk/0.2.0">'));
    assert.ok(isKilnProject('<Import Project="../../../managed/Kiln.Sdk/Sdk/Sdk.props" />'));
    assert.ok(!isKilnProject('<Project Sdk="Microsoft.NET.Sdk">'));
  });
  test("the repository's SampleTests is a Kiln.Testing mod", () => {
    const dir = path.join(REPO, "mods", "samples", "SampleTests");
    if (!fs.existsSync(dir)) return;
    const m = modFromDir(dir)!;
    assert.equal(m.id, "sample-tests");
    assert.equal(m.testing, true);
    assert.ok(m.project?.endsWith("SampleTests.csproj"));
  });
  test("a manifest-only folder (JSONC)", () => {
    const d = tmp();
    fs.writeFileSync(path.join(d, "kiln.mod.json"), '{ // c\n "id": "data-mod", "version": "1.0.0", }');
    const m = modFromDir(d)!;
    assert.deepEqual([m.id, m.version, m.project], ["data-mod", "1.0.0", undefined]);
  });
});
