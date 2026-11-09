// SPDX-License-Identifier: MIT
using Kiln;

namespace UiBlocks;

/// <summary>The mod's own settings. A window, a HUD line and a key bind to these by path: <c>config.status.show</c>, <c>config.audio.volume</c>.</summary>
public sealed class SampleConfig
{
    public StatusSection Status { get; set; } = new();
    public AudioSection Audio { get; set; } = new();
}

[Comment("The status line on the HUD.")]
public sealed class StatusSection
{
    [Comment("Show the line while playing.")]
    public bool Show { get; set; } = true;

    [Comment("What the line starts with.")]
    public string Label { get; set; } = "Kiln UI";
}

[Comment("Something for a slider and a drop-down to change.")]
public sealed class AudioSection
{
    [Comment("0 to 100."), Range(0, 100)]
    public int Volume { get; set; } = 50;

    [Range(0.5, 3.0)]
    public double Scale { get; set; } = 1.0;

    public Mood Mood { get; set; } = Mood.Calm;
}

public enum Mood { Calm, Busy, Loud }

/// <summary>
/// A settings window on the mod's config, a status line and a key, as blocks. Nothing here draws or reads input: each block says what it is, and Kiln.Ui does the rest on a
/// player's game. blocks/twins.json says the same in data. Press F8, or choose "UI blocks sample" in the pause menu.
/// </summary>
public sealed class UiBlocksMod : Mod
{
    public override void OnLoad()
    {
        // the config comes first: a block names its entries, and Kiln refuses a name that is none
        Config.Register<SampleConfig>(ConfigKind.Client);

        // an effect, and the ability a button casts: both are blocks of the same mod
        var calm = Blocks.Define("calm", new EffectBlock { Restores = Restore.Mana(10) });
        var pulse = Blocks.Define("pulse", new AbilityBlock { Name = "Pulse", Description = "A little health back.", Cooldown = 5, OnCast = [new EffectBlock { Heal = 10 }] });

        Blocks.Define("settings", new WindowBlock
        {
            Title = UiText.Of(("en", "UI blocks sample"), ("de", "UI-Blöcke")),
            Width = 380,
            Menu = new UiMenu { Text = "UI blocks sample", In = UiMenuIn.Both, Order = 30 },
            Rows =
            [
                new TextRow { Text = "{config.status.label}: {game.fps:0} fps, {game.state}", Color = "orange" },
                new SeparatorRow { Text = "Status line" },
                new ToggleRow { Label = "Show the line", Bind = "config.status.show", Tooltip = "Saved in kiln/config/ui-blocks-client.json" },
                new SliderRow { Label = "Volume", Bind = "config.audio.volume" },
                new SliderRow { Label = "Scale", Bind = "config.audio.scale" },
                new ChoiceRow { Label = "Mood", Bind = "config.audio.mood" },
                new SeparatorRow(),
                new ButtonRow { Label = "Pulse", Does = new UiAction { CastAbility = pulse }, Tooltip = "Asks the server to cast the ability: it needs this mod too" },
                new ButtonRow { Label = "Calm", SameLine = true, Does = new UiAction { ApplyEffect = calm } },
                new ButtonRow { Label = "Reset volume", Does = new UiAction { Set = new UiSet { Bind = "config.audio.volume", To = 50 } } },
                new ButtonRow { Label = "Close", SameLine = true, Does = new UiAction { CloseWindow = "settings" } },
            ],
        });

        // only while playing, and only while the line is switched on; the text updates twice a second
        Blocks.Define("status", new HudLineBlock
        {
            Text = "{config.status.label}  {game.fps:0} fps  {game.clock}",
            Anchor = HudAnchor.TopRight, Color = "cyan", When = "game.inGame && config.status.show", Refresh = 0.5,
        });

        // the player can move this in the Kiln menu's Keys tab
        Blocks.Define("open-settings", new KeyBindingBlock { Key = "F8", Description = "Open the UI blocks sample", Does = new UiAction { OpenWindow = "settings" } });
        Blocks.Define("toggle-status", new KeyBindingBlock { Key = "Ctrl+F8", Description = "Show or hide the status line", Does = new UiAction { Toggle = "config.status.show" } });
    }
}
