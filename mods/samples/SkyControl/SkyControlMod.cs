// SPDX-License-Identifier: MIT
// Sky control: the server's weather read, chosen and given back (docs/kiln/services/time.md, "Choosing the weather").
//   /sky                  each weather layer: what runs there, what Kiln holds, the names a layer can run
//   /sky rain 10m         every region's rain for ten minutes of game time, then the game's weather again
//   /sky clear            nothing on every layer, held until released
//   /sky <name>           that phenomenon on every layer that has it, held until released
//   /sky release          the weather is the game's again
// What to take from it: the weather is a few layers that each roll for themselves, and a name can be on several; a
// choice with a duration ends by itself, one without is held until it is released (a hold is the server's, not the
// mod's: it stays when this mod is unloaded, and it is gone after a server restart); the game runs the change and tells
// every player's game, so there is nothing to send.
using Kiln;

namespace SkyControl;

public sealed class SkyControlMod : Mod
{
    private const string WeatherCapability = "time.weather_choose";

    public override void OnLoad() =>
        Log.Info($"sky-control: choosing the weather is {(Capabilities.Is(WeatherCapability) ? "available" : $"not available ({Capabilities.Why(WeatherCapability)})")}");

    [Command("sky", Description = "the weather: no argument shows it; rain 10m, clear, <name>, or release", Permission = CommandPermission.Admin)]
    private async Task Sky(CommandContext c, string? what = null, TimeSpan duration = default)
    {
        // Two questions: can this server choose the weather at all (the capability), and has the world's weather run
        // yet (it has a moment after the world loaded). WeatherChoiceAvailable answers both.
        if (!Capabilities.Is(WeatherCapability))
            throw new CommandException($"this server cannot choose the weather ({WeatherCapability} is off: {Capabilities.Why(WeatherCapability)})");
        if (!Time.WeatherChoiceAvailable) throw new CommandException("the world's weather has not started yet: try again in a moment");
        if (what is null)
        {
            Show(c);
            return;
        }
        // No duration given: the choice is held. Kiln.Time takes null for that.
        TimeSpan? span = duration == TimeSpan.Zero ? null : duration;
        if (span is { } d && (d < TimeSpan.FromSeconds(1) || d > Time.MaxWeatherDuration))
            throw new CommandException($"a duration from one second to {Time.MaxWeatherDuration.TotalDays:0} days");
        WeatherResult result;
        if (what.Equals("release", StringComparison.OrdinalIgnoreCase))
        {
            // now: the running weather ends and the game rolls at once. Without it the running one finishes its time.
            result = await Time.ReleaseWeather(now: true);
            c.Reply(result.Ok ? "the weather is the game's again" : $"not released: {result.Reason}");
            return;
        }
        var name = what.Equals("clear", StringComparison.OrdinalIgnoreCase) ? WeatherPhenomenon.NothingName : what;
        // A name no layer rolls is a bad argument (ArgumentException), so it is looked up first and answered as a
        // sentence with the names that exist.
        if (name != WeatherPhenomenon.NothingName && !Time.WeatherPhenomena.Any(p => p.IsRolled && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new CommandException($"no weather named {what}: one of clear, {string.Join(", ", Names())}");
        result = await Time.SetWeather(name, span);
        if (!result.Ok)
        {
            c.Reply($"the weather was not changed: {result.Reason}");
            return;
        }
        c.Reply(span is { } run ? $"{what} for {run.TotalMinutes:0.#} minute(s) of game time" : $"{what}, held until /sky release");
    }

    private static void Show(CommandContext c)
    {
        foreach (var layer in Time.Weather)
            c.Reply($"  {layer}");
        c.Reply($"  names: clear, {string.Join(", ", Names())}");
    }

    // The names the game's rolls can pick, each once (rain is on several layers).
    private static IEnumerable<string> Names() =>
        Time.WeatherPhenomena.Where(p => p.IsRolled).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);

    // Every change of a layer, the game's own rolls too. ByKiln tells them apart.
    [Subscribe]
    private void OnWeatherChanged(WeatherChangedEvent e)
    {
        if (e.ByKiln) Log.Info($"sky-control: {e}");
    }
}
