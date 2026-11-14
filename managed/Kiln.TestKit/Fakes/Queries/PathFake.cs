// SPDX-License-Identifier: MIT
// The path half of the fake kiln.queries: the owner-scoped pull queue of kiln_queries.h with the service's rules, and a
// scripted answer in place of the game's search.
namespace Kiln.TestKit;

internal static unsafe class PathFake
{
    public const string Feature = "queries.path";
    private const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5, Revoked = 6;
    private const uint Created = 0, Queued = 1, Searching = 2, Completed = 3, Cancelled = 4, TimedOut = 5;
    private const uint NoGameResult = 0xffffffff;
    private const int GlobalSlots = 32, OwnerSlots = 8;

    private static KilnHarness? Harness => FakeTables.Active(QueriesAbi.Service);
    private static QueriesFakeState State(KilnHarness h) => KitFakes.State<QueriesFakeState>(h);

    private static void Push(QueriesFakeState s, ulong owner, FakePathUpdate u)
    {
        if (s.Retired.Contains(owner)) return;
        if (!s.Fifos.TryGetValue(owner, out var q)) s.Fifos[owner] = q = new();
        q.Enqueue(u);
    }

    private static void End(QueriesFakeState s, FakePath p, uint stage, FakePathAnswer a)
    {
        if (p.Terminal) return;
        p.Terminal = true;
        var before = p.Stage;
        p.Stage = stage;
        if (s.Retired.Contains(p.Owner)) p.Consumed = true;
        Push(s, p.Owner, new FakePathUpdate(p.Token, before, stage, p.Request.Anchor.Value, true, (uint)a.Status, a.Ok, a.GameResult ?? NoGameResult, a.Reaches, a.Ok ? a.Length : 0,
            a.Ok ? "" : a.Reason, a.Points, (uint)p.Age));
        Release(s, p);
    }

    private static void Release(QueriesFakeState s, FakePath p)
    {
        if (p.Terminal && p.Consumed) s.Open.Remove(p.Token);
    }

    public static int Submit(ulong owner, QueriesAbi.PathRequest* r, QueriesAbi.PathAdmission* a, Func<string, NativeStr> str)
    {
        if (a == null || a->Size < (uint)sizeof(QueriesAbi.PathAdmission)) return InvalidArgument;
        *a = new QueriesAbi.PathAdmission { Size = a->Size, Reason = str("") };
        if (owner == 0 || r == null || r->Size < (uint)sizeof(QueriesAbi.PathRequest) || r->Anchor == 0) return InvalidArgument;
        var from = new WorldPosition(r->From.X, r->From.Y, r->From.Z);
        var to = new WorldPosition(r->To.X, r->To.Y, r->To.Z);
        static bool Fin(WorldPosition p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
        if (!Fin(from) || !Fin(to) || !(from.DistanceTo(to) <= 200) || !(r->HorizontalSpace is >= 0 and <= 16) || !(r->VerticalSpace is >= 0 and <= 16) ||
            r->MaxIterations is < 1 or > 65535 || r->ClosestFallback > 1) return InvalidArgument;
        if (!FakeTables.OnTickThread) return WrongThread;
        if (Harness is not { } h || FakeTables.Off(Feature) || h.EntitiesFake is not { Started: true } world) return Unavailable;
        var s = State(h);
        lock (s.Lock)
        {
            if (s.Retired.Contains(owner)) return Revoked;
            int Refuse(QueryPathStatusFake status, string reason)
            {
                a->Status = (uint)status;
                a->Reason = str(reason);
                return Ok;
            }
            if (!world.Exists(r->Anchor) || world.PositionOf(r->Anchor) is null) return Refuse(QueryPathStatusFake.OwnerGone, "OwnerGone");
            var stamp = world.Bytes(r->Anchor, "SpawnTime");
            if (s.Unstamped.Contains(r->Anchor) || stamp is not { Length: 8 }) return Refuse(QueryPathStatusFake.OwnerIdentityUnavailable, "OwnerIdentityUnavailable");
            if (s.Open.Count >= GlobalSlots || s.Open.Values.Count(p => p.Owner == owner) >= OwnerSlots) return Refuse(QueryPathStatusFake.QueueFull, "QueueFull");
            var request = new FakePathRequest(new EntityId(r->Anchor), from, to, r->HorizontalSpace, r->VerticalSpace, r->MaxIterations, r->ClosestFallback != 0);
            var token = (ulong)(s.PathRequests.Count + 1);
            s.PathRequests.Add(request);
            s.Open[token] = new FakePath { Token = token, Owner = owner, Request = request, Stamp = BitConverter.ToUInt64(stamp) };
            Push(s, owner, new FakePathUpdate(token, Created, Queued, r->Anchor, false, 0, false, NoGameResult, false, 0, "", [], 0));
            a->Ok = 1;
            a->Request = token;
        }
        return Ok;
    }

    public static int Cancel(ulong owner, ulong token)
    {
        if (owner == 0 || token == 0) return InvalidArgument;
        if (!FakeTables.OnTickThread) return WrongThread;
        if (Harness is not { } h) return Unavailable;
        var s = State(h);
        lock (s.Lock)
        {
            if (s.Retired.Contains(owner)) return Revoked;
            if (!s.Open.TryGetValue(token, out var p) || p.Owner != owner || p.Terminal) return NotFound;
            End(s, p, Cancelled, FakePathAnswer.Ended(QueryPathStatusFake.Cancelled, "Cancelled"));
        }
        return Ok;
    }

    public static int Poll(ulong owner, QueriesAbi.PathUpdate* update, QueriesAbi.PathPoint* points, uint capacity, uint* needed, Func<string, NativeStr> str)
    {
        if (owner == 0 || update == null || update->Size < (uint)sizeof(QueriesAbi.PathUpdate) || needed == null) return InvalidArgument;
        *needed = 0;
        if (KitBackend.Instance.Harness is not { } h) return NotFound;   // polling needs no world and no capability
        var s = State(h);
        lock (s.Lock)
        {
            if (s.Retired.Contains(owner)) return Revoked;
            if (!s.Fifos.TryGetValue(owner, out var q) || q.Count == 0) return NotFound;
            var u = q.Peek();
            *needed = (uint)u.Points.Count;
            if (u.Points.Count > capacity || (u.Points.Count > 0 && points == null)) return BufferTooSmall;
            q.Dequeue();
            *update = new QueriesAbi.PathUpdate
            {
                Size = update->Size, Before = u.Before, After = u.After, Status = u.Status, Request = u.Token, Sequence = ++s.Sequence, Generation = 1, Anchor = u.Anchor,
                Terminal = u.Terminal ? 1u : 0, Ok = u.Ok ? 1u : 0, GameResult = u.GameResult, ReachesTarget = u.Reaches ? 1u : 0, PointCount = (uint)u.Points.Count, Steps = u.Steps,
                Length = u.Length, Reason = str(u.Reason),
            };
            for (var i = 0; i < u.Points.Count; i++)
                points[i] = new QueriesAbi.PathPoint { Position = new QueriesAbi.Vec3 { X = u.Points[i].Position.X, Y = u.Points[i].Position.Y, Z = u.Points[i].Position.Z }, RawMove = u.Points[i].Move };
            if (u.Terminal && s.Open.TryGetValue(u.Token, out var p))
            {
                p.Consumed = true;
                Release(s, p);
            }
        }
        return Ok;
    }

    public static int Retire(ulong owner)
    {
        if (owner == 0) return InvalidArgument;
        if (KitBackend.Instance.Harness is not { } h) return Ok;
        var s = State(h);
        lock (s.Lock)
        {
            s.Retired.Add(owner);
            s.Fifos.Remove(owner);
            foreach (var p in s.Open.Values.Where(p => p.Owner == owner).ToList()) s.Open.Remove(p.Token);
        }
        return Ok;
    }

    /// <summary>One step of the fake search: a queued request starts searching, a searching one answers when its steps are up;
    /// a request whose anchor went ends OwnerGone.</summary>
    public static void Step(KilnHarness h)
    {
        var s = State(h);
        List<(FakePath Path, Func<FakePathRequest, FakePathAnswer>? Script)> due = [];
        lock (s.Lock)
        {
            var creates = 0;
            foreach (var p in s.Open.Values.Where(p => !p.Terminal).ToList())
            {
                p.Age++;
                if (h.EntitiesFake is not { } world || !world.Exists(p.Request.Anchor.Value) || world.PositionOf(p.Request.Anchor.Value) == null ||
                    s.Unstamped.Contains(p.Request.Anchor.Value) || world.Bytes(p.Request.Anchor.Value, "SpawnTime") is not { Length: 8 } stamp || BitConverter.ToUInt64(stamp) != p.Stamp)
                {
                    End(s, p, Cancelled, FakePathAnswer.Ended(QueryPathStatusFake.OwnerGone, "OwnerGone"));
                    continue;
                }
                if (p.Stage == Queued)
                {
                    if (creates == 8) continue;
                    creates++;
                    p.Stage = Searching;
                    Push(s, p.Owner, new FakePathUpdate(p.Token, Queued, Searching, p.Request.Anchor.Value, false, 0, false, NoGameResult, false, 0, "", [], (uint)p.Age));
                }
                if (p.Age >= s.PathSteps) due.Add((p, s.Paths));
            }
        }
        foreach (var (p, script) in due)
        {
            var answer = script?.Invoke(p.Request) ?? FakePathAnswer.Route(p.Request.From, p.Request.To);
            lock (s.Lock) End(s, p, answer.Status == QueryPathStatusFake.TimedOut ? TimedOut : Completed, answer.For(p.Request));
        }
    }
}

// the service's status numbers (kiln_queries.h), for the fake's own use
internal enum QueryPathStatusFake
{
    Found = 1, Partial = 2, NoPath = 3, NoStartNode = 4, NoTargetNode = 5, IterationLimit = 6, OutOfMemory = 7, NotLoadedOrUnknown = 8, Cancelled = 9, OwnerGone = 10, TimedOut = 11,
    Truncated = 12, Refused = 13, UnsupportedTraversal = 14, MalformedResult = 15, QueueFull = 16, OwnerIdentityUnavailable = 17, OutOfGrid = 18, WorldClosed = 19,
}
