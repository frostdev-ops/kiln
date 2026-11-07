// SPDX-License-Identifier: MIT
// What Kiln.Blocks needs from the inside of Kiln: Kiln.Blocks compiles blocks down to what kiln.content, kiln.gamedata and kiln.assets
// take, as the calling mod or, for a data-only mod's blocks/*.json, as an owner of its own. docs/kiln/modding/blocks.md.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln;

internal static unsafe class BlocksBridge
{
    public static string? CallerModId => ModContext.CurrentMod?.Info.Id;

    /// <summary>The calling mod's owner: its layers come off with it.</summary>
    public static ulong CallerOwner() => Content.Owner();

    /// <summary>An owner of Kiln's for a data-only mod's blocks.</summary>
    public static ulong ServiceOwner(string label)
    {
        var owner = Backend.Current.OwnerCreate(label);
        if (Content.T != null && owner != 0)
        {
            using var s = new ContentNative.Utf8(label);
            Content.T->LabelOwner(owner, s.Str);
        }
        return owner;
    }

    public static void Log(LogLevel level, string text)
    {
        try { Backend.Current.Log(0, level, text); } catch { }
    }

    /// <summary>A loaded mod's folder, or empty.</summary>
    public static string ModDirectory(string mod) => Mods.Get(mod)?.Directory ?? "";

    /// <summary>This is a game client: it draws models, so a mod's cooked files matter here.</summary>
    public static bool IsClient => Kiln.Client.IsClient;

    /// <summary>Whether a native service runs on this side.</summary>
    public static bool HasService(string name, uint minVersion) => Backend.Current.Service(name, minVersion) != 0;

    /// <summary>A native service's table, for Kiln.Blocks' own bindings. 0: not here.</summary>
    /// <summary>The UI looks the texture key <paramref name="from"/> up as <paramref name="to"/> until kiln.assets serves an icon under it: a PNG icon of a
    /// block's own named in game data before the UI cache knows it. False: kiln.content here has no such call (an older build).</summary>
    public static bool IconAlias(Guid from, Guid to)
    {
        var t = Content.T;
        if (t == null || t->Header.Size < (uint)sizeof(ContentNative.Table) || t->IconAlias == null) return false;
        var f = from.ToByteArray();
        var g = to.ToByteArray();
        fixed (byte* pf = f, pg = g) return t->IconAlias(pf, pg) == 0;
    }

    public static nint ServiceTable(string name, uint minVersion) => (nint)Backend.Current.Service(name, minVersion);

    public static IDisposable AddTick(string name, Action<ulong> step) => ServiceTick.Add(name, (in TickInfo t) => step(t.Step));

    public static void Register(ItemDefinition d, ulong owner) => NewContent.Register(d, owner);
    public static void Register(RecipeDefinition d, ulong owner) => NewContent.Register(d, owner);
    public static void DefineClone(GameDataRegistry r, JsonObject d, ulong owner) => r.Define(d, owner);
    public static ContentReport ApplyAs(ulong owner, string json, string source) => Content.ApplyAs(owner, json, source);

    /// <summary>Where a mod's blocks report goes (kiln/data/blocks/&lt;mod&gt;.json): <c>kiln blocks</c> reads it.</summary>
    public static string BlockReportFile(string mod)
    {
        var dir = Mods.Get(mod)?.Directory ?? throw new InvalidOperationException($"mod {mod} is not loaded");
        return Path.GetFullPath(Path.Combine(dir, "..", "..", "data", "blocks", mod + ".json"));
    }

    /// <summary>What the content sync would send a Kiln client now.</summary>
    public static string? SyncExport() => ContentSync.ExportJson((1u << (int)ContentKind.Items) | (1u << (int)ContentKind.Recipes) | (1u << 16), out _);

    /// <summary>Runs after every content application, at the next tick when content is ready already, and whenever <paramref name="again"/>
    /// says so, until it says it is done: clones are registered a little after content is ready.</summary>
    public static void OnReady(Func<bool> run, Func<bool>? again = null)
    {
        var last = ulong.MaxValue;
        ServiceTick.Add("kiln blocks ready", (in TickInfo t) =>
        {
            if (t.Step % 4 != 1 || Content.Status() is not { Ready: true } st || (st.Generation == last && again?.Invoke() != true)) return;
            try { if (run()) last = st.Generation; }
            catch (Exception e)
            {
                last = st.Generation;
                Log(LogLevel.Error, $"blocks: applying config values: {e}");
            }
        });
    }

    /// <summary>Runs once, some steps from now, on the tick thread.</summary>
    public static void After(int steps, Action run)
    {
        ulong due = 0;
        IDisposable? tick = null;
        tick = ServiceTick.Add("kiln blocks later", (in TickInfo t) =>
        {
            if (due == 0) due = t.Step + (ulong)steps;
            if (t.Step < due) return;
            tick?.Dispose();
            try { run(); }
            catch (Exception e) { Log(LogLevel.Error, $"blocks: {e}"); }
        });
    }

    /// <summary>The manifest's <c>clients</c> of a loaded mod: <c>none</c>, <c>kiln</c> or <c>mod</c>, with the loader's defaults.</summary>
    public static string ManifestClients(string modId)
    {
        var m = Mods.Get(modId);
        if (m == null) return "mod";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(m.Directory, "kiln.mod.json")),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty("clients", out var c) && c.GetString() is { Length: > 0 } s) return s;
        }
        catch (Exception e) when (e is IOException or JsonException) { return "mod"; }
        return m.Side == ModSide.Server ? "none" : "mod";
    }
}
