// SPDX-License-Identifier: MIT
// A server's refusal on screen. The REJECT arrives about 1.5 s before the game's own login error, whose dialog ("An
// update of the game is available") names no mod. Kiln shows the server's reason as a toast at once (it stays over
// that dialog), and again when the game is back at its main menu, where a player who read the dialog first looks next.
namespace Kiln;

internal static class RefusalNotice
{
    internal const double InGameSeconds = 20, MenuSeconds = 30;
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);
    private static readonly object Lock = new();
    private static (string Text, DateTime At)? _pending;
    private static int _started;

    /// <summary>Subscribes once, as Kiln (no mod owns the handler, so no mod's unload removes it).</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        var mod = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try
        {
            Network.Rejected += OnRejected;
        }
        catch (Exception e)
        {
            UiEngine.Log(LogLevel.Warn, $"refusal notice: {e.Message} (a server's refusal shows only in kiln.log)");
        }
        finally
        {
            ModContext.CurrentMod = mod;
        }
    }

    /// <summary>Kiln.Network's client event (main thread).</summary>
    private static void OnRejected(string reason, IReadOnlyList<string> details)
    {
        var advert = Network.ServerAdvert;
        var text = NetRefusal.Notice(advert?.ServerName, reason, details, advert?.Modpack is not null);
        lock (Lock) _pending = (text, DateTime.UtcNow);
        UiEngine.AddToast(text, ToastKind.Error, InGameSeconds);
        UiEngine.Log(LogLevel.Info, "refusal shown: " + text.Replace("\n  ", " ").Replace('\n', ' '));
    }

    /// <summary>The game's screen changed (game UI thread): back at the main menu after a refusal, show it again.</summary>
    public static void ScreenChanged(GameScreen before, GameScreen now)
    {
        if (now.Kind != ScreenKind.MainMenu || before.Kind == ScreenKind.MainMenu) return;
        (string Text, DateTime At)? p;
        lock (Lock)
        {
            p = _pending;
            _pending = null;
        }
        if (p is not { } n || DateTime.UtcNow - n.At > KeepFor) return;
        UiEngine.AddToast(n.Text, ToastKind.Error, MenuSeconds);
        UiEngine.Log(LogLevel.Info, "refusal shown again at the main menu");
    }
}
