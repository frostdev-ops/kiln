// SPDX-License-Identifier: MIT
// Lab fixture only. The driver rewrites value.txt and reloads this mod while its protocol-only client stays joined.
using Kiln;
using Kiln.Registry;

namespace RegistryMpTest;

[RegistryEntryPoints(RegistryEntryPoint.GameStart | RegistryEntryPoint.WorldLoad | RegistryEntryPoint.HotReload)]
public sealed class RegistryMpTestMod : Mod
{
    public override void OnRegistry(RegistryContext context)
    {
        var registry = Kiln.Registry.Registries.For(context);
        if (context.EntryPoint == RegistryEntryPoint.GameStart)
        {
            registry.Alter(view => view.Balance.LevelCap = 60);
            return;
        }
        var value = int.Parse(File.ReadAllText(Path.Combine(Context.Info.Directory, "value.txt")).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        if (value > 0) registry.Alter(view => view.Items["game:material_t1_wood_logs"].MaxStack = value);
        if (context.Phase == RegistryPhase.Final) Log.Info($"registry-mp-test: authored {context.EntryPoint} maxStack={value}");
    }
}
