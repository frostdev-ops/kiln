// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

/// <summary>One line Kiln wrote to its log while a harness ran: a mod's <see cref="Logger"/>, Kiln's own code, or the
/// host (the loader and the lifecycle).</summary>
/// <param name="Sequence">Order of writing, from 1.</param>
/// <param name="Step">The harness's step when it was written (0 before the first).</param>
/// <param name="Level">The level.</param>
/// <param name="Source"><c>mod:&lt;id&gt;</c>, <c>kiln</c> (Kiln.Api and the services) or <c>host</c> (Kiln.Host).</param>
/// <param name="Text">The text.</param>
public sealed record LogLine(long Sequence, ulong Step, LogLevel Level, string Source, string Text)
{
    /// <summary>The mod that wrote it, or null for Kiln and the host.</summary>
    public string? ModId => Source.StartsWith("mod:", StringComparison.Ordinal) ? Source[4..] : null;

    /// <summary>"step LEVEL [source] text".</summary>
    public override string ToString() => $"{Step,6} {Level.ToString().ToUpperInvariant(),-5} [{Source}] {Text}";
}
