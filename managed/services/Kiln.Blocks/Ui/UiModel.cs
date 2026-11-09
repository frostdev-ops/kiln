// SPDX-License-Identifier: MIT
// UI as blocks: windows, HUD lines and key bindings, in C# and (the same shape) in the "windows", "hudLines" and "keyBindings" sections of
// blocks/*.json. They are what Kiln.Ui draws and binds on a player's game; on a dedicated server they are checked and then do nothing.
// docs/kiln/modding/ui-blocks.md.
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>A block a player's game draws or binds with Kiln.Ui: <see cref="WindowBlock"/>, <see cref="HudLineBlock"/>, <see cref="KeyBindingBlock"/>.</summary>
public interface IUiBlock : IBlock { }

/// <summary>What a window is.</summary>
public enum WindowKind
{
    /// <summary>An overlay window: the player can drag it, and the X closes it. With <see cref="WindowBlock.Interactive"/> false it is only looked at.</summary>
    Window,
    /// <summary>A full screen of the mod in the game's look: the game behind it dims, it takes the mouse and keyboard, and Escape closes it.</summary>
    Screen,
}

/// <summary>Which of the game's menus a window's entry goes into.</summary>
public enum UiMenuIn { Pause, Main, Both }

/// <summary>An entry in the game's own menus that opens a window.</summary>
public sealed record UiMenu
{
    /// <summary>The label. A plain text, or one per language: <c>UiText.Of(("en", "Settings"), ("de", "Einstellungen"))</c>.</summary>
    [JsonConverter(typeof(UiTextConverter))]
    public required UiText Text { get; init; }
    public UiMenuIn In { get; init; } = UiMenuIn.Pause;
    /// <summary>Lower first. Kiln's own Mods entry has 1000.</summary>
    public int Order { get; init; }
}

/// <summary>
/// A window or a screen of rows: text, values, buttons, toggles, sliders, choices and separators. A value is a <c>game.*</c> value Kiln reads or a
/// <c>config.*</c> entry of this mod's config; a button runs an <see cref="UiAction"/>. Open it from a <see cref="KeyBindingBlock"/>, a button, a
/// <see cref="Menu"/> entry or <see cref="OpenAtStart"/>.
/// <code>
/// Blocks.Define("settings", new WindowBlock
/// {
///     Title = "My mod", Width = 360,
///     Rows =
///     [
///         new ToggleRow { Label = "Show the clock", Bind = "config.clock.show" },
///         new SliderRow { Label = "Scale", Bind = "config.clock.scale" },
///         new ButtonRow { Label = "Close", Does = new UiAction { CloseWindow = "settings" } },
///     ],
/// });
/// </code>
/// It is drawn by each player's game from the mod's own file, so the mod needs <c>clients: mod</c>. On a dedicated server it is checked and kept, and nothing is drawn.
/// What its widgets show refreshes every <see cref="Refresh"/> seconds, not every frame, and a closed window costs nothing.
/// </summary>
[BlockClients(ClientsLevel.Mod)]
public sealed record WindowBlock : IUiBlock
{
    [JsonConverter(typeof(UiTextConverter))]
    public required UiText Title { get; init; }
    public WindowKind Kind { get; init; } = WindowKind.Window;
    /// <summary>Pixels at UI scale 1. 0: a window fits its rows, a screen is 720 wide.</summary>
    public float Width { get; init; }
    /// <summary>Pixels at UI scale 1. 0: a window fits its rows, a screen is 520 high.</summary>
    public float Height { get; init; }
    /// <summary>A window: where its centre is, [x, y] as 0..1 of the screen. Null: where the overlay puts it.</summary>
    public float[]? Position { get; init; }
    /// <summary>A window: it grows and shrinks with its rows.</summary>
    public bool AutoResize { get; init; }
    public bool TitleBar { get; init; } = true;
    /// <summary>A window: how opaque the background is, 0..1. Null: the theme's.</summary>
    public float? Alpha { get; init; }
    /// <summary>A window: false gives a panel the player only looks at (no mouse and keyboard taken from the game).</summary>
    public bool Interactive { get; init; } = true;
    /// <summary>A screen: darken the game behind it. Null: yes.</summary>
    public bool? Dim { get; init; }
    /// <summary>Open as soon as the mod's game has it. Default: closed until something opens it.</summary>
    public bool OpenAtStart { get; init; }
    /// <summary>Seconds between updates of the values and conditions it shows: 0.1 to 3600. A toggle or a button answers at once.</summary>
    public double Refresh { get; init; } = 0.25;
    /// <summary>An entry in the game's pause menu and/or main menu that opens it.</summary>
    public UiMenu? Menu { get; init; }
    public required IReadOnlyList<UiRow> Rows { get; init; }

    /// <summary>A note in a data file, ignored.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public JsonElement Comment { init { } }
}

/// <summary>One line of a window. <c>"type"</c> in JSON says which: text, value, toggle, slider, choice, separator, button.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(TextRow), "text")]
[JsonDerivedType(typeof(ValueRow), "value")]
[JsonDerivedType(typeof(ToggleRow), "toggle")]
[JsonDerivedType(typeof(SliderRow), "slider")]
[JsonDerivedType(typeof(ChoiceRow), "choice")]
[JsonDerivedType(typeof(SeparatorRow), "separator")]
[JsonDerivedType(typeof(ButtonRow), "button")]
public abstract record UiRow
{
    /// <summary>Shown only while this is true: <c>"game.inGame"</c>, <c>"!game.inGame"</c>, <c>"config.clock.show"</c>, <c>"game.screen == InGame"</c>, <c>"game.fps &lt; 30"</c>, joined with <c>&amp;&amp;</c>.</summary>
    public string? When { get; init; }
    /// <summary>On the same line as the row before it.</summary>
    public bool SameLine { get; init; }
    /// <summary>Shown while the pointer rests on the row's widget.</summary>
    [JsonConverter(typeof(UiTextConverter))]
    public UiText? Tooltip { get; init; }
}

/// <summary>A line of text. <c>{game.fps:0}</c> and <c>{config.clock.scale:0.00}</c> in it show a value; <c>{{</c> and <c>}}</c> are braces.</summary>
public sealed record TextRow : UiRow
{
    [JsonConverter(typeof(UiTextConverter))]
    public required UiText Text { get; init; }
    /// <summary>"#rrggbb", "#rrggbbaa", or white, black, gray, red, green, yellow, orange, cyan.</summary>
    public string? Color { get; init; }
    /// <summary>Break long text at the window's edge.</summary>
    public bool Wrap { get; init; }
}

/// <summary>A label and a value that cannot be edited here: <c>Label: value</c>.</summary>
public sealed record ValueRow : UiRow
{
    [JsonConverter(typeof(UiTextConverter))]
    public required UiText Label { get; init; }
    /// <summary><c>game.&lt;name&gt;</c> or <c>config.&lt;path&gt;</c> (<c>config.&lt;config id&gt;/&lt;path&gt;</c> when two configs have the path).</summary>
    public required string Bind { get; init; }
    /// <summary>How a number or a true/false shows: <c>0.00</c>, <c>yes|no</c>.</summary>
    public string? Format { get; init; }
}

/// <summary>A checkbox on a true/false config entry. A click writes the config.</summary>
public sealed record ToggleRow : UiRow
{
    [JsonConverter(typeof(UiTextConverter))]
    public required UiText Label { get; init; }
    public required string Bind { get; init; }
}

/// <summary>A slider on a whole-number or decimal config entry. A drag writes the config once it rests for a moment.</summary>
public sealed record SliderRow : UiRow
{
    [JsonConverter(typeof(UiTextConverter))]
    public required UiText Label { get; init; }
    public required string Bind { get; init; }
    /// <summary>Default: the entry's own range, which it must have when this and <see cref="Max"/> are left out.</summary>
    public double? Min { get; init; }
    public double? Max { get; init; }
}

/// <summary>A drop-down on an enum config entry, with its options.</summary>
public sealed record ChoiceRow : UiRow
{
    [JsonConverter(typeof(UiTextConverter))]
    public required UiText Label { get; init; }
    public required string Bind { get; init; }
}

/// <summary>A line, with a heading when it has text.</summary>
public sealed record SeparatorRow : UiRow
{
    [JsonConverter(typeof(UiTextConverter))]
    public UiText? Text { get; init; }
}

/// <summary>A button that runs an action.</summary>
public sealed record ButtonRow : UiRow
{
    [JsonConverter(typeof(UiTextConverter))]
    public required UiText Label { get; init; }
    public required UiAction Does { get; init; }
}

/// <summary>
/// What a button or a key does: exactly one of these. They run on the player's game, never on a dedicated server.
/// </summary>
public sealed record UiAction
{
    /// <summary>A command line of this game's command registry, as the console runs it ("reload", "/mymod-reset 5"): as the host's own character when this game hosts the world, else
    /// as the console. It does not reach a server this game joined: use <see cref="CastAbility"/> for what the server must do.</summary>
    public string? Command { get; init; }
    /// <summary>Flips a true/false config entry: <c>"config.clock.show"</c>.</summary>
    public string? Toggle { get; init; }
    /// <summary>Sets a config entry to a value.</summary>
    public UiSet? Set { get; init; }
    /// <summary>Asks the server to cast an ability of this mod (or <c>"other-mod:name"</c>), as its key would: its costs and cooldown apply, and a refusal shows as a toast. The server needs the mod.</summary>
    public AbilityRef? CastAbility { get; init; }
    /// <summary>Applies an effect to the player, through a hidden ability Kiln makes for it (<c>ui-apply.&lt;name&gt;</c>). The server needs the mod.</summary>
    public EffectRef? ApplyEffect { get; init; }
    public WindowRef? OpenWindow { get; init; }
    public WindowRef? CloseWindow { get; init; }
    /// <summary>A short message in the corner of the screen.</summary>
    [JsonConverter(typeof(UiTextConverter))]
    public UiText? Toast { get; init; }
}

/// <summary>A config entry and the value to give it.</summary>
public sealed record UiSet
{
    public required string Bind { get; init; }
    /// <summary>A true/false, a number or a text, which the entry takes: a number in its range, an option of an enum.</summary>
    public required object To { get; init; }
}

/// <summary>A window: what <see cref="Blocks.Define{T}"/> returned, its name in this mod, or <c>"other-mod:name"</c>.</summary>
[JsonConverter(typeof(WindowRefConverter))]
public readonly record struct WindowRef(string Value)
{
    public static implicit operator WindowRef(string value) => new(value);
    public static implicit operator WindowRef(BlockHandle<WindowBlock> handle) => new(handle.ToString());
    public override string ToString() => Value;
}

internal sealed class WindowRefConverter : JsonConverter<WindowRef>
{
    public override WindowRef Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => new(r.GetString() ?? "");
    public override void Write(Utf8JsonWriter w, WindowRef v, JsonSerializerOptions o) => w.WriteStringValue(v.Value);
}

/// <summary>
/// A line of text on the game's screen: <c>"FPS {game.fps:0}"</c>. It shows while its condition holds and updates at its own pace; hidden, it costs nothing.
/// <code>
/// Blocks.Define("fps", new HudLineBlock { Text = "{game.fps:0} fps", Anchor = HudAnchor.TopRight, When = "game.inGame", Refresh = 0.5 });
/// </code>
/// </summary>
[BlockClients(ClientsLevel.Mod)]
public sealed record HudLineBlock : IUiBlock
{
    /// <summary>The text, with <c>{game.fps:0}</c> and <c>{config.clock.scale:0.00}</c> for values.</summary>
    [JsonConverter(typeof(UiTextConverter))]
    public required UiText Text { get; init; }
    public HudAnchor Anchor { get; init; } = HudAnchor.TopLeft;
    /// <summary>"#rrggbb", "#rrggbbaa", or white, black, gray, red, green, yellow, orange, cyan. Default white.</summary>
    public string? Color { get; init; }
    /// <summary>Shown only while this is true; see <see cref="UiRow.When"/>.</summary>
    public string? When { get; init; }
    /// <summary>Seconds between updates: 0.1 to 3600.</summary>
    public double Refresh { get; init; } = 1;

    /// <summary>A note in a data file, ignored.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public JsonElement Comment { init { } }
}

/// <summary>
/// A key that runs an action. The player can rebind it in the Kiln menu's Keys tab; the choice is kept in keybinds.json under <c>&lt;mod&gt;:&lt;name&gt;</c>.
/// <code>
/// Blocks.Define("settings-key", new KeyBindingBlock { Key = "F8", Description = "Open the settings", Does = new UiAction { OpenWindow = "settings" } });
/// </code>
/// </summary>
[BlockClients(ClientsLevel.Mod)]
public sealed record KeyBindingBlock : IUiBlock
{
    /// <summary>The default key: "F8", "Ctrl+K", "Shift+Alt+F2", "Numpad5", or "None" to bind it only once the player chooses.</summary>
    public required string Key { get; init; }
    /// <summary>What the Keys tab says. Default: the block's name.</summary>
    [JsonConverter(typeof(UiTextConverter))]
    public UiText? Description { get; init; }
    /// <summary>The group in the Keys tab. Default: the mod's name.</summary>
    public string? Category { get; init; }
    public required UiAction Does { get; init; }
    /// <summary>A key that types something (a letter, a digit) fires in play only; true fires on every game screen too, chat included.</summary>
    public bool OnGameScreens { get; init; }
    /// <summary>Also fire while the Kiln menu is open.</summary>
    public bool WhileCapturing { get; init; }
    /// <summary>The name this block had before a rename: the player's rebinding follows it.</summary>
    public string? Was { get; init; }

    /// <summary>A note in a data file, ignored.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public JsonElement Comment { init { } }
}

/// <summary>A <see cref="UiText"/> in JSON: a string, or an object of language to string (<c>{ "en": "Save", "de": "Speichern" }</c>, <c>"*"</c> for every other language).</summary>
internal sealed class UiTextConverter : JsonConverter<UiText>
{
    public override UiText? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        if (r.TokenType == JsonTokenType.String) return new UiText(r.GetString() ?? "");
        if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("a text is a string, or an object of language to text: { \"en\": \"...\", \"de\": \"...\" }");
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            var language = r.GetString()!;
            r.Read();
            if (r.TokenType != JsonTokenType.String) throw new JsonException($"the text for \"{language}\" is a string");
            texts[language] = r.GetString()!;
        }
        if (texts.Count == 0) throw new JsonException("a text in languages needs at least one: { \"en\": \"...\" }");
        return new UiText(texts);
    }

    public override void Write(Utf8JsonWriter w, UiText v, JsonSerializerOptions o)
    {
        if (v.Languages.Count == 1 && v.Languages.First() == "*")
        {
            w.WriteStringValue(v.For("*"));
            return;
        }
        w.WriteStartObject();
        foreach (var language in v.Languages.Order(StringComparer.Ordinal)) w.WriteString(language, v.For(language));
        w.WriteEndObject();
    }
}
