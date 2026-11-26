// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Kiln.TestKit;

internal sealed record CraftSourceEntry(ulong Owner, ulong Handle, uint Player, uint Container, uint Order, bool Deny, uint[] Items, uint PlayerSession, bool Locks = false,
    bool Fills = false, bool Building = false, bool Respec = false)
{
    internal bool Allows(uint item) => Deny ? !Items.Contains(item) : Items.Contains(item);
}
internal sealed class CraftingState
{
    internal readonly object Gate=new();
    internal ulong Next;
    internal readonly List<CraftSourceEntry> Sources=[];
}

// Only the appended linked-source members are modelled. Older station operations explicitly refuse.
internal sealed unsafe class CraftingFake : IKitFake
{
    public string Service=>StationsNative.Name;
    private static StationsNative.Table* _table;
    private static KilnHarness? Harness=>FakeTables.Active(StationsNative.Name);
    private static CraftingState? State=>Harness is {} h ? KitFakes.State<CraftingState>(h) : null;
    private static bool IsProductionStation(uint entity)=>Harness?.EntitiesFake?.Block(entity,WorldAbi.Hash("FactoryStation")) is not null;
    public void Register()
    {
        if(_table==null)
        {
            _table=(StationsNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(StationsNative.Table));
            _table->Header=new ServiceHeader {Size=(uint)sizeof(StationsNative.Table),Version=1};
            var slots=(nint*)((byte*)_table+8);
            for(var i=0;i<30;i++) slots[i]=(nint)(delegate* unmanaged[Cdecl]<int>)&Unavailable;
            _table->CraftItems=&CraftItems;
            _table->AddSource=&Add;_table->RemoveSource=&Remove;_table->CancelSources=&Cancel;_table->SourceStock=&Stock;
            // GitHub #147: the lock snapshot is a game client's (no lock check runs in the kit): its members answer
            // unavailable, as on a server; the registrations' Locks flag and lock counts are modelled.
            _table->SetLockStock=(delegate* unmanaged[Cdecl]<uint,StationsNative.Stack*,uint,int>)(delegate* unmanaged[Cdecl]<int>)&Unavailable;
            _table->LockAsks=(delegate* unmanaged[Cdecl]<uint*,uint,uint*,int>)(delegate* unmanaged[Cdecl]<int>)&Unavailable;
            // GitHub #132: the building snapshot is a game client's too; the Fills and Building flags and counts are modelled
            _table->SetBuildStock=(delegate* unmanaged[Cdecl]<uint,StationsNative.Stack*,uint,int>)(delegate* unmanaged[Cdecl]<int>)&Unavailable;
            // GitHub #150: the respec snapshot is a game client's too; the Respec flag and the respec links' counts are modelled
            _table->SetRespecStock=(delegate* unmanaged[Cdecl]<uint,StationsNative.Stack*,uint,int>)(delegate* unmanaged[Cdecl]<int>)&Unavailable;
        }
        KitBackend.Instance.RegisterTable(Service,(nint)_table);
        FakeTables.Features([Crafting.SourcesCapability, Crafting.LockSourcesCapability]);
        FakeTables.Offline(Crafting.SourceUiCapability,"the test kit has no game client crafting screens");
        FakeTables.Offline(Crafting.BuildSourcesCapability,"the test kit has no build cursor or blueprint placement (the Building opt-in and its counts are recorded)");
        FakeTables.Offline("crafting.build_ui","the test kit has no game client build menu");
        FakeTables.Offline(Crafting.RespecSourcesCapability,"the test kit has no skill tree respec payment or respec screens (the Respec opt-in and its counts are recorded)");
        FakeTables.Offline("crafting.dye_sources", "the test kit records dye source counts but does not run the game's Tint transaction");
        FakeTables.Offline("crafting.dye_ui", "the test kit has no game client tinting screen");
        FakeTables.Offline("crafting.dye_sources_session", "the test kit has no native Tint transaction session");
        FakeTables.Offline("crafting.dye_ui_session", "the test kit has no native tinting-screen session");
    }
    public void Start(KilnHarness harness) { }
    public void BeforeStep(KilnHarness harness,ulong step) { }
    public void End(KilnHarness harness) {var s=KitFakes.State<CraftingState>(harness);lock(s.Gate)s.Sources.Clear();}
    public void AddCoverage(List<CoverageRow> rows)=>rows.Add(new CoverageRow("Player crafting sources",CoverageKind.RecordingFake,
        [StationsNative.Name,Crafting.SourcesCapability,Crafting.LockSourcesCapability,Crafting.BuildSourcesCapability,Crafting.RespecSourcesCapability],"Player/container registrations, copied allow/deny filters, order metadata, the Locks, Fills, Building and Respec opt-ins, per-player counts (all, and lock-, fill-, building- or respec-opted only), inventory permission, disposal and mod unload. No game RecipePaid hook, lock check or interaction payment, station fill, build cursor or placement payment, skill respec payment, base stock, station outputs or client screens are simulated."));
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static int Unavailable()=>FakeTables.Unavailable;
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static int CraftItems(uint* items,uint n,uint* output,uint capacity,uint* count)
    {
        if(count==null || (n!=0 && items==null) || (capacity!=0 && output==null))return FakeTables.InvalidArgument;
        *count=n;if(capacity<n)return FakeTables.BufferTooSmall;
        for(uint i=0;i<n;i++)output[i]=items[i];
        return FakeTables.Ok; // no game recipe catalog: only the supplied lent items are modelled
    }

    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static int Add(ulong owner,StationsNative.SourceRequest* r,ulong* handle)
    {
        try
        {
            if(handle==null)return FakeTables.InvalidArgument;*handle=0;
            if(r==null || r->Size<32 || r->Player is 0 or >1023 || r->Container<=1023 || r->Order>1 || r->Filter>1 || r->ItemCount>65536 || (r->ItemCount!=0 && r->Items==null))return FakeTables.InvalidArgument;
            if(!FakeTables.OnTickThread)return FakeTables.WrongThread;
            if(FakeTables.Off(Crafting.SourcesCapability) || State is not {} s)return FakeTables.Unavailable;
            if(IsProductionStation(r->Container) || Items.Read(new EntityId(r->Container)) is not {Initialized:true,IsPlayer:false,IsNpc:false})return FakeTables.InvalidArgument;
            if(Players.Find(new EntityId(r->Player)) is not {InGame:true} player)return FakeTables.InvalidArgument;
            // since 0.2.0-dev.31.12 (GitHub #147): flags when the request's size covers them, as kiln.stations
            var hasFlags=r->Size>=sizeof(StationsNative.SourceRequest);var flags=hasFlags?r->Flags:0u;
            const uint known=StationsNative.SourceLocks|StationsNative.SourceFills|StationsNative.SourceBuilding|StationsNative.SourceRespec;  // #147, #132, #150
            if((flags & ~known)!=0 || (hasFlags && r->Reserved!=0))return FakeTables.InvalidArgument;
            var items=new ReadOnlySpan<uint>(r->Items,(int)r->ItemCount).ToArray().Distinct().Order().ToArray();
            if(items.Contains(0u))return FakeTables.InvalidArgument;
            lock(s.Gate)
            {
                if(s.Sources.Count>=4096)return FakeTables.BufferTooSmall;
                *handle=++s.Next;s.Sources.Add(new(owner,*handle,r->Player,r->Container,r->Order,r->Filter==0,items,player.Handle,(flags & StationsNative.SourceLocks)!=0,
                    (flags & StationsNative.SourceFills)!=0,(flags & StationsNative.SourceBuilding)!=0,(flags & StationsNative.SourceRespec)!=0));
            }
            return FakeTables.Ok;
        }
        catch{return FakeTables.Internal;}
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static int Remove(ulong owner,ulong handle)
    {
        try {if(State is not {} s)return FakeTables.NotFound;lock(s.Gate)return s.Sources.RemoveAll(x=>x.Owner==owner && x.Handle==handle)>0?FakeTables.Ok:FakeTables.NotFound;}
        catch{return FakeTables.Internal;}
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static void Cancel(ulong owner)
    {
        try {if(State is {} s)lock(s.Gate)s.Sources.RemoveAll(x=>x.Owner==owner);}catch{ }
    }
    [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
    private static int Stock(uint player,uint flags,StationsNative.Stack* rows,uint capacity,uint* count)
    {
        try
        {
            const uint purpose=StationsNative.StockLocks|StationsNative.StockFills|StationsNative.StockBuilding|StationsNative.StockRespec;
            if(count==null || (capacity!=0 && rows==null) || player is 0 or >1023 || (flags & ~(StationsNative.StockAll|purpose))!=0 ||
               System.Numerics.BitOperations.PopCount(flags & purpose)>1)return FakeTables.InvalidArgument;*count=0;
            if(!FakeTables.OnTickThread)return FakeTables.WrongThread;
            if(FakeTables.Off(Crafting.SourcesCapability) || State is not {} s)return FakeTables.Unavailable;
            // GitHub #150: the respec row names the game's respec item, which only the game's payer knows: unavailable, as
            // kiln.stations answers where crafting.respec_sources did not resolve (HarnessCrafting.RespecStock has the counts)
            if((flags & StationsNative.StockRespec)!=0)return FakeTables.Unavailable;
            var stock=Counts(s,player,(flags & StationsNative.StockLocks)!=0,(flags & StationsNative.StockFills)!=0,(flags & StationsNative.StockBuilding)!=0);*count=(uint)stock.Count;
            if(capacity<*count)return FakeTables.BufferTooSmall;
            var i=0;foreach(var (item,n) in stock.OrderBy(x=>x.Key))rows[i++]=new(){Item=item,Count=(uint)Math.Min(n,uint.MaxValue)};
            return FakeTables.Ok;
        }
        catch{return FakeTables.Internal;}
    }
    // locks: the lock-opted registrations alone (KILN_CRAFT_SOURCE_STOCK_LOCKS), as kiln.stations counts them.
    // fills / building (GitHub #132): the Fills- or Building-opted registrations alone, likewise.
    // respec (GitHub #150): the Respec-opted registrations alone, every item.
    internal static Dictionary<uint,long> Counts(CraftingState state,uint player,bool locks=false,bool fills=false,bool building=false,bool respec=false)
    {
        var result=new Dictionary<uint,long>();
        if(Players.Find(new EntityId(player)) is not {InGame:true,PermissionsKnown:true} who || !who.Can(PlayerPermissions.AccessInventories))return result;
        CraftSourceEntry[] sources;lock(state.Gate)sources=[..state.Sources.Where(s=>s.Player==player && s.PlayerSession==who.Handle && (!locks || s.Locks) && (!fills || s.Fills) && (!building || s.Building) && (!respec || s.Respec))];
        var seen=new HashSet<(uint,uint)>();
        foreach(var source in sources)
            if(!IsProductionStation(source.Container) && Items.Read(new EntityId(source.Container)) is {Initialized:true,IsPlayer:false,IsNpc:false} inv)
                foreach(var group in inv.Slots.Where(x=>!x.IsEmpty).GroupBy(x=>x.Item.Value))
                    if(source.Allows(group.Key) && seen.Add((source.Container,group.Key)))result[group.Key]=result.GetValueOrDefault(group.Key)+group.Sum(x=>(long)x.Count);
        return result;
    }
}
