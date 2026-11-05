// SPDX-License-Identifier: MIT
// The mod profile as Kiln.Host sees it: read once from the core's kiln.profile service (kiln_profile.h). Which managed
// mod folders load (the profile's "kiln" entries, then kiln/mods drop-ins when there is no profile or dropins
// includes "kiln"), where configs go (KilnPaths.Config = configDir) and per-mod data (dataDir ?? kiln/data).
// docs/kiln/profiles.md, schema/profile.schema.json.
namespace Kiln.Host;

/// <summary>One managed mod folder the profile lists.</summary>
internal sealed record ProfileFolder(string Id, string? Version, string Directory, bool Enabled);

internal enum ProfileMode
{
    Legacy = 0,
    Active = 1,
    Once = 2,
    NoMods = 3,
    Invalid = 4,
}

/// <summary>What the host loads from. <see cref="Legacy"/> = no profile: kiln/mods only.</summary>
internal sealed class HostProfile
{
    public ProfileMode Mode { get; init; } = ProfileMode.Legacy;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public ulong Generation { get; init; }
    public string? ConfigDirectory { get; init; }
    public string? DataDirectory { get; init; }

    /// <summary>Profile entries of kind "kiln", in the profile's order (missing folders were left out by the core).</summary>
    public IReadOnlyList<ProfileFolder> Folders { get; init; } = [];

    /// <summary>Also load kiln/mods (no profile, or dropins true / incl. "kiln").</summary>
    public bool Dropins { get; init; } = true;

    /// <summary>INVALID: why (the core logged it too).</summary>
    public string? Error { get; init; }

    public static readonly HostProfile Legacy = new();

    public string ModeName => Mode switch
    {
        ProfileMode.Active => "active",
        ProfileMode.Once => "once",
        ProfileMode.NoMods => "no-mods",
        ProfileMode.Invalid => "invalid",
        _ => "legacy",
    };

    /// <summary>No managed mod loads at all (play-once no-mods, invalid profile).</summary>
    public bool LoadsNothing => Mode is ProfileMode.NoMods or ProfileMode.Invalid;

    public override string ToString() => Mode switch
    {
        ProfileMode.Active or ProfileMode.Once => $"profile {ModeName} '{Id}' generation {Generation}: {Folders.Count} kiln mod(s){(Dropins ? " + kiln/mods" : "")}",
        ProfileMode.NoMods => "play-once no-mods: no managed mods",
        ProfileMode.Invalid => $"invalid profile ({Error}): no managed mods",
        _ => "no profile: kiln/mods",
    };

    /// <summary>Reads kiln.profile; an older core without the service: the legacy layout.</summary>
    public static unsafe HostProfile Read(KilnApi* api)
    {
        var table = (KilnProfileApi*)NativeCalls.WithUtf8(KilnProfileApi.Name_, s => (nint)api->Service(s, KilnProfileApi.Version1));
        if (table == null || table->Size < (uint)sizeof(KilnProfileApi)) return Legacy;
        var mode = (ProfileMode)table->Mode();
        var folders = new List<ProfileFolder>();
        var n = table->Count(KilnProfileApi.KindKiln);
        for (uint i = 0; i < n; i++)
        {
            KilnProfileMod m = default;
            m.Size = (uint)sizeof(KilnProfileMod);
            if (table->Get(KilnProfileApi.KindKiln, i, &m) != KilnAbi.Ok) continue;
            var version = m.Version.ToString();
            folders.Add(new ProfileFolder(m.Id.ToString(), version.Length > 0 ? version : null, m.Path.ToString(), m.Enabled != 0));
        }
        var config = table->ConfigDir().ToString();
        var data = table->DataDir().ToString();
        return new HostProfile
        {
            Mode = mode,
            Id = table->Id().ToString(),
            Name = table->Name().ToString(),
            Generation = table->Generation(),
            ConfigDirectory = config.Length > 0 ? config : null,
            DataDirectory = data.Length > 0 ? data : null,
            Folders = folders,
            Dropins = (table->Dropins() & (1u << (int)KilnProfileApi.KindKiln)) != 0,
            Error = mode == ProfileMode.Invalid ? table->Json().ToString() : null,
        };
    }
}
