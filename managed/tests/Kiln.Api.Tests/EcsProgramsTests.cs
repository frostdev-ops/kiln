// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;
namespace Kiln.Api.Tests;
public sealed unsafe class EcsProgramsTests : KilnTest
{
    private readonly FakeNative _native;
    public EcsProgramsTests() { _native=new FakeNative(Backend); }
    public override void Dispose() { base.Dispose(); _native.Dispose(); }
    [Fact]
    public void Native_layouts_keep_v1_offsets_and_append_v2_fields()
    {
        Assert.Equal(240,sizeof(EcsWorldTable));
        Assert.Equal(192,(int)Marshal.OffsetOf<EcsWorldTable>(nameof(EcsWorldTable.WriteOther)));
        Assert.Equal(208,(int)Marshal.OffsetOf<EcsWorldTable>(nameof(EcsWorldTable.Bind)));
        Assert.Equal(184,sizeof(TypesNative.Table));
        Assert.Equal(168,(int)Marshal.OffsetOf<TypesNative.Table>(nameof(TypesNative.Table.SystemHook)));
    }
    [Fact]
    public void Bind_passes_owner_identity_and_declared_access_and_restores_mod_context()
    {
        var mod=NewMod("writer");
        var calls=0;
        using var binding=As(mod,()=>EcsPrograms.Bind("pulse",context=> {
            calls++;
            Assert.Same(mod,HostAccess.Current);
            Assert.True(context.TryReadOther<int>(new EntityId(5000),"Health",out var value));
            Assert.Equal(12,value);
            Assert.True(context.TryWriteOther(new EntityId(5000),"Health",19));
            Assert.False(context.TryWriteOther(new EntityId(5000),"Health",19L));
            Assert.Equal(11,BitConverter.ToInt32(context.ReadEvents("TestEvent1")[0]));
            Assert.Equal(22,BitConverter.ToInt32(context.ReadEvents("TestEvent2")[0]));
            Assert.True(context.EmitEvent("TestEvent1",BitConverter.GetBytes(33)));
        }));
        _native.Run();
        Assert.Equal(1,calls);
        Assert.Equal(mod.Owner,_native.Owner);
        Assert.Equal("writer",_native.Mod);
        Assert.Equal(19,*_native.Component);
        Assert.Equal(33,_native.Emitted);
        Assert.Null(HostAccess.Current);
        Assert.False(Backend.Logged("ECS program disabled"));
    }
    [Fact]
    public void Retiring_drains_and_releases_callback_and_same_descriptor_rebinds()
    {
        var mod=NewMod("writer"); var calls=0;
        var binding=As(mod,()=>EcsPrograms.Bind("pulse",_=>calls++));
        _native.Run(); mod.Retire(); _native.Run();
        Assert.Equal(1,calls); Assert.False(_native.Bound); Assert.Equal(1,_native.Removals);
        binding.Dispose(); Assert.Equal(1,_native.Removals);
        using var replacement=As(NewMod("writer"),()=>EcsPrograms.Bind("pulse",_=>calls++));
        _native.Run(); Assert.Equal(2,calls);
    }
    [Fact]
    public void Callback_fault_is_contained_and_disables_further_managed_calls()
    {
        var mod=NewMod("writer"); var calls=0;
        using var binding=As(mod,()=>EcsPrograms.Bind("pulse",_=> { calls++; throw new InvalidOperationException("expected"); }));
        _native.Run(); _native.Run();
        Assert.Equal(1,calls); Assert.True(Backend.Logged("ECS program disabled"));
    }
    [Fact]
    public void Old_service_and_missing_declaration_fail_without_retaining_callback()
    {
        var mod=NewMod("writer");
        Assert.Throws<InvalidOperationException>(()=>As(mod,()=>EcsPrograms.Bind("missing",_=>{})));
        Assert.False(_native.Bound);
        _native.Table->Prefix.Header.Size=8;
        Assert.False(EcsPrograms.Available);
        Assert.Throws<NotSupportedException>(()=>As(mod,()=>EcsPrograms.Bind("pulse",_=>{})));
    }
    [Fact]
    public void Wrong_thread_disposal_keeps_the_callback_alive_until_valid_removal()
    {
        var calls=0;
        using var binding=As(NewMod("writer"),()=>EcsPrograms.Bind("pulse",_=>calls++));
        Backend.TickThread=false;
        Assert.Throws<InvalidOperationException>(binding.Dispose);
        _native.Run(); Assert.Equal(1,calls);
        Backend.TickThread=true;
        binding.Dispose(); _native.Run(); Assert.Equal(1,calls);
    }
    [Fact]
    public void Retired_owner_cannot_create_a_binding_after_its_cleanup_event()
    {
        var mod=NewMod("retired"); mod.Retire();
        IDisposable? late=null;
        try { Assert.Throws<InvalidOperationException>(()=>As(mod,()=>late=EcsPrograms.Bind("pulse",_=>{}))); }
        finally { late?.Dispose(); }
        Assert.False(_native.Bound);
        Assert.Equal(0UL,_native.Owner);
    }
    [Fact]
    public void Duplicate_binding_and_undeclared_access_do_not_replace_first_callback()
    {
        var mod=NewMod("writer"); var calls=0;
        using var binding=As(mod,()=>EcsPrograms.Bind("pulse",context=> {
            calls++;
            try { context.TryReadOther<int>(new EntityId(5000),"Missing",out _); }
            catch (ArgumentException) { calls++; }
        }));
        Assert.Throws<InvalidOperationException>(()=>As(mod,()=>EcsPrograms.Bind("pulse",_=>{})));
        _native.Run(); Assert.Equal(2,calls);
    }
    private sealed class FakeNative : IDisposable
    {
        private static FakeNative? Current;
        internal readonly EcsWorldTable* Table;
        internal readonly int* Component;
        internal ulong Owner;
        internal string Mod="";
        internal int Removals,Emitted;
        private delegate* unmanaged[Cdecl]<void*,ulong*,uint,void*,void> _callback;
        private void* _user;
        internal bool Bound=>_callback!=null;
        internal FakeNative(FakeBackend backend)
        {
            Current=this;
            Table=(EcsWorldTable*)NativeMemory.AllocZeroed((nuint)sizeof(EcsWorldTable));
            Component=(int*)NativeMemory.AllocZeroed(4); *Component=12;
            Table->Prefix.Header=new ServiceHeader {Size=(uint)sizeof(EcsWorldTable),Version=2};
            Table->Prefix.ProgramSlot=(void*)(delegate* unmanaged[Cdecl]<ulong,int,NativeStr,int>)&Slot;
            Table->Prefix.ReadOther=(void*)(delegate* unmanaged[Cdecl]<void*,ulong,uint,uint*,void*>)&ReadOther;
            Table->WriteOther=&ReadOther; Table->Bind=&Bind; Table->Unregister=&Unregister;
            Table->Capabilities=&Capabilities; Table->Events=&Events; Table->Emit=&Emit;
            backend.Tables["kiln.world"]=(nint)Table;
        }
        internal void Run() {
            ulong* iterator=stackalloc ulong[4]; iterator[0]=11;iterator[1]=22;
            if (_callback!=null) _callback((void*)1234,iterator,0,_user);
        }
        public void Dispose() { Current=null; NativeMemory.Free(Component);NativeMemory.Free(Table); }
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
        private static uint Capabilities()=>7;
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
        private static int Slot(ulong handle,int kind,NativeStr name) => (kind,name.ToString()) switch {
            (5,"Health")=>0,(6,"Health")=>1,(7,"TestEvent1")=>2,(7,"TestEvent2")=>3,(8,"TestEvent1")=>2,_=>-1 };
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
        private static void* ReadOther(void* ctx,ulong accessor,uint entity,uint* size) { *size=4;return Current!.Component; }
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
        private static int Bind(ulong owner,NativeStr mod,NativeStr key,delegate* unmanaged[Cdecl]<void*,ulong*,uint,void*,void> callback,void* user,ulong* handle) {
            var self=Current!;
            if (self.Bound) return 1;
            if (key.ToString()!="pulse") return 2;
            self.Owner=owner;self.Mod=mod.ToString();self._callback=callback;self._user=user;*handle=1;return 0;
        }
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
        private static int Unregister(ulong handle) { var self=Current!;self._callback=null;self._user=null;self.Removals++;return 0; }
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
        private static int Events(void* ctx,NativeStr name,delegate* unmanaged[Cdecl]<void*,uint,void*,void> callback,void* user) {
            int value=name.ToString()=="TestEvent1"?11:22;callback(&value,4,user);return 0;
        }
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
        private static int Emit(void* ctx,NativeStr name,void* data,uint size) { Current!.Emitted=*(int*)data;return 0; }
    }
}

public sealed unsafe class GameSystemsHookTests : KilnTest
{
    private readonly FakeSystems _native;
    public GameSystemsHookTests() { _native=new FakeSystems(Backend); }
    public override void Dispose() { base.Dispose();_native.Dispose(); }
    [Fact]
    public void Before_after_run_as_owner_and_dispose_and_retirement_release_callbacks()
    {
        var mod=NewMod("hooks");var before=0;var after=0;
        var hook=As(mod,()=>GameSystems.Hook("sample",()=> { Assert.Same(mod,HostAccess.Current);before++; },()=>after++));
        _native.Run();Assert.Equal(1,before);Assert.Equal(1,after);Assert.Null(HostAccess.Current);
        Assert.Equal(mod.Owner,_native.Owner);
        Assert.Throws<NotSupportedException>(()=>As(mod,()=>GameSystems.Hook("sample",()=>{})));
        hook.Dispose();_native.Run();Assert.Equal(1,before);
        using var next=As(mod,()=>GameSystems.Hook("sample",()=>before++));
        mod.Retire();_native.Run();Assert.Equal(1,before);Assert.False(_native.Bound);
    }
    [Fact]
    public void Managed_faults_disable_hook_and_wrong_thread_dispose_keeps_root()
    {
        var mod=NewMod("hooks");var calls=0;
        using var hook=As(mod,()=>GameSystems.Hook("sample",()=>{ calls++;throw new InvalidOperationException("expected"); }));
        _native.Run();_native.Run();Assert.Equal(1,calls);Assert.True(Backend.Logged("system hook disabled"));
        Backend.TickThread=false;Assert.Throws<InvalidOperationException>(hook.Dispose);Assert.True(_native.Bound);
        Backend.TickThread=true;hook.Dispose();Assert.False(_native.Bound);
    }
    [Fact]
    public void Retired_owner_cannot_root_a_new_system_hook()
    {
        var mod=NewMod("retired"); mod.Retire();
        IDisposable? late=null;
        try { Assert.Throws<InvalidOperationException>(()=>As(mod,()=>late=GameSystems.Hook("sample",()=>{}))); }
        finally { late?.Dispose(); }
        Assert.False(_native.Bound);
        Assert.Equal(0UL,_native.Owner);
    }
    private sealed class FakeSystems : IDisposable
    {
        private static FakeSystems? Current;
        private readonly TypesNative.Table* _table;
        private readonly byte* _name;
        private delegate* unmanaged[Cdecl]<void*,void*,void> _before,_after;
        private void* _user;
        internal ulong Owner;
        internal bool Bound=>_before!=null || _after!=null;
        internal FakeSystems(FakeBackend backend)
        {
            Current=this;_name=(byte*)Marshal.StringToCoTaskMemUTF8("sample");
            _table=(TypesNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(TypesNative.Table));
            _table->Header=new ServiceHeader {Size=(uint)sizeof(TypesNative.Table),Version=1};
            _table->SystemFind=&Find;_table->SystemInfo=&Info;
            _table->SystemHook=&Hook;_table->SystemUnhook=&Unhook;_table->SystemReleaseOwner=&Release;
            backend.Tables["kiln.types"]=(nint)_table;
        }
        internal void Run() { if (_before!=null) _before((void*)1234,_user);if (_after!=null) _after((void*)1234,_user); }
        public void Dispose() { Current=null;NativeMemory.Free(_table);Marshal.FreeCoTaskMem((nint)_name); }
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
        private static int Find(NativeStr name)=>name.ToString()=="sample"?0:-1;
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
        private static int Info(uint index,TypesNative.SystemInfo* info) {
            info->Name=new NativeStr {Ptr=Current!._name,Len=6};info->Enabled=1;return 0;
        }
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
        private static int Hook(ulong owner,uint index,delegate* unmanaged[Cdecl]<void*,void*,void> before,
            delegate* unmanaged[Cdecl]<void*,void*,void> after,void* user,ulong* handle) {
            var self=Current!;if (self.Bound) return 1;
            self.Owner=owner;self._before=before;self._after=after;self._user=user;*handle=1;return 0;
        }
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
        private static int Unhook(ulong handle) { var self=Current!;self._before=null;self._after=null;self._user=null;return 0; }
        [UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]
        private static int Release(ulong owner) { var self=Current!;self._before=null;self._after=null;self._user=null;return 0; }
    }
}
