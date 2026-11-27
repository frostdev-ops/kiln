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
    /// <summary>The text; several lines separated by <c>\n</c>.</summary>
    public string Text { get; } = text;
}

/// <summary>Allowed range of a number (config entry, command argument). Config values outside are clamped with a
/// warning; command arguments outside are refused. For <see cref="TimeSpan"/> the bounds are seconds. A side marked
/// soft (<see cref="SoftMin"/>, <see cref="SoftMax"/>) only ends the config slider: values past it are allowed.</summary>
/// <example>A cost multiplier whose slider runs from 0 to 1 in steps of 0.1, where a player may still type 2 or 3 (or
/// write it in the file); below 0 stays refused:
/// <code>
/// [Range(0, 1, 0.1, SoftMax = true)] public double CostMultiplier { get; set; } = 1;
/// </code></example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class RangeAttribute(double min, double max) : Attribute
{
    /// <summary>A range whose config slider moves in steps of <paramref name="step"/> from <paramref name="min"/>
    /// (<c>min</c>, <c>min + step</c>, <c>min + 2 * step</c>, ... up to <c>max</c>). The step is editor metadata only:
    /// typed values, values in the file, server values and command arguments keep the plain bounds check, and a saved
    /// value off the grid is never rounded. A step that is not a positive finite number, is larger than
    /// <c>max - min</c>, or is not whole for a whole-number entry is ignored (the slider moves as without one).</summary>
    public RangeAttribute(double min, double max, double step) : this(min, max) => Step = step;

    /// <summary>The lowest allowed value (for a <see cref="TimeSpan"/>, in seconds).</summary>
    public double Min { get; } = min;
    /// <summary>The highest allowed value (for a <see cref="TimeSpan"/>, in seconds).</summary>
    public double Max { get; } = max;
    /// <summary>The slider step given to the three-argument constructor, as given; null for <c>[Range(min, max)]</c>.
    /// Config entries carry it on as <see cref="ConfigEntry.Step"/> only when it suits the entry.</summary>
    public double? Step { get; }

    /// <summary>The low end is soft (mod API 0.2.26): the config slider stops at <see cref="Min"/>, but a value below
    /// it, typed into the number field, read from the file, set with <see cref="ConfigFile.TrySet"/> or sent by a
    /// server, is kept as it is (no refusal, no clamp, no warning). Only the number's own type still bounds it (an
    /// <c>int</c> entry stays an int). A command argument has no low bound then. Default false: the bound is
    /// enforced.</summary>
    public bool SoftMin { get; set; }

    /// <summary>The high end is soft (mod API 0.2.26): the config slider stops at <see cref="Max"/>, but a value above
    /// it is kept as it is, wherever it comes from; see <see cref="SoftMin"/>. <c>[Range(0, 1, 0.1, SoftMax = true)]</c>:
    /// a slider from 0 to 1 in steps of 0.1, a typed 2 or 3 allowed, a typed -1 still refused.</summary>
    public bool SoftMax { get; set; }
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
    /// <summary>The JSON key.</summary>
    public string Key { get; } = key;
}
