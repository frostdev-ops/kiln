// SPDX-License-Identifier: MIT
// Fault mod of Mod.OnBoot: stages an item, then throws. What it staged must not reach the boot generation (BOOT-CALLBACK-EXCLUDED, observed by
// boot-definitions-test); its OnLoad must never run in this process.
using Kiln;

namespace BootCallbackFail;

public sealed class BootCallbackFailMod : Mod
{
    public override void OnBoot(BootContext boot)
    {
        var staged = BootDefinitions.For(boot).Define("fail-token", new ItemBlock { Name = "Boot Fail Token", Stack = 5 });
        if (!staged.Ok) throw new InvalidOperationException(staged.Reason);
        throw new InvalidOperationException("boot-callback-fail: thrown on purpose after staging fail-token");
    }

    public override void OnLoad() => Context.Log.Error("boot-callback-fail: FAIL BOOT-CALLBACK-EXCLUDED its runtime instance loaded although its OnBoot failed");
}
