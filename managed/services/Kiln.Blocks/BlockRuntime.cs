// SPDX-License-Identifier: MIT
// What blocks need while the game runs:
//  - config values and tooltip rows of items they made (a pack document, applied at every content application)
//  - weapons' OnHit: a program that follows the wielder. The game starts an item's own program only from equipment.impactEntity,
//    which every weapon needs for its stats (Base_Impact_Equipment), and perk programs only once an enhancement is unlocked; so Kiln
//    runs it beside them, for as long as the item is in an equipment slot, reading the game's own OnHit stream.
using System.Text.Json.Nodes;

namespace Kiln;

internal static class BlockRuntime
{
    private static readonly object Gate = new();
    private static readonly Dictionary<uint, (Guid Program, string Name)> Weapons = new();
    private static readonly Dictionary<(uint Actor, uint Item), EntityId> Running = new();
    private static readonly HashSet<(uint Actor, uint Item)> Starting = new();
    private static IDisposable? _tick;
    private static bool _checked;

    private const int EquipmentSlots = 11, SlotStride = 0x48;

    public static void AddWeapon(uint itemId, Guid program, string name)
    {
        lock (Gate)
        {
            Weapons[itemId] = (program, name);
            _tick ??= BlocksBridge.AddTick("kiln blocks", OnTick);
        }
    }

    public static void AddRules(ModBlocks mod, string section, string entry, JsonObject rules)
    {
        lock (Gate) mod.ReadyRules.Add((section, entry, rules));
        Hook(mod);
    }

    /// <summary>A program with values only the running game knows: finished and registered when content is ready.</summary>
    public static void AddLate(ModBlocks mod, Guid program)
    {
        lock (Gate) mod.LatePending.Add(program);
        Hook(mod);
    }

    /// <summary>A clone's reference to such a program: set once the program is there.</summary>
    public static void AddLateRule(ModBlocks mod, string section, string entry, string path, Guid program)
    {
        lock (Gate) mod.LateRules.Add((section, entry, path, program));
        Hook(mod);
    }

    /// <summary>A status with the icon of a game status: the icon is the game's data, read when content is ready.</summary>
    public static void AddIcon(ModBlocks mod, string buff, string source, string block)
    {
        lock (Gate) mod.Icons.Add((buff, source, block));
        Hook(mod);
    }

    private static void Hook(ModBlocks mod)
    {
        lock (Gate)
        {
            if (mod.ReadyHooked) return;
            mod.ReadyHooked = true;
        }
        BlocksBridge.OnReady(() => ApplyRules(mod));
    }

    private static Guid? ResolveLate(LateRef r)
    {
        if (r.Kind != "status") return Content.Templates.Get(r.Value)?.Guid;
        // the game's data has only a buff's id; its GUID is known when something refers to it. The natives only ever hash the reference to the id
        // (applyBuff [dis]: FNV-1a of the 16 bytes, then getBuffType), so any 16 bytes with that hash are the reference to it.
        if (Content.Buffs.Get(r.Value) is not { } buff) return null;
        return buff.Guid != Guid.Empty ? buff.Guid : BlockIds.WithFnv(buff.Id);
    }

    private static void FinishLate(ModBlocks mod, Action<string> problem)
    {
        List<Guid> todo;
        lock (Gate)
        {
            todo = mod.LatePending.ToList();
            mod.LatePending.Clear();
        }
        foreach (var guid in todo)
        {
            if (!mod.LateBuilders.TryGetValue(guid, out var b)) continue;
            var blob = b.Finish(guid, ResolveLate, out var failed);
            if (blob == null)
            {
                lock (Gate) mod.LateFailed.Add(guid);
                problem($"block {failed!.Block}, {failed.Path}: the game has no {failed.Kind} \"{failed.Value}\"");
                continue;
            }
            if (mod.Programs.TryGetValue(guid, out var had)) mod.Programs[guid] = (had.Key, blob, had.Listing);
            Assets.RegisterAs(mod.Owner, new AssetKey(guid, 0xd4922d88u), blob);
            BlocksBridge.Log(LogLevel.Info, $"blocks: {mod.Id}: program {had.Key} finished with the game's values");
        }
        if (todo.Count > 0 && Abilities.Available) Abilities.Programs.Refresh();
    }


    // Done once every clone of the mod is in the world: a rule can then name a clone.
    private static bool ApplyRules(ModBlocks mod)
    {
        if (!Mods.IsLoaded(mod.Id)) return true;
        var prefix = mod.Id + ".";
        var clones = Content.GameDataDefinitions().Where(d => d.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (clones.Any(d => d.State is "pending" or "waiting")) return false;
        CheckBalancing();
        void Problem(string text)
        {
            BlocksBridge.Log(LogLevel.Error, $"blocks: {mod.Id}: {text}");
            lock (mod.Errors) mod.Errors.Add(text);
            mod.ReportDirty = true;
        }
        FinishLate(mod, Problem);
        var doc = new JsonObject { ["format"] = 1 };
        lock (Gate)
        {
            foreach (var (section, entry, rules) in mod.ReadyRules)
            {
                var sec = (doc[section] ??= new JsonObject()).AsObject();
                if (sec[entry] is JsonObject have)
                    foreach (var (k, v) in rules) have[k] = v?.DeepClone();
                else sec[entry] = rules.DeepClone();
            }
            foreach (var (section, entry, path, program) in mod.LateRules)
            {
                if (mod.LateFailed.Contains(program)) continue;
                var sec = (doc[section] ??= new JsonObject()).AsObject();
                ((sec[entry] ??= new JsonObject()).AsObject())[path] = program.ToString();
            }
        }
        List<(string Buff, string Source, string Block)> icons;
        lock (Gate) icons = mod.Icons.ToList();
        foreach (var (buff, source, block) in icons)
        {
            if (Content.Buffs.Get(source)?.GetJson("icon")?["value"] is not { } icon)
            {
                var text = $"block {block}, icon: the game has no status \"{source}\"";
                bool known;
                lock (mod.Errors) known = mod.Errors.Contains(text);
                if (!known) Problem(text);
                continue;
            }
            var sec = (doc["buffs"] ??= new JsonObject()).AsObject();
            ((sec[buff] ??= new JsonObject()).AsObject())["icon"] = icon.DeepClone();
        }
        var report = BlocksBridge.ApplyAs(mod.Owner, doc.ToJsonString(), $"blocks of {mod.Id}");
        foreach (var e in report.Errors) Problem(e);
        // what kiln.content and kiln.gamedata could not take of a definition: a block's author reads it here, by name. An item's reference
        // to a clone is retried every 2 s: look when that has passed.
        BlocksBridge.After(150, () =>
        {
            foreach (var d in Content.GameDataDefinitions().Where(d => d.Name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                if (d.Error is { Length: > 0 } err) Problem($"{d.Name}: {err}");
                foreach (var w in d.Warnings) Problem($"{d.Name}: {w}");
            }
            foreach (var d in NewContent.Definitions().Where(d => !d.IsPlaceholder && d.Name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                if (d.Problem is { Length: > 0 } err) Problem($"{d.Name}: {err}");
                foreach (var w in d.Warnings) Problem($"{d.Name}: {w}");
            }
            WarnRenames(mod, prefix);
        });
        return true;
    }

    // A block's ids come from its name, so a rename without Was is a new item, and what the world holds of the old one stays as a placeholder.
    private static void WarnRenames(ModBlocks mod, string prefix)
    {
        var gone = NewContent.Placeholders().Where(d => d.Mod == mod.Id && d.Name.StartsWith(prefix, StringComparison.Ordinal)).Select(d => d.Name[prefix.Length..]).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (gone.Count == 0) return;
        var text = $"this world holds items no block of {mod.Id} makes any more: {string.Join(", ", gone.Take(8))}{(gone.Count > 8 ? $" and {gone.Count - 8} more" : "")}. " +
                   $"They stay as [Missing: ...] so nothing is lost. If you renamed the block, say Was = \"{gone[0]}\" on it (\"was\" in JSON): saves keep it";
        lock (mod.Warnings)
        {
            if (mod.Warnings.Contains(text)) return;
            mod.Warnings.Add(text);
        }
        mod.ReportDirty = true;
        BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod.Id}: {text}");
    }

    // The two numbers a spell's and a weapon's power follow from are the game's. A game update that changes them must not silently
    // change what authors wrote: it is read once and reported.
    private static void CheckBalancing()
    {
        if (_checked) return;
        _checked = true;
        try
        {
            var b = Assets.ReadResource(new AssetKey(new Guid("82706b40-61b1-4b8f-8b23-dcec6971bda1"), "keen::BalancingTable"));
            if (b is not { Length: >= 0x2c }) return;
            var seed = BitConverter.ToUInt32(b, 0);
            var creep = BitConverter.ToSingle(b, 0x28);
            if (seed != Compiler.Seed || Math.Abs(creep - Compiler.Creep) > 1e-6)
                BlocksBridge.Log(LogLevel.Warn, $"blocks: the game's damage curve changed (seed {seed}, creep {creep}; Kiln.Blocks was made for {Compiler.Seed}, {Compiler.Creep}): tooltips of blocks are off until Kiln follows");
        }
        catch (Exception e)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: cannot read the game's damage curve: {e.Message}");
        }
    }

    // What `kiln blocks` reads: each mod's blocks as they were lowered, with their programs as listings and what went wrong.
    internal static void WriteReports()
    {
        foreach (var mod in Blocks.AllMods)
        {
            if (!mod.ReportDirty) continue;
            mod.ReportDirty = false;
            try
            {
                var blocks = new JsonObject();
                foreach (var (name, lowered) in mod.Lowered.OrderBy(x => x.Key, StringComparer.Ordinal)) blocks[name] = lowered.DeepClone();
                var programs = new JsonObject();
                foreach (var (guid, (key, _, listing)) in mod.Programs.OrderBy(x => x.Value.Key, StringComparer.Ordinal))
                    programs[guid.ToString()] = new JsonObject { ["key"] = key, ["listing"] = listing };
                JsonArray Texts(List<string> list)
                {
                    lock (list) return new JsonArray(list.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                }
                var doc = new JsonObject { ["format"] = 1, ["mod"] = mod.Id, ["blocks"] = blocks, ["programs"] = programs, ["errors"] = Texts(mod.Errors), ["warnings"] = Texts(mod.Warnings) };
                var file = BlocksBridge.BlockReportFile(mod.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod.Id}: the report for `kiln blocks` was not written: {e.Message}");
            }
        }
    }

    /// <summary>What the content sync sends a Kiln client. For tests.</summary>
    internal static string? SyncExport() => BlocksBridge.SyncExport();

    // ---- sounds: a program says kiln.Signal(position, tag), the server relays the cue to Kiln clients near it ------------------------------------

    private static readonly Dictionary<uint, (string Mod, string Sound)> Sounds = new();
    private static IDisposable? _signals;
    private const uint SoundTagBit = 0xB10C0000;

    public static uint SoundTag(string mod, string sound)
    {
        var tag = SoundTagBit | (BlockIds.Fnv(System.Text.Encoding.UTF8.GetBytes($"{mod}|{sound}")) & 0xFFFF);
        lock (Gate)
        {
            Sounds[tag] = (mod, sound);
        }
        return tag;
    }

    // From the service's own tick, so that the subscription is the service's: a mod's would end with the mod, and other mods' sounds with it.
    internal static void SubscribeSignals()
    {
        lock (Gate)
            if (Sounds.Count > 0) _signals ??= Events.On<ImpactSignalEvent>(OnSignal);
    }

    private static void OnSignal(ImpactSignalEvent e)
    {
        if ((e.Tag & 0xFFFF0000) != SoundTagBit || e.Value.Length < 12) return;
        (string Mod, string Sound) s;
        lock (Gate)
            if (!Sounds.TryGetValue(e.Tag, out s)) return;
        try
        {
            if (!Sound.Available) return;
            var colon = s.Sound.IndexOf(':');
            var cue = Guid.TryParse(s.Sound, out var guid) ? SoundId.Of(guid)
                : colon > 0 ? SoundId.Named(s.Sound[..colon], s.Sound[(colon + 1)..]) : SoundId.Named(s.Mod, s.Sound);
            Sound.PlayAt(cue, new WorldPosition(BitConverter.ToSingle(e.Value, 0), BitConverter.ToSingle(e.Value, 4), BitConverter.ToSingle(e.Value, 8)));
        }
        catch (Exception ex)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: {s.Mod}: the sound \"{s.Sound}\" did not play: {ex.Message}");
        }
    }

    private static void OnTick(ulong step)
    {
        if (step % 6 != 2 || !Spawn.WorldRunning) return;
        Dictionary<uint, (Guid Program, string Name)> weapons;
        lock (Gate) weapons = new(Weapons);
        if (weapons.Count == 0) return;
        var seen = new HashSet<(uint, uint)>();
        foreach (var actor in World.Find("Equipment"))
        {
            var eq = World.ReadBytes(actor, "Equipment");
            if (eq is null) continue;
            for (var slot = 0; slot < EquipmentSlots; slot++)
            {
                var at = 8 + slot * SlotStride + 8;
                if (at + 4 > eq.Length) break;
                var item = BitConverter.ToUInt32(eq, at);
                if (item == 0 || !weapons.TryGetValue(item, out var w)) continue;
                var key = (actor.Value, item);
                seen.Add(key);
                if (!Running.ContainsKey(key) && Starting.Add(key)) _ = Start(actor, key, w.Program, w.Name);
            }
        }
        foreach (var (key, entity) in Running.ToArray())
        {
            if (seen.Contains(key)) continue;
            Running.Remove(key);
            if (World.Exists(entity)) Spawn.Destroy(entity);
        }
    }

    private static async Task Start(EntityId actor, (uint, uint) key, Guid program, string name)
    {
        try
        {
            var at = World.Position(actor);
            if (at is null) return;
            var e = await Abilities.RunProgram(program, at.Value, new ImpactOptions { Root = actor, Tag = "blocks-onhit" });
            Running[key] = e;
        }
        catch (Exception ex)
        {
            BlocksBridge.Log(LogLevel.Error, $"blocks: {name}: the OnHit program did not start on {actor}: {ex.Message}");
        }
        finally
        {
            Starting.Remove(key);
        }
    }

    /// <summary>For tests: the entity running a weapon's OnHit for an actor, or none.</summary>
    public static EntityId OnHitEntity(EntityId actor, uint item) => Running.TryGetValue((actor.Value, item), out var e) ? e : EntityId.None;
}
