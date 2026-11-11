// SPDX-License-Identifier: MIT
// SET 0.2.0's mod-author API through the production Lua bridge: Features(), key names, Settings.define and its
// settings.json, Hooks.rule / Hooks.param / Hooks.list on the real hook adapter (a fake kiln.hooks), and the write log
// that undoes a mod's resource changes. Included inside lua_runtime_test.cpp's main: L, one, two, root, logs, entry.
{
 // ---- Features() ----
 rt::setGameBuild(1793791052);
 must(L,two,R"lua(
 local f=Features()
 assert(f.game_build.compatible and f.game_build.available and f.game_build.reason=='' and f.game_build.label=='Game Build 1793791052')
 assert(f.events.compatible and f.events.available and f.events.reason=='')
 assert(f.game_options.compatible==false and f.game_options.available==false and f.game_options.reason:find('Kiln'))
 assert(f.field_of_view.compatible==false)
 assert(f.overlay.available==false and f.overlay.reason=='Not checked yet')
 assert(f.combat_xp.compatible==false and f.combat_xp.reason=='Not checked yet' and f.hit.available==false)
 assert(f.systems.compatible and f.systems.available and f.english_names.available and f.resources.compatible)
 assert(f.admin and f.admin_give and f.admin_settings.compatible==false and f.admin_settings.reason=='kiln.settings is not running')
 assert(f.hooks.available and f.admin_send.compatible==false and f.admin_send.reason:find('Kiln'))
 assert(f.mod_settings_page.available==false and f.mod_settings_page.reason=='Not checked yet')
 for id,v in pairs(f) do assert(type(v.compatible)=='boolean' and type(v.available)=='boolean' and type(v.reason)=='string' and type(v.label)=='string',id) end
 f.game_build=nil;assert(Features().game_build)   -- a new table on every call
 )lua");
 rt::setFeature("overlay",true);rt::setFeature("no_such_feature",true);
 must(L,two,"local f=Features();assert(f.overlay.compatible and f.overlay.available and f.overlay.reason=='' and f.no_such_feature==nil and f.mod_settings_page.available)");
 rt::setFeature("overlay",false,"Dedicated server: No overlay");
 must(L,two,"assert(Features().overlay.reason=='Dedicated server: No overlay')");
 gInWorld=false;
 must(L,two,"local e=Features().events;assert(e.compatible and not e.available and e.reason=='Not in a world you host')");
 gInWorld=true;
 rt::setGameBuild(5);
 must(L,two,"local b=Features().game_build;assert(not b.compatible and b.reason:find('Not tested') and GetGameBuild()==5)");
 rt::setGameBuild(1793791038);must(L,two,"assert(Features().game_build.compatible)");  // the dedicated server build Kiln's lab runs
 rt::setGameBuild(1793791052);
 must(L,two,"assert(GetVersion()=='0.2.0')");

 // ---- key names (SET 0.2.0's table) ----
 assert(rt::keyFromName("Space")==0x20&&rt::keyFromName("enter")==0x0D&&rt::keyFromName("Semicolon")==0xBA&&rt::keyFromName("<>")==0xE2);
 assert(rt::keyFromName("Num0")==0x60&&rt::keyFromName("NumAdd")==0x6B&&rt::keyFromName("Left")==0x25&&rt::keyFromName("Equals")==0xBB);
 assert(rt::keyFromName("Num10")==0&&rt::keyFromName("Semi")==0&&rt::keyFromName("F6")==0x75&&rt::keyFromName("0x41")==0x41);
 assert(rt::keyName(0x0D)=="Enter"&&rt::keyName(0x6B)=="NumAdd"&&rt::keyName(0xDE)=="Quote"&&rt::keyName(0x07)=="0x07");
 for(const auto& [name,vk]:kNamedKeys)assert(rt::keyFromName(name)==vk&&rt::keyName(vk)==name);
 {int mods=0,vk=0;assert(parseCombo("Ctrl+NumAdd",mods,vk)&&mods==rt::ModCtrl&&vk==0x6B);
  assert(comboName(rt::ModCtrl|rt::ModShift,0xBA)=="Ctrl+Shift+Semicolon");  // bindings.ini keeps names, not 0xBA
  assert(parseCombo("Ctrl+0xBA",mods,vk)&&vk==0xBA);}                         // a file from 0.1.1 still reads
 must(L,one,"Keys.on('Enter',function() end);Keys.on('Semicolon',function() end);assert(not pcall(Keys.on,'Bogus',function() end))");

 // ---- Settings.define ----
 must(L,two,R"lua(
 for _,bad in ipairs({ {}, {{key='a b',type='bool'}}, {{key='a',type='nope'}}, {{key='a',type='int',min=5,max=1}}, {{key='a',type='choice'}},
   {{key='a',type='bool'},{key='a',type='bool'}}, {{key='a',type='int',default='x'}}, {{type='bool'}}, {{key='a',type='text',max=0}}, {'x'} }) do
   assert(not pcall(Settings.define,bad))
 end
 )lua");
 assert(!mset::has("two"));  // a refused definition leaves nothing behind
 must(L,one,R"lua(
 s=Settings.define{
   {key='god',type='bool',default=false,label='God mode'},
   {key='regen',type='number',default=0,min=0,max=100},
   {key='count',type='int',default=0,min=1,max=10},
   {key='mode',type='choice',values={'off','slow','fast'}},
   {key='name',type='text',default='',max=4},
   onChange=function(k,v) changed=(changed or '')..k..'='..tostring(v)..';' end}
 assert(s.god==false and s.regen==0 and s.count==1 and s.mode=='off' and s.name=='')   -- defaults, moved into the range
 s.regen=500;assert(s.regen==100);s.regen=-3;assert(s.regen==0);s.regen=2.5
 s.count=99;assert(s.count==10 and math.type(s.count)=='integer')
 s.mode='fast';s.name='abcdefg';assert(s.name=='abcd');s.god=true
 for _,f in ipairs({function() s.count=1.5 end,function() s.regen=math.huge end,function() s.regen='1' end,function() s.mode='nope' end,
   function() s.god=1 end,function() s.name=5 end,function() s.unknown=1 end,function() return s.unknown end}) do assert(not pcall(f)) end
 assert(s.count==10 and s.mode=='fast' and s.god==true)
 assert(not pcall(Settings.define,{{key='a',type='bool'}}))   -- once per mod
 assert(getmetatable(s)==false and next(s)==nil and changed==nil)   -- its own writes don't run onChange
 )lua");
 mset::save(true,sink);
 assert(slurp(root/"one"/"settings.json")=="{\n  \"count\": 10,\n  \"god\": true,\n  \"mode\": \"fast\",\n  \"name\": \"abcd\",\n  \"regen\": 2.5\n}\n");
 assert(!std::filesystem::exists(root/"one"/"settings.json.tmp"));
 // settings.json belongs to Settings.define: a mod reads it, and can't open it to write.
 must(L,one,R"lua(
 for _,mode in ipairs({'w','a','r+','w+','wb'}) do local f,e=io.open('settings.json',mode);assert(f==nil and e:find('Settings.define')) end
 local f,e=io.open('SETTINGS.JSON.tmp','w');assert(f==nil and e:find('Settings.define'))
 local r=assert(io.open('settings.json'));assert(r:read('a'):find('"count": 10',1,true));r:close()
 local n=0;for line in io.lines('settings.json') do n=n+1 end;assert(n==7)
 local o=assert(io.open('other.json','w'));o:write('{}');o:close()
 )lua");
 assert(slurp(root/"one"/"settings.json").find("\"count\": 10")!=std::string::npos);
 #include "overlay_cases.inl"
 // Edits from the SET tools page reach onChange only at the next tick.
 assert(mset::set("one","regen",7,"",true)&&mset::set("one","regen",500,"",true));settingsEdits(L);
 must(L,one,"assert(changed=='regen=100.0;' and s.regen==100)");
 // A reload reads the saved values back. Wrong types fall back to the default, values are clamped to the new range,
 // and keys the mod no longer defines are kept in the file.
 mset::drop("one",sink);assert(!mset::has("one"));
 assert(slurp(root/"one"/"settings.json").find("\"regen\": 100")!=std::string::npos);
 std::ofstream(root/"one"/"settings.json")<<"\xEF\xBB\xBF{\"count\": \"x\", \"regen\": 42.5, \"legacy\": [1, 2], \"god\": true}";
 logs.clear();
 must(L,one,R"lua(
 s2=Settings.define{{key='count',type='int',default=3,min=1,max=10},{key='regen',type='number',max=40},{key='god',type='bool'}}
 assert(s2.count==3 and s2.regen==40 and s2.god==true);s2.count=4
 )lua");
 assert(logged("Settings: one: 'count' in settings.json isn't an integer, using the default"));
 mset::save(true,sink);
 {const std::string j=slurp(root/"one"/"settings.json");
  assert(j.find("\"legacy\": [1,2]")!=std::string::npos&&j.find("\"count\": 4")!=std::string::npos&&j.find("\"regen\": 40")!=std::string::npos);}
 // A broken file is set aside and the defaults are used.
 std::ofstream(root/"two"/"settings.json")<<"{ nope";
 logs.clear();
 must(L,two,"st=Settings.define{{key='a',type='int',default=5}};assert(st.a==5)");
 assert(logged("Renamed to settings.json.bad")&&slurp(root/"two"/"settings.json.bad")=="{ nope"&&!std::filesystem::exists(root/"two"/"settings.json"));
 must(L,two,"st.a=6");mset::dropAll(sink);  // what onShutdown / a reload does: saved at once
 assert(slurp(root/"two"/"settings.json")=="{\n  \"a\": 6\n}\n"&&!mset::has("one")&&!mset::has("two"));
 std::filesystem::remove(root/"two"/"settings.json");std::filesystem::remove(root/"two"/"settings.json.bad");

 // ---- Hooks.rule on a mod's own pattern ----
 hookScans["48 89 5C 24"]=0x900000;
 logs.clear();
 must(L,one,R"lua(
 local P,B='48 89 5C 24',{GetGameBuild()}
 assert(not pcall(Hooks.rule,{name='nobuilds',pattern=P,args={'i32'},set={{arg=1,add=1}}}))          -- builds is required
 assert(Hooks.rule{name='other build',pattern=P,builds={1,2},args={'i32'},set={{arg=1,add=1}}}==nil) -- not this build: nil, mod keeps running
 rid=Hooks.rule{name='more xp',pattern=P,builds=B,args={'i32','skip','f32','entity'},params={k=2},
   when={{arg=4,['in']={7,8}},{arg=1,ge=0}},set={{arg=3,mul='$k'},{arg=1,add=1}}}
 assert(math.type(rid)=='integer')
 -- a mod's own pattern takes number-argument changes only: no fields, no block, no after
 assert(not pcall(Hooks.rule,{name='blk',pattern=P,builds=B,args={'i32'},block=1}))
 assert(not pcall(Hooks.rule,{name='aft',pattern=P,builds=B,args={'i32'},after={{mul=2}}}))
 assert(not pcall(Hooks.rule,{name='fld',pattern=P,builds=B,args={'i32','skip','f32','entity*'},set={{arg=4,set=5}}}))
 assert(not pcall(Hooks.rule,{name='ptr',pattern=P,builds=B,args={'cstr'},set={{arg=1,set=5}}}))
 for _,bad in ipairs({
   {name='more xp',pattern=P,builds=B,args={'i32'},set={{arg=1,add=1}}},               -- the name is active
   {name='two ops',pattern=P,builds=B,args={'i32'},set={{arg=1,add=1,mul=2}}},
   {name='no op',pattern=P,builds=B,args={'i32'},set={{arg=1}}},
   {name='no param',pattern=P,builds=B,args={'i32'},set={{arg=1,add='$nope'}}},
   {name='bad arg',pattern=P,builds=B,args={'i32'},set={{arg=2,add=1}}},
   {name='skip arg',pattern=P,builds=B,args={'i32','skip'},set={{arg=2,add=1}}},
   {name='clash',pattern=P,builds=B,args={'f32'},set={{arg=1,add=1}}},                  -- disagrees with 'more xp'
   {name='nothing',pattern=P,builds=B,args={'i32'}},
   {name='a/b',pattern=P,builds=B,args={'i32'},set={{arg=1,add=1}}},
   {name='not a list',pattern=P,builds=B,args={'i32'},set={arg=1,add=1}},
   {name='two lists',pattern=P,builds=B,args={'entity','entity'},when={{arg=1,['in']={1}},{arg=2,['in']={2}}},set={{arg=1,add=1}}},
   {name='nine',pattern=P,builds=B,args={'i32'},set={{arg=1,add=1},{arg=1,add=1},{arg=1,add=1},{arg=1,add=1},{arg=1,add=1},{arg=1,add=1},{arg=1,add=1},{arg=1,add=1},{arg=1,add=1}}},
   {name='missing',pattern='AA BB CC DD',builds=B,args={'i32'},set={{arg=1,add=1}}},  -- the pattern isn't in the game
 }) do assert(not pcall(Hooks.rule,bad),bad.name) end
 )lua");
 assert(logged("one: Rule 'other build' off: Not verified on game build 1793791052"));
 assert(hookMids.count(0x900000)==1);
 // One call of the hooked function, as kiln.hooks runs the entry hook: registers in, registers out.
 auto fire=[&](uintptr_t fn,KilnRegisters& r){static uint64_t stack[9];if(!r.rsp)r.rsp=reinterpret_cast<uintptr_t>(stack);r.trampoline_rsp=r.rsp-8;auto& m=hookMids.at(fn);m.first(&r,m.second);};
 auto xmm=[](const KilnXmm& x){float f;memcpy(&f,x.bytes,4);return f;};
 auto call3=[&](uint64_t level,float mod,uint64_t entity){KilnRegisters r{};r.rcx=level;memcpy(r.xmm2.bytes,&mod,4);r.r9=entity;fire(0x900000,r);return std::pair<uint64_t,float>(r.rcx,xmm(r.xmm2));};
 assert(call3(10,1.5f,7)==std::make_pair(uint64_t(11),3.0f));       // in the list: both changes
 // Render actual SET rule diagnostics, not the generic Kiln hook registry.
 setui::bind(&toolsUi,1);assert(setui::service()->open({nullptr,0})==KILN_OK);toolsTab="Hooks";toolsRows.clear();setui::draw();
 assert(std::any_of(toolsRows.begin(),toolsRows.end(),[](const auto& row){return row.find("more xp")!=std::string::npos&&row.find("[active]")!=std::string::npos;}));
 assert(std::any_of(toolsRows.begin(),toolsRows.end(),[](const auto& row){return row.find("Hits: 1")!=std::string::npos;}));setui::stop();
 assert(call3(10,1.5f,9)==std::make_pair(uint64_t(10),1.5f));       // not in the list: untouched
 assert(call3(uint32_t(-1),1.5f,7).second==1.5f);                    // the other condition fails
 must(L,one,"Hooks.param(rid,'k',4);Hooks.list(rid,{9})");
 assert(call3(10,1.5f,9)==std::make_pair(uint64_t(11),6.0f)&&call3(10,1.5f,7).second==1.5f);
 must(L,one,R"lua(
 assert(not pcall(Hooks.param,rid,'nope',1) and not pcall(Hooks.param,rid,'k','x') and not pcall(Hooks.param,12345,'k',1))
 assert(not pcall(Hooks.list,rid,{-1}) and not pcall(Hooks.list,rid,{1.5}) and not pcall(Hooks.list,rid,'x'))
 assert(not pcall(Hooks.list,rid,{[1]=9,extra=7}))
 assert(not pcall(Hooks.param,rid,'k',math.huge) and not pcall(Hooks.param,rid,'k',0/0))
 for _,bad in ipairs({
   {name='hidden condition',pattern='48 89 5C 24',builds={GetGameBuild()},args={'i32'},
     when={[2]={arg=1,gt=0},extra=true},set={{arg=1,add=1}}},
   {name='bad builds',pattern='48 89 5C 24',builds={GetGameBuild(),extra=true},args={'i32'},set={{arg=1,add=1}}},
   {name='nan',pattern='48 89 5C 24',builds={GetGameBuild()},args={'i32'},set={{arg=1,add=0/0}}},
   {name='inf param',pattern='48 89 5C 24',builds={GetGameBuild()},args={'i32'},params={k=math.huge},set={{arg=1,add='$k'}}}
 }) do assert(not pcall(Hooks.rule,bad),bad.name) end
 local many={};for i=1,65 do many[i]=i end;assert(not pcall(Hooks.list,rid,many))
 nolist=Hooks.rule{name='plain',pattern='48 89 5C 24',builds={GetGameBuild()},args={'i32'},set={{arg=1,max=100}}}
 assert(not pcall(Hooks.list,nolist,{1}))
 )lua");
 {const std::string id=std::to_string(gNextHook-1);  // 'more xp' is one's: another mod can't change or remove it
  must(L,two,("assert(not pcall(Hooks.param,"+id+",'k',1) and not pcall(Hooks.list,"+id+",{1}) and not pcall(Hooks.remove,"+id+"))").c_str());}
 assert(call3(500,1.5f,9).first==100);                               // 'plain' runs after 'more xp': min(500 + 1, 100)
 gInConsole=true;assert(!run(L,one,"Hooks.rule{name='console',pattern='48 89 5C 24',builds={GetGameBuild()},args={'i32'},set={{arg=1,add=1}}}"));gInConsole=false;
 gInDraw=true;assert(!run(L,one,"Hooks.rule{name='draw',pattern='48 89 5C 24',builds={GetGameBuild()},args={'i32'},set={{arg=1,add=1}}}"));gInDraw=false;
 must(L,one,"Hooks.remove(rid);Hooks.remove(nolist);assert(not pcall(Hooks.param,rid,'k',1))");
 assert(call3(500,1.5f,9)==std::make_pair(uint64_t(500),1.5f));     // removed: the next calls are untouched
 must(L,one,"rid=Hooks.rule{name='more xp',pattern='48 89 5C 24',builds={GetGameBuild()},args={'i32','skip','f32','entity'},params={k=2},when={{arg=4,['in']={7,8}},{arg=1,ge=0}},set={{arg=3,mul='$k'},{arg=1,add=1}}}");
 assert(call3(10,1.5f,7)==std::make_pair(uint64_t(11),3.0f));       // the same rule made again works again (its slot is reused)
 for(const auto& t:hk::info())if(t.fn==0x900000)assert(t.rules.size()==2);

 // ---- Hooks.rule on catalog targets ----
 const auto& cat=contracts::catalog();
 contracts::setChecked({{"combat_xp",0,"Untested build",0,&cat[0]},{"hit",0x910000,"",1793791052,&cat[1]}});
 kset::hooktest::on_stack=[](uintptr_t,size_t){return true;};
 logs.clear();
 must(L,one,R"lua(
 local f=Features()
 assert(f.hit.compatible and f.hit.available and f.hit.reason=='' and not f.combat_xp.available and f.combat_xp.reason=='Untested build')
 assert(Hooks.rule{name='double xp',target='combat_xp',after={{mul=2},{max=5000}}}==nil)   -- the target is off on this build: nil
 for _,bad in ipairs({
   {name='nope',target='missing',set={{arg=1,add=1}}},
   {name='p',target='hit',pattern='48 89 5C 24',set={{arg=4,field='healthChange',mul=0.5}}},   -- the catalog has the pattern
   {name='a',target='hit',args={'skip'},set={{arg=4,field='healthChange',mul=0.5}}},
   {name='b',target='hit',block=true},                                                         -- hit can't be blocked
   {name='af',target='hit',after={{mul=2}}},
   {name='ro',target='hit',set={{arg=4,field='targetId',set=1}}},                              -- read-only field
   {name='nf',target='hit',set={{arg=4,field='nope',set=1}}},
   {name='arg',target='hit',set={{arg=1,set=1}}},                                              -- hit lets rules change fields only
 }) do assert(not pcall(Hooks.rule,bad),bad.name) end
 hid=Hooks.rule{name='tough skin',target='hit',params={k=0.5},when={{arg=4,field='targetId',eq='player'}},set={{arg=4,field='healthChange',mul='$k'}}}
 assert(math.type(hid)=='integer')
 )lua");
 assert(logged("one: Rule 'double xp' off: Catalog target 'combat_xp' is off (Untested build)"));
 {alignas(8) uint8_t hit[0x100]{};const uint32_t target=42;int32_t change=-40;
  memcpy(hit+0x94,&target,4);memcpy(hit+0xBC,&change,4);
  auto land=[&]{KilnRegisters r{};r.r9=reinterpret_cast<uintptr_t>(hit);fire(0x910000,r);memcpy(&change,hit+0xBC,4);return change;};
  rules::setPlayer(0);assert(land()==-40);                            // "player" never matches outside a world you host
  rules::setPlayer(42);assert(land()==-20);
  must(L,one,"Hooks.param(hid,'k',0.25)");assert(land()==-5);
  rules::setPlayer(43);assert(land()==-5);
  // The same target checked out for combat_xp: fields on argument 2, a block with a live param, and a return change.
  contracts::setChecked({{"combat_xp",0x920000,"",1793791052,&cat[0]},{"hit",0x910000,"",1793791052,&cat[1]}});
  must(L,one,"tid=Hooks.rule{name='tougher foes',target='combat_xp',when={{arg=2,field='isSet',eq=true}},set={{arg=2,field='level',add=5}}}");
  struct{uint32_t level;bool isSet;} victim{10,true};
  auto xp=[&](uint64_t players){KilnRegisters r{};r.rcx=20;r.rdx=reinterpret_cast<uintptr_t>(&victim);r.r9=players;fire(0x920000,r);return r;};
  {auto r=xp(1);assert(victim.level==15&&r.trampoline_rsp==r.rsp-8);}
  victim.isSet=false;xp(1);assert(victim.level==15);
  must(L,one,"fid=Hooks.rule{name='flat xp',target='combat_xp',params={xp=50},block='$xp'}");
  {auto r=xp(1);assert(r.trampoline_rsp==r.rsp&&r.rax==50&&victim.level==15);}   // blocked: returns to the caller with 50
  must(L,one,"Hooks.param(fid,'xp',75)");assert(xp(1).rax==75);
  must(L,one,"Hooks.remove(fid);did=Hooks.rule{name='double xp',target='combat_xp',after={{mul=2},{max=5000}}};assert(math.type(did)=='integer')");
  {auto r=xp(1);assert(r.trampoline_rsp==r.rsp-8);}                              // the original runs; the link changes its result (rules_review_test)
  must(L,one,"assert(not pcall(Hooks.rule,{name='both',target='combat_xp',block=1,after={{mul=2}}}))");
  // Without kiln.hooks a rule stays off the way an unchecked target does: nil and a yellow line, and Features() says so.
  hooksOn=false;kset::bindHooks(&core,1);logs.clear();
  must(L,one,"assert(Hooks.rule{name='nohooks',target='hit',set={{arg=4,field='healthChange',mul=2}}}==nil and not Features().hooks.available)");
  assert(logged("one: Rule 'nohooks' off: Native hooks are unavailable (kiln.hooks is not running)"));
  hooksOn=true;kset::bindHooks(&core,1);
  // A mod that stops (it failed while loading, or a reload) takes its rules with it.
  rules::setPlayer(42);change=-40;memcpy(hit+0xBC,&change,4);
  removeModHooks(L,"one");
  assert(land()==-40&&call3(10,1.5f,7)==std::make_pair(uint64_t(10),1.5f));
  victim={10,true};xp(1);assert(victim.level==10);
  rules::setPlayer(0);}
 contracts::setChecked({});kset::hooktest::on_stack=nullptr;

 // ---- the write log: a mod's resource changes go back when it stops ----
 {uint8_t data[20]{};auto value=[&]{uint32_t v;memcpy(&v,data,4);return v;};auto setValue=[&](uint32_t v){memcpy(data,&v,4);};
  setValue(100);resourcePtr=data;resourceSize=20;
  lua_rawgeti(L,LUA_REGISTRYINDEX,one);pushRes(L,entry);lua_setfield(L,-2,"resource");lua_pop(L,1);
  logs.clear();
  mustAs(L,one,"one","resource.value=150");assert(value()==150&&wlog::records()==1);
  mustAs(L,two,"two","resource.value=160;resource.floating=2.5");assert(value()==160&&wlog::records()==2);
  wlog::leave("one",sink);                                            // two changed it too: it stays until two leaves
  assert(value()==160&&logged("stays changed: two changed it too")&&wlog::records()==2);
  wlog::leave("two",sink);
  {float f;memcpy(&f,data+4,4);assert(value()==100&&f==0&&wlog::records()==0&&logged("Write Log: two: 2 change(s) undone"));}
  // The console's change is not logged, and a logged value the console changed afterwards is left alone.
  must(L,one,"resource.value=120");assert(value()==120&&wlog::records()==0);
  setValue(100);mustAs(L,one,"one","resource.value=170");must(L,one,"resource.value=180");
  logs.clear();wlog::leave("one",sink);
  assert(value()==180&&logged("was changed outside the mods")&&!logged("undone")&&wlog::records()==0);
  // The game's buffer for the resource may move between ticks: the original goes back into the current one.
  setValue(100);mustAs(L,one,"one","resource.value=5");
  uint8_t moved[20];memcpy(moved,data,20);resourcePtr=moved;setValue(77);
  wlog::leaveAll(sink);{uint32_t v;memcpy(&v,moved,4);assert(v==100&&value()==77&&wlog::records()==0);}
  resourcePtr=data;setValue(100);
  // Mods that stop off the tick thread (a reload from the main menu, kiln-set stepping aside on the UI thread) can't
  // reach the resource there. Their records must not be dropped: a write before the restore keeps the true original,
  // so a relative edit doesn't compound.
  mustAs(L,one,"one","resource.value=resource.value*2");assert(value()==200);
  tickThread=false;logs.clear();wlog::leaveAll(sink);
  const bool dropped=logged("Not restored");assert(value()==200);
  tickThread=true;mustAs(L,one,"one","resource.value=resource.value*2");assert(value()==400);  // the reloaded mod
  logs.clear();wlog::leave("one",sink);
  if(value()!=100)std::cerr<<"write log: after a reload off the tick thread the mod's leave restored "<<value()<<", not 100\n";
  assert(!dropped&&value()==100&&wlog::records()==0&&logged("one: 1 change(s) undone"));
  // Otherwise the record waits with no writer, and the server step (rt::settleWrites, kiln-set's on_pre; also after
  // stepping aside) restores it on the tick thread. Off the tick thread it keeps waiting.
  mustAs(L,one,"one","resource.value=6");tickThread=false;logs.clear();wlog::leaveAll(sink);
  assert(value()==6&&wlog::records()==1&&wlog::waiting()&&logged("1 change(s) go back at the next tick"));
  rt::settleWrites();assert(value()==6&&wlog::records()==1&&wlog::waiting());
  tickThread=true;logs.clear();rt::settleWrites();
  assert(value()==100&&wlog::records()==0&&!wlog::waiting()&&logged("Write Log: 1 change(s) undone"));
  // The same for one mod leaving off the tick thread while another's record stays.
  mustAs(L,one,"one","resource.value=7");mustAs(L,two,"two","resource.floating=3.5");
  tickThread=false;wlog::leave("one",sink);tickThread=true;assert(value()==7&&wlog::records()==2&&wlog::waiting());
  rt::settleWrites();{float f;memcpy(&f,data+4,4);assert(value()==100&&f==3.5f&&wlog::records()==1);}
  wlog::leave("two",sink);{float f;memcpy(&f,data+4,4);assert(f==0&&wlog::records()==0);}
  // A resource that can't be reached on the tick thread is reported, not written.
  mustAs(L,one,"one","resource.value=6");resourcePtr=nullptr;logs.clear();wlog::leaveAll(sink);resourcePtr=data;
  assert(value()==6&&wlog::records()==0&&logged("can't be reached any more: Not restored"));
  // A full log refuses the write instead of making a change it can't undo.
  setValue(100);wlog::setBudget(10);
  assert(!runAs(L,one,"one","resource.value=7")&&value()==100&&wlog::records()==0&&logged("write log full"));
  wlog::setBudget(16<<20);
  // A function built with load() and registered by a mod is that mod's: its writes are logged too.
  mustAs(L,one,"one","Tick.add(load('resource.value=8'))");
  gReload=false;runCallbacks(L,kTicks,"tick");
  assert(value()==8&&wlog::records()==1);
  // unloadMod (a mod that failed while loading) undoes its changes and drops its settings page and rules.
  must(L,one,"s3=Settings.define{{key='a',type='bool'}}");assert(mset::has("one"));
  logs.clear();unloadMod(L,"one");
  assert(value()==100&&wlog::records()==0&&!mset::has("one")&&logged("Write Log: one: 1 change(s) undone"));
  std::filesystem::remove(root/"one"/"settings.json");
  resourcePtr=nullptr;}
 logs.clear();
}
