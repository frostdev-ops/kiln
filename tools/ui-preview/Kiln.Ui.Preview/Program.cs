// SPDX-License-Identifier: MIT
// Renders the Kiln mod manager (managed/services/Kiln.Ui/ModManager.cs) to PNG files without the game: Kiln.Ui's real
// code draws through the headless kiln.ui shim (tools/ui-preview/shim.cpp), from sample scenes whose state files and
// profile are written into a scratch kiln folder and read by the real ModCatalog. Usage: tools/ui-preview/README.md.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kiln;

namespace Kiln.UiPreview;

internal static unsafe class Program
{
    private sealed class PreviewBackend(nint table) : IKilnBackend
    {
        public string Version => "0.2.0-dev";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public void Log(ulong owner, LogLevel level, string text)
        {
            if (level >= LogLevel.Warn) Console.Error.WriteLine($"[{level}] {text}");
        }
        public nint Service(string name, uint minVersion) => name == "kiln.ui" && minVersion <= 4 ? table : 0;
    }

    private static delegate* unmanaged[Cdecl]<int, int, float, byte*, byte*, nint> _open;
    private static delegate* unmanaged[Cdecl]<int, byte*> _frame;
    private static delegate* unmanaged[Cdecl]<int, int, void> _key;
    private static delegate* unmanaged[Cdecl]<float, float, int, void> _mouse;
    private static delegate* unmanaged[Cdecl]<int, void> _page;

    private static int Main(string[] args)
    {
        string? Arg(string name, string? def = null)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
        }
        var shim = Arg("--shim") ?? throw new ArgumentException("--shim PATH (libkiln_ui_preview.so)");
        var outDir = Path.GetFullPath(Arg("--out", "preview")!);
        var size = Arg("--size", "1920x1080")!.Split('x');
        int w = int.Parse(size[0]), h = int.Parse(size[1]);
        var scale = float.Parse(Arg("--scale", "1")!, System.Globalization.CultureInfo.InvariantCulture);
        var font = Arg("--font");
        var bold = Arg("--bold-font");
        var only = Arg("--scene");
        var suffix = Arg("--suffix", $"{w}x{h}" + (scale != 1 ? $"@{scale:0.##}" : ""))!;
        Directory.CreateDirectory(outDir);

        var lib = NativeLibrary.Load(shim);
        _open = (delegate* unmanaged[Cdecl]<int, int, float, byte*, byte*, nint>)NativeLibrary.GetExport(lib, "kiln_preview_open");
        _frame = (delegate* unmanaged[Cdecl]<int, byte*>)NativeLibrary.GetExport(lib, "kiln_preview_frame");
        _key = (delegate* unmanaged[Cdecl]<int, int, void>)NativeLibrary.GetExport(lib, "kiln_preview_key");
        _mouse = (delegate* unmanaged[Cdecl]<float, float, int, void>)NativeLibrary.GetExport(lib, "kiln_preview_mouse");
        _page = (delegate* unmanaged[Cdecl]<int, void>)NativeLibrary.GetExport(lib, "kiln_preview_page");
        if (bold is not null)
        {
            var setBold = (delegate* unmanaged[Cdecl]<byte*, void>)NativeLibrary.GetExport(lib, "kiln_preview_bold_font");
            var b = System.Text.Encoding.UTF8.GetBytes(bold + "\0");
            fixed (byte* p = b) setBold(p);
        }
        nint table;
        var fontBytes = System.Text.Encoding.UTF8.GetBytes((font ?? "") + "\0");
        fixed (byte* f = fontBytes) table = _open(w, h, scale, font is null ? null : f, null);

        var work = Path.Combine(outDir, "work");
        KilnPaths.Root = Path.Combine(work, "kiln-root");  // keybinds.json and the like land here, never in the tree
        Directory.CreateDirectory(KilnPaths.Root);
        Backend.Current = new PreviewBackend(table);
        UiNative.ResetForTests();
        UiEngine.EnsureStarted();
        Samples.RegisterExtras();

        var rendered = 0;
        foreach (var scene in Scenes.All)
        {
            if (only is not null && only != "all" && !scene.Name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            if (scene.OnlyAt is { } at && at != suffix && only is null) continue;
            if (suffix is "3440x1440" or "1920x1200" && !scene.GamePage && only is null) continue;  // these sizes are for the game page
            var root = Path.Combine(work, scene.Name);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var kilnMods = scene.Build(root);
            ModCatalog.Sources = () => new CatalogSources
            {
                KilnRoot = Path.Combine(root, "kiln"), KilnMods = () => kilnMods, Side = "client", ProcessStartUtc = DateTime.UtcNow.AddMinutes(-5),
            };
            ModCatalog.WaitForSnapshot();
            var page = ModManagerHost.Page;
            page.Section = scene.Section;
            page.Filter = scene.Filter;
            page.Search = scene.Search;
            page.Selected = scene.Selected;
            ModManagerHost.ResetForTests();
            GamePageHost.ResetForTests();
            // the game page: kiln.ui says the game shows it; the Kiln menu itself is not "open" (the stand-alone host)
            _page(scene.GamePage ? (scene.Bright ? 2 : 1) : 0);
            var menu = scene.GamePage ? 0 : 1;
            byte* px = null;
            for (var i = 0; i < 4; i++) px = _frame(menu);
            Thread.Sleep(250);  // the game font loads on a worker (style.cpp), as in the game
            for (var i = 0; i < 4; i++) px = _frame(menu);
            foreach (var (key, times) in scene.Keys)
                for (var t = 0; t < times; t++)
                {
                    _key(key, 1);
                    _frame(menu);
                    _key(key, 0);
                    _frame(menu);
                }
            for (var i = 0; i < 3; i++) px = _frame(menu);
            var file = Path.Combine(outDir, $"{scene.Name}-{suffix}.png");
            WritePng(file, px, w, h);
            Console.WriteLine($"{file}  ({ModCatalog.Snapshot(TimeSpan.FromHours(1)).Mods.Count} mods)");
            if (scene.Summary) File.WriteAllText(Path.ChangeExtension(file, ".txt"), ModCatalog.Summary(ModCatalog.Snapshot(TimeSpan.FromHours(1))));
            rendered++;
        }
        return rendered > 0 ? 0 : 1;
    }

    // PNG: RGB, one IDAT (zlib from .NET), CRCs.
    private static void WritePng(string path, byte* rgba, int w, int h)
    {
        var raw = new byte[(w * 3 + 1) * h];
        for (var y = 0; y < h; y++)
        {
            var o = y * (w * 3 + 1);
            raw[o] = 0;
            var src = rgba + (long)y * w * 4;
            for (var x = 0; x < w; x++)
            {
                raw[o + 1 + x * 3] = src[x * 4];
                raw[o + 2 + x * 3] = src[x * 4 + 1];
                raw[o + 3 + x * 3] = src[x * 4 + 2];
            }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(fs, "IHDR", ihdr);
        Chunk(fs, "IDAT", z.ToArray());
        Chunk(fs, "IEND", []);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        var crc = Crc(t, data);
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static uint Crc(byte[] a, byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var arr in new[] { a, b })
            foreach (var x in arr)
            {
                c ^= x;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>Bindings and a config page, so the Keys section and a mod's inline settings have something to show.</summary>
internal static class Samples
{
    public static float Range = 24, Speed = 1.25f;
    public static bool Glow = true;

    public static void RegisterExtras()
    {
        KeyBindings.Register("frost-edge.wheel", "F6", null, "Open the frost wheel", "Frost Edge");
        KeyBindings.Register("better-waypoints.place", "Ctrl+B", null, "Place a waypoint here", "Better Waypoints");
        KeyBindings.Register("quick-stack.all", "Q", null, "Stack into nearby chests", "Quick Stack");
        KeyBindings.Register("glider-tweaks.boost", "Shift+Space", null, "Glider boost", "Glider Tweaks");
        Ui.AddConfigPage("frost-edge", ui =>
        {
            ui.Slider("Frost range (m)", ref Range, 4, 64);
            ui.Slider("Projectile speed", ref Speed, 0.5f, 3f);
            ui.Checkbox("Glowing edge", ref Glow);
            ui.Button("Reset to defaults");
        });
    }
}

/// <summary>A sample session: the files of a game folder and Kiln's own mod list.</summary>
internal sealed record Scene(string Name, Func<string, IReadOnlyList<KilnModInput>> Build)
{
    public ModManagerSection Section { get; init; } = ModManagerSection.Mods;
    public EngineFilter Filter { get; init; } = EngineFilter.All;
    public string Search { get; init; } = "";
    public string? Selected { get; init; }
    /// <summary>ImGuiKey values pressed after the scene settles (navigation).</summary>
    public (int Key, int Times)[] Keys { get; init; } = [];
    /// <summary>Render only at this size suffix unless named with --scene.</summary>
    public string? OnlyAt { get; init; }
    public bool Summary { get; init; }
    /// <summary>Inside the game's main menu (GamePageHost) instead of the stand-alone page; also rendered at 3440x1440.</summary>
    public bool GamePage { get; init; }
    /// <summary>The game page over a bright, high-contrast stand-in (readability).</summary>
    public bool Bright { get; init; }
}

internal static class Scenes
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static int Pid => Environment.ProcessId;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static void Write(string path, object o)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(o, Json));
    }

    private static string Folder(string root, params string[] parts)
    {
        var p = Path.Combine([root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    private static KilnModInput K(string root, string id, string name, string version, ModSide side, ModState state, int index,
        string? error = null, params ModDependency[] deps) =>
        new(id, name, version, Folder(root, "library", "kiln", id, version), side, state, error, deps, index);

    private static object Entry(string root, string kind, string id, string version, bool enabled = true, bool create = true) =>
        new { id, version, path = create ? Folder(root, "library", kind, id, version) : Path.Combine(root, "library", kind, id, version), enabled };

    public static readonly Scene[] All =
    [
        new("typical", Typical) { Selected = "kiln/storage-labels", Summary = true },
        new("typical-emf", Typical) { Filter = new EngineFilter("emf"), Selected = "emf/fishing-plus" },
        new("typical-search", Typical) { Search = "map", OnlyAt = "1920x1080" },
        new("typical-config", Typical) { Selected = "kiln/frost-edge", OnlyAt = "1920x1080" },
        new("typical-keys", Typical) { Keys = [(516 /* ImGuiKey_DownArrow */, 3)], OnlyAt = "1920x1080" },
        new("problems", Problems) { Filter = EngineFilter.Attention, Summary = true },
        new("problems-ember", Problems) { Filter = new EngineFilter("ember"), OnlyAt = "1920x1080" },
        new("empty", Empty),
        new("network", Typical) { Section = ModManagerSection.Network, OnlyAt = "1920x1080" },
        new("keys", Typical) { Section = ModManagerSection.Keys, OnlyAt = "1920x1080" },
        new("overlay", Typical) { Section = ModManagerSection.Overlay, OnlyAt = "1920x1080" },
        new("game-page", Typical) { Selected = "kiln/storage-labels", GamePage = true },
        new("game-page-bright", Typical) { Selected = "kiln/storage-labels", GamePage = true, Bright = true },
        new("game-page-problems", Problems) { Filter = EngineFilter.Attention, GamePage = true, OnlyAt = "1920x1080" },
    ];

    private static void Boot(string kiln, string profileFile, params object[] plugins) =>
        Write(Path.Combine(kiln, "state", "boot.json"), new
        {
            format = 1, pid = Pid, side = "client", kiln = "0.2.0-dev",
            profile = new { mode = "active", id = "ember-run", name = "Ember run", file = profileFile, missing = Array.Empty<object>() },
            chain = Array.Empty<object>(), plugins,
        });

    // About 25 mods over five engines: Kiln, EMF, EML, SET, ShroudForge; one failed, one switched off.
    private static IReadOnlyList<KilnModInput> Typical(string root)
    {
        var kiln = Folder(root, "kiln");
        var kilnMods = new List<KilnModInput>
        {
            K(root, "frost-edge", "Frost Edge", "1.4.2", ModSide.Both, ModState.Loaded, 0),
            K(root, "large-watering-can", "Large Watering Can", "1.1.0", ModSide.Both, ModState.Loaded, 1),
            K(root, "better-waypoints", "Better Waypoints", "2.0.1", ModSide.Client, ModState.Loaded, 2),
            K(root, "quick-stack", "Quick Stack", "1.4.0", ModSide.Both, ModState.Loaded, 3),
            K(root, "storage-labels", "Storage Labels", "0.9.3", ModSide.Client, ModState.Failed, -1,
                "Needs Quick Stack 2.0 or newer, but 1.4.0 is installed.", new ModDependency("quick-stack", ">=2.0", false), new ModDependency("kiln", ">=0.2.0", false)),
            K(root, "day-length", "Day Length", "1.0.0", ModSide.Server, ModState.Loaded, 4),
            K(root, "glider-tweaks", "Glider Tweaks", "0.6.0", ModSide.Client, ModState.Loaded, 5, null, new ModDependency("frost-edge", "^1.2", true)),
            K(root, "creative-tools", "Creative Tools", "3.2.0", ModSide.Both, ModState.Loaded, 6),
            K(root, "kiln-test-helper", "Test helper for hosted worlds", "0.1.1", ModSide.Server, ModState.Skipped, -1,
                "side 'server': loads while this client hosts a world"),
        };
        var profileFile = Path.Combine(kiln, "profile.json");
        Write(profileFile, new
        {
            format = 1, id = "ember-run", name = "Ember run", generation = 7,
            mods = new Dictionary<string, object[]>
            {
                ["kiln"] = kilnMods.Select(m => (object)new { id = m.Id, version = m.Version, path = m.Directory }).ToArray(),
                ["emf"] =
                [
                    Entry(root, "emf", "beastmaster", "0.3.3"), Entry(root, "emf", "pet-wheel", "1.0.2"), Entry(root, "emf", "auto-torch", "0.4.0"),
                    Entry(root, "emf", "map-notes", "1.2.0"), Entry(root, "emf", "fishing-plus", "0.2.1", enabled: false),
                ],
                ["eml"] =
                [
                    Entry(root, "eml", "bigger-stacks", "0.1.0"), Entry(root, "eml", "brighter-nights", "1.0.0"),
                    Entry(root, "eml", "lantern-glow", "0.3.0"), Entry(root, "eml", "quiet-bells", "0.1.2"),
                ],
                ["set"] =
                [
                    Entry(root, "set", "sprint-forever", "1.0.0"), Entry(root, "set", "hotbar-plus", "0.5.0"),
                    Entry(root, "set", "fast-travel-anywhere", "2.1.0"), Entry(root, "set", "night-sky", "1.1.0"),
                ],
                ["shroudforge"] =
                [
                    Entry(root, "shroudforge", "sfpkg-core", "1.0.0"), Entry(root, "shroudforge", "shroud-timer", "0.8.0"),
                    Entry(root, "shroudforge", "builders-grid", "1.3.1"),
                ],
            },
        });
        Boot(kiln, profileFile);
        Write(Path.Combine(kiln, "state", "native.json"), new { format = 1, pid = Pid, phase = "boot", mods = Array.Empty<object>() });
        object Emf(string id, string name, string version, string status, string? reason = null) =>
            new { id, name, version, status, reason = reason ?? "", source = "profile", folder = Path.Combine(root, "library", "emf", id, version), side = "client" };
        Write(Path.Combine(kiln, "state", "emf.json"), new
        {
            format = 1, plugin = "kiln-emf", version = "0.1.0", side = "client", steppedAside = false, steppedAsideReason = "", emfIdle = "",
            updatedUnixMs = Now,
            mods = new[]
            {
                Emf("beastmaster", "Beastmaster", "0.3.3", "loaded"), Emf("pet-wheel", "Pet Wheel", "1.0.2", "loaded"),
                Emf("auto-torch", "Auto Torch", "0.4.0", "loaded"), Emf("map-notes", "Map Notes", "1.2.0", "loaded"),
                Emf("fishing-plus", "Fishing Plus", "0.2.1", "disabled", "switched off in the profile"),
            },
        });
        object Eml(string id, string name, string version) =>
            new { id, version, name, source = "profile", path = Path.Combine(root, "library", "eml", id, version), state = "loaded", reason = (string?)null };
        Write(Path.Combine(kiln, "state", "eml.json"), new
        {
            format = 1, pid = Pid, kilnEml = "0.1.0", eml = "0.2.0", phase = "done", patched = 13, error = (string?)null,
            mods = new[] { Eml("bigger-stacks", "Bigger Stacks", "0.1.0"), Eml("brighter-nights", "Brighter Nights", "1.0.0"), Eml("lantern-glow", "Lantern Glow", "0.3.0"), Eml("quiet-bells", "Quiet Bells", "0.1.2") },
        });
        object Set(string id, string name, string version) =>
            new { id, name, version, folder = Path.Combine(root, "shrouded_engine_tweaks", "mods", id), status = "loaded", reason = "" };
        Write(Path.Combine(kiln, "state", "set.json"), new
        {
            format = 1, plugin = "kiln-set", version = "0.1.1", pid = Pid, side = "client", status = "running", steppedAside = false, updatedUnixMs = Now,
            mods = new[] { Set("sprint-forever", "Sprint Forever", "1.0.0"), Set("hotbar-plus", "Hotbar Plus", "0.5.0"), Set("fast-travel-anywhere", "Fast Travel Anywhere", "2.1.0"), Set("night-sky", "Night Sky", "1.1.0") },
        });
        Write(Path.Combine(kiln, "state", "shroudforge.json"), new { format = 1, side = "client", mode = "hosted", reason = "", updated = Now / 1000, modsDir = Path.Combine(root, "mods") });
        foreach (var (id, name) in new[] { ("sfpkg-core", "SF Pack Core"), ("shroud-timer", "Shroud Timer"), ("builders-grid", "Builder's Grid") })
            Write(Path.Combine(root, "library", "shroudforge", id, id == "sfpkg-core" ? "1.0.0" : id == "shroud-timer" ? "0.8.0" : "1.3.1", "extended.mod.json"), new { id, name, enabled = true });
        return kilnMods;
    }

    // Many things wrong at once: failed mods in three engines, a missing folder, an engine that is not installed, Ember's
    // waiting mods, a stale report, a malformed one.
    private static IReadOnlyList<KilnModInput> Problems(string root)
    {
        var kiln = Folder(root, "kiln");
        var kilnMods = new List<KilnModInput>
        {
            K(root, "frost-edge", "Frost Edge", "1.4.2", ModSide.Both, ModState.Loaded, 0),
            K(root, "storage-labels", "Storage Labels", "0.9.3", ModSide.Client, ModState.Failed, -1,
                "Needs Quick Stack 2.0 or newer, but Quick Stack is not installed.", new ModDependency("quick-stack", ">=2.0", false)),
            K(root, "boss-rush", "Boss Rush", "0.2.0", ModSide.Both, ModState.Failed, -1,
                "OnLoad threw System.InvalidOperationException: the creature table has no entry 'fell_ogre_02'. The mod may be made for another game version."),
            K(root, "weather-control", "Weather Control", "1.1.0", ModSide.Both, ModState.Disabled, -1, "Switched off after 5 errors in 10 seconds (NullReferenceException in Tick)."),
        };
        var profileFile = Path.Combine(kiln, "profile.json");
        Write(profileFile, new
        {
            format = 1, id = "ember-run", name = "Ember run", generation = 9,
            mods = new Dictionary<string, object[]>
            {
                ["kiln"] =
                [
                    .. kilnMods.Select(m => (object)new { id = m.Id, version = m.Version, path = m.Directory }),
                    Entry(root, "kiln", "lost-mod", "1.0.0", create: false),
                ],
                ["emf"] = [Entry(root, "emf", "beastmaster", "0.3.3"), Entry(root, "emf", "pet-wheel", "1.0.2")],
                ["shroudforge"] = [Entry(root, "shroudforge", "shroud-timer", "0.8.0")],
                ["ember"] = [Entry(root, "ember", "server-motd", "0.1.0"), Entry(root, "ember", "vote-kick", "0.2.0")],
                ["greeneye"] = [Entry(root, "greeneye", "fps-unlock", "2.0.0")],
                ["flame"] = [Entry(root, "flame", "lantern-flame", "1.0.0")],
            },
        });
        Write(Path.Combine(kiln, "state", "boot.json"), new
        {
            format = 1, pid = Pid, side = "client", kiln = "0.2.0-dev",
            profile = new
            {
                mode = "active", id = "ember-run", name = "Ember run", file = profileFile,
                missing = new[] { new { kind = "kiln", id = "lost-mod", path = Path.Combine(root, "library", "kiln", "lost-mod", "1.0.0") } },
            },
            chain = new[] { new { path = Path.Combine(root, "old-fps-fix.dll"), source = "chain", status = "missing", detail = "", error = "the file is not there" } },
            plugins = new[] { new { id = "kiln-emf", file = "kiln-emf.dll", status = "loaded", error = (string?)null } },
        });
        Write(Path.Combine(kiln, "state", "emf.json"), new
        {
            format = 1, plugin = "kiln-emf", steppedAside = false, steppedAsideReason = "", emfIdle = "", updatedUnixMs = Now,
            mods = new object[]
            {
                new { id = "beastmaster", name = "Beastmaster", version = "0.3.3", status = "rejected", reason = "needs EMF API 29, this is 27: update Kiln" },
                new { id = "pet-wheel", name = "Pet Wheel", version = "1.0.2", status = "loaded", reason = "" },
            },
        });
        Write(Path.Combine(kiln, "state", "shroudforge.json"), new { format = 1, mode = "not-installed", reason = "no ShroudForge runtime found", updated = Now / 1000 });
        Write(Path.Combine(kiln, "state", "ember.json"), new
        {
            format = 1, plugin = "kiln-ember", abi = (object?)null, steppedAside = false, updatedUnixMs = Now,
            mods = new[] { new { id = "server-motd", status = "waiting-for-abi", reason = "" }, new { id = "vote-kick", status = "waiting-for-abi", reason = "" } },
        });
        // a report from yesterday's start, and one cut off mid-write
        Write(Path.Combine(kiln, "state", "greeneye.json"), new { format = 1, updatedUnixMs = Now - 86_400_000, mods = new[] { new { id = "fps-unlock", status = "loaded" } } });
        File.WriteAllText(Path.Combine(kiln, "state", "flame.json"), "{\"format\": 1, \"mods\": [{\"id\": \"lantern-fl");
        return kilnMods;
    }

    private static IReadOnlyList<KilnModInput> Empty(string root)
    {
        var kiln = Folder(root, "kiln");
        var profileFile = Path.Combine(kiln, "profile.json");
        Write(profileFile, new { format = 1, id = "vanilla", name = "Vanilla", generation = 1, mods = new { } });
        Boot(kiln, profileFile);
        return [];
    }
}
