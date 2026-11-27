// SPDX-License-Identifier: MIT
// Who wants what an NPC says, and what it says because of that: the rule of Kiln.Npcs without the game. Per NPC and
// kind of line (the bubble's greeting, the menu's farewell, the menu's own line), each owner (a mod) has at most one
// override (Suppress or Replace, with a priority and a pick) and the NPC has at most one Say. The effective override is
// the highest priority, on a tie the most recent; a Say shows over the greeting until its time. Two layers: Local (calls
// in this process) and Remote (what the server sent this joined client); Local wins over Remote, so a player's own
// client-only mod has the last word on their own screen. docs/kiln/services/npc-speech.md.
namespace Kiln;

/// <summary>What an NPC's greeting bubble does now.</summary>
public enum SpeechMode : byte
{
    /// <summary>The game's own lines.</summary>
    Game = 0,
    /// <summary>No greeting bubble and no greeting voice-over.</summary>
    Suppress = 1,
    /// <summary>The NPC picks from a mod's lines instead of its own, with no voice-over.</summary>
    Replace = 2,
}

/// <summary>Which line of an NPC an override is for (since 0.2.0-dev.32).</summary>
public enum NpcLineKind : byte
{
    /// <summary>The greeting in the speech bubble over its head (<see cref="Npcs.Speech"/>).</summary>
    Greeting = 0,
    /// <summary>The farewell in the dialogue menu's speech box when the player leaves it (<see cref="Npcs.Farewells"/>).</summary>
    Farewell = 1,
    /// <summary>The NPC's own line in the dialogue menu's speech box when the menu opens (<see cref="Npcs.MenuLine"/>).</summary>
    MenuLine = 2,
}

/// <summary>What a <see cref="SpeechPick.By"/> callback is asked: which of the lines this player sees next.</summary>
/// <param name="Npc">The NPC.</param>
/// <param name="Player">The player whose game shows the line (the host's own player in a hosted world).</param>
/// <param name="Kind">The greeting, the farewell or the menu's line.</param>
/// <param name="Opens">How many times this player has seen this kind of line of this NPC since the override began.</param>
/// <param name="Last">The index this player saw last; -1 when none (or the game's own line).</param>
/// <param name="Lines">How many lines the override has.</param>
public sealed record NpcLineContext(EntityId Npc, EntityId Player, NpcLineKind Kind, int Opens, int Last, int Lines);

/// <summary>How a <see cref="NpcSpeech.Replace(string[])"/> picks its line each time the NPC greets (says farewell, opens
/// its menu). Since 0.2.0-dev.32.</summary>
public sealed class SpeechPick
{
    private SpeechPick(byte code, uint seed, Func<NpcLineContext, int>? choose, string name)
    {
        Code = code;
        Seed = seed;
        Choose = choose;
        _name = name;
    }

    private readonly string _name;
    internal byte Code { get; }
    internal uint Seed { get; }
    internal Func<NpcLineContext, int>? Choose { get; }

    /// <summary>A new line at random each time (never the same line twice in a row when there are two or more), picked
    /// by each player's own game. The default.</summary>
    public static SpeechPick Random { get; } = new(SpeechNative.PickRandom, 0, null, "Random");

    /// <summary>The lines in turn, one per greeting, from the first, picked by each player's own game.</summary>
    public static SpeechPick InOrder { get; } = new(SpeechNative.PickOrder, 0, null, "InOrder");

    /// <summary>The same line for the same greeting everywhere: a mix of <paramref name="seed"/>, the NPC and how many
    /// times the player has seen it, picked by each player's own game with no message per greeting.</summary>
    /// <param name="seed">Any number: the same seed gives the same sequence for the same NPC, so every player's game agrees.</param>
    /// <returns>The pick to set as <see cref="NpcSpeech.Pick"/>.</returns>
    public static SpeechPick Seeded(int seed) => new(SpeechNative.PickSeeded, unchecked((uint)seed), null, $"Seeded({seed})");

    /// <summary>The host decides for each player: <paramref name="choose"/> runs on the host's tick thread and returns
    /// the index of the line that player sees next, or -1 for the game's own line (the menu: no line). It runs one
    /// greeting ahead: when the override is set, after each greeting of that player, and at
    /// <see cref="NpcSpeech.Refresh"/>; the player's game shows what it was given last. A throwing callback or an index
    /// out of range counts as -1.</summary>
    /// <param name="choose">Runs on the host's tick thread with the NPC, the player and how many times that player has seen the line
    /// (<see cref="NpcLineContext"/>); returns the line's index, or -1 for the game's own line.</param>
    /// <returns>The pick to set as <see cref="NpcSpeech.Pick"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="choose"/> is null.</exception>
    /// <example><code>
    /// var line = Npcs.MenuLine(npc);
    /// line.Pick = SpeechPick.By(ctx =&gt; ctx.Opens == 0 ? 0 : 1);     // the first line the first time each player opens the menu
    /// line.Replace("Welcome, Flameborn!", "Back again?");
    /// </code></example>
    public static SpeechPick By(Func<NpcLineContext, int> choose)
    {
        ArgumentNullException.ThrowIfNull(choose);
        return new SpeechPick(SpeechNative.PickHost, 0, choose, "By");
    }

    /// <summary>A joined client's copy of a host's By: the host picks, this game shows and reports.</summary>
    internal static SpeechPick Host { get; } = new(SpeechNative.PickHost, 0, null, "Host");

    internal static SpeechPick FromWire(byte code, uint seed) => code switch
    {
        SpeechNative.PickRandom => Random,
        SpeechNative.PickOrder => InOrder,
        SpeechNative.PickSeeded => new SpeechPick(code, seed, null, $"Seeded({unchecked((int)seed)})"),
        SpeechNative.PickHost => Host,
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };

    /// <summary>The same pick: the kind and seed, and the same callback.</summary>
    internal bool SameAs(SpeechPick o) => Code == o.Code && Seed == o.Seed && ReferenceEquals(Choose, o.Choose);

    /// <summary>Random, InOrder, Seeded(seed) or By.</summary>
    public override string ToString() => _name;
}

/// <summary>The effective speech of one NPC's line of one kind: its override and (greetings) its running Say.</summary>
internal sealed record SpeechEffect(SpeechMode Mode, IReadOnlyList<UiText> Lines, string? Owner, UiText? Say, long SayUntilMs, string? SayOwner)
{
    public static readonly SpeechEffect None = new(SpeechMode.Game, [], null, null, 0, null);

    /// <summary>How the override picks (Random when there is none).</summary>
    public SpeechPick Pick { get; init; } = SpeechPick.Random;

    /// <summary>Which layer the override came from (Local when there is none).</summary>
    public SpeechLayer Layer { get; init; }

    /// <summary>Same override and Say (texts compared by their JSON), pick aside.</summary>
    public bool SameAs(SpeechEffect o) =>
        Mode == o.Mode && SayUntilMs == o.SayUntilMs && SpeechText.Json(Say) == SpeechText.Json(o.Say) && SameLines(o);

    /// <summary>The same mode and lines (texts compared by their JSON).</summary>
    public bool SameLines(SpeechEffect o) =>
        Mode == o.Mode && Lines.Count == o.Lines.Count && Lines.Select(SpeechText.Json).SequenceEqual(o.Lines.Select(SpeechText.Json));
}

internal enum SpeechLayer : byte { Local = 0, Remote = 1 }

/// <summary>The rule, thread-safe (one lock). Times are ms of a clock the caller gives.</summary>
internal sealed class SpeechBook
{
    private sealed record Entry(string Owner, NpcLineKind Kind, SpeechMode Mode, UiText[] Lines, int Priority, long Seq, SpeechPick Pick);
    private sealed record SayEntry(string Owner, UiText Text, long UntilMs);

    /// <summary>One owner's topics of an NPC (Wire: the line id Kiln clients show it with); Seq keeps its place on the page.</summary>
    internal sealed record TopicEntry(string Owner, NpcTopic Topic, uint Wire, long Seq, SpeechLayer Layer);

    private sealed class Npc
    {
        public readonly List<Entry>[] Overrides = [new(), new()];
        public readonly SayEntry?[] Says = new SayEntry?[2];
        public readonly List<TopicEntry>[] Topics = [new(), new()];
        public bool Empty => Overrides[0].Count == 0 && Overrides[1].Count == 0 && Says[0] is null && Says[1] is null &&
                             Topics[0].Count == 0 && Topics[1].Count == 0;
    }

    private readonly object _gate = new();
    private readonly Dictionary<uint, Npc> _npcs = new();
    private long _seq;

    public int Count { get { lock (_gate) return _npcs.Count; } }

    /// <summary>The NPCs that have anything.</summary>
    public uint[] Npcs() { lock (_gate) return _npcs.Keys.ToArray(); }

    public void Set(SpeechLayer layer, uint npc, string owner, SpeechMode mode, UiText[] lines, int priority) =>
        Set(layer, npc, owner, NpcLineKind.Greeting, mode, lines, priority, SpeechPick.Random);

    public void Set(SpeechLayer layer, uint npc, string owner, NpcLineKind kind, SpeechMode mode, UiText[] lines, int priority, SpeechPick pick)
    {
        lock (_gate)
        {
            var n = Get(npc);
            var list = n.Overrides[(int)layer];
            list.RemoveAll(e => e.Owner == owner && e.Kind == kind);
            if (mode != SpeechMode.Game) list.Add(new Entry(owner, kind, mode, lines, priority, ++_seq, pick));
            Drop(npc, n);
        }
    }

    public void Say(SpeechLayer layer, uint npc, string owner, UiText? text, long untilMs)
    {
        lock (_gate)
        {
            var n = Get(npc);
            n.Says[(int)layer] = text is null ? null : new SayEntry(owner, text, untilMs);
            Drop(npc, n);
        }
    }

    /// <summary>Ends this owner's Say of the NPC (another owner's stays).</summary>
    public bool StopSay(SpeechLayer layer, uint npc, string owner)
    {
        lock (_gate)
        {
            if (!_npcs.TryGetValue(npc, out var n) || n.Says[(int)layer]?.Owner != owner) return false;
            n.Says[(int)layer] = null;
            Drop(npc, n);
            return true;
        }
    }

    /// <summary>A Say is running for the NPC (not past its time).</summary>
    public bool Saying(SpeechLayer layer, uint npc, long nowMs)
    {
        lock (_gate) return _npcs.TryGetValue(npc, out var n) && n.Says[(int)layer] is { } s && s.UntilMs > nowMs;
    }

    /// <summary>Drops everything an owner set (its mod unloaded); returns the NPCs whose state changed.</summary>
    public uint[] DropOwner(string owner)
    {
        lock (_gate)
        {
            var touched = new List<uint>();
            foreach (var (npc, n) in _npcs.ToArray())
            {
                var hit = false;
                foreach (var list in n.Overrides) hit |= list.RemoveAll(e => e.Owner == owner) > 0;
                foreach (var list in n.Topics) hit |= list.RemoveAll(e => e.Owner == owner) > 0;
                for (var i = 0; i < n.Says.Length; i++)
                    if (n.Says[i]?.Owner == owner) { n.Says[i] = null; hit = true; }
                if (hit) touched.Add(npc);
                Drop(npc, n);
            }
            return touched.ToArray();
        }
    }

    /// <summary>Drops a layer's overrides of some kinds (and, with <paramref name="says"/>, its Says; with
    /// <paramref name="topics"/>, its topics): a v1 or a kiln:npcdialog Reset from the server; returns the NPCs touched.</summary>
    public uint[] DropLayer(SpeechLayer layer, Func<NpcLineKind, bool> kinds, bool says, bool topics = false)
    {
        lock (_gate)
        {
            var touched = new List<uint>();
            foreach (var (npc, n) in _npcs.ToArray())
            {
                var hit = n.Overrides[(int)layer].RemoveAll(e => kinds(e.Kind)) > 0;
                if (says && n.Says[(int)layer] is not null)
                {
                    n.Says[(int)layer] = null;
                    hit = true;
                }
                if (topics && n.Topics[(int)layer].Count > 0)
                {
                    n.Topics[(int)layer].Clear();
                    hit = true;
                }
                if (hit) touched.Add(npc);
                Drop(npc, n);
            }
            return touched.ToArray();
        }
    }

    /// <summary>Drops a layer (a world ended: Local; the server connection ended: Remote); returns the NPCs touched.</summary>
    public uint[] DropLayer(SpeechLayer layer)
    {
        lock (_gate)
        {
            var touched = new List<uint>();
            foreach (var (npc, n) in _npcs.ToArray())
            {
                if (n.Overrides[(int)layer].Count > 0 || n.Says[(int)layer] is not null || n.Topics[(int)layer].Count > 0) touched.Add(npc);
                n.Overrides[(int)layer].Clear();
                n.Says[(int)layer] = null;
                n.Topics[(int)layer].Clear();
                Drop(npc, n);
            }
            return touched.ToArray();
        }
    }

    /// <summary>The override one owner set on the NPC in a layer (Game when none).</summary>
    public SpeechMode ModeOf(SpeechLayer layer, uint npc, string owner, NpcLineKind kind = NpcLineKind.Greeting)
    {
        lock (_gate)
            return _npcs.TryGetValue(npc, out var n) && n.Overrides[(int)layer].Find(e => e.Owner == owner && e.Kind == kind) is { } e
                ? e.Mode
                : SpeechMode.Game;
    }

    /// <summary>An owner's topics of the NPC become these (with their wire ids); its place on the page stays while it
    /// has any.</summary>
    public void SetTopics(SpeechLayer layer, uint npc, string owner, IReadOnlyList<(NpcTopic Topic, uint Wire)> topics)
    {
        lock (_gate)
        {
            var n = Get(npc);
            var list = n.Topics[(int)layer];
            var seq = list.Find(e => e.Owner == owner)?.Seq ?? ++_seq;
            list.RemoveAll(e => e.Owner == owner);
            list.AddRange(topics.Select(t => new TopicEntry(owner, t.Topic, t.Wire, seq, layer)));
            Drop(npc, n);
        }
    }

    /// <summary>One owner's topics of the NPC in a layer, in order.</summary>
    public IReadOnlyList<TopicEntry> TopicsOf(SpeechLayer layer, uint npc, string owner)
    {
        lock (_gate)
            return _npcs.TryGetValue(npc, out var n) ? n.Topics[(int)layer].Where(e => e.Owner == owner).ToArray() : [];
    }

    /// <summary>The NPC's page of Kiln topics: this process's own (Local) first, each owner's block where it began, then
    /// the server's; each line id once (a mod on both sides that set the same topic here and on the host: this process's
    /// own); at most <see cref="NpcTopics.MaxTopics"/>.</summary>
    public IReadOnlyList<TopicEntry> EffectiveTopics(uint npc, SpeechLayer? only = null)
    {
        lock (_gate)
        {
            if (!_npcs.TryGetValue(npc, out var n)) return [];
            var all = new List<TopicEntry>();
            var ids = new HashSet<uint>();
            foreach (var layer in new[] { 0, 1 })
                if (only is null || (int)only == layer)
                    foreach (var e in n.Topics[layer].Select((e, i) => (e, i)).OrderBy(x => x.e.Seq).ThenBy(x => x.i).Select(x => x.e))
                        if (ids.Add(e.Wire)) all.Add(e);   // kiln.npcspeech refuses a page with one id twice
            return all.Take(NpcTopics.MaxTopics).ToArray();
        }
    }

    /// <summary>What the NPC's greeting shows now: Local over Remote; the highest priority, then the most recent; a Say
    /// until its time.</summary>
    public SpeechEffect Effective(uint npc, long nowMs) => Effective(npc, NpcLineKind.Greeting, nowMs);

    /// <summary>What the NPC's line of <paramref name="kind"/> shows now (a Say only for the greeting).</summary>
    public SpeechEffect Effective(uint npc, NpcLineKind kind, long nowMs)
    {
        lock (_gate)
        {
            if (!_npcs.TryGetValue(npc, out var n)) return SpeechEffect.None;
            Entry? best = null;
            var from = SpeechLayer.Local;
            foreach (var layer in new[] { 0, 1 })
            {
                foreach (var e in n.Overrides[layer])
                    if (e.Kind == kind && (best is null || e.Priority > best.Priority || (e.Priority == best.Priority && e.Seq > best.Seq))) best = e;
                if (best is not null)
                {
                    from = (SpeechLayer)layer;
                    break;  // a Local override hides the Remote ones
                }
            }
            SayEntry? say = null;
            if (kind == NpcLineKind.Greeting)
                foreach (var layer in new[] { 0, 1 })
                    if (n.Says[layer] is { } s && s.UntilMs > nowMs) { say = s; break; }
            return new SpeechEffect(best?.Mode ?? SpeechMode.Game, best?.Lines ?? [], best?.Owner, say?.Text, say?.UntilMs ?? 0, say?.Owner)
            {
                Pick = best?.Pick ?? SpeechPick.Random,
                Layer = from,
            };
        }
    }

    private Npc Get(uint npc)
    {
        if (!_npcs.TryGetValue(npc, out var n)) _npcs[npc] = n = new Npc();
        return n;
    }

    private void Drop(uint npc, Npc n)
    {
        if (n.Empty) _npcs.Remove(npc);
    }
}

/// <summary>Texts of speech lines: their loca tag and JSON, and the limits.</summary>
internal static class SpeechText
{
    public const int MaxLines = 32;
    public const int MaxChars = 200;
    /// <summary>A line's JSON (every language of it) as UTF-8: one wire string, which a Kiln client reads with this
    /// limit (32 of them stay under the channel's 96 KiB).</summary>
    public const int MaxBytes = 2048;

    // Letters as they are ("Zurück", not "Zur\u00FCck"): six bytes for every non-ASCII letter would fill MaxBytes fast.
    // Quotes, backslashes and control characters are still escaped; the JSON only goes to kiln.content and to clients.
    private static readonly System.Text.Json.JsonSerializerOptions Options =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>{"en": "...", "*": "..."}: what kiln.content's loca hooks take, the languages in a stable order. A text
    /// with neither "en" nor "*" gets a "*" (the text UiText falls back to): kiln.content would leave every other
    /// language empty, an empty bubble.</summary>
    public static string Json(UiText? t)
    {
        if (t is null) return "";
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in t.Languages.Select(k => k.Trim().ToLowerInvariant()).Distinct()) map[k] = t.For(k);
        if (!map.ContainsKey("en") && !map.ContainsKey("*")) map["*"] = t.For("en");
        return System.Text.Json.JsonSerializer.Serialize(map, Options);
    }

    /// <summary>The text back from its JSON (a joined client's copy of what the server sent).</summary>
    public static UiText FromJson(string json)
    {
        var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? throw new FormatException("not a text map");
        if (map.Count == 0) throw new FormatException("a text without languages");
        return new UiText(map);
    }

    /// <summary>The loca tag Kiln's text answers to: FNV-1a-32 over "kiln.npcspeech" and the JSON (never 0). The same
    /// text always gets the same tag, so kiln.content keeps one entry per distinct text.</summary>
    public static uint Tag(string json)
    {
        var h = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes("kiln.npcspeech\n" + json))
        {
            h ^= b;
            h *= 16777619u;
        }
        return h == 0 ? 1u : h;
    }

    /// <summary>Null when the text is fine, else why not.</summary>
    public static string? Problem(UiText? t)
    {
        if (t is null) return "the text is null";
        if (t.Languages.Count == 0) return "the text has no language";
        foreach (var k in t.Languages)
        {
            var s = t.For(k);
            if (string.IsNullOrWhiteSpace(s)) return $"the text for '{k}' is empty";
            if (s.Length > MaxChars) return $"the text for '{k}' is longer than {MaxChars} characters";
        }
        var bytes = System.Text.Encoding.UTF8.GetByteCount(Json(t));
        if (bytes > MaxBytes)
            return $"its {t.Languages.Count} languages together are {bytes} bytes as sent to Kiln clients, more than {MaxBytes} (fewer languages or shorter texts)";
        return null;
    }
}
