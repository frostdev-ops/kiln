// SPDX-License-Identifier: MIT
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Kiln.Host;

namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private readonly List<string> _staged = [];
    private readonly Dictionary<string, ModHandle> _handles = new(StringComparer.Ordinal);

    /// <summary>
    /// Loads a second, isolated copy of the assembly that holds <typeparamref name="TMod"/>, as the game does: its files go
    /// into <c>kiln/mods/&lt;id&gt;</c> and Kiln.Host's loader loads them from there into a collectible load context of
    /// their own. Before the first step the mod waits there and loads with the others (dependency order); after it, this
    /// is a hot load, like the control command <c>load</c>, and its <c>OnLoad</c> has run when the call returns.
    /// </summary>
    /// <remarks>
    /// <para><b>The manifest</b> is <paramref name="manifest"/> when given (<c>entryAssembly</c> and <c>entryType</c> are
    /// added), else the mod's own file: <c>&lt;Assembly&gt;.kiln.mod.json</c> next to the assembly, or that folder's
    /// <c>kiln.mod.json</c> when its <c>entryAssembly</c> is this assembly. A missing <c>entryType</c> is set to
    /// <typeparamref name="TMod"/>, so an assembly with several mods loads the one named.</para>
    /// <para><b>The files</b> taken from the assembly's folder (a test project's output, among other files): the assembly
    /// and its <c>.pdb</c>; its private dependencies, i.e. the assemblies it references (transitively) that lie in the same
    /// folder, except Kiln.Api and Kiln's shared assemblies (<c>Kiln.*</c> beside Kiln.Host), which every mod shares as in the
    /// game; and its texts: <see cref="ModManifest.LocalesDirectory"/>, else <c>&lt;Assembly&gt;.locales/</c>, else the
    /// folder's <c>locales/</c> when the folder's <c>kiln.mod.json</c> is this mod's. Anything else a mod folder holds:
    /// build it as one and use <see cref="LoadFolder"/>.</para>
    /// <para>The handle gives the instance as <see cref="Kiln.Mod"/>: the copy's types are not the test project's.</para>
    /// </remarks>
    /// <exception cref="ModLoadException">A hot load the loader refused, or whose <c>OnLoad</c> threw.</exception>
    public ModHandle Load<TMod>(ModManifest? manifest = null) where TMod : Mod
    {
        ThrowIfDisposed();
        var type = typeof(TMod);
        var assembly = type.Assembly;
        if (string.IsNullOrEmpty(assembly.Location)) throw new InvalidOperationException($"{assembly.GetName().Name} was not loaded from a file: the harness copies the mod's files");
        var source = Path.GetDirectoryName(assembly.Location)!;
        var name = assembly.GetName().Name!;
        var entry = name + ".dll";
        var ownFolder = false;
        JsonObject json;
        if (manifest is not null)
        {
            json = (JsonObject)JsonNode.Parse(manifest.ToJson(entry, type.FullName))!;
        }
        else
        {
            var named = Path.Combine(source, name + ".kiln.mod.json");
            var plain = Path.Combine(source, Manifest.FileName);
            string file;
            if (File.Exists(named)) file = named;
            else if (File.Exists(plain) && EntryOfFile(plain) is { } e && string.Equals(e, entry, StringComparison.OrdinalIgnoreCase))
            {
                file = plain;
                ownFolder = true;
            }
            else
                throw new FileNotFoundException($"no manifest for {name}: pass a ModManifest, or put {name}.kiln.mod.json next to {entry} " +
                                                $"(or a {Manifest.FileName} whose entryAssembly is {entry}) in {source}");
            json = JsonNode.Parse(File.ReadAllText(file), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new InvalidDataException($"{file}: the manifest must be one JSON object");
            if (json["entryAssembly"] is { } ea && !string.Equals(ea.ToString(), entry, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{file}: entryAssembly is {ea}, but {type.FullName} is in {entry}");
            json["entryAssembly"] = entry;
            if (json["entryType"] is { } et && et.ToString() != type.FullName)
                throw new InvalidDataException($"{file}: entryType is {et}, not {type.FullName}");
            json["entryType"] = type.FullName;
        }
        var id = json["id"]?.ToString() is { Length: > 0 } i ? i : name.ToLowerInvariant();
        var dir = NewModFolder(id);
        File.WriteAllText(Path.Combine(dir, Manifest.FileName), json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        CopyAssembly(Path.Combine(source, entry), dir);
        foreach (var dependency in PrivateDependencies(source, Path.Combine(source, entry)))
            CopyAssembly(dependency, dir);
        var locales = manifest?.LocalesDirectory is { } l ? l
            : Directory.Exists(Path.Combine(source, name + ".locales")) ? Path.Combine(source, name + ".locales")
            : ownFolder ? Path.Combine(source, "locales") : null;
        if (locales is not null && Directory.Exists(locales)) CopyTree(locales, Path.Combine(dir, "locales"));
        return Admit(id, dir);
    }

    /// <summary>Loads a built mod folder (its <c>kiln.mod.json</c>, assemblies, <c>locales/</c> and data), copied into
    /// <c>kiln/mods/&lt;id&gt;</c> as Kiln's deploy copies one: without <c>*.deps.json</c>, <c>*.runtimeconfig.json</c> and
    /// Kiln's own <c>Kiln.*</c> assemblies, unless the manifest names one as its entry or an export. Before the first step
    /// it waits for the start; after it, a hot load.</summary>
    /// <exception cref="ModLoadException">A hot load the loader refused, or whose <c>OnLoad</c> threw.</exception>
    public ModHandle LoadFolder(string path)
    {
        ThrowIfDisposed();
        var source = Path.GetFullPath(path);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"no mod folder {source}");
        var manifestFile = Path.Combine(source, Manifest.FileName);
        if (!File.Exists(manifestFile)) throw new FileNotFoundException($"{source} has no {Manifest.FileName}", manifestFile);
        var json = JsonNode.Parse(File.ReadAllText(manifestFile), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        var id = json?["id"]?.ToString() is { Length: > 0 } i ? i : Path.GetFileName(source);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (json?["entryAssembly"]?.ToString() is { } entry) keep.Add(entry);
        if (json?["exports"] is JsonArray exports)
            foreach (var x in exports)
                if (x?.ToString() is { } e) keep.Add(e);
        var dir = NewModFolder(id);
        CopyTree(source, dir, file =>
        {
            var n = Path.GetFileName(file);
            if (n.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (!n.StartsWith("Kiln.", StringComparison.Ordinal) || Path.GetDirectoryName(file) != source) return true;
            var stem = Path.GetFileNameWithoutExtension(n);
            return keep.Contains(stem + ".dll");
        });
        return Admit(id, dir);
    }

    /// <summary>The handle of a mod this harness was given (by id).</summary>
    public ModHandle Mod(string id)
    {
        lock (_handles) return _handles.TryGetValue(id, out var h) ? h : throw new KeyNotFoundException($"the harness has no mod {id}");
    }

    /// <summary>
    /// Unloads <paramref name="mod"/>, and first every mod that depends on it (reverse order), as the control command
    /// <c>unload</c> does. With <paramref name="checkCollected"/> (default) the call then makes sure the load contexts of
    /// everything unloaded are gone, and throws <see cref="ModLeakException"/> naming the mod when one is still alive.
    /// </summary>
    public void Unload(ModHandle mod, bool checkCollected = true)
    {
        ArgumentNullException.ThrowIfNull(mod);
        var id = mod.Id;
        Call(() =>
        {
            var r = UnloadCore(id);
            if (!r.Ok) throw new InvalidOperationException(r.Message);
            if (checkCollected) CheckCollected();
        });
    }

    /// <summary>
    /// Reloads <paramref name="mod"/> from its folder with its dependents (they unload first and load again after it), as
    /// the control command <c>reload</c> does; <see cref="ModReloadedEvent"/> follows. With
    /// <paramref name="checkCollected"/> (default) the old instances' load contexts must be gone afterwards, else
    /// <see cref="ModLeakException"/>. Returns the same handle, which now reads the new instance.
    /// </summary>
    /// <exception cref="ModLoadException">The mod or a dependent did not load again.</exception>
    public ModHandle Reload(ModHandle mod, bool checkCollected = true)
    {
        ArgumentNullException.ThrowIfNull(mod);
        var id = mod.Id;
        Call(() =>
        {
            var r = ReloadCore(id);
            if (!r.Ok) throw new ModLoadException(id, EntryOf(id)?.State ?? ModState.Failed, Explain(r.Message));
            if (checkCollected) CheckCollected();
        });
        return mod;
    }

    // ---- on the tick thread; each its own frame, so nothing of an unloaded mod is on the stack when the GC runs ----

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ModOperationResult UnloadCore(string id)
    {
        if (!_modsLoaded) throw new InvalidOperationException($"mod {id} is not loaded yet: the server loads its mods at the first step");
        return _mods.Execute(ModOperation.Unload, id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ModOperationResult ReloadCore(string id)
    {
        if (!_modsLoaded) throw new InvalidOperationException($"mod {id} is not loaded yet: the server loads its mods at the first step");
        return _mods.Execute(ModOperation.Reload, id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ModOperationResult HotLoadCore(string folder) => _mods.Execute(ModOperation.Load, folder);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CheckCollected()
    {
        if (_mods.CollectAndCountLeaks(out var leaked) > 0) throw new ModLeakException(leaked);
    }

    private ModHandle Admit(string id, string dir)
    {
        var handle = new ModHandle(this, id, dir);
        lock (_handles) _handles[id] = handle;
        Call(() =>
        {
            if (!_modsLoaded)
            {
                _staged.Add(id);
                return;
            }
            var r = HotLoadCore(Path.GetFileName(dir));
            if (!r.Ok) throw new ModLoadException(id, EntryOf(id)?.State ?? ModState.Failed, Explain(r.Message));
        });
        return handle;
    }

    // At the first step: the mods given before it that did not load.
    private void ThrowIfStagedFailed()
    {
        var failed = _staged.Select(id => (Id: id, Entry: EntryOf(id))).Where(x => x.Entry is not { IsLoaded: true }).ToList();
        _staged.Clear();
        if (failed.Count == 0) return;
        if (failed.Count == 1)
            throw new ModLoadException(failed[0].Id, failed[0].Entry?.State ?? ModState.Failed, Explain(failed[0].Entry?.Error ?? "the loader never saw it"));
        throw new ModLoadException(string.Join(", ", failed.Select(f => f.Id)), ModState.Failed,
            string.Join("; ", failed.Select(f => $"{f.Id}: {Explain(f.Entry?.Error ?? "the loader never saw it")}")));
    }

    /// <summary>The loader's reason, and for a feature it names, what the capability row says.</summary>
    private string Explain(string reason)
    {
        const string marker = "needs feature";
        var at = reason.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return reason;
        var list = reason[(at + marker.Length)..].TrimStart('s', ' ');
        var end = list.IndexOf(", unavailable", StringComparison.Ordinal);
        if (end >= 0) list = list[..end];
        var why = list.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(f => Capabilities.Why(f.Trim()));
        return $"{reason} ({string.Join(" ", why)})";
    }

    internal ModEntry? EntryOf(string id) => ((IModsBackend)_mods).Get(id);

    internal LoadedMod LiveOf(string id) =>
        _mods.Find(id) ?? throw new InvalidOperationException($"mod {id} is not running ({EntryOf(id)?.State.ToString().ToLowerInvariant() ?? "waiting for the first step"})");

    // ---- files ----

    private string NewModFolder(string id)
    {
        foreach (var c in id)
            if (c is '/' or '\\' || c == Path.DirectorySeparatorChar || id is "." or "..")
                throw new ArgumentException($"mod id '{id}' cannot name a folder", nameof(id));
        lock (_handles)
            if (_handles.ContainsKey(id)) throw new InvalidOperationException($"the harness already has a mod {id}");
        var dir = Path.Combine(ModsDirectory, id);
        if (Directory.Exists(dir)) throw new InvalidOperationException($"kiln/mods/{id} exists already");
        return Directory.CreateDirectory(dir).FullName;
    }

    private static string? EntryOfFile(string manifest)
    {
        try
        {
            return (JsonNode.Parse(File.ReadAllText(manifest), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject)?["entryAssembly"]?.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void CopyAssembly(string dll, string dir)
    {
        File.Copy(dll, Path.Combine(dir, Path.GetFileName(dll)), overwrite: true);
        var pdb = Path.ChangeExtension(dll, ".pdb");
        if (File.Exists(pdb)) File.Copy(pdb, Path.Combine(dir, Path.GetFileName(pdb)), overwrite: true);
    }

    private static void CopyTree(string from, string to, Func<string, bool>? take = null)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            if (take?.Invoke(file) ?? true)
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var sub in Directory.EnumerateDirectories(from))
            CopyTree(sub, Path.Combine(to, Path.GetFileName(sub)), take);
    }

    /// <summary>The assemblies <paramref name="entry"/> references, transitively, that lie in <paramref name="folder"/>
    /// and are the mod's own: not Kiln.Api and not one of Kiln's shared assemblies (<c>Kiln.*</c> beside Kiln.Host, which
    /// the loader takes from the host in the game too). Read from metadata; nothing is loaded.</summary>
    private static List<string> PrivateDependencies(string folder, string entry)
    {
        var hostDir = Path.GetDirectoryName(typeof(ModManager).Assembly.Location) ?? "";
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileNameWithoutExtension(entry) };
        var queue = new Queue<string>([entry]);
        while (queue.Count > 0)
            foreach (var name in References(queue.Dequeue()))
            {
                if (!seen.Add(name) || name == "Kiln.Api") continue;
                if (name.StartsWith("Kiln.", StringComparison.Ordinal) && File.Exists(Path.Combine(hostDir, name + ".dll"))) continue;
                var file = Path.Combine(folder, name + ".dll");
                if (!File.Exists(file)) continue;  // the framework, or a package the test project does not carry
                found.Add(file);
                queue.Enqueue(file);
            }
        return found;
    }

    private static IEnumerable<string> References(string dll)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return [];
        var md = pe.GetMetadataReader();
        return md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
    }
}
