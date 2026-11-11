// SPDX-License-Identifier: MIT
// Executes the production Lua bridge and bundled Lua, with only game/OS services substituted.
#include <cassert>
#include <csignal>
#include <sys/resource.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <set>
#include "../src/lua_api.cpp"
#include "services/kiln_types.h"
#include "services/kiln_assets.h"
#include "services/kiln_players.h"
#include "../src/overlay.h"
#include "../src/inspector.h"
#include <memory>

#include "services/kiln_hooks.h"
#include "imgui/imgui.h"  // the vendored ImGui kiln.ui passes input_text flags to: the mock honours its real values
namespace kset { void bindHooks(const KilnApi* api, KilnOwner owner); }
namespace kset::hooktest { extern bool (*on_stack)(uintptr_t at, size_t n); }
void rt::openImGui(lua_State* L){for(const char* n:{"ImGui","ImGuiCond","ImGuiWindowFlags"}){lua_newtable(L);lua_setglobal(L,n);}}
void rt::imguiMark(){} void rt::imguiRecover(){}

namespace {
struct TypeFixture { std::string name,qn; uint32_t size; uint8_t kind; int inner=-1; std::vector<std::tuple<std::string,int,uint64_t>> fields; };
std::vector<TypeFixture> ts = {
 {"uint32","keen::uint32",4,KILN_TYPE_U32},
 {"float32","keen::float32",4,KILN_TYPE_F32},
 {"SignedEnum","keen::SignedEnum",4,KILN_TYPE_ENUM},
 {"Nested","keen::Nested",8,KILN_TYPE_STRUCT,-1,{{"a",0,0},{"b",2,4}}},
 {"Health","keen::ecs::Health",20,KILN_TYPE_STRUCT,-1,{{"value",0,0},{"floating",1,4},{"nested",3,8},{"choice",2,16}}},
 {"Cycle","keen::Cycle",8,KILN_TYPE_STRUCT,5},
 {"Packed","keen::Packed",5,KILN_TYPE_STRUCT,-1,{{"floating",1,1}}},
};
const KilnType* handle(size_t i){return reinterpret_cast<const KilnType*>(i+1);}
size_t idx(const KilnType* p){return reinterpret_cast<size_t>(p)-1;}
KilnStr str(const std::string& s){return {s.data(),s.size()};}
KilnTypesApi types{};
KilnWorldApi world{};
KilnAssetsApi assets{};
KilnPlayersApi toolsPlayers{};
bool toolsPlayerService=false;
// A fake kiln.hooks for Hooks.rule: one pattern is "found", the entry hook is kept so a test can run a call through it.
KilnHooksApi hooksApi{};
std::map<std::string,uintptr_t> hookScans;
std::map<uintptr_t,std::pair<KilnMidFn,void*>> hookMids;
bool hooksOn=true;
void initHooks(){
 hooksApi.header={sizeof(hooksApi),2};
 hooksApi.read=[](uintptr_t at,void* dst,size_t n)->KilnResult{if(!at)return KILN_ERR_INVALID_ARGUMENT;memcpy(dst,reinterpret_cast<const void*>(at),n);return KILN_OK;};
 hooksApi.scan=[](KilnStr p,uintptr_t* out,uint32_t,uint32_t* count)->KilnResult{auto it=hookScans.find(std::string(p.ptr,p.len));if(it==hookScans.end()){*count=0;return KILN_ERR_NOT_FOUND;}*count=1;out[0]=it->second;return KILN_OK;};
 hooksApi.mid=[](KilnOwner,uintptr_t at,int32_t,KilnMidFn fn,void* user,KilnHandle* out)->KilnResult{hookMids[at]={fn,user};*out=hookMids.size();return KILN_OK;};
 hooksApi.raw_detour=[](KilnOwner,uintptr_t,const KilnHookOrder*,void*,void** original,KilnHandle* out)->KilnResult{*original=nullptr;*out=99;return KILN_OK;};
}
const void* resourcePtr=nullptr;size_t resourceSize=0;
bool tickThread=true;
KilnApi core{};
std::vector<std::string> logs;
uint32_t actual=20;
uint8_t component[32]{};
std::set<uint64_t> disabled, released;
uint64_t nextOwner=10;
void initServices(){
 types.header={sizeof(types),1};
 types.type_count=[]()->uint32_t{return ts.size();};
 types.type_at=[](uint32_t i)->const KilnType*{return i<ts.size()?handle(i):nullptr;};
 types.type_by_hash=[](uint32_t h)->const KilnType*{for(size_t i=0;i<ts.size();++i)if(refl::fnv(ts[i].qn)==h)return handle(i);return nullptr;};
 types.type_by_name=[](KilnStr name)->const KilnType*{std::string_view n(name.ptr,name.len);for(size_t i=0;i<ts.size();++i)if(ts[i].qn==n||ts[i].name==n)return handle(i);return nullptr;};
 types.type_info=[](const KilnType* p,KilnTypeInfo* o)->KilnResult{if(!p||idx(p)>=ts.size())return KILN_ERR_NOT_FOUND;auto& t=ts[idx(p)];o->name=str(t.name);o->qualified_name=str(t.qn);o->type_size=t.size;o->kind=t.kind;o->hash=refl::fnv(t.qn);o->field_count=t.kind==KILN_TYPE_ENUM?2:t.fields.size();o->inner=t.inner<0?nullptr:handle(t.inner);return KILN_OK;};
 types.type_field=[](const KilnType* p,uint32_t i,KilnTypeField* o)->KilnResult{auto& fields=ts.at(idx(p)).fields;if(i>=fields.size())return KILN_ERR_NOT_FOUND;auto& [n,t,off]=fields[i];o->name=str(n);o->type=handle(t);o->offset=off;return KILN_OK;};
 types.type_enum=[](const KilnType*,uint32_t i,KilnStr* n,int64_t* v)->KilnResult{static std::string a="Negative",b="Positive";if(i>1)return KILN_ERR_NOT_FOUND;*n=str(i?b:a);*v=i?1:-1;return KILN_OK;};
 types.component_type_count=[]()->uint32_t{return 1;};types.component_type_at=[](uint32_t)->const KilnType*{return handle(4);};
 types.system_count=[]()->uint32_t{return 1;};
 types.status=[](KilnTypesStatus* o)->KilnResult{o->systems=1;return KILN_OK;};
 types.system_info=[](uint32_t i,KilnSystemInfo* o)->KilnResult{static std::string n="stamina_depletion_sprint";if(i)return KILN_ERR_NOT_FOUND;o->name=str(n);o->enabled=1;return KILN_OK;};
 types.system_set_enabled=[](KilnOwner o,uint32_t,bool on)->KilnResult{if(on)disabled.erase(o);else disabled.insert(o);return KILN_OK;};
 types.system_release_owner=[](KilnOwner o)->KilnResult{disabled.erase(o);released.insert(o);return KILN_OK;};
 world.header={sizeof(world),1};
 world.component=[](KilnEntity,uint32_t,uint32_t* size)->const void*{*size=actual;return component;};
 world.entity_count=[]()->uint32_t{return 1;};
 assets.header={sizeof(assets),1};assets.resource_data=[](const KilnAssetKey*,const void** p,size_t* n)->KilnResult{*p=resourcePtr;*n=resourceSize;return resourcePtr?KILN_OK:KILN_ERR_NOT_FOUND;};
 core.service=[](KilnStr n,uint32_t)->const void*{std::string_view s(n.ptr,n.len);return s=="kiln.types"?static_cast<const void*>(&types):s=="kiln.world"?static_cast<const void*>(&world):s=="kiln.assets"?static_cast<const void*>(&assets):s=="kiln.players"&&toolsPlayerService?static_cast<const void*>(&toolsPlayers):s=="kiln.hooks"&&hooksOn?static_cast<const void*>(&hooksApi):nullptr;};
 core.symbol=[](KilnStr,uintptr_t*)->KilnResult{return KILN_ERR_NOT_FOUND;};initHooks();
 core.on_tick_thread=[](){return tickThread;};core.server_world=[]()->uintptr_t{return 1;};
 core.owner_create=[](KilnStr)->KilnOwner{return ++nextOwner;};core.owner_revoke=[](KilnOwner){};
 kset::bind(&core);kset::bindHooks(&core,1);rt::bindKiln(&core,1);rt::setSystemsAvailable(true);
}
KilnUiApi toolsUi{};
std::string toolsTab="Mod Settings", toolsClick, toolsEnter;
std::map<std::string,std::string> toolsInputs;
std::vector<std::string> toolsRows;
uint32_t toolsDemand=0,toolsCapture=0,toolsPressed=0;
int toolsWindows=0,toolsIds=0,toolsUnsubscribed=0;
bool toolsClose=false,toolsEscapeDuringBegin=false;
KilnUiKeyFn toolsKey=nullptr;
KilnUiMenuFn toolsMenu=nullptr;
std::string textOf(KilnStr s){return std::string(s.ptr,s.len);}
void initTools(){
 toolsUi.header={sizeof(toolsUi),4};
 toolsUi.status=[](KilnUiStatus*)->KilnResult{return KILN_OK;};
 toolsUi.set_demand=[](uint32_t n){toolsDemand=n;};toolsUi.set_capture=[](uint32_t n){toolsCapture=n;};
 toolsUi.set_next_window_size=[](float,float,uint32_t){};
 toolsUi.begin=[](KilnStr,bool* open,uint32_t){++toolsWindows;if(toolsEscapeDuringBegin){toolsEscapeDuringBegin=false;KilnUiKey escape{};escape.down=1;escape.vk=0x1b;assert(toolsKey(&escape,nullptr));}if(toolsClose){*open=false;toolsClose=false;return false;}return true;};
 toolsUi.end=[](){--toolsWindows;};
 toolsUi.begin_tab_bar=[](KilnStr){return true;};toolsUi.end_tab_bar=[](){};
 toolsUi.begin_tab_item=[](KilnStr label){return textOf(label)==toolsTab;};toolsUi.end_tab_item=[](){};
 toolsUi.button=[](KilnStr label,float,float){if(textOf(label)!=toolsClick)return false;toolsClick.clear();return true;};
 toolsUi.checkbox=[](KilnStr label,bool* v){auto it=toolsInputs.find(textOf(label));if(it==toolsInputs.end())return false;*v=it->second=="true";toolsInputs.erase(it);return true;};
 // Like ImGui's InputText with live edit (its default): while a field holds typed text that differs from the
 // caller's buffer, every call copies it in and returns true. With EnterReturnsTrue only the call on which Enter is
 // pressed (toolsEnter) returns true, and Enter leaves the field. A field without that flag is left once the
 // caller's buffer already holds its text.
 toolsUi.input_text=[](KilnStr label,char* b,size_t cap,uint32_t flags){
  const std::string name=textOf(label);auto it=toolsInputs.find(name);
  if(it==toolsInputs.end()||!cap)return false;
  const std::string typed=it->second.substr(0,cap-1);const bool differs=typed!=b;
  if(differs)snprintf(b,cap,"%s",typed.c_str());
  if(flags&ImGuiInputTextFlags_EnterReturnsTrue){if(toolsEnter!=name)return false;toolsEnter.clear();toolsInputs.erase(it);return true;}
  if(!differs)toolsInputs.erase(it);
  return differs;};
 toolsUi.slider_int=[](KilnStr label,int32_t* v,int32_t,int32_t){auto it=toolsInputs.find(textOf(label));if(it==toolsInputs.end())return false;*v=std::stoi(it->second);toolsInputs.erase(it);return true;};
 toolsUi.combo=[](KilnStr label,int32_t* v,const char*){auto it=toolsInputs.find(textOf(label));if(it==toolsInputs.end())return false;*v=std::stoi(it->second);toolsInputs.erase(it);return true;};
 toolsUi.text=[](KilnStr v){toolsRows.push_back(textOf(v));};toolsUi.text_wrapped=toolsUi.text;
 toolsUi.text_colored=[](uint32_t,KilnStr v){toolsRows.push_back(textOf(v));};
 toolsUi.push_id=[](KilnStr){++toolsIds;};toolsUi.pop_id=[](){--toolsIds;};
 toolsUi.is_item_hovered=[](){return false;};toolsUi.set_tooltip=[](KilnStr){};
 toolsUi.collapsing_header=[](KilnStr,uint32_t){return true;};toolsUi.same_line=[](float,float){};
 toolsUi.selectable=[](KilnStr,bool){return false;};
 toolsUi.begin_child=[](KilnStr,float,float,uint32_t){return true;};toolsUi.end_child=[](){};
 toolsUi.take_pressed_key=[](uint32_t* mods)->uint32_t{*mods=rt::ModCtrl;return std::exchange(toolsPressed,0);};
 toolsUi.menu_add=[](KilnOwner,uint32_t,int32_t,KilnStr,KilnUiMenuFn fn,void*,KilnHandle* out)->KilnResult{toolsMenu=fn;*out=1;return KILN_OK;};
 toolsUi.subscribe_key=[](KilnOwner,KilnUiKeyFn fn,void*,KilnHandle* out)->KilnResult{toolsKey=fn;*out=3;return KILN_OK;};
 toolsUi.unsubscribe=[](KilnHandle)->KilnResult{++toolsUnsubscribed;return KILN_OK;};
}
void sink(const char* s){logs.emplace_back(s);}
int env(lua_State* L,const char* name){pushModEnv(L,name);int ref=luaL_ref(L,LUA_REGISTRYINDEX);return ref;}
bool run(lua_State* L,int ref,const char* script){assert(luaL_loadstring(L,script)==LUA_OK);lua_rawgeti(L,LUA_REGISTRYINDEX,ref);lua_setupvalue(L,-2,1);return call(L,0,0);}
// The same, as code of the mod: a chunk named like one of its files, so its writes are the mod's (the write log).
bool runAs(lua_State* L,int ref,const std::string& mod,const char* script){assert(luaL_loadbufferx(L,script,strlen(script),("@"+mod+"/test.lua").c_str(),"t")==LUA_OK);lua_rawgeti(L,LUA_REGISTRYINDEX,ref);lua_setupvalue(L,-2,1);return call(L,0,0);}
void mustAs(lua_State* L,int ref,const std::string& mod,const char* script){if(!runAs(L,ref,mod,script)){for(auto& l:logs)std::cerr<<l<<'\n';assert(false);}}
bool logged(const char* text){for(auto& l:logs)if(l.find(text)!=std::string::npos)return true;return false;}
std::string slurp(const std::filesystem::path& p){std::ifstream f(p,std::ios::binary);return std::string((std::istreambuf_iterator<char>(f)),{});}
// UTF-8 as MultiByteToWideChar(MB_ERR_INVALID_CHARS) takes it: no stray, cut, overlong or surrogate sequence.
bool validUtf8(const std::string& s){
 for(size_t i=0;i<s.size();){
  const unsigned char c=s[i];const size_t n=c<0x80?1:c>=0xc2&&c<=0xdf?2:(c&0xf0)==0xe0?3:c>=0xf0&&c<=0xf4?4:0;
  if(!n||i+n>s.size())return false;
  for(size_t k=1;k<n;++k)if((static_cast<unsigned char>(s[i+k])&0xc0)!=0x80)return false;
  const unsigned char d=s[i+1<s.size()?i+1:i];
  if((c==0xe0&&d<0xa0)||(c==0xed&&d>=0xa0)||(c==0xf0&&d<0x90)||(c==0xf4&&d>=0x90))return false;
  i+=n;
 }
 return true;}
void must(lua_State* L,int ref,const char* script){if(!run(L,ref,script)){for(auto& l:logs)std::cerr<<l<<'\n';assert(false);}}
}
int main(int argc,char** argv){
 assert(argc==2);initServices();
 // A scalar write can straddle regions with different protections. Each original protection must be restored,
 // including when the second region refuses access, before any bytes are copied.
 uint8_t protectedBytes[16]{};uint8_t source[8]={1,2,3,4,5,6,7,8};uint32_t protection=0;
 uintptr_t base=reinterpret_cast<uintptr_t>(protectedBytes);
 hostRegions={{base,8,2},{base+8,8,32}};
 assert(res::write(protectedBytes+4,source,8,protection));assert(hostRegions[0].protection==2&&hostRegions[1].protection==32);assert(!memcmp(protectedBytes+4,source,8));
 memset(protectedBytes,0,16);hostRegions[1].fail=true;
 assert(!res::write(protectedBytes+4,source,8,protection));assert(hostRegions[0].protection==2&&protectedBytes[4]==0);hostRegions.clear();
 assert(ecs::simTime(1)==0);
 {// WP-21: one kiln.types event_each per observed type per tick; the stream lookup (probe) runs once per (world, type).
  static unsigned calls=0;static KilnResult answer=KILN_OK;calls=0;
  auto saved=types.event_each;
  types.event_each=[](uintptr_t w,uint32_t h,KilnEventFn fn,void* u)->KilnResult{++calls;if(answer!=KILN_OK)return answer;if(h==7){uint8_t ev[4]={1,2,3,4};fn(ev,4,u);fn(ev,4,u);}(void)w;return KILN_OK;};
  ecs::forgetEvents();uint64_t p0=ecs::eventProbes();unsigned seen=0;
  assert(ecs::eventIndex(77,7)==0&&calls==1&&ecs::eventProbes()==p0+1);   // Events.post's check: probe on the miss
  assert(ecs::eventIndex(77,7)==0&&calls==1&&ecs::eventProbes()==p0+1);   // cached: no second probe
  for(int tick=0;tick<100;++tick)assert(ecs::forEachEvent(77,7,[&](const uint8_t* e){seen+=e[0];})==0);
  assert(calls==101&&seen==200&&ecs::eventProbes()==p0+1);                // one read per tick, never a probe
  answer=KILN_ERR_NOT_FOUND;calls=0;
  assert(ecs::forEachEvent(77,9,[](const uint8_t*){})==-int(KILN_ERR_NOT_FOUND)&&calls==1);
  for(int tick=0;tick<50;++tick)assert(ecs::forEachEvent(77,9,[](const uint8_t*){})==-int(KILN_ERR_NOT_FOUND));
  assert(calls==1&&ecs::eventIndex(77,9)==-int(KILN_ERR_NOT_FOUND)&&calls==1);  // a stream that is missing is not asked again
  answer=KILN_ERR_INVALID_ARGUMENT;calls=0;                                         // kiln.types: not a known world
  assert(ecs::forEachEvent(77,7,[](const uint8_t*){})==-int(KILN_ERR_INVALID_ARGUMENT)&&calls==1);
  answer=KILN_OK;assert(ecs::eventIndex(77,9)==0&&calls==2&&ecs::eventProbes()==p0+2);  // whole cache dropped: looked up again
  answer=KILN_ERR_WRONG_THREAD;calls=0;assert(ecs::forEachEvent(78,7,[](const uint8_t*){})<0);answer=KILN_OK;
  assert(ecs::eventIndex(78,7)==0&&calls==2);                                         // new world, WRONG_THREAD not cached
  ecs::forgetEvents();calls=0;assert(ecs::eventIndex(78,7)==0&&calls==1);           // world left: starts over
  answer=KILN_ERR_INTERNAL;calls=0;assert(ecs::forEachEvent(78,11,[](const uint8_t*){})==-int(KILN_ERR_INTERNAL));  // a failed read
  answer=KILN_OK;assert(ecs::forEachEvent(78,11,[](const uint8_t*){})==0&&calls==2);  // is not cached: read again next tick
  static KilnResult postAnswer;auto savedPost=types.event_post;types.event_post=[](uintptr_t,uint32_t,const void*,uint32_t){return postAnswer;};
  uint8_t pay[4]={};calls=0;answer=KILN_ERR_NOT_FOUND;assert(ecs::forEachEvent(78,12,[](const uint8_t*){})<0&&calls==1);answer=KILN_OK;
  postAnswer=KILN_ERR_INVALID_ARGUMENT;assert(!ecs::postEvent(78,7,pay,4));                // a bad payload: the cache is kept
  assert(ecs::forEachEvent(78,12,[](const uint8_t*){})==-int(KILN_ERR_NOT_FOUND)&&calls==1);
  postAnswer=KILN_ERR_INTERNAL;assert(!ecs::postEvent(78,7,pay,4)&&ecs::eventIndex(78,7)==0&&calls==1);  // stream still known
  types.event_post=savedPost;
  ecs::forgetEvents();types.event_each=saved;
  std::cout<<"SET event stream cache (one read per type per tick, probe once): PASS\n";
 }
 assert(refl::Type{reinterpret_cast<const uint8_t*>(UINTPTR_MAX)}.size()==0);
 world.header.size=sizeof(KilnServiceHeader);kset::bind(&core);assert(!ecs::component(1,1,0));world.header.size=sizeof(world);kset::bind(&core);
auto root=std::filesystem::path(argv[1])/"mods";std::filesystem::remove_all(root);std::filesystem::create_directories(root/"one");std::filesystem::create_directories(root/"two");
 std::ofstream(root/"secret.lua")<<"return 'outside'";std::ofstream(root/"one"/"util.lua")<<"return 42";
 std::ofstream(root/"one"/"cycle.lua")<<"return require('cycle')";
 std::filesystem::create_directory_symlink(root/"two",root/"one"/"escape");
 std::filesystem::create_symlink(root/"secret.lua",root/"one"/"link.lua");
 std::filesystem::create_hard_link(root/"secret.lua",root/"one"/"hard.lua");
 rt::configure(root.string(),sink);auto* L=rt::open();int one=env(L,"one"),two=env(L,"two");
 {unsigned n=0;auto saved=types.event_each;static unsigned* cnt;cnt=&n;types.event_each=[](uintptr_t,uint32_t,KilnEventFn,void*)->KilnResult{++*cnt;return KILN_OK;};assert(!gObserving);for(int i=0;i<10;++i)rt::afterTick(1);assert(n==0);
  lua_getfield(L,LUA_REGISTRYINDEX,kObservers);lua_createtable(L,2,0);lua_pushboolean(L,0);lua_rawseti(L,-2,1);lua_pushboolean(L,0);lua_rawseti(L,-2,2);lua_rawseti(L,-2,0x1234);lua_pop(L,1);
  gObserving=true;dispatchEvents(L,1);assert(n==0&&!gObserving);  // every handler dropped (false left in place): no read, afterTick idle again
  lua_getfield(L,LUA_REGISTRYINDEX,kObservers);lua_pushnil(L);lua_rawseti(L,-2,0x1234);lua_pop(L,1);
  types.event_each=saved;}  // WP-21: no Observe, no per-tick work
 must(L,one,R"lua(
 assert(package==nil and require~=nil and io.open~=nil and io.popen==nil and os.execute==nil and os.exit==nil and debug==nil)
 assert(_G.package==nil and _G.require==require)
 assert(require('util')==42)
 for _,name in ipairs({'../secret','..\\secret','/secret','C:secret','util..x','link','hard','escape.x'}) do assert(not pcall(require,name)) end
 assert(not pcall(require,'cycle'))
 for _,p in ipairs({'../secret.lua','/tmp/x','C:x','NUL','foo.','escape/x','link.lua','hard.lua'}) do local ok,f=pcall(io.open,p,'w');assert(not ok or f==nil) end
 local f=assert(io.open('local.txt','w'));assert(getmetatable(f)==false);f:write('a\nb\n');f:close()
 local text='';for line in io.lines('local.txt') do text=text..line end;assert(text=='ab')
 assert(getmetatable('')==false)
 assert(not pcall(setmetatable,{}, {__gc=function() while true do end end}))
 assert(not pcall(setmetatable,Ecs,{}))
 assert(not pcall(rawset,Ecs,'get',false))
 local next,state=pairs(Ecs);assert(state==nil)
 local saved=Ecs.get;_G.foo=123;Ecs=nil;assert(saved~=nil)
 )lua");
 {std::ifstream f(root/"secret.lua");std::string sentinel((std::istreambuf_iterator<char>(f)),{});assert(sentinel=="return 'outside'");}
 must(L,two,"assert(foo==nil and Ecs.get~=nil and string.find('a','a')==1)");
 must(L,two,"local v=Types.new('Packed');v.floating=1.25;assert(v.floating==1.25)");
 auto start=std::chrono::steady_clock::now();
 for(const char* script:{"while true do end","while true do pcall(function() while true do end end) end","while true do xpcall(function() while true do end end,function(e) return e end) end","xpcall(function() while true do end end,function() while true do end end)","xpcall(function() error('ordinary') end,function() while true do end end)","local c=coroutine.create(function() while true do end end);while true do coroutine.resume(c) end","local c=coroutine.wrap(function() while true do end end);while true do pcall(c) end"}) assert(!run(L,two,script));
 assert(std::chrono::steady_clock::now()-start<std::chrono::seconds(3));
 must(L,one,"Tick.add(function() while true do end end)");
 must(L,two,"Tick.add(function() error({}) end);Tick.add(function() survived=true end)");runCallbacks(L,kTicks,"tick");must(L,two,"assert(survived)");
 gWorld=1;gInWorld=true;
 must(L,two,"h=Ecs.get(1,'Health');assert(getmetatable(h)==false);h.value=7;h.choice='Negative';assert(h.choice==-1)");
 uint32_t old=0;memcpy(&old,component,4);assert(old==7);
 actual=4;assert(!run(L,two,"h.floating=1"));assert(component[4]==0);actual=20;
 assert(!run(L,two,"h.floating=math.huge"));assert(!run(L,two,"h.floating=0/0"));assert(!run(L,two,"h.floating=1e100"));
 auto fields=ts[3].fields;ts[3].fields={{"loop",3,0}};
 assert(!run(L,two,"local t={};t.loop=t;h.nested=t"));ts[3].fields=fields;
 uint32_t invalidEnum=99;memcpy(component+12,&invalidEnum,4);
 must(L,two,"local v=Types.copy(h.nested);assert(not pcall(function() h.nested=v end));h.nested.b='Positive'");
 uint8_t before[32];memcpy(before,component,sizeof before);assert(!run(L,two,"h.nested={a=8,b=2^40}"));assert(memcmp(before,component,sizeof before)==0);
 must(L,two,"assert(not pcall(function() h.value=-1 end))");
 // Broken base/typedef graphs stop at a finite bound rather than recurse forever.
 unsigned fieldCount=0;refl::forFields(refl::lookup("Cycle"),[&](auto&){++fieldCount;});assert(fieldCount==0);
 assert(!refl::plain(refl::lookup("Cycle")));
 res::Entry entry{};entry.type=refl::fnv("keen::ecs::Health");
 auto previousResource=std::make_unique<uint8_t[]>(20);uint32_t first=11;memcpy(previousResource.get(),&first,4);resourcePtr=previousResource.get();resourceSize=20;
 lua_rawgeti(L,LUA_REGISTRYINDEX,two);pushRes(L,entry);lua_setfield(L,-2,"resource");lua_pop(L,1);
 must(L,two,"assert(resource.value==11)");
 uint8_t nextResource[20]{};uint32_t second=22;memcpy(nextResource,&second,4);resourcePtr=nextResource;previousResource.reset();
 must(L,two,"assert(resource.value==22);resource.value=23");memcpy(&second,nextResource,4);assert(second==23);
 resourceSize=4;assert(!run(L,two,"resource.floating=2"));resourceSize=20;
 tickThread=false;assert(!run(L,two,"local x=resource.value"));tickThread=true;
 resourcePtr=nullptr;assert(!run(L,two,"local x=resource.value"));
 gInDraw=true;assert(!run(L,two,"Admin.teleport(1,2,3)"));gInDraw=false;
 assert(!run(L,two,"Admin.teleport(0/0,2,3)"));assert(!run(L,two,"Admin.teleport(1,2,3,1)"));assert(gAdminQueue.empty());
 assert(!run(L,two,"Admin.teleport(1,2,3);error('discard queued command')"));assert(gAdminQueue.empty());
 KilnSettingsApi settings{};
 settings.find=[](KilnStr,uint32_t* idx)->KilnResult{*idx=0;return KILN_OK;};
 settings.info=[](uint32_t,KilnSettingInfo* o)->KilnResult{o->kind=KILN_SETTING_BOOL;return KILN_OK;};
 gKSettings=&settings;assert(!run(L,two,"Admin.changeSettings{flag=0}"));gKSettings=nullptr;
 must(L,two,"local ok,e=pcall(Admin.give,'x',1,0,4294967296);assert(not ok and tostring(e):find('rarity'))");
 // WP-05: English names (Admin.give, Res.text) from the shimmed item catalog and kiln.spawn; Admin.setLevel on a
 // shimmed kiln.progression.
 {
  static const SetItemName names[]={{7u,"Material_T1_Wood_Logs","Wood Logs"},{9u,"Material_T1_Wood_Logs_Blueprint","Wood Logs"},{8u,"Potion","Mana \"Potion\""}};
  gItemNames=names;gItemNameCount=3;resetNames();
  KilnItemsApi items{};
  items.item_id_of_guid=[](const uint8_t g[16])->KilnItemId{return g[0]==0x11?7u:0u;};
  items.find_item=[](KilnStr)->KilnItemId{return 0;};
  items.item_info=[](KilnItemId id,KilnItemInfo* o)->KilnResult{o->id=id;o->item_flags=id==9?0x8u:0u;return KILN_OK;};
  KilnSpawnApi spawn{};
  spawn.template_get=[](const KilnGuid* g,KilnTemplateInfo* o)->KilnResult{if(g->b[0]!=0x22)return KILN_ERR_NOT_FOUND;strcpy(o->display,"Common Goat");return KILN_OK;};
  static std::vector<KilnProgressionRequest> submitted;static KilnResult submitRc=KILN_OK;
  KilnProgressionApi prog{};
  prog.submit=[](KilnOwner,const KilnProgressionRequest* q,uint64_t*)->KilnResult{submitted.push_back(*q);return submitRc;};
  if(!core.log)core.log=[](KilnOwner,int32_t,KilnStr){};
  assets.resource_list=[](uint32_t,KilnAssetKey*,size_t,size_t* n)->KilnResult{*n=0;return KILN_OK;};
  // Features() off the tick thread (the SET tools' Compatibility tab and support report run on the render thread)
  // never runs the whole resource scan there; the first one on the tick thread does. (Nothing scanned before here.)
  {static unsigned lists;lists=0;auto saved=assets.resource_list;
   assets.resource_list=[](uint32_t,KilnAssetKey*,size_t,size_t* n)->KilnResult{++lists;*n=0;return KILN_OK;};
   auto resources=[]{for(auto& f:rt::features())if(f.id=="resources")return f;assert(false);return rt::Feature{};};
   tickThread=false;const auto off=resources();tickThread=true;
   if(lists)std::cerr<<"features: the resource scan ran off the tick thread ("<<lists<<" resource_list calls)\n";
   assert(lists==0&&!off.available&&off.reason=="Not loaded yet"&&gScanAsked);
   scanIfAsked();assert(lists>0&&!gScanAsked);   // the next world tick scans (tickMods)
   assets.resource_list=saved;}
  gKItems=&items;gKSpawn=&spawn;
  must(L,two,"assert(Res.text('11111111-1111-1111-1111-111111111111')=='Wood Logs')");
  must(L,two,"assert(Res.text('22222222-2222-2222-2222-222222222222')=='Common Goat')");
  must(L,two,"assert(Res.text('33333333-3333-3333-3333-333333333333')==nil)");
  must(L,two,"assert(not pcall(Res.text,'wood logs'))");
  // before kiln.items' catalog loads the blueprint flags are unknown (the lowest id wins, here the blueprint); that
  // choice must not stick once the catalog is there
  static uint32_t catalogCount=0;
  static const SetItemName bpFirst[]={{5u,"Axe_Blueprint","Axe"},{6u,"Axe","Axe"}};
  KilnItemsApi early{};
  early.item_count=[]()->uint32_t{return catalogCount;};
  early.item_info=[](KilnItemId id,KilnItemInfo* o)->KilnResult{if(!catalogCount)return KILN_ERR_UNAVAILABLE;o->id=id;o->item_flags=id==5?0x8u:0u;return KILN_OK;};
  gKItems=&early;gItemNames=bpFirst;gItemNameCount=2;resetNames();
  assert(itemIdByDisplay("axe")==5);catalogCount=2;assert(itemIdByDisplay("axe")==6);
  gKItems=&items;gItemNames=names;gItemNameCount=3;resetNames();
  assert(itemIdByDisplay("WOOD LOGS")==7&&itemIdByDisplay("mana \"potion\"")==8&&itemIdByDisplay("nope")==0);
  size_t queued=gAdminQueue.size();
  must(L,two,"Admin.give('wood logs',5)");assert(gAdminQueue.size()==queued+1);
  must(L,two,"local ok,e=pcall(Admin.give,'Nothing Like This');assert(not ok and tostring(e):find('no ItemInfo'))");
  // setLevel: range, the service, and the refusal with no player (kiln.players is not shimmed: ecs::player() is 0)
  must(L,two,"local ok,e=pcall(Admin.setLevel,20);assert(not ok and tostring(e):find('unavailable'))");
  gKProgression=&prog;
  must(L,two,"local ok,e=pcall(Admin.setLevel,0);assert(not ok and tostring(e):find('1 to 255'))");
  must(L,two,"local ok,e=pcall(Admin.setLevel,256);assert(not ok and tostring(e):find('1 to 255'))");
  must(L,two,"local ok,e=pcall(Admin.setLevel,20);assert(not ok and tostring(e):find('no player'))");
  assert(gAdminQueue.size()==queued+1&&submitted.empty());
  // a later refusal from kiln.progression is logged with its reason
  KilnProgressionResult later{};later.status=KILN_ERR_NOT_FOUND;strcpy(later.reason,"level above the cap");logs.clear();setLevelDone(&later,nullptr);
  assert(logs.size()==1&&logs[0].find("level above the cap")!=std::string::npos);
  gKItems=nullptr;gKSpawn=nullptr;gKProgression=nullptr;gItemNames=nullptr;gItemNameCount=0;resetNames();
 }
 // WP-15: Admin.setFlameLevel on a shimmed kiln.knowledge (the real one writes the world's Level attribute, lab-tested), and
 // Res.text for any loca tag from a catalog file.
 {
  gAdminQueue.clear();
  must(L,two,"local ok,e=pcall(Admin.setFlameLevel,3);assert(not ok and tostring(e):find('unavailable'))");
  static std::vector<uint32_t> asked;static KilnResult setRc=KILN_OK,resultRc=KILN_ERR_NOT_FOUND;static KilnFlameResult out{};
  KilnKnowledgeApi know{};know.header={sizeof(know),KILN_KNOWLEDGE_VERSION};
  know.flame_set=[](KilnOwner,uint32_t level,uint64_t* id)->KilnResult{if(setRc==KILN_OK){asked.push_back(level);*id=7;}return setRc;};
  know.flame_result=[](uint64_t request,KilnFlameResult* o)->KilnResult{if(request!=7||resultRc!=KILN_OK)return KILN_ERR_NOT_FOUND;*o=out;return KILN_OK;};
  gKKnowledge=&know;
  must(L,two,"local ok,e=pcall(Admin.setFlameLevel,0);assert(not ok and tostring(e):find('1 to 255'))");
  must(L,two,"local ok,e=pcall(Admin.setFlameLevel,256);assert(not ok and tostring(e):find('1 to 255'))");
  must(L,two,"local ok,e=pcall(Admin.setFlameLevel,'x');assert(not ok)");
  gInWorld=false;must(L,two,"local ok,e=pcall(Admin.setFlameLevel,5);assert(not ok and tostring(e):find('not in a world you host'))");gInWorld=true;
  assert(gAdminQueue.empty()&&asked.empty());
  must(L,two,"Admin.setFlameLevel(6)");assert(gAdminQueue.size()==1&&asked.empty());  // queued, as SET
  logs.clear();armAdmin(0);assert(asked.size()==1&&asked[0]==6&&gFlameWatch.size()==1&&logs.empty());
  armAdmin(0);assert(gFlameWatch.size()==1&&logs.empty());                              // no result yet: waits
  out={};out.size=sizeof out;out.status=KILN_OK;out.requested=6;out.after=6;out.mirrored=1;resultRc=KILN_OK;
  armAdmin(0);assert(gFlameWatch.empty()&&logs.empty());                                // written as asked: silent
  resultRc=KILN_ERR_NOT_FOUND;must(L,two,"Admin.setFlameLevel(250)");armAdmin(0);assert(asked.back()==250&&gFlameWatch.size()==1);
  out.requested=250;out.after=20;out.clamped=1;resultRc=KILN_OK;armAdmin(0);                             // the game's formula limited it
  assert(gFlameWatch.empty()&&logs.size()==1&&logs[0].find("above the game's maximum")!=std::string::npos&&logs[0].find("20")!=std::string::npos);
  logs.clear();setRc=KILN_ERR_UNAVAILABLE;must(L,two,"Admin.setFlameLevel(4)");armAdmin(0);
  assert(gFlameWatch.empty()&&logs.size()==1&&logs[0].find("was not run by the game")!=std::string::npos);
  logs.clear();setRc=KILN_OK;resultRc=KILN_ERR_NOT_FOUND;must(L,two,"Admin.setFlameLevel(4)");armAdmin(0);
  out={};out.size=sizeof out;out.status=KILN_ERR_INTERNAL;strcpy(out.reason,"the attribute write faulted");resultRc=KILN_OK;armAdmin(0);
  assert(gFlameWatch.empty()&&logs.size()==1&&logs[0].find("the attribute write faulted")!=std::string::npos);
  logs.clear();resultRc=KILN_ERR_NOT_FOUND;must(L,two,"Admin.setFlameLevel(4)");armAdmin(0);
  out={};out.size=sizeof out;out.status=KILN_OK;out.after=4;out.mirrored=0;strcpy(out.reason,"NetworkServerProgress.level did not follow within 180 updates");resultRc=KILN_OK;armAdmin(0);
  assert(gFlameWatch.empty()&&logs.size()==1&&logs[0].find("not mirrored")!=std::string::npos&&logs[0].find("180 updates")!=std::string::npos);
  logs.clear();resultRc=KILN_ERR_NOT_FOUND;
  for(int i=0;i<8;++i) { must(L,two,"Admin.setFlameLevel(4)"); }
  armAdmin(0);
  for(int i=0;i<1700;++i)armAdmin(0);  // eight serialized 181-step mirror waits exceed the old watcher deadline
  assert(gFlameWatch.size()==8&&logs.empty());
  out={};out.size=sizeof out;out.status=KILN_OK;out.after=4;out.mirrored=1;resultRc=KILN_OK;armAdmin(0);
  assert(gFlameWatch.empty()&&logs.empty());
  know.header.size=8;must(L,two,"local ok,e=pcall(Admin.setFlameLevel,3);assert(not ok and tostring(e):find('unavailable'))");  // an older kiln.knowledge
  gKKnowledge=nullptr;gAdminQueue.clear();logs.clear();

  // Res.text: any loca tag from the catalog's `loca` section (a server has no loca of its own). Hermetic: no other place may
  // hold a catalog (this machine may have a real one under HOME).
  std::vector<std::pair<std::string,std::string>> savedEnv;
  for(const char* n:{"HOME","XDG_DATA_HOME","KILN_DATA_DIR","WINE_HOST_HOME","WINE_HOST_XDG_DATA_HOME","WINEHOMEDIR","LOCALAPPDATA","KILN_CATALOG"}){
   if(const char* v=std::getenv(n))savedEnv.emplace_back(n,v);
   unsetenv(n);
  }
  uint8_t g[16];assert(res::parseGuid("4d8d0e3a-1b0f-4783-808c-1203bdd118ca",g));
  const uint32_t tag=refl::fnv(std::string_view(reinterpret_cast<const char*>(g),16));
  char hex[16];snprintf(hex,sizeof hex,"0x%08x",tag);
  auto catalog=root/"catalog";std::filesystem::create_directories(catalog);
  std::ofstream(catalog/"client.json")<<"{\"format\":1,\"game\":{\"steamBuild\":\"26430625\",\"svn\":\"1287369\"},\"sections\":{\"items\":[{\"name\":\"x\",\"id\":\"0x00000001\",\"text\":\"not loca\"}],\"loca\":["
    "{\"id\":\""<<hex<<"\",\"text\":\"Shock Resistance\"},{\"id\":\"0x00000002\",\"text\":\"<b>Bold</b> \\\"quoted\\\"\"},{\"id\":\"bad\",\"text\":\"skipped\"}],"
    "\"fields\":{\"loca\":[]}}}";
  setenv("KILN_CATALOG",catalog.string().c_str(),1);settexts::reset();
  std::string guidText="4d8d0e3a-1b0f-4783-808c-1203bdd118ca";
  must(L,two,("assert(Res.text('"+guidText+"')=='Shock Resistance')").c_str());
  assert(settexts::table()->text.size()==2&&settexts::table()->build=="26430625 (svn 1287369)");
  assert(settexts::table()->text.at(2)=="<b>Bold</b> \"quoted\"");                                   // raw, with the game's markup
  {
   // Bad entries must not truncate the table or turn an oversized hexadecimal id into a real low id.
   std::ofstream(catalog/"bad-entries.json")<<"{\"sections\":{\"loca\":[null,5,[],{\"id\":\"0x100000001\",\"text\":\"wrapped\"},"
     "{\"id\":\"0x00000007\",\"text\":\"seven\"},{\"id\":\"0x00000008\",\"id\":\"bad\",\"text\":\"bad duplicate\"}]}}";
   std::string why;
   auto parsed=settexts::parseFile(catalog/"bad-entries.json",why);
   assert(parsed&&parsed->text.size()==1&&parsed->text.at(7)=="seven");
   std::ofstream(catalog/"deep.json")<<std::string(70,'[')<<std::string(70,']');
   assert(!settexts::parseFile(catalog/"deep.json",why)&&why.find("not a readable catalog")!=std::string::npos);
  }
  must(L,two,"assert(Res.text('00000000-0000-0000-0000-000000000000')==nil)");                           // the zero GUID
  must(L,two,"assert(Res.text('4d8d0e3a-1b0f-4783-808c-1203bdd118cb')==nil)");                           // an unknown tag
  tickThread=false;must(L,two,("assert(Res.text('"+guidText+"')==nil)").c_str());tickThread=true;    // reads on the tick thread only
  // a catalog without a loca section (a server catalog), then none at all: nil, one console line per attempt, no retry for 60 s
  std::ofstream(catalog/"client.json")<<"{\"format\":1,\"sections\":{\"items\":[]}}";settexts::reset();logs.clear();
  must(L,two,("assert(Res.text('"+guidText+"')==nil)").c_str());
  assert(logs.size()==1&&logs[0].find("no 'loca' section")!=std::string::npos);
  must(L,two,("assert(Res.text('"+guidText+"')==nil)").c_str());assert(logs.size()==1);
  {
   // 60 s later the unchanged catalog is not parsed again, and the same reason is not printed again
   const size_t parses=settexts::gParses;
   settexts::gFailedAt=std::chrono::steady_clock::now()-std::chrono::seconds(61);
   must(L,two,("assert(Res.text('"+guidText+"')==nil)").c_str());
   assert(settexts::gParses==parses&&logs.size()==1);
   // a changed file (size) is read again
   std::ofstream(catalog/"client.json")<<"{\"format\":1,\"sections\":{\"items\":[ ]}}";
   settexts::gFailedAt=std::chrono::steady_clock::now()-std::chrono::seconds(61);
   must(L,two,("assert(Res.text('"+guidText+"')==nil)").c_str());
   assert(settexts::gParses==parses+1&&logs.size()==1);
  }
  std::ofstream(catalog/"client.json")<<"{ not json";settexts::reset();logs.clear();
  must(L,two,("assert(Res.text('"+guidText+"')==nil)").c_str());assert(logs.size()==1&&logs[0].find("not a readable catalog")!=std::string::npos);
  setenv("KILN_CATALOG",(root/"nowhere").string().c_str(),1);settexts::reset();logs.clear();
  must(L,two,("assert(Res.text('"+guidText+"')==nil)").c_str());assert(logs.size()==1&&logs[0].find("no client catalog found")!=std::string::npos);
  {
   // The first lookup reads the whole client catalog: the real one has 16,740 texts in 21.6 MB and takes 80-140 ms, more
   // than a callback's 50 ms. That one-time load is Kiln's work, not the mod's: the callback runs to its end. The rest
   // of the callback stands in for settest's onWorldEnter after its first Res.text (thousands of instructions).
   auto writeBig=[&](const std::filesystem::path& file,bool loca){
    std::ofstream f(file,std::ios::binary);f<<"{\"format\":1,\"game\":{\"steamBuild\":\"1\",\"svn\":\"2\"},\"sections\":{\"items\":[";
    for(int i=0;i<190000;++i)f<<(i?",":"")<<"{\"name\":\"Filler_Item_"<<i<<"\",\"id\":\"0x"<<std::hex<<i<<std::dec<<"\",\"text\":\"an entry the reader skips, as the real catalog's items\"}";
    f<<"]";
    if(loca){
     f<<",\"loca\":[";
     for(int i=0;i<16739;++i)f<<"{\"id\":\"0x"<<std::hex<<(0x10000000+i)<<std::dec<<"\",\"text\":\"Loca text "<<i<<", about as long as one of the game's own texts\"},";
     f<<"{\"id\":\""<<hex<<"\",\"text\":\"Shock Resistance\"}]";
    }
    f<<"}}";};
   const auto big=catalog/"big-client.json";writeBig(big,true);
   setenv("KILN_CATALOG",big.string().c_str(),1);settexts::reset();logs.clear();
   const auto t0=std::chrono::steady_clock::now();
   const bool ran=run(L,two,("local t=Res.text('"+guidText+"');local n=0;for i=1,20000 do n=n+i end;firstText=t").c_str());
   const double ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-t0).count();
   std::cerr<<"texts: "<<std::filesystem::file_size(big)/1000000<<" MB catalog, first lookup's callback "<<ms<<" ms\n";
   if(!ran){std::cerr<<"texts: the first lookup's catalog load stopped the callback:";for(auto& l:logs)std::cerr<<" ["<<l<<"]";std::cerr<<"\n";}
   assert(ms>50&&ran&&settexts::table()&&settexts::table()->text.size()==16740);
   must(L,two,"assert(firstText=='Shock Resistance')");
   // The 50 ms still hold for the mod's own code, in that callback and in the next ones (the table is loaded once).
   settexts::reset();
   assert(!run(L,two,("local t=Res.text('"+guidText+"');while true do end").c_str()));
   assert(!run(L,two,("while true do Res.text('"+guidText+"') end").c_str()));
   // Nor can a mod farm the excuse: a refused catalog is parsed once, and a lookup loop then ends at the budget.
   writeBig(big,false);settexts::reset();
   const size_t before=settexts::gParses;const auto t1=std::chrono::steady_clock::now();
   assert(!run(L,two,("while true do Res.text('"+guidText+"') end").c_str()));
   assert(settexts::gParses==before+1&&std::chrono::steady_clock::now()-t1<std::chrono::seconds(10));
   std::filesystem::remove(big);
  }
  unsetenv("KILN_CATALOG");settexts::reset();logs.clear();
  for(auto& [n,v]:savedEnv)setenv(n.c_str(),v.c_str(),1);
 }
 #include "set020_cases.inl"
 #include "inspector_cases.inl"
 for(auto& [name,owner]:gSystemOwners){(void)name;sys::releaseOwner(owner);}
 lua_close(L);gSystemOwners.clear();gInWorld=false;gWorld=0;
 // Failed initialization removes callbacks and releases this mod's system owner; reload runs shutdown and creates a fresh state.
 std::ofstream(root/"one"/"init.lua")<<"Systems.enable('stamina_depletion_sprint',false);Tick.add(print);Tick.add(function() print('bad callback') end);Tick.add(assert(load(\"return function() print('bad callback') end\"))()); error('bad init')";
 std::ofstream(root/"two"/"init.lua")<<"Systems.enable('stamina_depletion_sprint',false);print('good init');Tick.add(print); Tick.add(function() print('good tick') end);registerForEvent('onShutdown',function() print('good shutdown') end)";
 static int reported=0;rt::onModsReported([]{++reported;});
 std::ofstream(root/"two"/"mod.json")<<"{\"name\": \"Two Good\", \"version\": \"1.2.0\"}";
 // a mod folder with a space, Cyrillic and CJK in its name (a Windows player's Steam library and user names look like this): found,
 // loaded, and its io.open / require reach the folder's own files
 const std::filesystem::path unicodeDir=root/std::filesystem::path(u8"Мод 日本語");std::filesystem::create_directories(unicodeDir);
 std::ofstream(unicodeDir/"init.lua")<<"local f=assert(io.open('данные.txt','w'));f:write('ok');f:close();local g=assert(io.open('данные.txt'));assert(g:read('a')=='ok');g:close();assert(require('util')==7)";
 std::ofstream(unicodeDir/"util.lua")<<"return 7";
 rt::frame();assert(rt::modsLoaded());
 // what kiln-set writes into state/set.json: each folder's result, with mod.json's name and version
 {auto r=rt::modReports();assert(reported==1);
  auto find=[&](const char* n){for(auto& m:r)if(m.name==n)return m;return rt::ModReport{};};
  auto one=find("one"),two=find("two");
  assert(one.status=="failed"&&one.reason.find("bad init")!=std::string::npos);
  assert(two.status=="loaded"&&two.title=="Two Good"&&two.version=="1.2.0"&&two.reason.empty()&&two.folder.find("two")!=std::string::npos);
  auto unicode=find(reinterpret_cast<const char*>(u8"Мод 日本語"));assert(unicode.status=="loaded"&&unicode.reason.empty());}
 assert(logged("Mod Failed: one")&&logged("Mod Loaded: two")&&!logged("Mod loaded")&&logged("Mods: 2 loaded, 1 failed"));  // SET 0.2.0's wording
 rt::requestReload();rt::frame();assert(reported==2);rt::onModsReported(nullptr);rt::gameClosing();
 unsigned numeric=0;for(auto& l:logs){char* end=nullptr;strtod(l.c_str(),&end);if(end!=l.c_str()&&*end==0)++numeric;}assert(numeric==2);
 unsigned good=0,bad=0,shutdown=0;for(auto& l:logs){if(l=="good tick")++good;if(l=="bad callback")++bad;if(l=="good shutdown")++shutdown;}assert(good>=2&&bad==0&&shutdown==2&&!released.empty()&&disabled.empty());
 std::filesystem::remove_all(root);
 std::cout<<"SET Lua sandbox, budgets, isolation, reload, reflection bounds, mod reports: PASS\n";
}
