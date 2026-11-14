// SPDX-License-Identifier: MIT
// Kiln.Queries' path half on the test kit's kiln.queries (chapter 02 sections 5.2, 5.3): tickets, results, the event before
// the task, one end per request, refusals that are tickets and not exceptions, and what a result means for CanReach.
using Kiln;
using Kiln.TestKit;
using Xunit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace QueriesTests;

public sealed class PathTests
{
    private static readonly WorldPosition From = new(0, 10, 0), To = new(12, 10, 0);

    private static (KilnHarness Kiln, EntityId Anchor) Started()
    {
        var kiln = new KilnHarness();
        kiln.Step(2);
        var anchor = kiln.Entities.Create(From);
        kiln.Entities.SetBytes(anchor, "SpawnTime", BitConverter.GetBytes(42ul));
        return (kiln, anchor);
    }

    private static QueryPathResult Wait(KilnHarness kiln, Task<QueryPathResult> task, int steps = 12)
    {
        for (var i = 0; i < steps && !task.IsCompleted; i++) kiln.Step();
        Assert.True(task.IsCompleted, "the path request did not end");
        return task.Result;
    }

    [Fact]
    public void A_request_goes_through_its_states_and_the_event_of_its_end_comes_before_its_task()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        var events = new List<(PathQueryChangedEvent Event, bool TaskDone)>();
        QueryPathTicket? ticket = null;
        kiln.Run(() =>
        {
            Events.On<PathQueryChangedEvent>(e => events.Add((e, ticket!.Completion.IsCompleted)));
            ticket = Queries.RequestPath(new QueryPathRequest(anchor, From, To));
        });
        Assert.True(ticket!.Ok);
        Assert.Null(ticket.Reason);
        Assert.NotEqual(0ul, ticket.Request);
        Assert.False(ticket.Completion.IsCompleted);
        Assert.Empty(events);   // nothing is delivered from inside the call
        var r = Wait(kiln, ticket.Completion);
        Assert.Equal((true, null, QueryPathStatus.Found, true, (byte?)1), (r.Ok, r.Reason, r.Status, r.ReachesTarget, r.GameResult));
        Assert.Equal(ticket.Request, r.Request);
        Assert.Equal([From, To], r.Points.Select(p => p.Position));
        Assert.All(r.Points, p => Assert.Equal(QueryPathMove.Walk, p.Move));
        Assert.Equal(12, r.Length);
        Assert.Equal([(QueryPathState.Created, QueryPathState.Queued), (QueryPathState.Queued, QueryPathState.Searching), (QueryPathState.Searching, QueryPathState.Completed)],
            events.Select(e => (e.Event.Before, e.Event.After)));
        Assert.Equal([null, null, QueryPathStatus.Found], events.Select(e => e.Event.TerminalStatus));
        Assert.All(events, e => Assert.Equal((ticket.Request, anchor), (e.Event.Request, e.Event.Anchor)));
        Assert.All(events, e => Assert.False(e.TaskDone));   // also the last: handlers run before the task completes
        Assert.Equal(new FakePathRequest(anchor, From, To, 0.5, 2, 2000, false), Assert.Single(kiln.Queries.PathRequests));
        Assert.Equal(0, kiln.Queries.OpenPaths);
        kiln.Step(5);
        Assert.Equal(3, events.Count);   // one end, ever
    }

    public static TheoryData<string, QueryPathStatus, string?, bool, bool, string?> Answers => new()
    {
        // answer, status, path reason, CanReach ok, reachable, CanReach reason
        { "partial", QueryPathStatus.Partial, null, true, false, null },
        { "nopath", QueryPathStatus.NoPath, "NoPathFound", true, false, null },
        { "nostart", QueryPathStatus.NoStartNode, "NoStartNodeFound", false, false, "NoStartNodeFound" },
        { "notarget", QueryPathStatus.NoTargetNode, "NoTargetNodeFound", false, false, "NoTargetNodeFound" },
        { "iterations", QueryPathStatus.IterationLimit, "MaxIterationsReached", false, false, "MaxIterationsReached" },
        { "notloaded", QueryPathStatus.NotLoadedOrUnknown, "NoEngineAnswer", false, false, "NoEngineAnswer" },
        { "timeout", QueryPathStatus.TimedOut, "TimedOut", false, false, "TimedOut" },
        { "jump", QueryPathStatus.UnsupportedTraversal, "UnsupportedTraversal", false, false, "UnsupportedTraversal" },
    };

    private static FakePathAnswer Answer(string name) => name switch
    {
        "partial" => FakePathAnswer.Route(From, new WorldPosition(6, 10, 0)),
        "nopath" => FakePathAnswer.NoPath,
        "nostart" => FakePathAnswer.NoStartNode,
        "notarget" => FakePathAnswer.NoTargetNode,
        "iterations" => FakePathAnswer.IterationLimit,
        "notloaded" => FakePathAnswer.NotLoaded,
        "timeout" => FakePathAnswer.TimedOut,
        _ => FakePathAnswer.RouteWithMove(2, From, To),
    };

    [Theory]
    [MemberData(nameof(Answers))]
    public void What_the_search_answers_is_a_result_and_never_more_than_it_knows(string answer, QueryPathStatus status, string? reason, bool reachOk, bool reachable, string? reachReason)
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        kiln.Queries.Paths = _ => Answer(answer);
        var path = Wait(kiln, kiln.Run(() => Queries.FindPath(new QueryPathRequest(anchor, From, To))));
        Assert.Equal((status, reason), (path.Status, path.Reason));
        Assert.Equal(reason == null, path.Ok);
        Assert.False(path.ReachesTarget);
        if (status == QueryPathStatus.Partial) Assert.Equal((2, 6.0), (path.Points.Count, path.Length));
        else if (status == QueryPathStatus.UnsupportedTraversal)
        {
            // the points are kept for diagnosis, with the move the game named; it is no route: no length, not Ok
            Assert.Equal(2, path.Points.Count);
            Assert.Equal(QueryPathMove.JumpUp, path.Points[1].Move);
            Assert.Equal(0, path.Length);
        }
        else Assert.Empty(path.Points);

        var reach = kiln.Run(() => Queries.CanReach(new QueryPathRequest(anchor, From, To)));
        for (var i = 0; i < 12 && !reach.IsCompleted; i++) kiln.Step();
        Assert.Equal((reachOk, reachable, reachReason, status), (reach.Result.Ok, reach.Result.Reachable, reach.Result.Reason, reach.Result.Status));
    }

    [Fact]
    public void A_route_that_arrives_is_reachable_and_the_same_question_in_one_step_is_one_search()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        var request = new QueryPathRequest(anchor, From, To);
        var (a, b, other) = kiln.Run(() => (Queries.CanReach(request), Queries.CanReach(request with { }), Queries.CanReach(request with { To = new WorldPosition(12, 10, 5) })));
        Assert.Same(a, b);
        Assert.NotSame(a, other);
        for (var i = 0; i < 12 && !(a.IsCompleted && other.IsCompleted); i++) kiln.Step();
        Assert.Equal((true, true, null, QueryPathStatus.Found), (a.Result.Ok, a.Result.Reachable, a.Result.Reason, a.Result.Status));
        Assert.Equal(2, kiln.Queries.PathRequests.Count);
        // a later step asks again
        var later = kiln.Run(() => Queries.CanReach(request));
        Assert.NotSame(a, later);
        Assert.Equal(3, kiln.Queries.PathRequests.Count);
    }

    [Fact]
    public void A_refusal_is_a_ticket_that_is_complete_and_holds_no_slot()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        var saved = kiln.Entities.Create(From);
        kiln.Queries.WithoutSpawnTime(saved);
        var unmarked = kiln.Run(() => Queries.RequestPath(new QueryPathRequest(saved, From, To)));
        Assert.Equal((false, "OwnerIdentityUnavailable", 0ul), (unmarked.Ok, unmarked.Reason, unmarked.Request));
        Assert.True(unmarked.Completion.IsCompleted);
        var r = unmarked.Completion.Result;
        Assert.Equal((false, "OwnerIdentityUnavailable", QueryPathStatus.OwnerIdentityUnavailable, 0ul, 0u, (byte?)null, 0u), (r.Ok, r.Reason, r.Status, r.Request, r.Generation, r.GameResult, r.Steps));
        Assert.Empty(r.Points);

        var gone = kiln.Run(() => Queries.RequestPath(new QueryPathRequest(new EntityId(99999), From, To)));
        Assert.Equal((false, QueryPathStatus.OwnerGone), (gone.Ok, gone.Completion.Result.Status));
        Assert.Equal(0, kiln.Queries.OpenPaths);
        Assert.Empty(kiln.Queries.PathRequests);

        // eight are open for one caller; the ninth is refused, and none of the eight is disturbed
        var tickets = kiln.Run(() => Enumerable.Range(0, 9).Select(_ => Queries.RequestPath(new QueryPathRequest(anchor, From, To))).ToList());
        Assert.All(tickets.Take(8), t => Assert.True(t.Ok));
        Assert.Equal((false, "QueueFull", QueryPathStatus.QueueFull), (tickets[8].Ok, tickets[8].Reason, tickets[8].Completion.Result.Status));
        var reach = kiln.Run(() => Queries.CanReach(new QueryPathRequest(anchor, From, new WorldPosition(1, 10, 1))));
        Assert.Equal((false, false, "QueueFull"), (reach.Result.Ok, reach.Result.Reachable, reach.Result.Reason));
        foreach (var t in tickets.Take(8)) Assert.Equal(QueryPathStatus.Found, Wait(kiln, t.Completion).Status);
        Assert.True(kiln.Run(() => Queries.RequestPath(new QueryPathRequest(anchor, From, To))).Ok);   // the slots are free again
    }

    [Fact]
    public void Numbers_that_are_no_request_throw_before_anything_is_asked()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        var good = new QueryPathRequest(anchor, From, To);
        foreach (var bad in new[]
                 {
                     good with { Anchor = default }, good with { To = new WorldPosition(double.NaN, 0, 0) }, good with { To = new WorldPosition(201, 10, 0) },
                     good with { HorizontalSpace = -1 }, good with { VerticalSpace = 16.5 }, good with { MaxIterations = 0 }, good with { MaxIterations = 70000 },
                 })
        {
            Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.RequestPath(bad)));
            Assert.Throws<ArgumentException>(() => { kiln.Run(() => (object)Queries.FindPath(bad)); });
            Assert.Throws<ArgumentException>(() => { kiln.Run(() => (object)Queries.CanReach(bad)); });
        }
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.CancelPath(0)));
        Assert.Empty(kiln.Queries.PathRequests);
        // the same point twice is a request (the game answers a same-start route), and 200 m is the limit, not beyond it
        Assert.True(kiln.Run(() => Queries.RequestPath(good with { To = From })).Ok);
        Assert.True(kiln.Run(() => Queries.RequestPath(good with { To = new WorldPosition(200, 10, 0) })).Ok);
        Assert.Throws<InvalidOperationException>(() => Queries.RequestPath(good));   // off the tick thread
    }

    [Fact]
    public void A_cancelled_request_ends_once_as_cancelled()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        kiln.Queries.PathSteps = 6;
        var ends = new List<QueryPathStatus?>();
        var ticket = kiln.Run(() =>
        {
            Events.On<PathQueryChangedEvent>(e => { if (e.TerminalStatus != null) ends.Add(e.TerminalStatus); });
            return Queries.RequestPath(new QueryPathRequest(anchor, From, To));
        });
        kiln.Step();
        var cancel = kiln.Run(() => Queries.CancelPath(ticket.Request));
        Assert.Equal((true, null, ticket.Request), (cancel.Ok, cancel.Reason, cancel.Request));
        var again = kiln.Run(() => Queries.CancelPath(ticket.Request));
        Assert.Equal((false, "UnknownRequest"), (again.Ok, again.Reason));
        Assert.Equal((false, "UnknownRequest"), kiln.Run(() => { var c = Queries.CancelPath(424242); return (c.Ok, c.Reason); }));
        var r = Wait(kiln, ticket.Completion);
        Assert.Equal((false, "Cancelled", QueryPathStatus.Cancelled, (byte?)null), (r.Ok, r.Reason, r.Status, r.GameResult));
        kiln.Step(8);
        Assert.Equal([QueryPathStatus.Cancelled], ends);

        // a token cancels the same way, from any thread: the task completes, it is not a cancelled task
        using var source = new CancellationTokenSource();
        var task = kiln.Run(() => Queries.FindPath(new QueryPathRequest(anchor, From, To), source.Token));
        kiln.Step();
        source.Cancel();
        var byToken = Wait(kiln, task);
        Assert.Equal(QueryPathStatus.Cancelled, byToken.Status);
        Assert.False(task.IsCanceled);
    }

    [Fact]
    public void An_anchor_that_goes_ends_the_request_as_owner_gone()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        kiln.Queries.PathSteps = 6;
        var task = kiln.Run(() => Queries.FindPath(new QueryPathRequest(anchor, From, To)));
        kiln.Step(2);
        kiln.Entities.Destroy(anchor);
        var r = Wait(kiln, task);
        Assert.Equal((false, "OwnerGone", QueryPathStatus.OwnerGone), (r.Ok, r.Reason, r.Status));
        Assert.Empty(r.Points);
        Assert.Equal(0, kiln.Queries.OpenPaths);
    }

    [Fact]
    public void A_row_that_is_off_throws_by_its_name()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        var request = new QueryPathRequest(anchor, From, To);
        Assert.True(kiln.Run(() => Capabilities.Is("queries.path") && Capabilities.Is("queries.reachability")));
        kiln.Capabilities.Override("queries.path", false, "switched off by the test");
        Assert.Equal("queries.path", Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Queries.RequestPath(request))).Capability);
        Assert.Equal("queries.path", Assert.Throws<KilnUnavailableException>(() => { kiln.Run(() => (object)Queries.FindPath(request)); }).Capability);
        Assert.Equal("queries.reachability", Assert.Throws<KilnUnavailableException>(() => { kiln.Run(() => (object)Queries.CanReach(request)); }).Capability);
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Queries.RequestPath(request with { MaxIterations = 0 })));   // still an argument error
        Assert.Empty(kiln.Queries.PathRequests);
    }

    [Fact]
    public void The_reachability_mapping_knows_only_three_answers()
    {
        static QueryPathResult R(QueryPathStatus s, string? reason = null) => new(reason == null, reason, 1, 1, s, null, s == QueryPathStatus.Found, [], 0, 1);
        Assert.Equal((true, true), (Queries.Reachability(R(QueryPathStatus.Found)).Ok, Queries.Reachability(R(QueryPathStatus.Found)).Reachable));
        foreach (var no in new[] { QueryPathStatus.Partial, QueryPathStatus.NoPath })
            Assert.Equal((true, false, null), (Queries.Reachability(R(no, no == QueryPathStatus.NoPath ? "NoFreePath" : null)).Ok, Queries.Reachability(R(no)).Reachable, Queries.Reachability(R(no, no == QueryPathStatus.NoPath ? "NoFreePath" : null)).Reason));
        foreach (var unknown in Enum.GetValues<QueryPathStatus>().Except([QueryPathStatus.Found, QueryPathStatus.Partial, QueryPathStatus.NoPath]))
        {
            var r = Queries.Reachability(R(unknown, unknown.ToString()));
            Assert.Equal((false, false, unknown.ToString(), unknown), (r.Ok, r.Reachable, r.Reason, r.Status));
        }
    }

    [Fact]
    public unsafe void The_path_mirrors_are_the_headers_sizes()
    {
        Assert.Equal(80, sizeof(QueriesNative.PathRequest));
        Assert.Equal(40, sizeof(QueriesNative.PathAdmission));
        Assert.Equal(88, sizeof(QueriesNative.PathUpdate));
        Assert.Equal(32, sizeof(QueriesNative.PathPoint));
    }

    private static int _pollFault;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int FaultyPoll(ulong owner, QueriesNative.PathUpdate* update, QueriesNative.PathPoint* points, uint capacity, uint* needed)
    {
        *needed = _pollFault == 1 ? 4097u : 0u;
        update->Before = 99;
        return _pollFault == 1 ? QueriesNative.BufferTooSmall : _pollFault == 2 ? QueriesNative.Revoked : QueriesNative.Ok;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public unsafe void A_bad_or_revoked_native_mailbox_does_not_leave_tasks_waiting(int fault)
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        var tasks = kiln.Run(() => Enumerable.Range(0, 2).Select(_ => Queries.FindPath(new QueryPathRequest(anchor, From, To))).ToArray());
        var table = QueriesNative.T;
        var original = table->PathPoll;
        _pollFault = fault;
        try
        {
            table->PathPoll = &FaultyPoll;
            kiln.Run(PathPump.Pump);
            Assert.All(tasks, t => Assert.True(t.IsCompletedSuccessfully, "an accepted task hung after a native mailbox failure"));
            Assert.All(tasks, t => Assert.Equal(fault == 2 ? QueryPathStatus.OwnerRetired : QueryPathStatus.Refused, t.Result.Status));
            Assert.Equal(0, kiln.Queries.OpenPaths);
        }
        finally { table->PathPoll = original; Capabilities.Reset(); }
    }

    [Fact]
    public void A_cached_reachability_answer_still_checks_arguments_thread_and_capability()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        var request = new QueryPathRequest(anchor, From, new WorldPosition(200, 10, 0));
        kiln.Run(() => Queries.CanReach(request));
        Assert.Throws<ArgumentException>(() => { kiln.Run(() => (object)Queries.CanReach(request with { To = new WorldPosition(200.1, 10, 0) })); });
        Assert.Throws<InvalidOperationException>(() => { var task = Queries.CanReach(request); Assert.NotNull(task); });
        kiln.Capabilities.Override("queries.path", false, "off after admission");
        Assert.Equal("queries.reachability", Assert.Throws<KilnUnavailableException>(() => { kiln.Run(() => (object)Queries.CanReach(request)); }).Capability);
    }

    [Fact]
    public async Task A_reachability_cancellation_does_not_borrow_another_callers_token()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        using var cancel = new CancellationTokenSource();
        var request = new QueryPathRequest(anchor, From, To);
        var a = kiln.Run(() => Queries.CanReach(request));
        var b = kiln.Run(() => Queries.CanReach(request, cancel.Token));
        cancel.Cancel();
        kiln.Step(8);
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(a.IsCompletedSuccessfully && b.IsCompletedSuccessfully);
        Assert.Equal(QueryPathStatus.Found, a.Result.Status);
        Assert.Equal(QueryPathStatus.Cancelled, b.Result.Status);
    }

    [Fact]
    public void A_reentrant_admission_waits_for_a_later_step_to_dispatch()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        QueryPathTicket? child = null;
        ulong childStep = 0;
        var childEvents = new List<ulong>();
        kiln.Run(() =>
        {
            Events.On<PathQueryChangedEvent>(e =>
            {
                if (child == null)
                {
                    childStep = ServiceTick.Step;
                    child = Queries.RequestPath(new QueryPathRequest(anchor, From, To));
                    Queries.CancelPath(child.Request);
                }
                else if (e.Request == child.Request) childEvents.Add(ServiceTick.Step);
            });
            Queries.RequestPath(new QueryPathRequest(anchor, From, To));
        });
        kiln.Step(4);
        Assert.NotEmpty(childEvents);
        Assert.All(childEvents, step => Assert.True(step > childStep, "the pump dispatched a reentrant request in its admission step"));
        Assert.True(child!.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public void A_surviving_mod_gets_a_fresh_private_lease_after_world_close()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        var mod = HostAccess.CreateContext(new ModInfo("path-lease-test", "Path lease test", "1", ""), Backend.Current.OwnerCreate("path-lease-test"), "");
        try
        {
            kiln.Run(() =>
            {
                ModContext.CurrentMod = mod;
                try
                {
                    var firstOwner = PathPump.OwnerOfCaller();
                    Assert.NotEqual(mod.Owner, firstOwner);
                    var first = Queries.FindPath(new QueryPathRequest(anchor, From, To));
                    Events.Post(new ServerStoppingEvent(ServiceTick.Step, "world-stopping"));
                    Assert.True(first.IsCompletedSuccessfully);
                    Assert.Equal(QueryPathStatus.WorldClosed, first.Result.Status);
                    Assert.Equal(0, kiln.Queries.OpenPaths);
                    Lifecycle.Phase = ServerPhase.Booting;
                    Assert.NotEqual(firstOwner, PathPump.OwnerOfCaller());
                }
                finally { ModContext.CurrentMod = null; }
            });
        }
        finally { kiln.Run(mod.Retire); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_world_close_completes_now_and_notifies_surviving_code_on_the_tick_thread(bool retire)
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        var mod = HostAccess.CreateContext(new ModInfo("path-close-test", "Path close test", "1", ""), Backend.Current.OwnerCreate("path-close-test"), "");
        var ends = new List<PathQueryChangedEvent>();
        Task<QueryPathResult>? task = null;
        try
        {
            kiln.Run(() =>
            {
                ModContext.CurrentMod = mod;
                try
                {
                    Events.On<PathQueryChangedEvent>(e => { if (e.TerminalStatus != null) ends.Add(e); });
                    task = Queries.FindPath(new QueryPathRequest(anchor, From, To));
                }
                finally { ModContext.CurrentMod = null; }
            });
            kiln.Step(); // observe generation and Searching before the stop
            var nativeSession = 1u; // this fake world's generation
            kiln.Run(() => Events.Post(new ServerStoppingEvent(ServiceTick.Step, "world-stopping")));
            Assert.True(task!.IsCompletedSuccessfully);
            Assert.Equal((QueryPathStatus.WorldClosed, nativeSession), (task.Result.Status, task.Result.Generation));
            Assert.Empty(ends);
            if (retire) kiln.Run(mod.Retire);
            kiln.Step();
            if (retire) Assert.Empty(ends);
            else Assert.Equal(QueryPathStatus.WorldClosed, Assert.Single(ends).TerminalStatus);
        }
        finally { kiln.Run(mod.Retire); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_path_fake_refuses_malformed_and_overlong_routes_as_results(bool overlong)
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        kiln.Queries.Paths = _ => overlong ? FakePathAnswer.Route(Enumerable.Repeat(From, 4097).ToArray()) : FakePathAnswer.Route(From, new WorldPosition(double.NaN, 10, 0));
        var result = Wait(kiln, kiln.Run(() => Queries.FindPath(new QueryPathRequest(anchor, From, To))));
        Assert.Equal(overlong ? QueryPathStatus.Truncated : QueryPathStatus.MalformedResult, result.Status);
        Assert.False(result.Ok);
        Assert.Empty(result.Points);
        Assert.True(kiln.Run(() => Capabilities.Is("queries.path")));
    }

    [Fact]
    public void Losing_the_anchors_spawn_marker_also_ends_a_fake_search()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        var task = kiln.Run(() => Queries.FindPath(new QueryPathRequest(anchor, From, To)));
        kiln.Step();
        kiln.Queries.WithoutSpawnTime(anchor);
        Assert.Equal(QueryPathStatus.OwnerGone, Wait(kiln, task).Status);
    }

    [Fact]
    public void Changing_the_anchors_stamp_does_not_transfer_a_fake_search_to_the_replacement()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        kiln.Entities.SetBytes(anchor, "SpawnTime", BitConverter.GetBytes(42ul));
        var task = kiln.Run(() => Queries.FindPath(new QueryPathRequest(anchor, From, To)));
        kiln.Entities.SetBytes(anchor, "SpawnTime", BitConverter.GetBytes(43ul));
        Assert.Equal(QueryPathStatus.OwnerGone, Wait(kiln, task).Status);
    }

    [Fact]
    public void The_fake_starts_at_most_eight_searches_per_step_across_owners()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        var mods = Enumerable.Range(0, 4).Select(i => HostAccess.CreateContext(new ModInfo("path-budget-" + i, "Path budget", "1", ""), Backend.Current.OwnerCreate("path-budget-" + i), "")).ToArray();
        var answered = 0;
        kiln.Queries.PathSteps = 1;
        kiln.Queries.Paths = r => { answered++; return FakePathAnswer.Route(r.From, r.To); };
        try
        {
            kiln.Run(() =>
            {
                try
                {
                    foreach (var mod in mods)
                    {
                        ModContext.CurrentMod = mod;
                        for (var i = 0; i < 8; ++i) Assert.True(Queries.RequestPath(new QueryPathRequest(anchor, From, To)).Ok);
                    }
                }
                finally { ModContext.CurrentMod = null; }
            });
            kiln.Step();
            Assert.Equal(8, answered);
            kiln.Step();
            Assert.Equal(16, answered);
        }
        finally { foreach (var mod in mods) kiln.Run(mod.Retire); }
    }

    [Fact]
    public void A_world_close_before_the_first_pump_keeps_the_native_generation()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        var task = kiln.Run(() => Queries.FindPath(new QueryPathRequest(anchor, From, To)));
        kiln.Run(() => Events.Post(new ServerStoppingEvent(ServiceTick.Step, "world-stopping")));
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal((QueryPathStatus.WorldClosed, 1u), (task.Result.Status, task.Result.Generation));
    }

    [Fact]
    public void An_already_cancelled_token_wins_before_the_next_engine_step()
    {
        var (kiln, anchor) = Started();
        using var _ = kiln;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        kiln.Queries.PathSteps = 1;
        var task = kiln.Run(() => Queries.FindPath(new QueryPathRequest(anchor, From, To), cancel.Token));
        Assert.Equal(QueryPathStatus.Cancelled, Wait(kiln, task).Status);
    }
}
