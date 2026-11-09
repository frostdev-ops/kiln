// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Api.Tests;

public sealed class FixtureLifetimeTests : KilnTest
{
    private static KilnTest Fixture(string kind) => kind switch
    {
        "world" => new EcsProgramsTests(),
        "systems" => new GameSystemsHookTests(),
        "blobs" => new AssetsBlobTests(),
        "resources" => new ReflectedResourceTests(),
        _ => throw new ArgumentException(nameof(kind)),
    };

    [Theory]
    [InlineData("world", "kiln.world")]
    [InlineData("systems", "kiln.types")]
    [InlineData("blobs", "kiln.assets")]
    [InlineData("resources", "kiln.assets")]
    public void Fixture_retires_owners_before_withdrawing_its_services(string kind, string service)
    {
        var cleanups = 0;
        var servicesAliveDuringCleanup = false;
        FakeBackend backend;
        using (var fixture = Fixture(kind))
        {
            backend = fixture.Backend;
            var registered = backend.Service(service, 1);
            Assert.NotEqual(0, registered);
            fixture.NewMod("fixture-owner").Retiring += () =>
            {
                cleanups++;
                servicesAliveDuringCleanup = ReferenceEquals(Kiln.Backend.Current, backend)
                    && backend.Service(service, 1) == registered;
            };
        }
        Assert.Equal(1, cleanups);
        Assert.True(servicesAliveDuringCleanup);
        // Only inspect the managed registry; never dereference a released fake table.
        Assert.Equal(0, backend.Service(service, 1));
        Assert.Same(Backend, Kiln.Backend.Current);
    }

    [Theory]
    [InlineData("world", "kiln.world")]
    [InlineData("systems", "kiln.types")]
    [InlineData("blobs", "kiln.assets")]
    [InlineData("resources", "kiln.assets")]
    public void Fixture_withdraws_only_its_own_service_registration(string kind, string service)
    {
        FakeBackend backend;
        // Opaque registry markers, never passed to a service facade or dereferenced.
        nint replacement = 123;
        using (var fixture = Fixture(kind))
        {
            backend = fixture.Backend;
            backend.Tables[service] = replacement;
            backend.Tables["unrelated"] = 456;
        }
        Assert.Equal(replacement, backend.Service(service, 1));
        Assert.Equal(456, backend.Service("unrelated", 1));
        if (kind == "resources") Assert.Equal(0, backend.Service("kiln.types", 1));
    }

    [Fact]
    public void Resource_fixture_retirement_runs_registered_restore_callbacks()
    {
        var restores = 0;
        var serviceAliveDuringRestore = false;
        using (var fixture = new ReflectedResourceTests())
        {
            var table = fixture.Backend.Service("kiln.assets", 1);
            As(fixture.NewMod("fixture-claim"), () => Overrides.Claim("fixture-claim", () => { }, () =>
            {
                restores++;
                serviceAliveDuringRestore = fixture.Backend.Service("kiln.assets", 1) == table;
            }));
        }
        Assert.Equal(1, restores);
        Assert.True(serviceAliveDuringRestore);
        Assert.Empty(Overrides.Active);
    }
}
