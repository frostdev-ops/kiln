// SPDX-License-Identifier: MIT
// Executes the production Lua bridge and bundled Lua, with only game/OS services substituted.
#include <cassert>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <set>
#include "../src/lua_api.cpp"
#include "services/kiln_types.h"
#include "services/kiln_assets.h"
#include <memory>

namespace hk {
void setLog(Log){} void flushLogs(){} std::vector<std::string> diagnostics(){return{};} uint64_t problems(){return 0;}
double seconds(int64_t){return 0;} size_t retainedBytes(){return 0;} void reserve(uintptr_t,const char*){}
uintptr_t findPattern(uintptr_t,std::string_view,std::string& e){e="unavailable";return 0;}
void restoreCode(uintptr_t,uint8_t*,size_t){} int subscribe(uintptr_t,const std::string&,const std::vector<Arg>&,uint32_t,std::string&){return -1;}
void unsubscribe(int){} void unsubscribeAll(){} void discardPending(){} bool drain(int,Batch&){return false;}
bool delivered(int,uint64_t){return false;} void refuse(const std::string&){}
}
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
 core.service=[](KilnStr n,uint32_t)->const void*{std::string_view s(n.ptr,n.len);return s=="kiln.types"?static_cast<const void*>(&types):s=="kiln.world"?static_cast<const void*>(&world):s=="kiln.assets"?&assets:nullptr;};
 core.on_tick_thread=[](){return tickThread;};core.server_world=[]()->uintptr_t{return 1;};
 core.owner_create=[](KilnStr)->KilnOwner{return ++nextOwner;};core.owner_revoke=[](KilnOwner){};
 kset::bind(&core);rt::bindKiln(&core,1);rt::setSystemsAvailable(true);
}
void sink(const char* s){logs.emplace_back(s);}
int env(lua_State* L,const char* name){pushModEnv(L,name);int ref=luaL_ref(L,LUA_REGISTRYINDEX);return ref;}
bool run(lua_State* L,int ref,const char* script){assert(luaL_loadstring(L,script)==LUA_OK);lua_rawgeti(L,LUA_REGISTRYINDEX,ref);lua_setupvalue(L,-2,1);return call(L,0,0);}
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
 {unsigned n=0;auto saved=types.event_each;static unsigned* cnt;cnt=&n;types.event_each=[](uintptr_t,uint32_t,KilnEventFn,void*)->KilnResult{++*cnt;return KILN_OK;};assert(!gObserving);for(int i=0;i<10;++i)rt::afterTick(1);assert(n==0);types.event_each=saved;}  // WP-21: no Observe, no per-tick work
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
 for(auto& [name,owner]:gSystemOwners){(void)name;sys::releaseOwner(owner);}
 lua_close(L);gSystemOwners.clear();gInWorld=false;gWorld=0;
 // Failed initialization removes callbacks and releases this mod's system owner; reload runs shutdown and creates a fresh state.
 std::ofstream(root/"one"/"init.lua")<<"Systems.enable('stamina_depletion_sprint',false);Tick.add(print);Tick.add(function() print('bad callback') end);Tick.add(assert(load(\"return function() print('bad callback') end\"))()); error('bad init')";
 std::ofstream(root/"two"/"init.lua")<<"Systems.enable('stamina_depletion_sprint',false);print('good init');Tick.add(print); Tick.add(function() print('good tick') end);registerForEvent('onShutdown',function() print('good shutdown') end)";
 rt::frame();assert(rt::modsLoaded());rt::requestReload();rt::frame();rt::gameClosing();
 unsigned numeric=0;for(auto& l:logs){char* end=nullptr;strtod(l.c_str(),&end);if(end!=l.c_str()&&*end==0)++numeric;}assert(numeric==2);
 unsigned good=0,bad=0,shutdown=0;for(auto& l:logs){if(l=="good tick")++good;if(l=="bad callback")++bad;if(l=="good shutdown")++shutdown;}assert(good>=2&&bad==0&&shutdown==2&&!released.empty()&&disabled.empty());
 std::filesystem::remove_all(root);
 std::cout<<"SET Lua sandbox, budgets, isolation, reload, reflection bounds: PASS\n";
}
