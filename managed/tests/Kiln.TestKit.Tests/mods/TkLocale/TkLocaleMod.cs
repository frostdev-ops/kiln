// SPDX-License-Identifier: MIT
using Kiln;

namespace TkLocale;

/// <summary>A mod folder with its own kiln.mod.json and locales; logs its greeting on a "greet" message.</summary>
public sealed class TkLocaleMod : Mod
{
    public override void OnLoad() => Context.Messages.On("greet", _ => Log.Info($"text: {Text("greet", new { name = "Ada" })} / {Text("only.english")} / {Text("no.such.key")}"));
}
