// SPDX-License-Identifier: MIT
#pragma warning disable KILNX031
using Kiln;
using Kiln.Registry;
using DataRegistry = Kiln.Registry.Registry;

namespace TestMods;

public sealed class TkRegistryMod : Mod
{
    private ItemEntry? _retained;
    public override void OnRegistry(RegistryContext context)
    {
        var registry = DataRegistry.For(context);
        registry.Add(view => view.Items.AddLike(new("tk-registry:logs"), new("game:wood"), item => item.MaxStack = 17));
        registry.Alter(view => view.Loot.Remove("game:wood"));
        _retained = registry.Items["game:wood"];
        _retained.MaxStack = context.OffTick ? 1 : _retained.MaxStack + 5;
        // Final: every seeded game:bulk_* item, one edit each (the cap test), after a tick-thread call (the off-tick test)
        registry.Final(view =>
        {
            Game.AssertTickThread();
            foreach (var item in view.Items.Select("game:bulk_*")) item.MaxStack = 2;
            view.Items["game:wood"].MaxStack = 3;
        });
    }
    public override void OnUnload()
    {
        if (_retained is null) return;
        try { _ = _retained.MaxStack; throw new InvalidOperationException("retained cell remained usable"); }
        catch (RegistryContextException e) when (e.Code is RegistryContextError.Closed or RegistryContextError.Unloaded) { }
        _retained = null;
    }
}
