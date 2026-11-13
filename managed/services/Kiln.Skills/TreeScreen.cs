// SPDX-License-Identifier: MIT
// Kiln.Skills client part: each Kiln talent tree as a Kiln.Ui screen in the game's look (UiStyle.Game), opened from an
// entry in the game's pause menu and/or a key. Nodes sit on a grid; links are drawn between them; a side panel shows
// the selected node with its Unlock / Refund buttons; Respec at the bottom. State comes from the server
// (TalentNet.Client); actions go back through TalentNet.Send (a hosting client calls the server part directly).
namespace Kiln;

internal static class TalentUi
{
    private sealed class State
    {
        public UiScreen? Screen;
        public MenuEntry? Menu;
        public KeyBinding? Key;
        public readonly Dictionary<string, UiImage?> Icons = new(StringComparer.Ordinal);
        public string? Selected;
        public long AskedAt;
    }

    public static void Attach(Skills.Entry e)
    {
        if (!Ui.Available) return;
        var t = e.Tree;
        var st = new State();
        e.Ui = st;
        var cols = t.Nodes.Count == 0 ? 1 : t.Nodes.Max(n => n.Column) + 1;
        var rows = t.Nodes.Count == 0 ? 1 : t.Nodes.Max(n => n.Row) + 1;
        st.Screen = Ui.Screen(t.Name, ui => Draw(e, st, ui), new UiScreenOptions
        {
            Style = UiStyle.Game,
            Width = Math.Clamp(cols * Cell + 360, 640, 1500),
            Height = Math.Clamp(rows * Row + 190, 420, 960),
        });
        st.Screen.Opened += () => Ask(t.Id, st);
        if (t.MenuEntry is { } label) st.Menu = Ui.Menus.Add(MenuLocation.PauseMenu, label, () => st.Screen.Open(), t.MenuOrder);
        if (t.Key is { Length: > 0 } key)
            st.Key = KeyBindings.Register("skills." + t.Id, key, () => st.Screen.Toggle(), $"Open the talent tree {t.Name}", "Talents");
        if (t.Directory is { } dir)
            foreach (var n in t.Nodes)
                if (n.Icon is { } icon)
                {
                    var p = Path.Combine(dir, icon);
                    st.Icons[n.Id] = File.Exists(p) ? Ui.LoadImage(p) : null;
                }
    }

    public static void Detach(Skills.Entry e)
    {
        if (e.Ui is not State st) return;
        try
        {
            st.Screen?.Close();
            st.Screen?.Dispose();
            st.Menu?.Dispose();
            st.Key?.Dispose();
            foreach (var i in st.Icons.Values) i?.Dispose();
        }
        catch (Exception ex)
        {
            SkillsInternals.Warn($"tree {e.Tree.Id} screen: {ex.Message}");
        }
        e.Ui = null;
    }

    private static void Ask(string tree, State st)
    {
        st.AskedAt = Environment.TickCount64;
        if (!TalentNet.Send(new TalentRequest(0, tree, "")))
            TalentNet.Client[tree] = new TalentStateMessage(tree, 0, 0, 0, new(), false,
                (Network.ServerHasKiln ? Frame.NoServerTrees : Frame.NotConnected).ToString(), 0, 0);
    }

    // The frame in the languages Kiln's own strings have (a talent's name and text are the mod's). What the server answers stays its
    // own: a server doesn't know its players' languages.
    internal static class Frame
    {
        public static readonly UiText Points = UiText.Of(("en", "Points: {0} available  ({1} of {2} spent)"), ("de", "Punkte: {0} verfügbar  ({1} von {2} ausgegeben)"));
        public static readonly UiText Waiting = UiText.Of(("en", "Waiting for the server..."), ("de", "Warte auf den Server..."));
        public static readonly UiText NoServerTrees = UiText.Of(("en", "the server does not have Kiln talent trees"), ("de", "Der Server hat keine Kiln-Talentbäume"));
        public static readonly UiText NotConnected = UiText.Of(("en", "not connected to a Kiln server"), ("de", "Nicht mit einem Kiln-Server verbunden"));
        public static readonly UiText Select = UiText.Of(("en", "Select a talent."), ("de", "Wähle ein Talent."));
        public static readonly UiText LevelCost = UiText.Of(("en", "Level {0} / {1}   Cost {2}"), ("de", "Stufe {0} / {1}   Kosten {2}"));
        public static readonly UiText NeedsAll = UiText.Of(("en", "Needs all of: {0}"), ("de", "Braucht alle von: {0}"));
        public static readonly UiText NeedsOne = UiText.Of(("en", "Needs one of: {0}"), ("de", "Braucht eines von: {0}"));
        public static readonly UiText NeedsPoints = UiText.Of(("en", "Needs {0} points spent in this tree"), ("de", "Braucht {0} ausgegebene Punkte in diesem Baum"));
        public static readonly UiText NeedsLevel = UiText.Of(("en", "Needs character level {0}"), ("de", "Braucht Charakterstufe {0}"));
        public static readonly UiText Unlock = UiText.Of(("en", "Unlock ({0})"), ("de", "Freischalten ({0})"));
        public static readonly UiText NextLevel = UiText.Of(("en", "Next level ({0})"), ("de", "Nächste Stufe ({0})"));
        public static readonly UiText Refund = UiText.Of(("en", "Refund"), ("de", "Zurücknehmen"));
        public static readonly UiText Respec = UiText.Of(("en", "Respec (free)"), ("de", "Neu verteilen (kostenlos)"));
        public static readonly UiText Close = UiText.Of(("en", "Close"), ("de", "Schließen"));
        public static readonly UiText Tooltip = UiText.Of(("en", "{0}  ({1}/{2}, cost {3})"), ("de", "{0}  ({1}/{2}, Kosten {3})"));
        public static readonly UiText Paused = UiText.Of(("en", "The game is paused: this applies when you resume."), ("de", "Das Spiel ist pausiert: Das gilt, sobald du weiterspielst."));
        public static readonly UiText EffectAttribute = UiText.Of(("en", "attribute {0} +{1}/level"), ("de", "Attribut {0} +{1}/Stufe"));
        public static readonly UiText EffectBuff = UiText.Of(("en", "buff {0}"), ("de", "Buff {0}"));
        public static readonly UiText EffectGameNode = UiText.Of(("en", "game node {0}"), ("de", "Spielknoten {0}"));
        public static readonly UiText EffectProgram = UiText.Of(("en", "program {0}"), ("de", "Programm {0}"));
        public static readonly UiText EffectAbility = UiText.Of(("en", "ability {0}"), ("de", "Fähigkeit {0}"));
        public static readonly UiText EffectCode = UiText.Of(("en", "code"), ("de", "Code"));

        public static string F(UiText t, params object?[] args) => string.Format(t.ToString(), args);
    }

    // grid (unscaled pixels)
    private const float Cell = 120, Row = 108, Node = 64;
    private static readonly Color Gold = new(0xf5, 0xc5, 0x58), Light = new(0xc0, 0xcd, 0xd5), Dim = new(0x5a, 0x55, 0x6a),
        LinkOn = new(0xca, 0xac, 0x54), LinkOff = new(0x6a, 0x66, 0x7a), Tile = new(0x36, 0x32, 0x46, 0xc8),
        Warn = new(0xf2, 0x6e, 0x6e);

    private static void Draw(Skills.Entry e, State st, UiDraw ui)
    {
        var t = e.Tree;
        var s = ui.Scale;
        TalentNet.Client.TryGetValue(t.Id, out var state);
        if (state is null && Environment.TickCount64 - st.AskedAt > 3000) Ask(t.Id, st);
        if (t.Description is { } d) ui.TextWrapped(d.ToString());
        if (state is null) ui.TextDisabled(Frame.Waiting.ToString());
        else if (!state.Ok && state.Levels.Count == 0 && state.Total == 0) ui.Text(Warn, state.Message);
        else ui.Text(Gold, Frame.F(Frame.Points, state.Available, state.Spent, state.Total));
        var levels = state?.Levels ?? new Dictionary<string, int>();
        int Lv(string id) => levels.TryGetValue(id, out var l) ? l : 0;
        bool Available(TalentNode n)
        {
            if (Lv(n.Id) >= n.MaxLevel) return false;
            if (n.Requires.Count > 0)
            {
                var have = n.Requires.Count(r => Lv(r) > 0);
                if (n.RequireAll ? have < n.Requires.Count : have == 0) return false;
            }
            return (state?.Spent ?? 0) >= n.PointsSpentRequired && (state?.Available ?? 0) >= n.Cost;
        }

        var cols = t.Nodes.Count == 0 ? 1 : t.Nodes.Max(n => n.Column) + 1;
        var rows = t.Nodes.Count == 0 ? 1 : t.Nodes.Max(n => n.Row) + 1;
        float cell = Cell * s, row = Row * s, node = Node * s;
        // UiStyle.Game: a bordered child's padding is WindowPadding (14, 12), items are ItemSpacing (10, 8) apart
        float padX = 14 * s, padY = 12 * s, gapY = 8 * s;
        var canvasW = cols * cell + 2 * padX;
        var canvasH = rows * row + 2 * padY;
        if (ui.BeginChild("talent-canvas", canvasW, canvasH))
        {
            var (cx, cy, _, _) = ui.WindowRect;
            float X(TalentNode n) => cx + padX + n.Column * cell + (cell - node) / 2;
            float Y(TalentNode n) => cy + padY + n.Row * row + (row - node) / 2 - 8 * s;
            // links first (free drawing is on top of the window, so they stop at the node edges)
            foreach (var n in t.Nodes)
                foreach (var r in n.Requires)
                    if (t.Node(r) is { } from)
                        Dotted(ui, X(from) + node / 2, Y(from) + node / 2, X(n) + node / 2, Y(n) + node / 2, node / 2 + 3 * s,
                            Lv(r) > 0 && Lv(n.Id) > 0 ? LinkOn : LinkOff, s);
            // nodes: one line per grid row (ImGui lays out top to bottom; SameLine places them in the row)
            var curY = 0f;
            foreach (var group in t.Nodes.GroupBy(n => n.Row).OrderBy(g => g.Key))
            {
                var target = group.Key * row + (row - node) / 2 - 8 * s;
                if (target - curY - gapY > 0.5f) ui.Space(1, target - curY - gapY);
                ui.Space(1, node);  // the row's line
                foreach (var n in group.OrderBy(n => n.Column))
                {
                    ui.SameLine(padX + n.Column * cell + (cell - node) / 2);
                    using (ui.Id(n.Id))
                    {
                        var clicked = st.Icons.TryGetValue(n.Id, out var img) && img is not null
                            ? ui.ImageButton("node", img, node - 20 * s, node - 12 * s)
                            : ui.Button(Short(n.Name.ToString()), node, node);
                        if (clicked) st.Selected = n.Id;
                        if (ui.ItemHovered) ui.Tooltip(Describe(t, n, Lv(n.Id)));
                    }
                    var lv = Lv(n.Id);
                    var col = lv > 0 ? Gold : Available(n) ? Light : Dim;
                    float x = X(n), y = Y(n);
                    ui.DrawRect(x - 2 * s, y - 2 * s, x + node + 2 * s, y + node + 2 * s, col, 3 * s, filled: false);
                    if (st.Selected == n.Id) ui.DrawRect(x - 5 * s, y - 5 * s, x + node + 5 * s, y + node + 5 * s, Gold, 4 * s, filled: false);
                    if (n.MaxLevel > 1 || lv > 0) ui.DrawText(x + node - 22 * s, y + node - 18 * s, $"{lv}/{n.MaxLevel}", col);
                    var name = n.Name.ToString();
                    var (tw, _) = ui.MeasureText(name);
                    ui.DrawText(x + node / 2 - tw / 2, y + node + 4 * s, name, lv > 0 ? Gold : Light);
                }
                curY = target + node + gapY;
            }
        }
        ui.EndChild();
        ui.SameLine();
        if (ui.BeginChild("talent-info", 300 * s, canvasH))
        {
            var sel = st.Selected is { } id ? t.Node(id) : null;
            if (sel is null)
            {
                ui.TextDisabled(Frame.Select.ToString());
            }
            else
            {
                var lv = Lv(sel.Id);
                ui.Text(Gold, sel.Name.ToString());
                ui.Text(Frame.F(Frame.LevelCost, lv, sel.MaxLevel, sel.Cost));
                if (sel.Description is { } dsc) ui.TextWrapped(dsc.ToString());
                if (sel.Requires.Count > 0)
                    ui.TextDisabled(Frame.F(sel.RequireAll ? Frame.NeedsAll : Frame.NeedsOne,
                                            string.Join(", ", sel.Requires.Select(r => t.Node(r)?.Name.ToString() ?? r))));
                if (sel.PointsSpentRequired > 0) ui.TextDisabled(Frame.F(Frame.NeedsPoints, sel.PointsSpentRequired));
                if (sel.LevelRequired > 0) ui.TextDisabled(Frame.F(Frame.NeedsLevel, sel.LevelRequired));
                ui.Spacing();
                if (lv < sel.MaxLevel && ui.Button(Frame.F(lv == 0 ? Frame.Unlock : Frame.NextLevel, sel.Cost)))
                    TalentNet.Send(new TalentRequest(1, t.Id, sel.Id));
                if (lv > 0)
                {
                    ui.SameLine();
                    if (ui.Button(Frame.Refund.ToString())) TalentNet.Send(new TalentRequest(2, t.Id, sel.Id));
                }
            }
            if (state is { Ok: false, Message.Length: > 0 }) ui.Text(Warn, state.Message);
            else if (state is { Message.Length: > 0 }) ui.TextWrapped(state.Message);  // the panel is narrow: the pause note has to wrap
        }
        ui.EndChild();
        if (t.AllowRespec && ui.Button(Frame.Respec.ToString())) TalentNet.Send(new TalentRequest(3, t.Id, ""));
        ui.SameLine();
        if (ui.Button(Frame.Close.ToString())) ui.CloseScreen();
    }

    private static string Short(string s) => s.Length <= 9 ? s : s[..8] + ".";

    private static string Describe(TalentTree t, TalentNode n, int lv)
    {
        var lines = new List<string> { Frame.F(Frame.Tooltip, n.Name, lv, n.MaxLevel, n.Cost) };
        if (n.Description?.ToString() is { Length: > 0 } d) lines.Add(d);
        if (n.ListEffects)
            foreach (var e in n.Effects)
                if (e.PlayerText is { } line)
                    lines.Add("- " + line);
        return string.Join("\n", lines);
    }

    // a line as dots (Kiln.Ui draws rectangles): from (x0, y0) to (x1, y1), leaving `inset` at both ends
    private static void Dotted(UiDraw ui, float x0, float y0, float x1, float y1, float inset, Color c, float s)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len <= 2 * inset) return;
        dx /= len;
        dy /= len;
        var step = 5 * s;
        var r = 1.5f * s;
        for (var d = inset; d < len - inset; d += step)
        {
            var x = x0 + dx * d;
            var y = y0 + dy * d;
            ui.DrawRect(x - r, y - r, x + r, y + r, c, r);
        }
    }
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick: the channel, /talents, and the
/// "trees" of every loaded mod's skills/*.json (data-only trees: no code needed). Trees a mod's code registers win.</summary>
internal static class SkillsStartup
{
    static SkillsStartup()
    {
        try
        {
            GameTree.Rows.Ensure();  // the capability rows, before a mod asks for them
            Skills.EnsureStarted();
            var n = 0;
            foreach (var m in Mods.All)
            {
                if (!m.IsLoaded || m.Directory.Length == 0 || !Directory.Exists(Path.Combine(m.Directory, "skills"))) continue;
                n += Skills.LoadTreesOf(m.Directory, skipRegistered: true).Count;
            }
            SkillsInternals.Info($"started: {n} talent tree(s) from loaded mods' skills/*.json (data-only), {Skills.Trees.Count} in all");
        }
        catch (Exception ex)
        {
            SkillsInternals.Warn($"startup: {ex.Message}");
        }
    }
}
