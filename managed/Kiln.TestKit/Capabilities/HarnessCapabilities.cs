// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;

namespace Kiln.TestKit;

/// <summary>One capability row as a test sees it.</summary>
/// <param name="Name">The row's name: <c>kiln.&lt;service&gt;</c> for a Kiln service, a game feature's name, or a mod's own.</param>
/// <param name="Available">Whether it is available.</param>
/// <param name="ReasonText">Why not (or, when available, where it comes from).</param>
public readonly record struct CapabilityRow(string Name, bool Available, string ReasonText);

/// <summary>
/// The capability rows mods see offline (<see cref="KilnHarness.Capabilities"/>): every Kiln service the kit has no fake
/// for is unavailable with reason <c>offline</c>, so a mod's own <c>Available</c> checks take the path they take on a game
/// build without it; a game feature has no row (unavailable) unless the test overrides it. Overrides end with the harness.
/// </summary>
public sealed class HarnessCapabilities
{
    /// <summary>Owner of the test's overrides in the registry.</summary>
    internal const string TestOwner = "test";

    /// <summary>Owner of the rows that hold while a harness runs (<c>tick</c>).</summary>
    internal const string HarnessOwner = "testkit-harness";

    private static readonly Regex NamePattern = new("^[A-Za-z0-9._/-]{1,96}$", RegexOptions.CultureInvariant);
    private readonly HashSet<string> _overridden = new(StringComparer.Ordinal);

    internal HarnessCapabilities() { }

    private static ICapabilityRegistry Registry => KitBackend.Instance.Capabilities;

    /// <summary>Whether <paramref name="name"/> is available (what <see cref="Game.HasFeature"/> answers).</summary>
    public bool Is(string name) => Registry.Is(name);

    /// <summary>One sentence saying whether and why not, as a mod could log it.</summary>
    public string Why(string name) => Registry.Why(name);

    /// <summary>Every row, sorted by name.</summary>
    public IReadOnlyList<CapabilityRow> All => Registry.All.Select(r => new CapabilityRow(r.Name, r.IsAvailable, r.ReasonText)).ToArray();

    /// <summary>
    /// Switches a row on or off for this harness. Off works for anything: a game feature, a mod's row, or a Kiln service
    /// the kit fakes (the fake reads its row and answers as the service does when it is off). On works for a game feature
    /// (only <see cref="Game.HasFeature"/> and the resolver's check change; nothing behind the feature is faked) and for a
    /// faked service; switching on a Kiln service (<c>kiln.*</c>) the kit has no fake for throws, since a mod would then
    /// find it on and fail inside it.
    /// </summary>
    /// <exception cref="InvalidOperationException">On for an unfaked service, or for a row another report holds off.</exception>
    public void Override(string name, bool available, string? reasonText = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!NamePattern.IsMatch(name)) throw new ArgumentException($"capability name '{name}' must be 1 to 96 characters of [A-Za-z0-9._/-]", nameof(name));
        if (available)
        {
            if (name.StartsWith("kiln.", StringComparison.Ordinal) && !KitBackend.Instance.HasTable(name))
                throw new InvalidOperationException($"{name} cannot be switched on: the test kit has no fake of this service, so its table stays absent and a mod " +
                                                    "that saw it on would fail inside it. Unfaked services stay unavailable (reason offline).");
            var without = Registry.GetWithout(TestOwner, name);
            if (without.Reason != CapReason.Unknown && !without.IsAvailable)
                throw new InvalidOperationException($"{name} cannot be switched on: {OfflineCapabilities.Explain(without)} The worst report wins.");
            Registry.Report(TestOwner, name, CapState.Available, CapReason.None, reasonText ?? "switched on by the test", TestOwner);
        }
        else Registry.Report(TestOwner, name, CapState.Unavailable, CapReason.Policy, reasonText ?? "switched off by the test", TestOwner);
        lock (_overridden) _overridden.Add(name);
    }

    /// <summary>Drops the test's override of <paramref name="name"/>.</summary>
    public void ClearOverride(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Registry.Withdraw(TestOwner, name);
        lock (_overridden) _overridden.Remove(name);
    }

    internal void Clear()
    {
        string[] names;
        lock (_overridden)
        {
            names = _overridden.ToArray();
            _overridden.Clear();
        }
        foreach (var n in names) Registry.Withdraw(TestOwner, n);
    }
}
