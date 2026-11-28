// Made by tools/docs-site/snippets.py from docs/kiln/services/taming.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_services_taming_1
{
    using static DocSnippets.docs_services_taming_1.Stubs;
    internal static class Stubs
    {
        public static ItemId meat => default!;
        public static ItemId whistle => default!;
    }
    public sealed partial class Snippet : Mod
    {
        public async Task Run()
        {
#line 28 "docs/kiln/services/taming.md"
var defined = Taming.Define(new TamingDefinition
{
    Id = "my-mod:wolf",
    Creatures = ["game:enemy_wildbeast_wolf_earlygame"],          // creatures registry ids; "game:animal_wildlife_dog_*" for a prefix
    Steps = [new WeakenStep(0.3), new FeedStep(meat, 2), new UseToolStep(whistle, Wear: 5)],
    Pet = "my-mod:wolf-pet",                             // a Kiln.Pets definition: a tame grants a record of it
    Refund = TamingRefundPolicy.Escrow,                  // the default is NonRefundable
});
if (!defined.Ok) Log.Warn($"{defined.Refusal}: {defined.Reason}");   // DefinitionInUse: attempts of the old one run
#line default
        }
    }
}
