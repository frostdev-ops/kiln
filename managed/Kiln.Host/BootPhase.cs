// SPDX-License-Identifier: MIT
// Mod.OnBoot's phase on the host side (chapter 08 §4.2-4.4; kiln.boot, native/core/abi/services/kiln_boot.h). Runs inside Kiln.Host's Init,
// on the boot thread, before the game's entry point: in a boot profile kiln.boot is collecting, and Kiln.Host is the owner that commits the
// one generation. Without the commit the core holds the entry point and ends the start (exit code 0x4B01).
//
//   1. The boot graph: the profile's enabled Kiln mods and the drop-ins (as LoadAll finds them) whose kiln.mod.json says "boot", with every
//      role this process may give them (a dedicated server: DedicatedServer; a game client: Client, and HostedServer when hosted server mods
//      are on). A required dependency that is missing or out excludes its dependent (ExcludedDependency); boot dependencies run first.
//   2. Each author: a transient instance in its own collectible load context, OnBoot once under its boot-author frame (kiln.boot
//      begin_owner/stage/finish_owner), then unloaded; later instances get OnLoad only. Its staged definitions are lowered at once
//      (Kiln.Blocks BootLowering.LowerOwner, by reflection: Kiln.Host does not reference Kiln.Blocks); a failure there is the author's.
//   3. The IR envelope (owners, their documents per collector) is staged as the host's and committed; the reports go to Mods.BootState.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kiln.Host;

[StructLayout(LayoutKind.Sequential)]
internal struct KilnBootCommitResultV1
{
    public uint Size, Status, OwnerCount, IssueCount;
    public ulong Generation;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnBootStageResultV1
{
    public uint Size, Ok;
    public fixed byte Reason[256];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnBootOwnerStateV1
{
    public uint Size, State, DefinitionCount, IssueCount;
    public ulong Generation;
    public fixed byte ModId[128];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnBootIssueV1
{
    public uint Size, Code;
    public fixed byte ModId[128];
    public fixed byte Kind[32];
    public fixed byte Key[192];
    public fixed byte Phase[32];
    public fixed byte Reason[256];
}

/// <summary>Mirror of <c>KilnBootApiV1</c> (kiln_boot.h): field order is the ABI (pinned by BootPhaseTests).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnBootApi
{
    public uint Size, Version;
    public delegate* unmanaged[Cdecl]<KilnBootCommitResultV1*, int> Status;
    public delegate* unmanaged[Cdecl]<ulong, KilnStr, uint, ulong*, int> BeginOwner;
    public delegate* unmanaged[Cdecl]<ulong, ulong, KilnStr, KilnStr, KilnStr, KilnBootStageResultV1*, int> Stage;
    public delegate* unmanaged[Cdecl]<ulong, ulong, uint, int> FinishOwner;
    public delegate* unmanaged[Cdecl]<ulong, KilnStr, byte*, nuint, nuint*, int> ReadResource;
    public delegate* unmanaged[Cdecl]<ulong, ulong, KilnBootCommitResultV1*, int> Commit;
    public delegate* unmanaged[Cdecl]<KilnStr, KilnBootOwnerStateV1*, int> OwnerState;
    public delegate* unmanaged[Cdecl]<ulong, KilnBootIssueV1*, uint, uint, uint*, int> Issues;
    public delegate* unmanaged[Cdecl]<ulong, KilnStr, void*, void*, int> Collector;
    public delegate* unmanaged[Cdecl]<KilnStr, ulong*, void*> ActivePlan;
}

/// <summary>The offline game-data view over kiln.boot's read_resource (chapter 08 §4.1): one query per call, the author's frame.</summary>
internal sealed unsafe class BootGameData(KilnBootApi* table, ulong author) : IReadOnlyGameData
{
    internal BootContext? Frame { get; set; }

    public BootDataResult Read(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Frame is { Open: false }) return new BootDataResult(false, "PhaseClosed: the boot-author frame is closed; the game's data is read inside OnBoot only", null);
        if (!Kiln.Capabilities.Is("mods.boot.game_data")) throw new KilnUnavailableException("mods.boot.game_data", Kiln.Capabilities.Why("mods.boot.game_data"));
        var bytes = Encoding.UTF8.GetBytes(key);
        nuint capacity = 4096;
        while (true)
        {
            var buf = new byte[(int)capacity];
            nuint required = 0;
            int rc;
            fixed (byte* k = bytes)
            fixed (byte* b = buf)
                rc = table->ReadResource(author, new KilnStr { Ptr = k, Len = (nuint)bytes.Length }, b, capacity, &required);
            switch (rc)
            {
                case KilnAbi.Ok:
                {
                    var n = Array.IndexOf(buf, (byte)0);
                    using var doc = JsonDocument.Parse(buf.AsMemory(0, n < 0 ? buf.Length : n));
                    var root = doc.RootElement;
                    var value = new BootDataValue(Guid.Parse(root.GetProperty("resourceId").GetString()!), Convert.ToUInt32(root.GetProperty("typeHash").GetString()!, 16),
                        root.GetProperty("part").GetInt32(), root.GetProperty("value").Clone());
                    return new BootDataResult(true, null, value);
                }
                case 5 when required > capacity && required <= 4u << 20:  // KILN_ERR_BUFFER_TOO_SMALL: again with the room it needs
                    capacity = required;
                    continue;
                case 2: return new BootDataResult(false, $"NotFound: no record {key} in the game's data", null);
                case 6: return new BootDataResult(false, "PhaseClosed: the boot-author frame is closed", null);
                case 1: throw new ArgumentException($"\"{key}\" is not a game-data query (item:, template:, skill:0x..., material:, resource:)", nameof(key));
                default:
                    if (!Kiln.Capabilities.Is("mods.boot.game_data")) throw new KilnUnavailableException("mods.boot.game_data", Kiln.Capabilities.Why("mods.boot.game_data"));
                    return new BootDataResult(false, $"Unavailable: kiln.boot could not answer {key} (rc {rc}): a type the view cannot decode, or the budget is spent", null);
            }
        }
    }
}

internal static unsafe class BootPhase
{
    internal const uint StatusCollecting = 0, StatusPublished = 2;
    private static readonly string[] StatusNames = ["collecting", "preparing", "published", "aborted", "activation-fault"];
    private static readonly string[] StateCodes = ["NotParticipating", "Staged", "Committed", "ExcludedCallback", "ExcludedDependency", "RejectedPrepare",
        "GenerationAborted", "MissedPhase", "ActivationFault"];

    /// <summary>In a boot profile: runs the authors and commits the generation. Never throws (a failure leaves kiln.boot collecting, and the
    /// core stops the start with its own diagnostics).</summary>
    public static void Run(Func<string, uint, nint> service, ulong host, ModManager mods, Action<LogLevel, string> log)
    {
        try
        {
            var t = (KilnBootApi*)service("kiln.boot", 1);
            if (t == null || t->Size < (uint)sizeof(KilnBootApi)) return;
            var st = new KilnBootCommitResultV1 { Size = (uint)sizeof(KilnBootCommitResultV1) };
            if (t->Status(&st) != KilnAbi.Ok) return;  // not a boot profile: nothing to do
            if (st.Status != StatusCollecting)
            {
                log(LogLevel.Warn, $"boot: the generation is {Name(st.Status)} already; Kiln.Host does not commit it");
                return;
            }
            var session = new BootSession(t, host, st.Generation, mods, log);
            session.RunAuthors();
            session.Commit();
        }
        catch (Exception e)
        {
            log(LogLevel.Error, $"boot: the boot phase failed: {e}");
        }
    }

    internal static string Name(uint status) => status < StatusNames.Length ? StatusNames[status] : status.ToString();

    internal static BootModState State(uint state) => state <= 8 ? (BootModState)state : BootModState.NotParticipating;

    /// <summary>One process's boot phase on the host side.</summary>
    private sealed class BootSession(KilnBootApi* t, ulong host, ulong generation, ModManager mods, Action<LogLevel, string> log)
    {
        private readonly Dictionary<string, Author> _authors = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];
        private readonly JsonObject _collectors = new();

        private sealed class Author
        {
            public required Manifest Manifest;
            public required string Directory;
            public BootRoles Roles;
            public List<string> Requires = [];
            public BootModState State = BootModState.NotParticipating;
            public List<BootIssue> Issues = [];
            public List<BootStaged> Committed = [];
            public string Fingerprint = "";
        }

        public void RunAuthors()
        {
            var candidates = mods.BootCandidates();
            var ids = new HashSet<string>(candidates.Select(c => c.Manifest.Id), StringComparer.Ordinal);
            foreach (var (m, dir) in candidates.Where(c => c.Manifest.Boot != ManifestBoot.None))
            {
                var roles = mods.BootRoles(m);
                if (roles == 0) continue;
                _authors[m.Id] = new Author { Manifest = m, Directory = dir, Roles = roles, Fingerprint = ModManager.BootFingerprint(m, dir) };
            }
            // required dependencies: a boot mod's run first; one missing from the load order excludes the dependent
            foreach (var a in _authors.Values)
                foreach (var d in a.Manifest.Dependencies.Where(d => !d.Optional && d.Id != Manifest.KilnId))
                {
                    if (_authors.ContainsKey(d.Id)) a.Requires.Add(d.Id);
                    else if (!ids.Contains(d.Id)) Exclude(a, BootModState.ExcludedDependency, "Dependency", $"requires {d.Id}, which is not in the load order");
                }
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            void Visit(Author a)
            {
                if (_order.Contains(a.Manifest.Id)) return;
                if (!visiting.Add(a.Manifest.Id))
                {
                    Exclude(a, BootModState.ExcludedDependency, "Dependency", "its boot dependencies form a cycle");
                    return;
                }
                foreach (var r in a.Requires) Visit(_authors[r]);
                visiting.Remove(a.Manifest.Id);
                _order.Add(a.Manifest.Id);
            }
            foreach (var a in _authors.Values.OrderBy(a => a.Manifest.Id, StringComparer.Ordinal)) Visit(a);
            var bootOn = Kiln.Capabilities.Is("mods.boot");
            foreach (var id in _order) RunOne(_authors[id], bootOn);
        }

        private static void Exclude(Author a, BootModState state, string code, string reason)
        {
            if (a.State is BootModState.ExcludedCallback or BootModState.ExcludedDependency) return;
            a.State = state;
            a.Issues.Add(new BootIssue(a.Manifest.Id, "", "", "callback", code, reason));
        }

        private void RunOne(Author a, bool bootOn)
        {
            var id = a.Manifest.Id;
            var roles = (uint)a.Roles | (a.Manifest.Boot == ManifestBoot.Required ? 1u << 31 : 0u);
            var owner = mods.Host.OwnerCreate("boot-author:" + id);
            try
            {
                ulong began = 0;
                int rc;
                fixed (byte* p = U(id)) rc = t->BeginOwner(owner, S(p, id), roles, &began);
                var gen = began;
                if (rc != KilnAbi.Ok)
                {
                    a.State = BootModState.ExcludedCallback;
                    a.Issues.Add(new BootIssue(id, "", "", "callback", "CallbackFailed", $"kiln.boot refused its author frame (rc {rc})"));
                    return;
                }
                var dependencyOut = a.Requires.FirstOrDefault(r => _authors[r].State is not BootModState.Staged);
                if (a.State == BootModState.ExcludedDependency || dependencyOut != null)
                {
                    if (dependencyOut != null) Exclude(a, BootModState.ExcludedDependency, "Dependency", $"requires {dependencyOut}, which is out of the boot generation");
                    t->FinishOwner(owner, gen, 2);
                    return;
                }
                if (!bootOn)
                {
                    a.State = BootModState.ExcludedCallback;
                    a.Issues.Add(new BootIssue(id, "", "", "callback", "BootCapOff", $"mods.boot is off: {Kiln.Capabilities.Why("mods.boot")}; its OnBoot does not run"));
                    t->FinishOwner(owner, gen, 0);
                    return;
                }
                var data = new BootGameData(t, owner);
                var boot = new BootContext(id, gen, a.Roles, data) { Stage = (kind, key, json) => Stage(owner, gen, kind, key, json) };
                data.Frame = boot;
                var ok = mods.RunBootAuthor(a.Manifest, a.Directory, owner, boot, out var why);
                if (ok && !Lower(a, boot, out why)) ok = false;
                if (!ok)
                {
                    a.State = BootModState.ExcludedCallback;
                    a.Issues.Add(new BootIssue(id, "", "", "callback", "CallbackFailed", why));
                    log(LogLevel.Error, $"boot: {id}: {why}; none of its definitions or files are in the boot generation");
                }
                else
                {
                    a.State = BootModState.Staged;
                }
                t->FinishOwner(owner, gen, ok ? 1u : 0u);
            }
            finally
            {
                mods.Host.OwnerRevoke(owner);
            }
        }

        private (bool, string?) Stage(ulong owner, ulong gen, string kind, string key, string json)
        {
            var r = new KilnBootStageResultV1 { Size = (uint)sizeof(KilnBootStageResultV1) };
            int rc;
            byte[] kb = U(kind), nb = U(key), jb = U(json);
            fixed (byte* k = kb)
            fixed (byte* n = nb)
            fixed (byte* j = jb)
                rc = t->Stage(owner, gen, S(k, kb), S(n, nb), S(j, jb), &r);
            if (rc != KilnAbi.Ok) throw new ArgumentException($"kiln.boot refused {kind}/{key} (rc {rc}): a bad kind, name or JSON");
            return r.Ok == 1 ? (true, null) : (false, Text(r.Reason, 256));
        }

        // Kiln.Blocks lowers what the author staged into the collectors' documents (BootLowering.LowerOwner).
        private bool Lower(Author a, BootContext boot, out string why)
        {
            why = "";
            if (boot.Staged.Count == 0) return true;
            var lower = Type.GetType("Kiln.BootLowering, Kiln.Blocks")?.GetMethod("LowerOwner", BindingFlags.Static | BindingFlags.NonPublic);
            if (lower == null)
            {
                why = "Kiln.Blocks has no boot lowering in this build: its staged definitions cannot be lowered";
                return false;
            }
            var request = new JsonObject
            {
                ["id"] = a.Manifest.Id, ["clients"] = a.Manifest.Clients.ToString().ToLowerInvariant(), ["dir"] = a.Directory,
                ["staged"] = new JsonArray(boot.Staged.Select(s => (JsonNode)new JsonObject { ["kind"] = s.Kind, ["key"] = s.Key, ["json"] = s.Json }).ToArray()),
            };
            JsonObject answer;
            try { answer = JsonNode.Parse((string)lower.Invoke(null, [request.ToJsonString()])!)!.AsObject(); }
            catch (TargetInvocationException e) when (e.InnerException is { } inner)
            {
                why = $"its boot definitions do not lower: {inner.GetType().Name}: {inner.Message}";
                return false;
            }
            if (answer["ok"]?.GetValue<bool>() != true)
            {
                why = $"its boot definitions do not lower: {(string?)answer["reason"]}";
                return false;
            }
            foreach (var issue in answer["issues"]!.AsArray())
                a.Issues.Add(new BootIssue(a.Manifest.Id, (string)issue!["kind"]!, (string)issue["key"]!, "freeze", (string)issue["code"]!, (string)issue["reason"]!));
            var lowered = answer["definitions"]!.AsArray().Select(d => (string)d!).ToHashSet(StringComparer.Ordinal);
            a.Committed = boot.Staged.Where(s => lowered.Contains($"{s.Kind}/{s.Key}")).ToList();
            foreach (var (collector, list) in answer["docs"]!.AsObject())
            {
                var into = (_collectors[collector] ??= new JsonArray()).AsArray();
                foreach (var d in list!.AsArray())
                    into.Add(new JsonObject { ["owner"] = a.Manifest.Id, ["key"] = (string)d!["key"]!, ["document"] = d["document"]!.DeepClone() });
            }
            return true;
        }

        public void Commit()
        {
            var owners = new JsonArray();
            foreach (var id in _order)
            {
                var a = _authors[id];
                if (a.State != BootModState.Staged) continue;
                owners.Add(new JsonObject
                {
                    ["id"] = id, ["roles"] = (int)a.Roles, ["fingerprint"] = a.Fingerprint,
                    ["requires"] = new JsonArray(a.Requires.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                });
            }
            var envelope = new JsonObject { ["schema"] = 1, ["owners"] = owners, ["reservations"] = new JsonObject(), ["collectors"] = _collectors };
            var text = envelope.ToJsonString();
            var sr = new KilnBootStageResultV1 { Size = (uint)sizeof(KilnBootStageResultV1) };
            int src;
            byte[] kb = U("ir"), nb = U("envelope"), jb = U(text);
            fixed (byte* k = kb)
            fixed (byte* n = nb)
            fixed (byte* j = jb)
                src = t->Stage(host, generation, S(k, kb), S(n, nb), S(j, jb), &sr);
            if (src != KilnAbi.Ok || sr.Ok != 1) log(LogLevel.Error, $"boot: kiln.boot refused the IR envelope (rc {src}): {Text(sr.Reason, 256)}");
            var r = new KilnBootCommitResultV1 { Size = (uint)sizeof(KilnBootCommitResultV1) };
            var rc = t->Commit(host, generation, &r);
            if (rc != KilnAbi.Ok)
            {
                log(LogLevel.Error, $"boot: kiln.boot refused the commit (rc {rc}); the start stops before the game's entry point");
                return;
            }
            Record(r.Generation);
            log(r.Status == StatusPublished ? LogLevel.Info : LogLevel.Error,
                $"boot: generation {r.Generation} {Name(r.Status)}: {_authors.Values.Count(a => a.State == BootModState.Committed)} boot mod(s) committed, " +
                $"{text.Length} bytes of IR, {r.IssueCount} issue(s) (state/boot.json)");
        }

        private void Record(ulong gen)
        {
            var native = NativeIssues(gen);
            var reports = new List<BootModReport>();
            var committed = new Dictionary<string, IReadOnlyList<BootStaged>>(StringComparer.Ordinal);
            foreach (var (id, a) in _authors)
            {
                var s = new KilnBootOwnerStateV1 { Size = (uint)sizeof(KilnBootOwnerStateV1) };
                int orc;
                byte[] ib = U(id);
                fixed (byte* k = ib) orc = t->OwnerState(S(k, ib), &s);
                if (orc == KilnAbi.Ok && s.State != 0) a.State = State(s.State);
                var issues = a.Issues.Concat(native.Where(i => i.ModId == id && !a.Issues.Any(x => x.Code == i.Code && x.Reason == i.Reason))).ToList();
                var defs = a.State == BootModState.Committed ? a.Committed : [];
                if (defs.Count > 0) committed[id] = defs;
                reports.Add(new BootModReport(id, gen, a.State, defs.Select(d => $"{d.Kind}/{d.Key}").ToList(), issues));
                mods.SetBootFingerprint(id, a.Fingerprint);
            }
            BootGeneration.Record(gen, reports, committed);
        }

        private List<BootIssue> NativeIssues(ulong gen)
        {
            uint n = 0;
            t->Issues(gen, null, 0, (uint)sizeof(KilnBootIssueV1), &n);
            if (n == 0) return [];
            var buf = new KilnBootIssueV1[n];
            fixed (KilnBootIssueV1* p = buf)
                if (t->Issues(gen, p, n, (uint)sizeof(KilnBootIssueV1), &n) != KilnAbi.Ok) return [];
            var list = new List<BootIssue>();
            foreach (var i in buf)
            {
                var x = i;
                list.Add(new BootIssue(Text(x.ModId, 128), Text(x.Kind, 32), Text(x.Key, 192), Text(x.Phase, 32), CodeName(x.Code), Text(x.Reason, 256)));
            }
            return list;
        }
    }

    internal static string CodeName(uint code) => code switch
    {
        1 => "DuplicateDefinition", 2 => "CallbackFailed", 3 => "Dependency", 4 => "PrepareRefused", 5 => "CollectorFault", 6 => "CollectorMissing",
        7 => "GenerationAborted", 8 => "NotCommitted", 9 => "WrongRole", 10 => "ActivationFault", 0x80000001u => "ShadowedByFile",
        _ => $"0x{code:x8}",
    };

    internal static byte[] U(string s) => Encoding.UTF8.GetBytes(s);
    internal static KilnStr S(byte* p, byte[] b) => new() { Ptr = p, Len = (nuint)b.Length };
    internal static KilnStr S(byte* p, string s) => new() { Ptr = p, Len = (nuint)Encoding.UTF8.GetByteCount(s) };

    internal static string Text(byte* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != 0) n++;
        return Encoding.UTF8.GetString(p, n);
    }
}

internal sealed partial class ModManager
{
    private readonly Dictionary<string, string> _bootFingerprints = new(StringComparer.Ordinal);

    internal IHostServices Host => _host;

    /// <summary>Every Kiln mod folder this start would load, as LoadAll finds them (profile entries that are enabled, then drop-ins without
    /// .disabled whose id the profile does not list), with a readable manifest.</summary>
    internal List<(Manifest Manifest, string Directory)> BootCandidates()
    {
        var list = new List<(Manifest, string)>();
        var profile = _options.Profile;
        if (profile.LoadsNothing) return list;
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in profile.Folders)
        {
            listed.Add(f.Id);
            if (!f.Enabled) continue;
            if (ModCandidate.Read(f.Directory).Manifest is { } m && m.Id == f.Id) list.Add((m, f.Directory));
        }
        foreach (var dir in DropinFolders())
        {
            if (File.Exists(System.IO.Path.Combine(dir, ".disabled"))) continue;
            if (ModCandidate.Read(dir).Manifest is { } m && !listed.Contains(m.Id)) list.Add((m, dir));
        }
        return list;
    }

    /// <summary>The roles a boot author takes in this process (chapter 08 §4.2); 0: none (a client-only mod on a dedicated server).</summary>
    internal BootRoles BootRoles(Manifest m)
    {
        if (_options.Side != ManifestSide.Client) return m.Side == ManifestSide.Client ? 0 : Kiln.BootRoles.DedicatedServer;
        BootRoles r = 0;
        if (m.Side is ManifestSide.Client or ManifestSide.Both) r |= Kiln.BootRoles.Client;
        if (m.Side is ManifestSide.Server or ManifestSide.Both && _options.HostedServerMods) r |= Kiln.BootRoles.HostedServer;
        return r;
    }

    /// <summary>What a later runtime load compares: the manifest and the entry assembly, hashed.</summary>
    internal static string BootFingerprint(Manifest m, string dir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in new[] { Manifest.FileName, m.EntryAssembly })
        {
            var path = System.IO.Path.Combine(dir, f);
            if (f.Length > 0 && File.Exists(path)) sha.AppendData(File.ReadAllBytes(path));
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    internal void SetBootFingerprint(string id, string fingerprint) => _bootFingerprints[id] = fingerprint;

    /// <summary>The transient boot-author instance: its own collectible load context, a context of its own owner, OnBoot once under the frame,
    /// then unloaded. False: it did not load or OnBoot threw (<paramref name="why"/>).</summary>
    internal bool RunBootAuthor(Manifest manifest, string directory, ulong owner, BootContext boot, out string why)
    {
        why = "";
        if (!manifest.HasCode) return true;  // a data-only boot mod: its files only
        var alc = new ModLoadContext(manifest.Id, directory);
        try
        {
            var assembly = alc.LoadFile(System.IO.Path.Combine(directory, manifest.EntryAssembly));
            var type = manifest.EntryType is { } name
                ? assembly.GetType(name, throwOnError: true)!
                : assembly.GetExportedTypes().SingleOrDefault(t => t.IsSubclassOf(typeof(Kiln.Mod)) && !t.IsAbstract)
                  ?? throw new InvalidDataException($"{manifest.EntryAssembly} has no public Kiln.Mod subclass (or set entryType)");
            var instance = (Kiln.Mod)Activator.CreateInstance(type)!;
            var context = HostAccess.CreateContext(Info(manifest, directory), owner, System.IO.Path.Combine(_dataDirectory, manifest.Id));
            HostAccess.Bind(instance, context);
            var previous = ModContext.CurrentMod;
            BootFrame.Current = boot;
            ModContext.CurrentMod = context;
            try
            {
                instance.OnBoot(boot);
            }
            finally
            {
                boot.Open = false;
                BootFrame.Current = null;
                ModContext.CurrentMod = previous;
            }
            _host.Log(LogLevel.Info, $"boot: {manifest.Id}: OnBoot staged {boot.Staged.Count} definition(s)");
            return true;
        }
        catch (Exception e)
        {
            boot.Open = false;
            why = $"OnBoot failed: {e.GetType().Name}: {e.Message}";
            return false;
        }
        finally
        {
            alc.Unload();
        }
    }

    /// <summary>A boot mod's runtime load against this process's boot generation (chapter 08 §4.4): null when it may load (a committed mod
    /// whose fingerprint is unchanged), else why not (<c>RestartRequiredBoot</c>; an absent author is recorded <c>MissedPhase</c>).</summary>
    internal string? BootLoadRefusal(Manifest m, string directory)
    {
        if (m.Boot == ManifestBoot.None) return null;
        var report = BootGeneration.Report(m.Id);
        if (report is { State: BootModState.Committed })
            return _bootFingerprints.TryGetValue(m.Id, out var fp) && fp != BootFingerprint(m, directory)
                ? $"RestartRequiredBoot: mod {m.Id} changed since the boot generation committed its boot content; restart to load it"
                : null;
        if (report == null || report.State == BootModState.NotParticipating)
        {
            BootGeneration.Set(new BootModReport(m.Id, BootGeneration.Number, BootModState.MissedPhase, [],
                [new BootIssue(m.Id, "", "", "load", "MissedPhase", "enabled after the boot phase: its boot content needs a restart")]));
            return BootGeneration.Number == 0
                ? $"RestartRequiredBoot: mod {m.Id} has boot content (\"boot\" in kiln.mod.json) and this process ran no boot phase " +
                  $"(mods.boot: {Kiln.Capabilities.Why("mods.boot") ?? "off"}); it does not load"
                : $"RestartRequiredBoot: mod {m.Id} has boot content and was not in this process's boot generation; restart to load it";
        }
        return $"RestartRequiredBoot: mod {m.Id}'s boot content is not in this process ({report.State}); restart after fixing it";
    }
}
