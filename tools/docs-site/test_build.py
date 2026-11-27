#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Build orchestration regressions, with a stub DocFX and real documentation scripts.

  python3 -I tools/docs-site/test_build.py
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

HERE = Path(__file__).resolve().parent


class Build(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.here = self.root / "repo/tools/docs-site"
        self.here.mkdir(parents=True)
        for name in ("build.sh", "docfx.json", "index.md", "toc.yml", "filterConfig.yml", "annotate.py", "apiids.py", "docxml.py",
                     "samples.py", "services.json", "services.py", "since.py", "undocumented.py", "undocumented-baseline.txt",
                     "stage-guide.py", "check-links.py", "theme.py", "search_index.py", "wiki.json"):
            shutil.copy2(HERE / name, self.here / name)
        shutil.copytree(HERE / "template", self.here / "template")
        # since.py reads the release tooling's ordering of releases; the ledger and Mods.cs are what annotate.py --repo reads
        (self.root / "repo/tools/release/lib").mkdir(parents=True)
        shutil.copy2(HERE.parent / "release/lib/apihistory.py", self.root / "repo/tools/release/lib/apihistory.py")
        (self.root / "repo/managed/Kiln.Api").mkdir(parents=True)
        (self.root / "repo/managed/Kiln.Api/Mods.cs").write_text('public const string ApiVersion = "0.2.15";\n')
        (self.root / "repo/managed/api-history.tsv").write_text("kind\tproject\tentry\trelease\tmod_api\n"
                                                                 "managed\tKiln.Api\tKiln.Mock\t0.2.0-dev.30\t0.2.12\n")
        shutil.copytree(HERE / "apidoc", self.here / "apidoc")
        guide = self.root / "repo/docs/kiln/modding"
        (guide / "schemas").mkdir(parents=True)
        (guide / "README.md").write_text(
            "[Caps](../services/caps.md) [Fields](schemas/) [Local](local.md)\n"
            "[Reference][caps]\n[caps]: ../services/caps.md#states\n"
            "`[example](../services/caps.md)`\n```md\n[example](../services/caps.md)\n```\n")
        (guide / "local.md").write_text("# Local\n")
        (guide / "schemas/example.md").write_text("# Example\n")
        services = self.root / "repo/docs/kiln/services"
        services.mkdir()
        (services / "caps.md").write_text("# Caps\n\n## States\n")
        subprocess.run(["git", "init", "-q", str(self.root / "repo")], check=True)
        subprocess.run(["git", "-C", str(self.root / "repo"), "add", "docs"], check=True)
        (guide / "private.md").write_text("# Never ship this untracked page\n")
        for name in ("building-blocks-plan.md", "world-blocks-plan.md", "phase2-status.md"):
            (guide.parent / name).write_text("# Private\n")
        self.out = self.root / "out"
        (self.out / "mdk-stage").mkdir(parents=True)
        (self.out / "mdk-stage/Kiln.Api.xml").write_text(
            "<doc><assembly><name>Kiln.Api</name></assembly><members/></doc>")
        tool = self.out / "tools/docfx/.store/mock"
        (tool / "templates").mkdir(parents=True)
        (tool / "docfx.dll").touch()
        (tool / "docfx.runtimeconfig.json").write_text('{"runtimeOptions":{"configProperties":{}}}')
        (self.out / "tools/docfx/docfx").touch(mode=0o755)
        sdk = self.root / "sdk/8.0.0"
        sdk.mkdir(parents=True)
        (sdk / "Microsoft.Extensions.Mock.dll").touch()
        bin_dir = self.root / "bin"
        bin_dir.mkdir()
        dotnet = bin_dir / "dotnet"
        dotnet.write_text('''#!/usr/bin/env python3
import json, os, pathlib, sys
if sys.argv[1] == "--list-sdks":
    print("8.0.0 [" + os.environ["MOCK_SDK"] + "]")
elif sys.argv[1] == "exec":
    action = sys.argv[3]
    if action == "metadata":
        config = json.loads(pathlib.Path(sys.argv[4]).read_text())
        dest = pathlib.Path(config["metadata"][0]["dest"])
        dest.mkdir(parents=True, exist_ok=True)
        typed = os.environ["MOCK_METADATA"] == "typed"
        if dest.name == "api" or (dest.name == "api-raw" and (typed or os.environ.get("MOCK_RAW"))):
            text = "items:\\n- uid: Kiln.Mock\\n  type: Class\\n"
            if typed:
                text += "  commentId: T:Kiln.Mock\\n  name: Mock\\n  namespace: Kiln\\n  assemblies:\\n  - Kiln.Api\\n  summary: A summary.\\n"
            if os.environ["MOCK_METADATA"] == "crash":
                text = 'items:\\n- uid: "bad\\\\q"\\n  type: Class\\n'
            elif os.environ["MOCK_METADATA"] == "documented":
                text += "  summary: A summary.\\n"
            (dest / "Kiln.Mock.yml").write_text(text)
    elif action == "build":
        import re
        site = pathlib.Path(sys.argv[sys.argv.index("--output") + 1])
        site.mkdir(parents=True)
        (site / "index.html").write_text("built")
        (site / "xrefmap.yml").touch()
        (site / "index.json").write_text("{}")
        (site / "api").mkdir()
        for page in [] if os.environ.get("MOCK_NO_PAGES") else pathlib.Path("api").glob("*.yml"):
            ids = [re.sub(r"[^A-Za-z0-9_]", "_", u.strip()) for u in re.findall(r"^- uid: (.+)$", page.read_text(), re.M)]
            (site / "api" / (page.stem + ".html")).write_text("".join(f'<h1 id="{i}"></h1>' for i in ids))
    else:
        sys.exit("unexpected docfx action")
else:
    sys.exit("unexpected dotnet command")
''')
        dotnet.chmod(0o755)
        self.env = {**os.environ, "PATH": str(bin_dir) + os.pathsep + os.environ["PATH"],
                    "KILN_OUT": str(self.out), "MOCK_SDK": str(sdk.parent)}
        self.env.pop("KILN_DOCS_BASELINE", None)
        self.env.pop("KILN_DOCS_TRACKED", None)

    def build(self, metadata, strict=False, args=(), **extra):
        """strict: False -> KILN_DOCS_STRICT=0, True -> 1, None -> not set (the default is strict)."""
        env = {**self.env, "MOCK_METADATA": metadata, **extra}
        if strict is not None:
            env["KILN_DOCS_STRICT"] = "1" if strict else "0"
        else:
            env.pop("KILN_DOCS_STRICT", None)
        return subprocess.run(["bash", str(self.here / "build.sh"), *args], env=env, text=True, capture_output=True)

    def test_the_reference_gets_a_table_of_contents_by_assembly_and_an_overview(self):
        result = self.build("documented")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("annotate: 0 members with a thread/side line, 0 with a capability, 0 with a since line", result.stdout)
        self.assertIn("services: 0 assemblies", result.stdout)
        api = self.out / "docs-work/api"
        self.assertTrue((api / "toc.yml").read_text().startswith("### YamlMime:TableOfContent\n- name: Overview\n  href: index.md\n"))
        self.assertIn("# Kiln API reference", (api / "index.md").read_text())
        # the pages and the table of contents are part of what docfx builds
        self.assertIn("api/services/*.md", (self.out / "docs-work/docfx.json").read_text())
        self.assertIn("api/toc/*.json", (self.out / "docs-work/docfx.json").read_text())

    def test_the_site_has_the_template_the_navigation_and_its_own_search(self):
        (self.out / "mdk-stage/Kiln.Api.xml").write_text(
            "<doc><assembly><name>Kiln.Api</name></assembly><members><member name='T:Kiln.Mock'><summary>A mock.</summary></member></members></doc>")
        result = self.build("typed", KILN_DOCS_RELEASE="0.2.0-dev.31")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        config = json.loads((self.out / "docs-work/docfx.json").read_text())["build"]
        self.assertEqual(config["template"], ["default", "modern", "template"])
        meta = config["globalMetadata"]
        self.assertEqual(meta["_kilnRelease"], "0.2.0-dev.31")
        self.assertRegex(meta["_kilnAssets"]["css"], r"^static/kiln-docs\.[0-9a-f]{10}\.css$")
        self.assertTrue((self.out / "docs-work/template" / meta["_kilnAssets"]["js"]).is_file())
        self.assertTrue((self.out / "docs-work/template/layout/_master.tmpl").is_file())
        nav = json.loads((self.out / "docs-work/template" / meta["_kilnAssets"]["nav"]).read_text())
        self.assertEqual(nav, {"v": 1, "groups": [["Kiln.Api", "api/services/Kiln.Api.html", 1]],
                               "guides": [["README", "modding/README.html"], ["Local", "modding/local.html"]],
                               "schemas": [["Example", "modding/schemas/example.html"]]})
        self.assertIn("theme: 7 static files", result.stdout)
        site = self.out / "docs-site"
        self.assertFalse((site / "index.json").exists())           # DocFX's 10 MB index of every page's text
        pages = json.loads((site / "search/pages.json").read_text())
        self.assertEqual(pages["t"], [["Mock", "Kiln.Mock.html", "c", 0, "A mock.", 0]])
        self.assertIn("search: 1 types, 0 members", result.stdout)

    def test_a_search_entry_without_its_page_stops_the_build(self):
        (self.out / "mdk-stage/Kiln.Api.xml").write_text(
            "<doc><assembly><name>Kiln.Api</name></assembly><members><member name='T:Kiln.Mock'><summary>A mock.</summary></member></members></doc>")
        result = self.build("typed", MOCK_NO_PAGES="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("search: 1 entries point at no page or anchor of the site", result.stderr)

    def test_a_typed_member_gets_its_since_line_and_a_service_page(self):
        (self.out / "mdk-stage/Kiln.Api.xml").write_text(
            "<doc><assembly><name>Kiln.Api</name></assembly><members>"
            "<member name='T:Kiln.Mock'><summary>A mock.</summary></member></members></doc>")
        result = self.build("typed")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("1 with a since line", result.stdout)
        doc = (self.out / "docs-work/stage/Kiln.Api.xml").read_text()
        self.assertIn("<para>Since mod API 0.2.12 (0.2.0-dev.30)</para>", doc)
        # the original stage is not touched: annotate.py works on a copy
        self.assertNotIn("Since mod API", (self.out / "mdk-stage/Kiln.Api.xml").read_text())
        api = self.out / "docs-work/api"
        self.assertIn("- [Mock](../Kiln.Mock.yml) (class): A mock.", (api / "services/Kiln.Api.md").read_text())
        self.assertIn('- name: "Kiln.Api"\n  href: services/Kiln.Api.md\n', (api / "toc.yml").read_text())
        self.assertEqual(json.loads((api / "toc/Kiln.Api.json").read_text())["types"], [["Mock", "Kiln.Mock.html", "c"]])

    def test_a_build_without_the_api_history_ledger_fails_instead_of_leaving_the_since_lines_out(self):
        (self.root / "repo/managed/api-history.tsv").unlink()
        result = self.build("documented")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("no API history ledger", result.stderr)
        self.assertFalse((self.out / "docs-site/index.html").exists())

    def test_metadata_parser_crash_fails_build_even_without_strict(self):
        for strict in (False, True):
            with self.subTest(strict=strict):
                result = self.build("crash", strict)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("JSONDecodeError", result.stderr)
                self.assertNotIn("undocumented: none", result.stdout)
                self.assertFalse((self.out / "docs-site/index.html").exists())

    def test_new_undocumented_members_warn_without_strict(self):
        result = self.build("missing")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Kiln.Mock", result.stderr)
        self.assertTrue((self.out / "docs-site/index.html").is_file())

    def test_new_undocumented_members_fail_with_strict(self):
        result = self.build("missing", strict=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("KILN_DOCS_STRICT=1: failing on them", result.stderr)
        self.assertFalse((self.out / "docs-site/index.html").exists())

    def test_the_gate_is_strict_unless_it_is_switched_off(self):
        result = self.build("missing", strict=None)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Kiln.Mock", result.stderr)
        self.assertIn("KILN_DOCS_STRICT=1: failing on them", result.stderr)
        self.assertFalse((self.out / "docs-site/index.html").exists())

    def test_the_baseline_in_the_repository_names_nothing(self):
        names = [l for l in (HERE / "undocumented-baseline.txt").read_text().splitlines() if l.strip() and not l.startswith("#")]
        self.assertEqual(names, [])

    def test_gate_only_checks_the_first_pass_and_builds_no_site(self):
        failed = self.build("missing", strict=None, args=["--gate-only"], MOCK_RAW="1")
        self.assertEqual(failed.returncode, 1, failed.stdout + failed.stderr)
        self.assertIn("Kiln.Mock", failed.stderr)
        passed = self.build("documented", strict=None, args=["--gate-only"], MOCK_RAW="1")
        self.assertEqual(passed.returncode, 0, passed.stdout + passed.stderr)
        self.assertIn("undocumented: none", passed.stdout)
        self.assertFalse((self.out / "docs-site").exists())
        self.assertFalse((self.out / "docs-work").exists())         # the release build's folder is left alone
        self.assertTrue((self.out / "docs-gate-work/api-raw/Kiln.Mock.yml").is_file())

    def test_gate_only_takes_the_stage_it_is_given_and_needs_its_xml(self):
        empty = self.root / "empty-stage"
        empty.mkdir()
        result = self.build("documented", args=["--gate-only"], MOCK_RAW="1", KILN_DOCS_STAGE=str(empty))
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("no Kiln.Api.xml", result.stderr)

    def test_documented_members_pass_with_strict(self):
        result = self.build("documented", strict=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("undocumented: none", result.stdout)

    def test_site_links_to_unshipped_pages_use_the_public_repository(self):
        result = self.build("documented")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        page = (self.out / "docs-work/modding/README.md").read_text()
        self.assertIn("[Caps](https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/services/caps.md)", page)
        self.assertIn("[Fields](https://github.com/frostdev-ops/kiln/tree/main/docs/kiln/modding/schemas/)", page)
        self.assertIn("[caps]: https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/services/caps.md#states", page)
        self.assertIn("[Local](local.md)", page)
        self.assertIn("`[example](../services/caps.md)`", page)
        self.assertIn("```md\n[example](../services/caps.md)\n```", page)

    def test_link_to_a_private_page_fails_staging(self):
        (self.root / "repo/docs/kiln/modding/README.md").write_text("[Private](../building-blocks-plan.md)\n")
        result = self.build("documented")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("missing or private link target ../building-blocks-plan.md", result.stderr)

    def test_site_stages_only_git_tracked_pages(self):
        result = self.build("documented")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        staged = {p.name for p in (self.out / "docs-work").rglob("*.md")}
        self.assertNotIn("private.md", staged)
        for name in ("building-blocks-plan.md", "world-blocks-plan.md", "phase2-status.md"):
            self.assertNotIn(name, staged)
        self.assertIn("local.md", staged)

    def export(self):
        """The repository as release.sh exports it: no .git, and the list of its files."""
        shutil.rmtree(self.root / "repo/.git")
        listed = self.root / "src-files"
        listed.write_bytes(b"\0".join(n.encode() for n in (
            "docs/kiln/modding/README.md", "docs/kiln/modding/local.md", "docs/kiln/modding/schemas/example.md",
            "docs/kiln/services/caps.md")) + b"\0")
        return listed

    def test_an_export_stages_the_listed_pages(self):
        self.env["KILN_DOCS_TRACKED"] = str(self.export())
        result = self.build("documented")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("stage-guide: 3 git-tracked pages", result.stdout)
        staged = {p.name for p in (self.out / "docs-work").rglob("*.md")}
        self.assertIn("local.md", staged)
        self.assertNotIn("private.md", staged)
        page = (self.out / "docs-work/modding/README.md").read_text()
        self.assertIn("[Caps](https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/services/caps.md)", page)

    def test_an_export_without_its_list_fails_staging(self):
        self.export()
        result = self.build("documented")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("is not the top of a git checkout", result.stderr)

    def test_a_folder_git_ignores_inside_another_repository_fails_staging(self):
        # dev.28: the release's export inside ~/Work/kiln/.out (ignored) staged 0 pages without an error
        self.export()
        outer = self.root
        (outer / ".gitignore").write_text("repo/\n")
        subprocess.run(["git", "init", "-q", str(outer)], check=True)
        result = self.build("documented")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("is not the top of a git checkout", result.stderr)

    def test_found_names_have_a_distinct_exit_code(self):
        api = self.root / "api"
        api.mkdir()
        (api / "Mock.yml").write_text("items:\n- uid: Kiln.Mock\n  type: Class\n")
        command = ["python3", "-I", str(HERE / "undocumented.py"), "--baseline",
                   str(HERE / "undocumented-baseline.txt")]
        found = subprocess.run(command + [str(api)], capture_output=True, text=True)
        self.assertEqual(found.returncode, 3, found.stderr)
        self.assertEqual(found.stdout, "Kiln.Mock\n")
        error = subprocess.run(command + [str(self.root / "absent")], capture_output=True, text=True)
        self.assertEqual(error.returncode, 1)
        self.assertIn("no api folder", error.stderr)


class GuideContent(unittest.TestCase):
    """The repository's own docfx.json against its own guide pages (no build)."""

    def test_every_guide_page_the_site_stages_is_built(self):
        # stage-guide.py stages every git-tracked page under docs/kiln/modding, subfolders too, and links between them stay
        # relative; a staged page docfx.json's build content leaves out is a dead link on the site (registries/ was: 17
        # InvalidFileLink warnings in the 0.2.0-dev.32 beta 1 build)
        repo = HERE.parent.parent
        listed = subprocess.run(["git", "-C", str(repo), "ls-files", "-z", "--", "docs/kiln/modding"], check=True,
                                capture_output=True).stdout.decode("utf-8").split("\0")
        pages = sorted(Path(p).relative_to("docs/kiln/modding").as_posix() for p in listed if p.endswith(".md"))
        self.assertIn("registries/README.md", pages)
        globs = [g for c in json.loads((HERE / "docfx.json").read_text())["build"]["content"] if c.get("src") == "modding"
                 for g in c["files"]]

        def built(page):   # docfx globs: "*" stays within one folder
            return any(len(g.split("/")) == len(page.split("/"))
                       and all(Path(part).match(pat) for part, pat in zip(page.split("/"), g.split("/"))) for g in globs)
        self.assertEqual([p for p in pages if not built(p)], [])


if __name__ == "__main__":
    unittest.main()
