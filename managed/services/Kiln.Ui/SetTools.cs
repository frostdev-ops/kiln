// SPDX-License-Identifier: MIT
// Private bridge to kiln-set's own tools. The page and Settings.define values stay in the native plugin;
// no config files are read or written on the render thread by the managed mod manager.
using System.Runtime.InteropServices;
namespace Kiln;

internal static unsafe class SetTools
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<NativeStr, int> Open;
    }

    internal static int Open(Table* table, string mod) =>
        table is null || table->Header.Size < sizeof(Table) || table->Header.Version < 1 || table->Open == null
            ? -1 : table->Open(Utf8.Of(mod));

    internal static void Draw(UiDraw ui, CatalogMod mod)
    {
        ui.Separator();
        ui.TextWrapped("SET Mod Settings, Compatibility, Entities, Hooks, Bindings, Console and local support report.");
        var table = Services.Get<Table>("kiln.set.ui", 1);
        if (table is null) ui.TextDisabled("SET tools are unavailable (kiln-set is not running).");
        else if (ui.Button("SET settings and tools"))
        {
            if (Open(table, Path.GetFileName(mod.Folder ?? mod.Id)) != 0)
                Hud.Toast("SET tools are unavailable; check the engine status.", ToastKind.Warning);
        }
    }
}
