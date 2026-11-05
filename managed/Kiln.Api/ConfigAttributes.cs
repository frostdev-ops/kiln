// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>Where a config applies (Forge's <c>ModConfig.Type</c>). The kind picks the file name:
/// Common <c>&lt;configDir&gt;/&lt;modid&gt;.json</c> (configDir: kiln/config or the profile's), Server <c>&lt;modid&gt;-server.json</c>, Client
/// <c>&lt;modid&gt;-client.json</c>.</summary>
public enum ConfigKind
{
    /// <summary>Read by the mod wherever it runs.</summary>
    Common = 0,
    /// <summary>Server rules; entries marked <see cref="SyncedAttribute"/> are meant to be sent to clients.</summary>
    Server = 1,
    /// <summary>A player's own preferences (written on a server too, unused there).</summary>
    Client = 2,
}

/// <summary>Text written above a config entry or section (several lines: use \n). Also the description of a
/// command argument.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Class | AttributeTargets.Parameter)]
public sealed class CommentAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}

/// <summary>Allowed range of a number (config entry, command argument). Config values outside are clamped with a
/// warning; command arguments outside are refused. For <see cref="TimeSpan"/> the bounds are seconds.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class RangeAttribute(double min, double max) : Attribute
{
    public double Min { get; } = min;
    public double Max { get; } = max;
}

/// <summary>A change of this entry only applies after the mod reloads (or the server restarts): a live edit is
/// written to the file and reported as pending, and the running value stays.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class RequiresRestartAttribute : Attribute;

/// <summary>Marks an entry (or a whole section) of a server or common config that clients must know (Forge: a server
/// config synced at login). Kiln.Network's config sync sends these values to Kiln clients when they join and whenever
/// they change; on the client they replace the mod's local values while connected (read-only there) and the local
/// values come back on leave (<see cref="ConfigFile.ServerValues"/>, <see cref="ConfigChangeSource.Server"/>).
/// <see cref="ConfigFile.SyncedValuesJson"/> is what is sent. Entries of client configs are never synced.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Class)]
public sealed class SyncedAttribute : Attribute;

/// <summary>Not part of the config file.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ConfigIgnoreAttribute : Attribute;

/// <summary>The JSON key of an entry (default: the member name in camelCase).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class ConfigKeyAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}
