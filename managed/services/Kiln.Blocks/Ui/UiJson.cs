// SPDX-License-Identifier: MIT
// The sections of blocks/*.json for UI blocks: BlockJson reads them. Rows, actions and texts are the records' own shapes (UiModel.cs): System.Text.Json fills them.
using System.Text.Json;

namespace Kiln;

internal static class UiJson
{
    public static readonly (string Section, Func<ModBlocks, string, JsonElement, BlockHandle> Make)[] Kinds =
    [
        ("windows", BlockJson.Make<WindowBlock>), ("hudLines", BlockJson.Make<HudLineBlock>), ("keyBindings", BlockJson.Make<KeyBindingBlock>),
    ];
}
