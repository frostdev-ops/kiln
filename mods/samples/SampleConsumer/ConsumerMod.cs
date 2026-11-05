// SPDX-License-Identifier: MIT
using System.Runtime.Loader;
using Kiln;
using SampleLib.Api;

namespace SampleConsumer;

public sealed class ConsumerMod : Mod
{
    public override void OnLoad()
    {
        var greeter = Greeters.All.FirstOrDefault();
        var from = AssemblyLoadContext.GetLoadContext(typeof(IGreeter).Assembly)?.Name;
        var lib = Mods.Get("sample-lib");
        Log.Info($"sample-consumer: \"{greeter?.Greet("sample-consumer") ?? "no greeter"}\"; IGreeter type from {from}; " +
                 $"sample-lib {lib?.Version} {lib?.State}; optional sample-absent loaded: {Mods.IsLoaded("sample-absent")}");
        Context.Messages.On("echo-reply", m =>
            Log.Info($"sample-consumer: IMC reply from {m.Sender}: {m.PayloadAs<EchoRequest>()} (same type: {m.Payload is EchoRequest})"));
        var sent = Mods.Send("sample-lib", "echo", new EchoRequest("hello over IMC", 42));
        Log.Info($"sample-consumer: IMC echo sent to sample-lib: {sent}");
    }

    public override void OnUnload() => Log.Info("sample-consumer unloading");
}
