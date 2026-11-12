// SPDX-License-Identifier: MIT
using TkBlock;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class LifecycleWatchdogTests
{
    [Theory]
    [InlineData("dispose-unload", "OnUnload")]
    [InlineData("dispose-event", "Stopping")]
    [InlineData("unload", "OnUnload")]
    [InlineData("reload", "OnUnload")]
    [InlineData("step", "Block")]
    public async Task PlainCallsAndDisposalStopBlockedModCode(string operation, string frame)
    {
        var kiln = new KilnHarness(new KilnHarnessOptions
        {
            CallTimeout = TimeSpan.FromMilliseconds(250),
            StopTimeout = TimeSpan.FromMilliseconds(250),
        });
        var mod = operation == "dispose-event" ? kiln.Load<TkStoppingBlockMod>(new ModManifest("tk-stuck"))
            : operation == "step" ? kiln.Load<TkTickBlockMod>(new ModManifest("tk-stuck"))
            : kiln.Load<TkUnloadBlockMod>(new ModManifest("tk-stuck"));
        if (operation != "step") kiln.Boot();
        var call = Task.Run(() => Record.Exception(() =>
        {
            switch (operation)
            {
                case "dispose-unload":
                case "dispose-event": kiln.Dispose(); break;
                case "unload": kiln.Unload(mod); break;
                case "reload": kiln.Reload(mod); break;
                default: kiln.Step(); break;
            }
        }), TestContext.Current.CancellationToken);
        try
        {
            var error = await call.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.NotNull(error);
            Assert.Contains("tk-stuck", error.Message);
            Assert.Contains(frame, error.Message);
            Assert.Contains("blocked the tick thread", error.Message);
            if (!operation.StartsWith("dispose", StringComparison.Ordinal))
            {
                Assert.IsType<HarnessStoppedException>(error);
                Assert.Equal(HarnessState.Abandoned, kiln.State);
                Assert.Throws<HarnessStoppedException>(() => kiln.Step());
            }
        }
        finally
        {
            // Keep the red-first test bounded even when the harness has no watchdog yet.
            if (!call.IsCompleted) kiln.TickThread.Thread.Interrupt();
            await call.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (kiln.State != HarnessState.Disposed) kiln.GiveUp("lifecycle watchdog test cleanup");
            kiln.Dispose();
        }
        Assert.True(kiln.TickThread.Thread.Join(TimeSpan.FromSeconds(5)));
        Assert.False(TestMods.Subscribed("tk-stuck"));
        using var next = new KilnHarness();
        next.Boot();
    }
}
