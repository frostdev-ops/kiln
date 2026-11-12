// SPDX-License-Identifier: MIT
using Kiln;

namespace TkConfig;

public sealed class CommonSettings
{
    [Comment("How many.")] public int Count { get; set; } = 3;
}

public sealed class ServerSettings
{
    public bool Pvp { get; set; }
}

public sealed class ClientSettings
{
    public string Nickname { get; set; } = "ada";
}

/// <summary>One config of each global kind; logs every change of the common one, and its value on a "values" message.</summary>
public sealed class TkConfigMod : Mod
{
    private ModConfig<CommonSettings>? _common;

    public override void OnLoad()
    {
        _common = Config.Register<CommonSettings>();
        Config.Register<ServerSettings>(ConfigKind.Server);
        Config.Register<ClientSettings>(ConfigKind.Client);
        _common.Changed += e => Log.Info($"changed: {string.Join(",", e.Changed)} count {_common.Value.Count} source {e.Source}");
        Context.Messages.On("values", _ => Log.Info($"values: count {_common.Value.Count}"));
        Log.Info($"config: count {_common.Value.Count}");
    }
}
