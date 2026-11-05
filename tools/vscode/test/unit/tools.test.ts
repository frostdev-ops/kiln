// SPDX-License-Identifier: MIT
// The assembler bridge (python/kimp_check.py over tools/impact) and the problem matchers in package.json.
import * as assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";

const EXT = path.resolve(__dirname, "../../..");
const REPO = path.resolve(EXT, "../..");
const IMPACT = path.join(REPO, "tools", "impact");
const SAMPLE = path.join(REPO, "mods", "samples", "ImpactSample", "impact");

function check(file: string, text: string, natives: string[] = []) {
  const args = [path.join(EXT, "python", "kimp_check.py"), "--impact", IMPACT, "--path", file, ...natives.flatMap((n) => ["--natives", n])];
  const r = spawnSync("python3", args, { input: text, encoding: "utf8" });
  assert.equal(r.status, 0, r.stderr);
  return JSON.parse(r.stdout);
}

suite("kimp_check.py", function () {
  this.timeout(20000);
  suiteSetup(function () {
    if (!fs.existsSync(path.join(IMPACT, "impact", "asm.py"))) this.skip();
  });
  test("the sample program assembles and lints; variable findings point at their var line", () => {
    const text = fs.readFileSync(path.join(SAMPLE, "heal_over_time.kimp"), "utf8");
    const r = check(path.join(SAMPLE, "heal_over_time.kimp"), text, [path.join(SAMPLE, "natives.json")]);
    assert.equal(r.ok, true);
    assert.equal(r.program.id, "0xa1bcb09a");
    const amount = r.findings.find((f: { msg: string }) => f.msg.startsWith("$amount:"));
    assert.ok(amount);
    assert.match(text.split("\n")[amount.line - 1], /var \$amount/);
  });
  test("assembler errors carry the line", () => {
    const text = ".kimp 1\nprogram aad149f6-5485-4607-875e-62722822c77f\nvar $t ecs.EntityId\ncode:\n  ecall getBuffTarget($t, $t)\n  halt\n";
    const r = check("x.kimp", text);
    assert.equal(r.ok, false);
    assert.equal(r.error.line, 5);
    assert.match(r.error.message, /takes 1 argument/);
  });
  test("lint findings map pcs back to lines", () => {
    const text = ".kimp 1\nprogram aad149f6-5485-4607-875e-62722822c77f\nvar $a sint32\ncode:\n  gload $a\n  pop\nl: br l\n  halt\n";
    const r = check("y.kimp", text);
    const loop = r.findings.find((f: { code: string }) => f.code === "W_LOOP");
    assert.equal(loop.line, 7);
    const depth = check("z.kimp", ".kimp 1\nprogram aad149f6-5485-4607-875e-62722822c77f\ncode:\n  iconst1\n  halt\n");
    assert.ok(depth.findings.length >= 0);
  });
  test("a .kpatch is checked without its program's variables", () => {
    const text = fs.readFileSync(path.join(SAMPLE, "chain.kpatch"), "utf8");
    assert.equal(check(path.join(SAMPLE, "chain.kpatch"), text, [path.join(SAMPLE, "natives.json")]).ok, true);
    const bad = check("p.kpatch", "  ecall noSuchNative($x)\n  pop\n");
    assert.equal(bad.ok, false);
    assert.equal(bad.error.line, 1);
    const label = check("q.kpatch", "  br nowhere\n");
    assert.match(label.error.message, /no label/);
  });
});

suite("problem matchers", () => {
  const pkg = JSON.parse(fs.readFileSync(path.join(EXT, "package.json"), "utf8"));
  const pm = (name: string) => {
    const m = pkg.contributes.problemMatchers.find((x: { name: string }) => x.name === name);
    return { re: new RegExp(m.pattern.regexp), p: m.pattern };
  };
  const get = (name: string, line: string) => {
    const { re, p } = pm(name);
    const m = re.exec(line);
    if (!m) return undefined;
    const out: Record<string, string> = {};
    for (const k of ["file", "line", "column", "severity", "code", "message"]) if (p[k] && m[p[k]] !== undefined) out[k] = m[p[k]];
    return out;
  };
  test("KILN1xx analyzers (with a location)", () => {
    const l = "/home/u/MyMod/MyModMod.cs(12,9): warning KILN101: Game state touched off the tick thread [/home/u/MyMod/MyMod.csproj]";
    assert.deepEqual(get("kiln-sdk", l), { file: "/home/u/MyMod/MyModMod.cs", line: "12", column: "9", severity: "warning", code: "KILN101", message: "Game state touched off the tick thread" });
    assert.equal(get("kiln-dotnet", l), undefined, "kiln-dotnet leaves KILN codes alone");
    assert.equal(get("kiln-sdk-file", l), undefined);
  });
  test("KILN0xx on kiln.mod.json (no line) and on the project (no file)", () => {
    const f = "/home/u/MyMod/kiln.mod.json : error KILN001: 'version' must be a semantic version [/home/u/MyMod/MyMod.csproj]";
    assert.deepEqual(get("kiln-sdk-file", f), { file: "/home/u/MyMod/kiln.mod.json", severity: "error", code: "KILN001", message: "'version' must be a semantic version" });
    assert.equal(get("kiln-sdk", f), undefined);
    const p = "error KILN010: KilnDeploy needs KilnServer or KilnModsDir [/home/u/MyMod/MyMod.csproj]";
    assert.deepEqual(get("kiln-sdk-project", p), { file: "/home/u/MyMod/MyMod.csproj", severity: "error", code: "KILN010", message: "KilnDeploy needs KilnServer or KilnModsDir" });
    const p2 = "  MSBUILD : error KILN004: unknown Kiln service 'Weathr' [/home/u/MyMod/MyMod.csproj::TargetFramework=net10.0]";
    assert.equal(get("kiln-sdk-project", p2)?.file, "/home/u/MyMod/MyMod.csproj");
  });
  test("C# errors go to kiln-dotnet", () => {
    const l = "/home/u/MyMod/MyModMod.cs(3,1): error CS1002: ; expected [/home/u/MyMod/MyMod.csproj]";
    assert.equal(get("kiln-dotnet", l)?.code, "CS1002");
    assert.equal(get("kiln-sdk", l), undefined);
  });
  test("kiln lint text and the impact assembler", () => {
    assert.deepEqual(get("kiln-lint", "mods/x/kiln.mod.json:8:4: warning KL104: unknown key 'dependecies'"), {
      file: "mods/x/kiln.mod.json", line: "8", column: "4", severity: "warning", code: "KL104", message: "unknown key 'dependecies'",
    });
    assert.equal(get("kiln-lint", "kiln lint: 1 error(s), 1 warning(s), 0 info(s) in 1 file(s)"), undefined);
    assert.deepEqual(get("kiln-impact", "impact: heal_over_time.kimp:17: getBuffTarget takes 1 argument(s), 2 given"), {
      file: "heal_over_time.kimp", line: "17", message: "getBuffTarget takes 1 argument(s), 2 given",
    });
  });
});
