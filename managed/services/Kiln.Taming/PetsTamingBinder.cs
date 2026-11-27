// SPDX-License-Identifier: MIT
// A finished Kiln tame binds a Kiln.Pets record (dossier 19e 4.3): a definition that names a pet (TamingDefinition.Pet)
// holds a roster slot of it from Begin, checks it again before the last step's cost (no cost for a tame the roster cannot
// hold), and at the commit the owner's character gets a new pet record (Pets.Grant: the ownership commit). Then, as the
// definition says, the tamed wild body goes and the pet is summoned; neither undoes the record. A definition with no pet
// binds nothing.
namespace Kiln;

internal sealed class PetsTamingBinder : ITamingBinder
{
    // Kiln.Pets as the binder asks it (tests replace these)
    internal Func<PlayerRef, CharacterKey?> CharacterOf { get; init; } = DefaultCharacter;
    internal Func<string, PetDefinition?> PetDefinition { get; init; } = Pets.Definition;
    internal Func<CharacterKey, IReadOnlyList<PetSnapshot>> PetsOf { get; init; } = Pets.Of;
    internal Func<CharacterKey, string, PetRequest, Task<PetResult>> Grant { get; init; } = Pets.Grant;
    internal Func<PlayerRef, PetId, PetRequest, Task<PetResult>> Summon { get; init; } = Pets.Summon;
    internal Func<EntityRef, bool> RemoveTarget { get; init; } = DefaultRemove;
    internal Action<string> Log { get; init; } = m => Backend.Current.Log(0, LogLevel.Info, m);

    /// <summary>The roster's own cap of records per character (Kiln.Pets).</summary>
    internal const int MaxRecords = PetRoster.MaxRecordsPerCharacter;

    private readonly Func<string, TamingDefinition?> _definitions;
    private readonly Dictionary<TameAttemptId, (CharacterKey Owner, string Pet)> _held = new();

    internal PetsTamingBinder(Func<string, TamingDefinition?> definitions) => _definitions = definitions;

    internal int Held => _held.Count;

    private static CharacterKey? DefaultCharacter(PlayerRef player)
    {
        Player? p = null;
        try { p = !player.Entity.IsNone ? Players.Find(player.Entity.Id) : Players.FindPerson(player); }
        catch (NotSupportedException) { }
        if (p is null) return null;
        var key = CharacterData.KeyOf(p.Entity);
        return key.IsNone || key.IsAccountOnly ? null : key;
    }

    private static bool DefaultRemove(EntityRef target) => target.IsAlive && Spawn.Destroy(target.Id);

    // the room a new record of `pet` needs, leaving out `except`'s own hold
    private (string Reason, bool Transient)? Room(CharacterKey owner, string pet, TameAttemptId except)
    {
        if (PetDefinition(pet) is not { } def) return ($"DefinitionUnknown: no pet definition {pet} is defined", false);
        var have = PetsOf(owner);
        var held = _held.Where(h => !h.Key.Equals(except) && h.Value.Owner == owner).Select(h => h.Value.Pet).ToList();
        var of = have.Count(p => p.Definition == pet) + held.Count(p => p == pet);
        if (of >= def.Caps.RetainedPerCharacter)
            return ($"Cap: the character keeps {def.Caps.RetainedPerCharacter} pet(s) of {pet}{(held.Contains(pet) ? " (with the tames running)" : "")}", true);
        if (have.Count + held.Count >= MaxRecords) return ($"Cap: a character keeps at most {MaxRecords} pet records", true);
        return null;
    }

    public string? Reserve(TamingBindRequest request)
    {
        if (request.Definition.Pet is not { } pet) return null;
        if (CharacterOf(request.Owner) is not { } owner) return "NoCharacter: the player's character is not known (account-only players cannot own pets)";
        if (Room(owner, pet, request.Attempt) is { } full) return full.Reason;
        _held[request.Attempt] = (owner, pet);
        return null;
    }

    public (string Reason, bool Transient)? Preflight(TamingBindRequest request)
    {
        if (request.Definition.Pet is not { } pet) return null;
        if (CharacterOf(request.Owner) is not { } owner) return ("NoCharacter: the player's character is not known", false);
        return Room(owner, pet, request.Attempt);
    }

    public Task<TamingBindResult> Bind(TamingBindRequest request)
    {
        var d = request.Definition;
        if (d.Pet is not { } pet) return Task.FromResult(new TamingBindResult(true, null, null, false));
        if (CharacterOf(request.Owner) is not { } owner) return Task.FromResult(new TamingBindResult(false, null, "NoCharacter: the player's character is not known", false));
        // a new operation for every try: a transient refusal must not be answered again by the roster's replay cache. The
        // record is made at the roster's next step; the mapping below has no side effects (it may run off the tick thread)
        return Grant(owner, pet, PetRequest.New()).ContinueWith(Map, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal static TamingBindResult Map(Task<PetResult> t)
    {
        if (!t.IsCompletedSuccessfully) return new TamingBindResult(false, null, $"Refused: {t.Exception?.GetBaseException().Message ?? "the grant did not settle"}", false);
        var granted = t.Result;
        if (granted is { Ok: true, Pet: { } snapshot }) return new TamingBindResult(true, snapshot, null, false);
        var why = granted.Reason ?? "Refused";
        var transient = why.StartsWith("Capacity", StringComparison.Ordinal) || why.StartsWith("LevelUnavailable", StringComparison.Ordinal);
        return new TamingBindResult(false, null, why, transient);
    }

    /// <summary>The tame committed (tick thread): the wild body goes and the pet is summoned, as the definition says.
    /// Neither undoes the record.</summary>
    public void Committed(TamingBindRequest request, TamingBindResult result)
    {
        var d = request.Definition;
        if (d.Pet is not { } pet || result.Pet is not { } snapshot) return;
        try
        {
            if (d.RemoveTarget && !RemoveTarget(request.Target))
                Log($"Kiln.Taming: {request.Attempt} made pet {snapshot.Id} of {pet}; the tamed body {request.Target.Id} could not be removed (it was gone already, or kiln.spawn refused)");
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            Log($"Kiln.Taming: removing the tamed body {request.Target.Id} failed: {e.Message}");
        }
        if (!d.SummonPet) return;
        try
        {
            _ = Summon(request.Owner, snapshot.Id, PetRequest.New()).ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully || !t.Result.Ok)
                    Log($"Kiln.Taming: pet {snapshot.Id} of {pet} was made; its summon was refused ({(t.IsCompletedSuccessfully ? t.Result.Reason : t.Exception?.GetBaseException().Message)}): it is Ready");
            }, TaskScheduler.Default);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            Log($"Kiln.Taming: pet {snapshot.Id} of {pet} was made; its summon failed ({e.Message}): it is Ready");
        }
    }

    public void Release(TamingBindRequest request) => _held.Remove(request.Attempt);
}
