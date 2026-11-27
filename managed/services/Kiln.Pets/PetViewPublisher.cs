// SPDX-License-Identifier: MIT
// The server's side of the owners' view (19d §4.8): each owner in game gets their own pets' full snapshot on kiln:pets,
// at most 5 times a second while something changed and once a second as a keepalive; never anyone else's, never to all.
// A fresh random nonzero session token per join; a strictly increasing sequence within it.
using System.Security.Cryptography;

namespace Kiln;

internal sealed class PetViewPublisher
{
    public const double MinInterval = 0.2, Keepalive = 1.0;

    private sealed class Link
    {
        public ulong Join;        // the owner's PetOwner.Session this token belongs to
        public ulong Token;
        public ulong Sequence;
        public double LastSent = double.NegativeInfinity;
        public bool Dirty = true;
    }

    private readonly Dictionary<CharacterKey, Link> _links = [];

    /// <summary>Tests: a fixed token source.</summary>
    internal Func<ulong> NewToken { get; set; } = () =>
    {
        Span<byte> b = stackalloc byte[8];
        ulong t;
        do
        {
            RandomNumberGenerator.Fill(b);
            t = BitConverter.ToUInt64(b);
        } while (t == 0);
        return t;
    };

    public void MarkDirty(CharacterKey owner)
    {
        if (_links.TryGetValue(owner, out var l)) l.Dirty = true;
    }

    /// <summary>One step: each owner in game whose view is due gets it through <paramref name="send"/> (false: it was not
    /// sent; tried again at the next due time). An owner whose game cannot take a view (<paramref name="reachable"/> false: a
    /// game without Kiln or without the channel, a lab bot) gets none built: its view stays due and goes out at the first
    /// step its game can take it.</summary>
    public void Step(PetRoster roster, IReadOnlyList<PetOwner> online, ulong epoch, double now, Func<PetOwner, byte[], bool> send,
        Func<PetOwner, bool>? reachable = null)
    {
        var seen = new HashSet<CharacterKey>();
        foreach (var o in online)
        {
            seen.Add(o.Key);
            if (!_links.TryGetValue(o.Key, out var l) || l.Join != o.Session)
                _links[o.Key] = l = new Link { Join = o.Session, Token = NewToken() };
            if (reachable is not null && !reachable(o)) continue;
            var due = l.Dirty ? now - l.LastSent >= MinInterval : now - l.LastSent >= Keepalive;
            if (!due) continue;
            var view = new PetOwnerView(epoch, l.Token, l.Sequence + 1, roster.ViewOf(o.Key).Take(PetViewCodec.MaxEntries).ToList());
            byte[] bytes;
            try { bytes = PetViewCodec.Write(view); }
            catch (ArgumentException e)
            {
                PetsRuntime.Log(LogLevel.Warn, $"Kiln.Pets: the view of {o.Key} was not sent: {e.Message}");
                l.LastSent = now;
                continue;
            }
            l.LastSent = now;
            if (!send(o, bytes)) continue;
            l.Sequence++;
            l.Dirty = false;
        }
        foreach (var gone in _links.Keys.Where(k => !seen.Contains(k)).ToList()) _links.Remove(gone);
    }
}
