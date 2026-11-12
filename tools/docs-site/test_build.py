#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Build orchestration regressions, with a stub DocFX and real documentation scripts.

  python3 -I tools/docs-site/test_build.py
"""
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
        for name in ("build.sh", "docfx.json", "index.md", "toc.yml", "filterConfig.yml",
                     "annotate.py", "undocumented.py", "undocumented-dev24.txt", "stage-guide.py", "check-links.py"):
            shutil.copy2(HERE / name, self.here / name)
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
        if dest.name == "api":
            text = "items:\\n- uid: Kiln.Mock\\n  type: Class\\n"
            if os.environ["MOCK_METADATA"] == "crash":
                text = 'items:\\n- uid: "bad\\\\q"\\n  type: Class\\n'
            elif os.environ["MOCK_METADATA"] == "documented":
                text += "  summary: A summary.\\n"
            (dest / "Kiln.Mock.yml").write_text(text)
    elif action == "build":
        site = pathlib.Path(sys.argv[sys.argv.index("--output") + 1])
        site.mkdir(parents=True)
        (site / "index.html").write_text("built")
        (site / "xrefmap.yml").touch()
    else:
        sys.exit("unexpected docfx action")
else:
    sys.exit("unexpected dotnet command")
''')
        dotnet.chmod(0o755)
        self.env = {**os.environ, "PATH": str(bin_dir) + os.pathsep + os.environ["PATH"],
                    "KILN_OUT": str(self.out), "MOCK_SDK": str(sdk.parent)}
        self.env.pop("KILN_DOCS_BASELINE", None)

    def build(self, metadata, strict=False):
        return subprocess.run(["bash", str(self.here / "build.sh")],
                              env={**self.env, "MOCK_METADATA": metadata,
                                   "KILN_DOCS_STRICT": "1" if strict else "0"},
                              text=True, capture_output=True)

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

    def test_found_names_have_a_distinct_exit_code(self):
        api = self.root / "api"
        api.mkdir()
        (api / "Mock.yml").write_text("items:\n- uid: Kiln.Mock\n  type: Class\n")
        command = ["python3", "-I", str(HERE / "undocumented.py"), "--baseline",
                   str(HERE / "undocumented-dev24.txt")]
        found = subprocess.run(command + [str(api)], capture_output=True, text=True)
        self.assertEqual(found.returncode, 3, found.stderr)
        self.assertEqual(found.stdout, "Kiln.Mock\n")
        error = subprocess.run(command + [str(self.root / "absent")], capture_output=True, text=True)
        self.assertEqual(error.returncode, 1)
        self.assertIn("no api folder", error.stderr)


if __name__ == "__main__":
    unittest.main()
