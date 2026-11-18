// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Kiln.Host;

// Optional native service access: Kiln.Host must not acquire a reference to Kiln.Gameplay.
internal static class GameplayBuildDemand
{
    // Optional service facades that themselves consume gameplay events/rules. Keep this explicit: following every
    // shared Kiln.* dependency would make an unrelated facade count as using every service.
    internal const uint HostReady=0; // KilnGameplayApi.request_hooks announcement; no hook bits
    private static uint GroupsForReference(string? name) => name switch {
        "Kiln.Gameplay" => 31u,
        "Kiln.Creatures" => 8u, // CreatureVariants natural-spawn events/replacements
        // Kiln.Rules: none. Its BuildEvent (KeepBuildsOutsideBases) is followed only while an applied rule set keeps
        // builds, and asks at that moment (a safe Pre while a world runs); most rules mods never touch building.
        "Kiln.Quests" => 7u, // craft/build objectives
        "Kiln.Blocks" => 15u, // NPC variants and declarative craft/build objectives
        _ => 0u,
    };
    internal static uint GroupsFor(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Aggregate(0u,(mask,n)=>mask|GroupsForReference(n.Name));

    internal static unsafe void Request(IHostServices host, ulong owner, uint demand=31)
    {
        var table = (byte*)host.Service("kiln.gameplay", 1);
        // KilnGameplayApi: request_build stays at 128, request_hooks follows at 136.
        var offset = 8 + 15 * sizeof(nint);
        if (table == null || *(uint*)table < offset + sizeof(nint) || *(uint*)(table + 4) < 1) return;
        if (*(uint*)table >= 144) {
            var groups = *(delegate* unmanaged[Cdecl]<ulong, uint, int>*)(table + 136);
            if (groups != null) {
                if (groups(owner,demand) != 0) host.Log(LogLevel.Warn,$"mod owner {owner}: gameplay hook demand refused (see group capability rows)");
                return;
            }
        }
        if((demand & 1)==0) return; // Old service already installs the four other groups at startup.
        var request = *(delegate* unmanaged[Cdecl]<ulong, int>*)(table + offset);
        if (request != null && request(owner) != 0)
            host.Log(LogLevel.Warn, $"mod owner {owner}: gameplay.build_hooks request refused: {Kiln.Capabilities.Why("gameplay.build_hooks")}");
    }

    // Cache only metadata, not dependency closure: exports differ between admitted mods. A changed identity is read
    // again. Bounds make uncertain/oversized input conservative demand, never evidence that no hooks are needed.
    internal sealed class MetadataPass
    {
        private readonly record struct Identity(string Path,long Length,long Written,long Created);
        private readonly Dictionary<Identity,string[]?> _cache=new();
        private long _bytes;
        internal int AssembliesRead { get; private set; }
        internal string[] References(string path)
        {
            path=Path.GetFullPath(path);
            if(OperatingSystem.IsWindows()) path=path.ToUpperInvariant();
            var info=new FileInfo(path);
            var key=new Identity(path,info.Length,info.LastWriteTimeUtc.Ticks,info.CreationTimeUtc.Ticks);
            if(_cache.TryGetValue(key,out var cached))
                return cached ?? throw new BadImageFormatException("cached unreadable assembly: "+path);
            if(AssembliesRead>=4096 || key.Length>64L*1024*1024 || _bytes+key.Length>256L*1024*1024)
                throw new IOException("gameplay metadata pass budget reached (4096 files / 64 MiB per file / 256 MiB total)");
            ++AssembliesRead; _bytes+=key.Length;
            try {
                using var stream=File.OpenRead(path);
                using var pe=new PEReader(stream);
                var refs=pe.HasMetadata ? ReadReferences(pe.GetMetadataReader()) : [];
                info.Refresh();
                if(!info.Exists || info.Length!=key.Length || info.LastWriteTimeUtc.Ticks!=key.Written || info.CreationTimeUtc.Ticks!=key.Created)
                    throw new IOException("assembly changed during gameplay metadata pass: "+path);
                _cache[key]=refs;
                return refs;
            } catch { _cache[key]=null; throw; }
        }
        /// <summary>Every assembly name the assemblies read so far reference.</summary>
        internal string[] ReferencedNames() =>
            _cache.Values.Where(v => v is not null).SelectMany(v => v!).Distinct(StringComparer.Ordinal).ToArray();
        private static string[] ReadReferences(MetadataReader metadata) => metadata.AssemblyReferences
            .Select(h=>metadata.GetString(metadata.GetAssemblyReference(h).Name)).ToArray();
    }

    // Startup demand must precede the first world even on dedicated servers, whose ordinary OnLoad runs at Pre.
    // Inspect metadata only: do not instantiate a mod or run its module initializer/OnLoad earlier.
    internal static uint GroupsFor(string entry, IEnumerable<string> exportedAssemblies, MetadataPass? pass=null)
    {
        pass ??= new MetadataPass();
        var pending = new Queue<string>(); pending.Enqueue(entry);
        // A shipped helper can be loaded by name in OnLoad, without an AssemblyRef from the entry assembly.
        // Conservatively inspect those helpers too, before the first world; actual loads are checked again.
        int helpers=0;
        foreach (var helper in Directory.EnumerateFiles(Path.GetDirectoryName(entry)!, "*.dll"))
        {
            if(++helpers>4096) throw new IOException("gameplay preflight helper-count budget reached");
            var name = Path.GetFileName(helper);
            if (name.StartsWith("Kiln.", StringComparison.Ordinal) &&
                File.Exists(Path.Combine(Path.GetDirectoryName(typeof(ModManager).Assembly.Location)!, name))) continue;
            pending.Enqueue(helper);
        }
        uint demand=0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var exports = exportedAssemblies.ToArray();
        while (pending.TryDequeue(out var path))
        {
            if (!seen.Add(path)) continue;
            foreach (var name in pass.References(path))
            {
                demand |= GroupsForReference(name);
                if(demand==31) return demand;
                if (name != Path.GetFileName(name) || name.Contains('\\')) continue;
                // Match the loader: shared Kiln.* assemblies are provided by the host, not the mod's own folder.
                if (name.StartsWith("Kiln.", StringComparison.Ordinal) &&
                    File.Exists(Path.Combine(Path.GetDirectoryName(typeof(ModManager).Assembly.Location)!, name + ".dll"))) continue;
                var own = Path.Combine(Path.GetDirectoryName(path)!, name + ".dll");
                if (File.Exists(own)) pending.Enqueue(own);
                else if (exports.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase) && File.Exists(p)) is { } exported)
                    pending.Enqueue(exported);
            }
        }
        return demand;
    }
}

internal sealed partial class ModManager
{
    internal void PrepareGameplayHooks()
    {
        // Zero is an ABI-pinned announcement, not demand. Old tables ignore it through the size-checked adapter.
        GameplayBuildDemand.Request(_host,0,GameplayBuildDemand.HostReady);
        var timer=Stopwatch.StartNew();
        var metadata=new GameplayBuildDemand.MetadataPass();
        var candidates = BootCandidates(); // Same profile/disabled/duplicate rules as the boot admission pass.
        var roles = ResolveBootRoles(candidates, out _);
        var admitted = candidates.Where(c => roles.ContainsKey(c.Directory)).ToArray();
        _bootAdmitted = admitted;  // and the references read below: what ModPrefetch reads ahead of the first load
        foreach (var (manifest, directory) in admitted)
        {
            try
            {
                var dependencies = new HashSet<string>(StringComparer.Ordinal);
                void AddDependencies(Manifest m) {
                    foreach (var d in m.Dependencies)
                        if (dependencies.Add(d.Id) && admitted.FirstOrDefault(c => c.Manifest.Id == d.Id).Manifest is { } dependency)
                            AddDependencies(dependency);
                }
                AddDependencies(manifest);
                uint demand=Directory.Exists(Path.Combine(directory,"blocks")) ? 15u : 0u;
                if (Directory.Exists(Path.Combine(directory,"quests"))) demand |= 7u;
                if(manifest.HasCode) demand |= GameplayBuildDemand.GroupsFor(Path.Combine(directory, manifest.EntryAssembly),
                        admitted.Where(c => dependencies.Contains(c.Manifest.Id))
                            .SelectMany(c => c.Manifest.Exports.Select(f => Path.Combine(c.Directory, f))),metadata);
                if(demand==0) continue;
                var owner = _host.OwnerCreate("mod:" + manifest.Id);
                try { GameplayBuildDemand.Request(_host, owner,demand); }
                finally { _host.OwnerRevoke(owner); }
            }
            catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                _host.Log(LogLevel.Warn, $"mod {manifest.Id}: gameplay preflight could not read assembly references: {e.Message}; conservatively requesting all hook groups before the world");
                // Unknown metadata must not discard demand or defer building protection until the next world.
                var owner = _host.OwnerCreate("mod:" + manifest.Id);
                try { GameplayBuildDemand.Request(_host, owner); }
                finally { _host.OwnerRevoke(owner); }
            }
        }
        _bootReferences = metadata.ReferencedNames();
        _host.Log(LogLevel.Info,$"gameplay pre-world demand: {metadata.AssembliesRead} assemblies read in {timer.Elapsed.TotalMilliseconds:F1} ms");
    }
}
