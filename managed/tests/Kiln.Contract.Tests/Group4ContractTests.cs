// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Contract.Tests;

/// <summary>Kiln.Audio and Kiln.Vfx by the contract: the relay rows of the server side, and the thread attributes of
/// members whose thread does not depend on the side.</summary>
public sealed class AudioAndVfxContractTests : ContractTest
{
    [Theory]
    [InlineData("audio.relay")]
    [InlineData("vfx.relay")]
    [InlineData("screen.relay")]
    public void With_no_network_service_a_relay_row_is_off_and_names_kiln_net(string row)
    {
        StartRows(typeof(Sound).Assembly);
        StartRows(typeof(Vfx).Assembly);
        Assert.StartsWith("needs kiln.net", AssertOffline(row).ReasonText);
        // unavailable from the start: not a row that follows the session
        Assert.False(Capabilities.Get(row).SessionDependent);
    }

    [Fact]
    public void Available_says_what_the_rows_say()
    {
        StartRows(typeof(Sound).Assembly);
        StartRows(typeof(Vfx).Assembly);
        Assert.False(Sound.Available);
        Assert.False(Voiceover.Available);
        Assert.False(Vfx.Available);
        Assert.False(ScreenEffects.Available);
        Assert.Equal(Sound.Available, Capabilities.Is("audio.relay"));
        Assert.Equal(Vfx.Available, Capabilities.Is("vfx.relay"));
        Assert.Equal(ScreenEffects.Available, Capabilities.Is("screen.relay"));
    }

    [Fact]
    public void Reads_are_any_thread_and_relayed_calls_carry_no_thread_attribute()
    {
        AssertHas<AnyThreadAttribute>(Method(typeof(Sound), "IsPlaying", typeof(SoundHandle)));
        AssertHas<AnyThreadAttribute>(Property(typeof(Sound), "Status"));
        AssertHas<AnyThreadAttribute>(Method(typeof(Vfx), "State", typeof(VfxHandle)));
        AssertHas<AnyThreadAttribute>(Method(typeof(ScreenEffects), "IsPlaying", typeof(ScreenHandle)));
        // tick thread on the server side (the relay), any thread in the game client: neither attribute is true
        var play = Method(typeof(Sound), "PlayAt", typeof(SoundId), typeof(WorldPosition), typeof(float), typeof(float));
        Assert.Empty(play.GetCustomAttributes(typeof(TickThreadOnlyAttribute), false));
        Assert.Empty(play.GetCustomAttributes(typeof(AnyThreadAttribute), false));
    }
}
