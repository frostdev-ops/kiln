// SPDX-License-Identifier: MIT
// Issue #44 phase 1c: private Ctrl+R history search panel over the *unchanged*
// original game chat editor. Key handling and acceptance stay on the UI thread.
namespace Kiln;

// Guard the managed renderer from a malformed or stale native snapshot.
// This is pure and exercised without Enshrouded in Kiln.Ui.Tests.
internal static class ChatSearchDisplayRules
{
    internal static bool Valid(in UiNative.ChatSearch s) =>
        s.Active != 0 && s.Count <= 10 && s.QueryLen <= 256 &&
        float.IsFinite(s.X) && float.IsFinite(s.Y) &&
        float.IsFinite(s.Width) && float.IsFinite(s.Height) &&
        s.Width > 0 && s.Height > 0 && s.First <= s.Total &&
        s.Count <= s.Total - s.First &&
        (s.Total == 0 || (s.Selected < s.Total && s.Selected >= s.First &&
                          s.Selected < s.First + s.Count));
}

internal static unsafe class ChatSearchPanel
{
    internal static void Draw(UiDraw ui)
    {
        var snapshot = UiNative.ChatSearchInput();
        if (snapshot is not { } input || !ChatSearchDisplayRules.Valid(input)) return;

        var count = (int)input.Count;
        var rows = Math.Max(1, count);
        var (x, y, width, height) = ChatCompletionDisplayRules.PopupRect(
            ui, input.X, input.Y, input.Width, input.Height, rows, extraHeaderHeight: 36f);
        var rowHeight = 40f * ui.Scale;
        // The non-focusable list does not steal Enter/Tab/Escape from the
        // native chat key router. Same flags and player theme as completion.
        const uint flags = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 3) |
                           (1u << 8) | (1u << 12) | (1u << 18) | (1u << 19);
        var hovered = false;
        ui.PushStyleRaw(UiStyle.Player);
        try
        {
            ui.NextWindowPos(x, y, 1);
            ui.NextWindowSize(width, height, 1);
            if (ui.BeginWindowNoClose(KilnText.T("chat.search.title") + "###kiln.chat.search", flags))
            {
                var top = ui.Cursor;
                ui.SetCursor(top.X, top.Y + 8f * ui.Scale);
                using (ui.FontSize(20))
                {
                    ui.Text(KilnText.T("chat.search.hint"));
                    ui.Text(KilnText.T("chat.search.query") + ": " + UiNative.ChatSearchQuery(input));
                }
                if (ui.BeginChild("##kiln.chat.search.rows", 0, rows * rowHeight + 4f * ui.Scale))
                {
                    using (ui.FontSize(26))
                    {
                        if (count == 0)
                            ui.Text(KilnText.T("chat.search.none"));
                        else for (var i = 0; i < count; ++i)
                        {
                            var absolute = input.First + (uint)i;
                            var line = UiNative.ChatSearchLine(input, i);
                            if (ui.Selectable(line + "##chat-history-" + absolute,
                                              absolute == input.Selected, 0, rowHeight))
                                UiNative.ChatSearchSelect(input, absolute);
                            if (ui.ItemHovered) hovered = true;
                        }
                    }
                }
                ui.EndChild();
            }
            ui.EndWindow();
        }
        finally
        {
            UiNative.ChatHover(hovered);
            ui.PopStyleRaw();
        }
    }
}
