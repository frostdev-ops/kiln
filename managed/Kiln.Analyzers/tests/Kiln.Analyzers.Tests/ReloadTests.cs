// SPDX-License-Identifier: MIT
using Xunit;
using static Kiln.Analyzers.Tests.Harness;

namespace Kiln.Analyzers.Tests;

public sealed class ProcessWideTests
{
    private static readonly ProcessWideAnalyzer A = new();

    private static readonly Microsoft.CodeAnalysis.MetadataReference Lib = Library("""
        namespace Shared;
        public static class Registry
        {
            public static readonly List<object> Items = new();
            public static Action? Hook;
            public static object? Current { get; set; }
            public static event Action? Changed;
            public static void Raise() => Changed?.Invoke();
        }
        """);

    [Fact]
    public async Task Process_wide_events()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    AppDomain.CurrentDomain.ProcessExit += OnExit;
                    TaskScheduler.UnobservedTaskException += (s, e) => { };
                    Shared.Registry.Changed += OnChanged;
                    Context.Tick.Pre += (in TickInfo t) => { };
                    Chat.Received += m => { };
                    Players.Joined += p => { };
                    Mine += () => { };
                }
                public static event Action? Mine;
                private void OnExit(object? s, EventArgs e) { }
                private void OnChanged() { }
                public override void OnUnload() => Shared.Registry.Changed -= OnChanged;
            }
            """, extra: [Lib]);
        Ids(d, "KILN103", "KILN103");
        Assert.Contains("AppDomain.CurrentDomain.ProcessExit", d[0].GetMessage());
        Assert.Equal("unsubscribe", d[0].Properties[ProcessWideAnalyzer.FixKey]);
        Assert.Contains("TaskScheduler.UnobservedTaskException", d[1].GetMessage());
        Assert.False(d[1].Properties.ContainsKey(ProcessWideAnalyzer.FixKey));
    }

    [Fact]
    public async Task Fix_adds_the_removal_to_OnUnload()
    {
        var s = await Fix(A, new ProcessWideEventCodeFix(), """
            public sealed class M : Mod
            {
                public override void OnLoad() => AppDomain.CurrentDomain.ProcessExit += OnExit;
                private void OnExit(object? s, EventArgs e) { }
            }
            """);
        Assert.Contains("public override void OnUnload()", s);
        Assert.Contains("AppDomain.CurrentDomain.ProcessExit -= OnExit;", s);
    }

    [Fact]
    public async Task Fix_extends_an_existing_OnUnload()
    {
        var s = await Fix(A, new ProcessWideEventCodeFix(), """
            public sealed class M : Mod
            {
                public override void OnLoad() => AppDomain.CurrentDomain.ProcessExit += OnExit;
                public override void OnUnload() => Log.Info("bye");
                private void OnExit(object? s, EventArgs e) { }
            }
            """);
        Assert.Contains("Log.Info(\"bye\");", s);
        Assert.Contains("AppDomain.CurrentDomain.ProcessExit -= OnExit;", s);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(s!, "OnUnload"));
    }

    [Fact]
    public async Task Mod_objects_in_process_wide_state()
    {
        var d = await Run(A, """
            using System.Runtime.InteropServices;
            public sealed class M : Mod
            {
                GCHandle _weak;
                public override void OnLoad()
                {
                    Shared.Registry.Items.Add(this);
                    Shared.Registry.Items.Add("just text");
                    Shared.Registry.Hook = OnHook;
                    Shared.Registry.Current = new Settings();
                    Shared.Registry.Current = 42;
                    AppContext.SetData("my-mod", new Settings());
                    GCHandle.Alloc(this);
                    _weak = GCHandle.Alloc(this, GCHandleType.Weak);
                    System.ComponentModel.TypeDescriptor.AddAttributes(typeof(Settings), new System.ComponentModel.DescriptionAttribute("x"));
                }
                private void OnHook() { }
            }
            public sealed class Settings { }
            """, extra: [Lib]);
        Ids(d, "KILN104", "KILN104", "KILN104", "KILN104", "KILN104", "KILN104");
        Assert.Contains("Registry.Items", d[0].GetMessage());
        Assert.Contains("a delegate to OnHook", d[1].GetMessage());
        Assert.Contains("a Settings", d[2].GetMessage());
        Assert.Contains("SetData", d[3].GetMessage());
        Assert.Contains("GCHandle", d[4].GetMessage());
        Assert.Contains("TypeDescriptor", d[5].GetMessage());
    }

    [Fact]
    public async Task Freed_handles_and_kiln_state_are_fine()
    {
        var d = await Run(A, """
            using System.Runtime.InteropServices;
            public sealed class M : Mod
            {
                GCHandle _h;
                public static M? Instance;
                public override void OnLoad()
                {
                    _h = GCHandle.Alloc(this);
                    Instance = this;
                    Chat.ServerPrefix = "[M] ";
                }
                public override void OnUnload() => _h.Free();
            }
            """);
        Ids(d);
    }
}

public sealed class UnreleasedTests
{
    private static readonly UnreleasedAnalyzer A = new();

    [Fact]
    public async Task Timers_threads_and_watchers()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                System.Threading.Timer? _timer;
                System.Threading.Timer? _released;
                FileSystemWatcher _watch = new(".");
                public override void OnLoad()
                {
                    _timer = new System.Threading.Timer(_ => { }, null, 0, 1000);
                    _released = new System.Threading.Timer(_ => { }, null, 0, 1000);
                    new Thread(() => { }).Start();
                    var local = new System.Timers.Timer(100);
                    local.Start();
                    using var fs = new FileStream("x", FileMode.Create);
                    using (var w = new StreamWriter("y")) { }
                    var handed = new System.Timers.Timer(5);
                    Keep(handed);
                    Commands.Register("hi", c => { });
                }
                private static void Keep(object o) { }
                public override void OnUnload() => _released?.Dispose();
            }
            """);
        Ids(d, "KILN106", "KILN106", "KILN106", "KILN106");
        Assert.Contains("'_watch'", d[0].GetMessage());
        Assert.Equal("dispose", d[0].Properties[UnreleasedAnalyzer.FixKey]);
        Assert.Contains("'_timer'", d[1].GetMessage());
        Assert.Contains("A thread", d[2].GetMessage());
        Assert.Contains("local 'local'", d[3].GetMessage());
    }

    [Fact]
    public async Task Released_through_another_object_in_another_type()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                sealed class Conn { public Thread? Reader; public System.Threading.Timer? Ping; }
                readonly List<Conn> _conns = new();
                public override void OnLoad()
                {
                    var c = new Conn();
                    c.Reader = new Thread(() => { }) { IsBackground = true };
                    c.Reader.Start();
                    c.Ping = new System.Threading.Timer(_ => { }, null, 0, 100);
                    _conns.Add(c);
                }
                public override void OnUnload()
                {
                    foreach (var c in _conns) { Join(c.Reader); c.Ping?.Dispose(); }
                }
                private static void Join(Thread? t) => t?.Join(1000);
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task Fix_disposes_in_OnUnload()
    {
        var s = await Fix(A, new UnreleasedCodeFix(), """
            public sealed class M : Mod
            {
                System.Threading.Timer? _timer;
                public override void OnLoad() => _timer = new System.Threading.Timer(_ => { }, null, 0, 1000);
            }
            """);
        Assert.Contains("_timer?.Dispose();", s);
        Assert.Contains("public override void OnUnload()", s);
    }
}
