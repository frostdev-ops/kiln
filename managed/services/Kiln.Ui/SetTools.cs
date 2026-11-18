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
        ui.TextWrapped(KilnText.T("set.about"));
        var table = Services.Get<Table>("kiln.set.ui", 1);
        if (table is null) ui.TextDisabled(KilnText.T("set.unavailable"));
        else if (ui.Button(KilnText.T("set.open")))
        {
            if (Open(table, Path.GetFileName(mod.Folder ?? mod.Id)) != 0)
                Hud.Toast(KilnText.T("set.open_failed"), ToastKind.Warning);
        }
    }
}
