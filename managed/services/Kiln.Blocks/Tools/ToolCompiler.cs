// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
namespace Kiln;

internal static partial class Compiler
{
    /// <summary>The brush radius as an item rule: the base brush's FloatImpactConfig of id 0x5258131e (BrushRadius).</summary>
    internal const string BrushRadiusRule="impactValues.0x5258131e";
    /// <summary>The vanilla Overgrowth shape whose cursor preview a brush of this radius shows (kiln#153). The game's preview is
    /// the shape item's own <c>equipment.cursorVfx</c>, one fixed effect per vanilla size (Small 0.5 m, Medium 1.5 m, Big 2.5 m),
    /// not drawn from the radius: the nearest of the three, a tie going to the larger. The stroke uses the brush's own radius.</summary>
    internal static string BrushPreviewShape(double radius)=>radius<1.0?"Building_Overgrowth_Shape_Small":radius<2.0?"Building_Overgrowth_Shape_Medium":"Building_Overgrowth_Shape_Big";
    private static BlockHandle DefineToolPool(BlockContext ctx,Lowered low,string name,ToolMaterialPoolBlock block)
    {
        ctx.Need(ClientsLevel.Mod,null,"a tool pool and its terrain identities must agree in every player's game");
        var node=ToolBlockValidation.PoolNode(block);
        // A mod's collection expression may use a compiler-generated type from its collectible assembly.
        // Handles and runtime policies must retain Kiln-owned arrays, never that collection implementation.
        block=block with {Entries=block.Entries.ToArray()};
        for(var i=0;i<block.Entries.Count;i++)node["entries"]![i]!["item"]=ctx.ItemName(block.Entries[i].Item,$"entries[{i}].item");
        var (status,_)=AskTables(ctx,low,"toolMaterialPools",name,node);
        low.Json["toolMaterialPool"]=node.DeepClone();
        return new TableBlockHandle<ToolMaterialPoolBlock>(ctx.Mod.Id,name,BlockIds.Derive(ctx.Mod.Id,"toolmaterialpool",block.Was??name),ctx.Mod.Id+":"+name,block,status,-1);
    }
    private static BlockHandle DefineTerrainBrush(BlockContext ctx,Lowered low,string name,TerrainBrushBlock block)
    {
        ctx.Need(ClientsLevel.Mod,null,"a terrain brush is an independently identified item in each player's game");
        ToolBlockValidation.Brush(block);
        var guid=LegacyIdentities.Claim(ctx,"item",block.Identity,"identity",BlockIds.Derive(ctx.Mod.Id,"item",block.Was??name));
        low.Json["brush"]=new JsonObject {["name"]=block.Name,["description"]=block.Description,["shape"]="sphere",["radius"]=block.Radius,["item"]=guid.ToString()};
        // It is a part until a tool owns it, not a free item in vanilla Overgrowth's family.
        return new BlockHandle<TerrainBrushBlock>(ctx.Mod.Id,name,guid,ctx.Mod.Id+"."+name,block);
    }
    private static BlockHandle DefineBuildingTool(BlockContext ctx,Lowered low,string name,BuildingToolBlock block)
    {
        ctx.Need(ClientsLevel.Mod,null,"a building tool, its actions, pool and brushes must match in every player's game");
        ToolBlockValidation.Text(block.Name,block.Description);
        if(block.Primary is null||block.Secondary is null||block.Brushes is not {Count:>=1 and <=64})throw new BlockError(ctx.Block,"brushes","a tool needs primary/secondary actions and 1..64 named brushes");
        if(block.Was is {} was&&!MaterialPoolValidation.Valid(was))throw new BlockError(ctx.Block,"was","invalid old tool key");
        foreach(var action in new[]{block.Primary,block.Secondary}) {
            if(!action.TerrainOnly||action.Mode is not (TerrainToolMode.Remove or TerrainToolMode.Add)||action.RemoveMaterials is null||action.RemoveMaterials.Any(c=>c is <1 or >127)||
                (action.Mode==TerrainToolMode.Add&&action.RemoveMaterials.Count!=0))throw new BlockError(ctx.Block,"actions","actions must be terrain-only, with terrain removal cells on Remove only");
        }
        block=block with {Brushes=block.Brushes.ToArray(),
            Primary=block.Primary with {RemoveMaterials=block.Primary.RemoveMaterials.ToArray()},
            Secondary=block.Secondary with {RemoveMaterials=block.Secondary.RemoveMaterials.ToArray()}};
        var guid=LegacyIdentities.Claim(ctx,"item",block.Identity,"identity",BlockIds.Derive(ctx.Mod.Id,"item",block.Was??name));
        TableBlockHandle<ToolMaterialPoolBlock>? pool=null;
        if(block.MaterialPool is {} poolRef) {
            if(poolRef.Name is null)throw new BlockError(ctx.Block,"materialPool","define a named tool pool first");
            pool=ctx.Find(poolRef.Name,"materialPool") as TableBlockHandle<ToolMaterialPoolBlock>??throw new BlockError(ctx.Block,"materialPool","the reference must name a tool material pool");
        }
        if(pool is null&&(block.Primary.Mode==TerrainToolMode.Add||block.Secondary.Mode==TerrainToolMode.Add))throw new BlockError(ctx.Block,"materialPool","an Add action requires a tool material pool");
        var brushes=new List<BlockHandle<TerrainBrushBlock>>();var ids=new HashSet<uint>{BlockIds.Fnv(guid)};
        foreach(var reference in block.Brushes) {
            if(reference?.Name is null)throw new BlockError(ctx.Block,"brushes","define each named brush before its owning tool");
            var brush=ctx.Find(reference.Name,"brushes") as BlockHandle<TerrainBrushBlock>??throw new BlockError(ctx.Block,"brushes","a reference must name a terrain brush");
            if(!ids.Add(BlockIds.Fnv(brush.Guid)))throw new BlockError(ctx.Block,"brushes","a family cannot repeat a brush or alias its root identity");
            ToolBlockValidation.Brush(brush.Block);brushes.Add(brush);
        }
        var entries=pool?.Block.Entries??[];
        low.Json["toolFamily"]=new JsonObject {["tool"]=BlockIds.Fnv(guid),["identity"]=guid.ToString(),["name"]=block.Name,["description"]=block.Description,
            ["pool"]=pool?.Guid.ToString(),["brushes"]=new JsonArray(brushes.Select(b=>(JsonNode?)new JsonObject {["item"]=b.Guid.ToString(),["radius"]=b.Block.Radius}).ToArray())};
        // The raw fill-byte brush entries go through the same boot registry writer as building shapes, but a
        // separate, terrain-only section. A pending descriptor must never be presented as live game registration.
        var status=pool?.Status??TableBlockStatus.Registered;
        foreach(var brush in brushes) {
            var descriptor=new JsonObject {["item"]=brush.Guid.ToString(),["shape"]="sphere",["radius"]=brush.Block.Radius};
            var (brushStatus,_)=AskTables(ctx,low,"toolBrushShapes",brush.Name,descriptor);
            if(brushStatus!=TableBlockStatus.Registered)status=brushStatus;
        }
        low.Json["status"]=status==TableBlockStatus.Registered?"registered":status==TableBlockStatus.Pending?"pending":"off";
        if(status==TableBlockStatus.Registered) {
            var actions=LowerToolActions(ctx,low,block.Was??name,name,block.Primary,block.Secondary);
            // This is the qualified vanilla TerrainMaterials GUID, not the policy's private identity. A second
            // game-visible row repeats magic-chest enumeration even while a vanilla tool is held.
            var refs=new Dictionary<string,string> {["equipment.voxelBlueprintMaterialPool"]="256c43ba-dd20-4369-b3bf-f49c671a59ec"};
            var root=new ItemDefinition {Guid=guid,Name=ctx.Mod.Id+"."+name,BaseOn="Tool_Building_Overgrowth",DisplayName=block.Name,Description=block.Description??"",Refs=refs,
                Icon=block.Icon is {} icon?BlockIcons.ItemIcon(ctx,icon,"icon"):null};
            // The root launches building mode; the decoded root has no sequence slots. Only brush
            // starters carry Secondary/Primary sequence entries that an action plan can replace.
            low.Item(root);
            foreach(var brush in brushes) {
                // Its cursor preview is the nearest vanilla size's effect: the base's own is Small's, whatever the radius (kiln#153).
                var item=new ItemDefinition {Guid=brush.Guid,Name=brush.GameName,BaseOn="Building_Overgrowth_Shape_Small",DisplayName=brush.Block.Name,Description=brush.Block.Description??"",
                    Refs=new Dictionary<string,string>(refs) {["equipment.terrainVoxelObject"]=brush.Guid.ToString(),["equipment.cursorVfx"]="item:"+BrushPreviewShape(brush.Block.Radius)}};
                low.Item(ApplyToolActions(ctx,low,item,actions));
                // The radius is the brush item's own impact config: Building_Overgrowth_Shape_Small carries FloatImpactConfig
                // 0x5258131e (0.5), the config the tool programs' BrushRadius reads. It is no item field of that name.
                var radius=new JsonObject {[BrushRadiusRule]=brush.Block.Radius};
                low.ItemRules(brush.GameName,radius);
                ((low.Json["toolItemRules"]??=new JsonObject()).AsObject())[brush.GameName]=radius.DeepClone();
                // No item tag: the picker's query scans only the game data's terraformingTools list, where no runtime brush
                // is, so the native frame adapter writes the family's rows (brush_frame.h). A root GUID is no ItemTag either.
            }
            low.AfterCommit(()=>BlockRuntime.AddReady(ctx.Mod,problem=>ToolFamilyRuntime.Publish(ctx.Mod,name,guid,pool?.Guid,block,brushes,entries,actions,problem)));
        }
        return new TableBlockHandle<BuildingToolBlock>(ctx.Mod.Id,name,guid,ctx.Mod.Id+"."+name,block,status,-1);
    }
}

internal static class ToolFamilyRuntime
{
    private static readonly Dictionary<string,List<(ulong Owner,ulong Handle)>> Handles=new(StringComparer.Ordinal);
    internal static void Publish(ModBlocks mod,string name,Guid identity,Guid? pool,BuildingToolBlock block,IReadOnlyList<BlockHandle<TerrainBrushBlock>> brushes,
        IReadOnlyList<ToolMaterialPoolEntry> entries,ToolActionPlan actions,Action<string> problem)
    {
        var materials=new List<ToolFamilyMaterial>();
        for(var i=0;i<entries.Count;i++) {
            var ctx=new BlockContext(mod,"buildingtool "+name);var entry=Content.Items.Get(ctx.ItemName(entries[i].Item,$"entries[{i}].item"));
            if(entry is null) {problem($"{name}, entries[{i}].item: no live item");return;}
            materials.Add(new(entry.Id,entries[i].TerrainMaterial,entries[i].VoxelsPerItem));
        }
        var definition=new ToolFamilyDefinition(identity,pool is {} p?BlockIds.Fnv(p):0,block.Name,block.Description??"",new(block.Primary,BlockIds.Fnv(actions.PrimaryProgram)),
            new(block.Secondary,BlockIds.Fnv(actions.SecondaryProgram)),materials.ToArray(),brushes.Select(b=>new ToolFamilyBrush(BlockIds.Fnv(b.Guid),b.Block.Radius)).ToArray());
        var result=ToolsNative.Define(mod.Owner,definition,out var handle);
        if(result!=0) {problem($"{name}: the complete native tool policy is unavailable or refused ({result}); no tool action is admitted");return;}
        if(!Handles.TryGetValue(mod.Id,out var held))Handles[mod.Id]=held=[];held.Add((mod.Owner,handle));
    }
    internal static void Forget(string mod)
    {
        if(!Handles.Remove(mod,out var handles))return;
        foreach(var (owner,handle) in handles)ToolsNative.Remove(owner,handle);
    }
}
