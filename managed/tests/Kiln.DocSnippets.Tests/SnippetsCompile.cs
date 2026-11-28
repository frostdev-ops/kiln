// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.DocSnippetsTests;

/// <summary>The check is the build: Generated/*.snippets.cs holds the docs' snippets (tools/docs-site/snippets.py). This test only
/// says that the assembly that holds them built, and how many there are.</summary>
public sealed class SnippetsCompile
{
    [Fact]
    public void The_docs_snippets_compiled() =>
        Assert.Contains(typeof(SnippetsCompile).Assembly.GetTypes(), t => t.Namespace?.StartsWith("DocSnippets.", StringComparison.Ordinal) == true);
}
