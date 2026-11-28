#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Tests of tools/docs-site/snippets.py (the docs' C# snippets, compiled by managed/tests/Kiln.DocSnippets.Tests).

    python3 -I tools/docs-site/test_snippets.py        (exit 0 = every test passed)
"""
import io
import json
import shutil
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import snippets  # noqa: E402

PAGE = """# A page

```csharp
var r = await Pets.Summon(who, pet, PetRequest.New());
if (!r.Ok) Log.Info(r.Reason);
```

```json
{ "not": "csharp" }
```

1. In a list:

   ```cs
   public sealed class M : Mod
   {
   }
   ```

~~~csharp
```not a fence end
~~~

```csharp
Signature(int a);
```
"""


class Tree:
    def __init__(self, manifest, page=PAGE):
        self.root = Path(tempfile.mkdtemp(prefix="snippets-test-"))
        (self.root / "docs").mkdir()
        (self.root / "docs" / "page.md").write_text(page)
        (self.root / "tools" / "docs-site").mkdir(parents=True)
        (self.root / snippets.MANIFEST).write_text(json.dumps(manifest))

    def run(self, check):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            rc = snippets.run(self.root, check)
        return rc, out.getvalue() + err.getvalue()

    def generated(self):
        return {p.name: p.read_text() for p in (self.root / snippets.OUT).glob("*")}

    def close(self):
        shutil.rmtree(self.root, ignore_errors=True)


GOOD = {"pages": {"docs/page.md": {"cover": "all", "fences": {
    "1": {"wrap": "method", "stubs": ["PlayerRef who", "PetId pet", "bool Ready(PetId p) => true", "void Feed(EntityId e) { }"]},
    "2": {"wrap": "file"},
    "3": {"wrap": "test", "before": ["using var h = new KilnHarness();"]},
    "4": {"skip": "a signature listing"}}}}}


class Fences(unittest.TestCase):
    def test_only_csharp_fences_count_and_an_indented_one_is_dedented(self):
        f = snippets.cs_fences(PAGE)
        self.assertEqual([s for s, _ in f], [4, 15, 21, 25])
        self.assertEqual(snippets.dedent(f[1][1])[0], "public sealed class M : Mod")
        self.assertEqual(f[2][1], ["```not a fence end"])     # a ~~~ fence ends only at ~~~

    def test_a_using_line_is_hoisted_and_its_line_kept(self):
        o = "\n".join(snippets.emit("docs/page.md", 1, {"wrap": "method"}, 10, ["using Kiln.Registry;", "var x = 1;", "using var h = H();"]))
        self.assertIn("    using Kiln.Registry;\n", o)
        self.assertIn('#line 10 "docs/page.md"\n\nvar x = 1;\nusing var h = H();\n#line default', o)


class Generate(unittest.TestCase):
    def test_wraps_stubs_and_lines(self):
        t = Tree(GOOD)
        try:
            rc, out = t.run(False)
            self.assertEqual(rc, 0, out)
            g = t.generated()
            self.assertEqual(list(g), ["docs_page.snippets.cs"])      # not *.g.cs: the analyzers skip generated code
            text = g["docs_page.snippets.cs"]
            self.assertNotIn("auto-generated", text)
            self.assertIn("namespace DocSnippets.docs_page_1", text)
            self.assertIn("public static PlayerRef who => default!;", text)
            self.assertIn("public static bool Ready(PetId p) => true;", text)
            self.assertIn("public static void Feed(EntityId e) { }", text)
            self.assertIn("public async Task Run()", text)
            self.assertIn('#line 4 "docs/page.md"\nvar r = await Pets.Summon', text)
            self.assertIn('#line 15 "docs/page.md"\npublic sealed class M : Mod', text)
            self.assertIn("using Kiln.TestKit;", text)
            self.assertIn("using var h = new KilnHarness();", text)
            self.assertNotIn("Signature(int a)", text)                  # skipped
            rc, out = t.run(True)
            self.assertEqual(rc, 0, out)
            self.assertIn("current", out)
        finally:
            t.close()

    def test_check_finds_a_stale_and_an_extra_file(self):
        t = Tree(GOOD)
        try:
            t.run(False)
            out = t.root / snippets.OUT
            (out / "docs_page.snippets.cs").write_text("// edited by hand\n")
            (out / "gone.snippets.cs").write_text("// a page no longer listed\n")
            rc, text = t.run(True)
            self.assertEqual(rc, 1)
            self.assertIn("docs_page.snippets.cs", text)
            self.assertIn("gone.snippets.cs", text)
            rc, _ = t.run(False)
            self.assertEqual(rc, 0)
            self.assertFalse((out / "gone.snippets.cs").exists())
        finally:
            t.close()

    def test_a_covered_page_needs_every_fence(self):
        m = json.loads(json.dumps(GOOD))
        del m["pages"]["docs/page.md"]["fences"]["3"]
        t = Tree(m)
        try:
            rc, out = t.run(True)
            self.assertEqual(rc, 1)
            self.assertIn("docs/page.md:20: C# fence 3 has no entry", out)
            m["pages"]["docs/page.md"]["cover"] = "listed"
            (t.root / snippets.MANIFEST).write_text(json.dumps(m))
            t.run(False)
            self.assertEqual(t.run(True)[0], 0)
        finally:
            t.close()

    def test_manifest_mistakes(self):
        for change, word in (
                (lambda f: f.update({"9": {"wrap": "method"}}), "fence 9 does not exist"),
                (lambda f: f.update({"4": {"skip": " "}}), "a skip says why"),
                (lambda f: f.update({"2": {"wrap": "class"}}), "wrap is one of"),
                (lambda f: f.update({"2": {"wrap": "file", "nowarn": ["KILN121"]}}), "a nowarn reads")):
            m = json.loads(json.dumps(GOOD))
            change(m["pages"]["docs/page.md"]["fences"])
            t = Tree(m)
            try:
                rc, out = t.run(True)
                self.assertEqual(rc, 1, word)
                self.assertIn(word, out)
            finally:
                t.close()
        m = json.loads(json.dumps(GOOD))
        m["pages"]["docs/missing.md"] = {"fences": {}}
        t = Tree(m)
        try:
            self.assertIn("no such page", t.run(True)[1])
        finally:
            t.close()

    def test_nowarn_wraps_the_fence_in_a_pragma(self):
        o = "\n".join(snippets.emit("docs/page.md", 2, {"wrap": "file", "nowarn": ["KILN121: the page shows the warning"]}, 3, ["class A {}"]))
        self.assertIn("#pragma warning disable KILN121\n#line 3", o)
        self.assertIn("#line default\n#pragma warning restore KILN121", o)


class Repository(unittest.TestCase):
    def test_the_repository_is_current(self):
        """snippets.py --check on this tree: every C# fence of a covered page has an entry and Generated/ is fresh."""
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            rc = snippets.main(["--check", "--root", str(HERE.parent.parent)])
        self.assertEqual(rc, 0, err.getvalue())


if __name__ == "__main__":
    unittest.main()
