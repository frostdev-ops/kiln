using Kiln;

namespace KilnPack1;

/// <summary>The data pack is content/*.json; Kiln applies it without any code. This class logs what it changed, so the
/// pack's effect shows in kiln/logs/kiln.log at every world load and pack reload.</summary>
public sealed class KilnPack1Mod : Mod
{
    public override void OnLoad()
    {
        if (!Content.Available)
        {
            Log.Warn("kiln.content is not available on this game build: the data pack does nothing");
            return;
        }
        Content.OnReady(e =>
        {
            var logs = Content.Items.Get("Material_T1_Wood_Logs");
            Log.Info($"data pack applied (generation {e.Generation}{(e.Reload ? ", reload" : "")}): " +
                     $"Material_T1_Wood_Logs maxStackSize {logs?.Get("maxStackSize")} (game: {logs?.Original("maxStackSize")})");
        });
    }
}
