// SPDX-License-Identifier: MIT
// Kiln's own services' per-character records (19d §4.1, the "service-data bridge"): a trusted Kiln service (Kiln.Pets'
// roster) keeps one JSON document per character of a world under the bundle scope "service/<service>/character/<key>",
// staged and committed with the world's other mod data through the same manifests, never by a file writer or save clock of
// its own. Not a public arbitrary scope: only Kiln's assemblies reach it (InternalsVisibleTo), and no mod context chooses it.
//
// The receipt seam (19d §4.7): a service learns exactly what each staging serialized (the bytes' sha256, the generation,
// whether it was the final snapshot staging) and which generation the game then committed, so it can acknowledge only the
// state that is in the committed bundle, never what is live when the save event arrives. Tick thread.
using System.Security.Cryptography;

namespace Kiln;

internal static class ServiceCharacters
{
    internal const string Prefix = "service/";

    /// <summary>A staging is about to serialize the bundles (tick thread): a service writes its documents now
    /// (<see cref="Set"/>). <c>Snapshot</c> true: the final staging before the game's snapshot (nothing simulates between
    /// this and the write).</summary>
    internal readonly record struct StagingInfo(uint Generation, bool Snapshot, bool Late, int Session);

    /// <summary>One service document as a staging serialized it: its bundle bytes and their sha256 (hex, lower case).</summary>
    internal readonly record struct Receipt(string Service, CharacterKey Character, uint Generation, bool Snapshot, int Session, string Sha256, byte[] Bytes);

    /// <summary>The game wrote the world file of a generation: every receipt of that generation's last staging is in the
    /// committed manifest.</summary>
    internal readonly record struct CommitInfo(uint Generation, uint WorldUnix, int Session);

    internal static event Action<StagingInfo>? Staging;
    internal static event Action<Receipt>? Staged;
    internal static event Action<CommitInfo>? Committed;

    internal static string Scope(string service, CharacterKey character) => $"{Prefix}{service}/character/{character.Text}";

    private static void CheckService(string service)
    {
        if (string.IsNullOrEmpty(service) || !service.StartsWith("kiln.", StringComparison.Ordinal) || service.Contains('/'))
            throw new ArgumentException("a service is kiln.<name>", nameof(service));
    }

    private static void CheckCharacter(CharacterKey character)
    {
        if (character.IsNone || character.IsAccountOnly) throw new ArgumentException("a service record needs a character (account and character id)", nameof(character));
    }

    /// <summary>The service's document for the character, or null. Tick thread.</summary>
    internal static string? Get(string service, CharacterKey character)
    {
        CheckService(service);
        CheckCharacter(character);
        if (!SavesRuntime.Ensure()) return null;
        var store = SavesRuntime.Store;
        store.EnsureOpen();
        return store.FindBundle(Scope(service, character)) is { } b && b.Keys.TryGetValue("record", out var s) ? s.Json : null;
    }

    /// <summary>Replaces the service's document for the character (written with the next staging). Tick thread.</summary>
    internal static bool Set(string service, CharacterKey character, string json)
    {
        CheckService(service);
        CheckCharacter(character);
        ArgumentException.ThrowIfNullOrEmpty(json);
        if (!SavesRuntime.Ensure()) return false;
        var b = SavesRuntime.Store.GetBundleRaw(Scope(service, character), "kiln:" + service);
        b.Keys["record"] = new WorldStore.Slot { Json = json };
        return true;
    }

    /// <summary>Removes the service's document for the character (from the next staging on). Tick thread.</summary>
    internal static bool Remove(string service, CharacterKey character)
    {
        CheckService(service);
        CheckCharacter(character);
        if (!SavesRuntime.Ensure()) return false;
        return SavesRuntime.Store.FindBundle(Scope(service, character)) is { } b && b.Keys.Remove("record");
    }

    /// <summary>The characters the service has documents for in this world. Tick thread.</summary>
    internal static IReadOnlyList<CharacterKey> Characters(string service)
    {
        CheckService(service);
        if (!SavesRuntime.Ensure()) return [];
        var store = SavesRuntime.Store;
        store.EnsureOpen();
        var prefix = $"{Prefix}{service}/character/";
        var list = new List<CharacterKey>();
        foreach (var b in store.BundlesWithPrefix(prefix))
            if (b.Keys.ContainsKey("record") && CharacterKey.TryParse(b.Scope[prefix.Length..], out var key)) list.Add(key);
        return list;
    }

    // ---- WorldStore's side ----

    internal static void RaiseStaging(uint gen, bool snapshot, bool late, int session)
    {
        if (Staging is not { } handlers) return;
        foreach (Action<StagingInfo> h in handlers.GetInvocationList())
        {
            try { h(new StagingInfo(gen, snapshot, late, session)); }
            catch (Exception e) { SavesRuntime.Log(LogLevel.Error, $"Kiln.Saves: a service's staging participant threw: {e.Message}"); }
        }
    }

    /// <summary>A bundle was serialized: a service document's receipt goes to its observers.</summary>
    internal static void RaiseStaged(string scope, byte[] bytes, uint gen, bool snapshot, int session)
    {
        if (Staged is not { } handlers || !scope.StartsWith(Prefix, StringComparison.Ordinal)) return;
        var rest = scope[Prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return;
        var service = rest[..slash];
        const string marker = "/character/";
        if (!rest[slash..].StartsWith(marker, StringComparison.Ordinal) || !CharacterKey.TryParse(rest[(slash + marker.Length)..], out var key)) return;
        var receipt = new Receipt(service, key, gen, snapshot, session, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
        foreach (Action<Receipt> h in handlers.GetInvocationList())
        {
            try { h(receipt); }
            catch (Exception e) { SavesRuntime.Log(LogLevel.Error, $"Kiln.Saves: a service's receipt observer threw: {e.Message}"); }
        }
    }

    internal static void RaiseCommitted(uint gen, uint unix, int session)
    {
        if (Committed is not { } handlers) return;
        foreach (Action<CommitInfo> h in handlers.GetInvocationList())
        {
            try { h(new CommitInfo(gen, unix, session)); }
            catch (Exception e) { SavesRuntime.Log(LogLevel.Error, $"Kiln.Saves: a service's commit observer threw: {e.Message}"); }
        }
    }
}
