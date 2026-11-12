// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private string? _language;

    /// <summary>The game's language as mods see it (<see cref="Game.Language"/>, and so <see cref="ModLocale.Get"/>):
    /// <c>"en-us"</c>, as a dedicated server always says, until a test sets another (<c>"de-de"</c>, <c>"pt-br"</c>), as a
    /// client in that language would. Takes effect at once, on every thread.</summary>
    public string Language
    {
        get => _language ?? Game.Language;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            ThrowIfDisposed();
            var tag = ModLocale.Normalize(value);
            _language = tag;
            GameLanguageSource.Override = () => tag;
        }
    }
}
