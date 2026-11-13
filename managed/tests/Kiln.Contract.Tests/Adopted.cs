// SPDX-License-Identifier: MIT
using System.Reflection;
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>The managed services that follow the dev.26 contract and are held to <see cref="ConformanceTests"/>.
/// A service is added here in the commit that adopts it.</summary>
internal static class Adopted
{
    /// <summary>One public type per adopted service assembly.</summary>
    internal static readonly Type[] Anchors =
    [
        typeof(Spawn),
    ];

    internal static IEnumerable<Assembly> Assemblies => Anchors.Select(t => t.Assembly).Distinct();

    public static TheoryDataRow<string>[] Names() =>
        Assemblies.Select(a => new TheoryDataRow<string>(a.GetName().Name!)).ToArray();

    internal static Assembly Named(string name) => Assemblies.Single(a => a.GetName().Name == name);
}
