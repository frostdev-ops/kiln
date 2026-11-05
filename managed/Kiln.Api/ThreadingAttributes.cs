// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>Call this on the tick thread only. The KILN101 analyzer warns about other threads.</summary>
[AttributeUsage(AttributeTargets.All)]
public sealed class TickThreadOnlyAttribute : Attribute;

/// <summary>An advanced API: the mod's manifest needs <c>advanced: true</c> (analyzer rule).</summary>
[AttributeUsage(AttributeTargets.All)]
public sealed class AdvancedApiAttribute : Attribute;
