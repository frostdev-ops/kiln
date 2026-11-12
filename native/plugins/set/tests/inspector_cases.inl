// SPDX-License-Identifier: MIT
// The production inspector against fake kiln.world/types. Only tick reads or edits game memory.
{
 auto savedFind=world.find;auto savedExists=world.exists;auto savedComponent=world.component;auto savedPosition=world.position;
 toolsPlayers.header={sizeof(toolsPlayers),1};toolsPlayers.list=[](uint32_t,KilnPlayer* out,uint32_t cap,uint32_t* count)->KilnResult{*count=1;if(cap)out[0].entity=6000;return KILN_OK;};
 toolsPlayerService=true;kset::bind(&core);
 world.position=[](KilnEntity id,KilnVec3* out)->KilnResult{*out={id==6000?0.0:5001.0-id,0,0};return KILN_OK;};
 static bool present=true,changeDuringRead=false;present=true;changeDuringRead=false;static unsigned reads=0;reads=0;
 world.exists=[](KilnEntity id){return present&&id==1;};
 world.find=[](uint32_t,KilnEntity* out,uint32_t capacity,uint32_t* total)->KilnResult{*total=5000;for(uint32_t i=0;i<capacity;++i)out[i]=i+1;return KILN_ERR_BUFFER_TOO_SMALL;};
 world.component=[](KilnEntity id,uint32_t,uint32_t* n)->const void*{++reads;*n=actual;if(std::exchange(changeDuringRead,false))setinspect::request(true,"Health",2);return present&&id==1?component:nullptr;};
 actual=20;uint32_t seven=7;memcpy(component,&seven,4);float f=1.25f;memcpy(component+4,&f,4);
 setinspect::clear();setinspect::request(true,"Health",1);setinspect::tick(1);
 auto v=setinspect::view();assert(v.inWorld&&!v.gone&&v.selected==1&&v.entities.size()==200&&v.matched==5000);
 auto field=[&](){auto snapshot=setinspect::view();for(auto& f:snapshot.fields)if(f.component=="Health"&&f.path=="value")return f;assert(false);return setinspect::Field{};};
 auto old=field();assert(old.value=="7");
 assert(v.entities.front()==5000&&v.rows.front().distance==1&&v.entities.back()==4801); // nearest from all 5000 candidates
 setinspect::pin(v.serial,1,old);setinspect::pin(v.serial,1,old);setinspect::tick(1);
 assert(setinspect::view().pins.size()==1&&setinspect::view().pins[0].have);
 // Repeated scalar watches are deduplicated and live value snapshots follow game changes.
 seven=8;memcpy(component,&seven,4);setinspect::request(true,"Health",1,true);setinspect::tick(1);assert(setinspect::view().pins[0].field.value=="8");
 seven=7;memcpy(component,&seven,4);setinspect::request(true,"Health",1,true);setinspect::tick(1);v=setinspect::view();
 setinspect::edit(v.serial,old,"9");setinspect::tick(1);uint32_t result=0;memcpy(&result,component,4);assert(result==9&&field().value=="9");
 setinspect::edit(v.serial,old,"11");setinspect::tick(1);memcpy(&result,component,4);assert(result==9); // stale displayed value
 setinspect::edit(v.serial,field(),"4294967296");setinspect::tick(1);memcpy(&result,component,4);assert(result==9); // uint32 range
 setinspect::edit(v.serial,field(),"nan");setinspect::tick(1);memcpy(&result,component,4);assert(result==9);
 // Every tick checks presence even between 500 ms detail refreshes; recycled ids are not silently followed.
 auto latest=field();present=false;setinspect::tick(1);assert(setinspect::view().gone&&setinspect::view().pins[0].gone);
 present=true;setinspect::edit(v.serial,latest,"13");setinspect::tick(1);memcpy(&result,component,4);assert(result==9&&setinspect::view().gone&&setinspect::view().pins[0].gone);
 setinspect::unpin(1,old);assert(setinspect::view().pins.empty());
 setinspect::clear();setinspect::request(true,"Health",1);setinspect::edit(v.serial,old,"15");setinspect::tick(1);memcpy(&result,component,4);assert(result==9); // old world/serial
 // Even without an explicit leave callback, a new world address invalidates queued edits.
 v=setinspect::view();setinspect::edit(v.serial,field(),"17");setinspect::tick(2);
 memcpy(&result,component,4);assert(result==9&&setinspect::view().serial!=v.serial);
 // A renderer changing selection during component gathering invalidates the pending write too.
 v=setinspect::view();setinspect::edit(v.serial,field(),"19");changeDuringRead=true;setinspect::tick(2);
 memcpy(&result,component,4);assert(result==9);setinspect::request(true,"Health",1);setinspect::tick(2);
 unsigned before=reads;tickThread=false;setinspect::request(true,"Health",2);setinspect::tick(1);assert(reads==before);tickThread=true;
 // Smaller buffers never expose out-of-bounds reflected fields.
 actual=4;setinspect::clear();setinspect::request(true,"Health",1);setinspect::tick(1);auto shortView=setinspect::view();assert(shortView.fields.empty());actual=20;
 // Distinct NaN payloads format identically; compare exact original bytes, not their display strings.
 uint32_t nan1=0x7fc00001,nan2=0x7fc00002;memcpy(component+4,&nan1,4);setinspect::request(true,"Health",1,true);setinspect::tick(1);v=setinspect::view();
 auto shownNan=*std::find_if(v.fields.begin(),v.fields.end(),[](const auto& f){return f.path=="floating";});
 memcpy(component+4,&nan2,4);setinspect::edit(v.serial,shownNan,"2");setinspect::tick(1);memcpy(&result,component+4,4);assert(result==nan2);
 // The documented bounds, each with a case that fails when it goes: at most 65,536 candidates (and the note), 512
 // fields, nesting depth 6, 32 elements per fixed array, 64 pins, 32 queued edits, 256-byte edit values, 500 ms refresh.
 {
  const size_t first=ts.size();
  ts.push_back({"Arr40","keen::Arr40",160,KILN_TYPE_STATIC_ARRAY,0});                 // 40 uint32
  for(int k=7;k>=1;--k){                                                               // Dk {v; D(k+1) next}, D7 {v}
   TypeFixture d{"D"+std::to_string(k),"keen::D"+std::to_string(k),uint32_t(4*(8-k)),KILN_TYPE_STRUCT};
   d.fields.push_back({"v",0,0});
   if(k<7)d.fields.push_back({"next",int(ts.size()-1),4});
   ts.push_back(d);
  }
  TypeFixture wide{"Bounds","keen::Bounds",188+4*600,KILN_TYPE_STRUCT};
  wide.fields.push_back({"arr",int(first),0});wide.fields.push_back({"deep",int(first+7),160});
  for(int i=0;i<600;++i)wide.fields.push_back({"w"+std::to_string(i),0,uint64_t(188+4*i)});
  ts.push_back(wide);
  static size_t boundsType;boundsType=ts.size()-1;
  static uint8_t bounds[4096];memset(bounds,0,sizeof bounds);
  static uint32_t asked,boundsReads;asked=boundsReads=0;
  auto savedCount=types.component_type_count;auto savedAt=types.component_type_at;auto savedFind2=world.find;auto savedExists2=world.exists;auto savedComponent2=world.component;
  types.component_type_count=[]()->uint32_t{return 1;};types.component_type_at=[](uint32_t)->const KilnType*{return handle(boundsType);};
  world.exists=[](KilnEntity id){return id==1;};
  world.find=[](uint32_t,KilnEntity* out,uint32_t capacity,uint32_t* total)->KilnResult{asked=capacity;*total=70000;for(uint32_t i=0;i<capacity;++i)out[i]=i+1;return KILN_ERR_BUFFER_TOO_SMALL;};
  world.component=[](KilnEntity id,uint32_t,uint32_t* n)->const void*{++boundsReads;*n=sizeof bounds;return id==1?bounds:nullptr;};
  setinspect::clear();setinspect::request(true,"Bounds",1);setinspect::tick(1);
  auto b=setinspect::view();
  auto has=[&](const std::string& path){return std::any_of(b.fields.begin(),b.fields.end(),[&](const auto& f){return f.component=="Bounds"&&f.path==path;});};
  assert(asked==65536&&b.matched==70000&&b.entities.size()==200&&b.note.find("capped at 65536")!=std::string::npos);
  assert(b.fields.size()==512&&has("arr[31]")&&!has("arr[32]")&&has("w474")&&!has("w475"));             // 32 + 5 + 475
  assert(has("deep.next.next.next.next.v")&&!has("deep.next.next.next.next.next.v"));                   // depth 6, not 7
  // 500 ms: an unchanged selection is not copied again sooner; then it is.
  const uint32_t readsAfterRefresh=boundsReads;setinspect::tick(1);assert(boundsReads==readsAfterRefresh);
  std::this_thread::sleep_for(std::chrono::milliseconds(520));setinspect::tick(1);assert(boundsReads>readsAfterRefresh);
  b=setinspect::view();
  auto fieldNamed=[&](const std::string& path){for(auto& f:b.fields)if(f.path==path)return f;assert(false);return setinspect::Field{};};
  for(int i=0;i<65;++i)setinspect::pin(b.serial,1,fieldNamed("w"+std::to_string(i)));                    // 64 pins
  setinspect::tick(1);assert(setinspect::view().pins.size()==64);
  b=setinspect::view();
  for(int i=0;i<33;++i)setinspect::edit(b.serial,fieldNamed("w"+std::to_string(100+i)),"1");             // 32 edits
  setinspect::tick(1);
  {unsigned applied=0;for(int i=0;i<33;++i){uint32_t v;memcpy(&v,bounds+188+4*(100+i),4);applied+=v==1;}assert(applied==32);}
  b=setinspect::view();
  setinspect::edit(b.serial,fieldNamed("w200"),std::string(256,'0')+"7");                                // 257 bytes: refused
  setinspect::edit(b.serial,fieldNamed("w201"),std::string(255,'0')+"8");                                // 256 bytes: taken
  setinspect::tick(1);
  {uint32_t v200,v201;memcpy(&v200,bounds+188+800,4);memcpy(&v201,bounds+188+804,4);assert(v200==0&&v201==8);}
  setinspect::clear();
  types.component_type_count=savedCount;types.component_type_at=savedAt;world.find=savedFind2;world.exists=savedExists2;world.component=savedComponent2;
  ts.resize(first);
 }
 // A native inspector failure must not poison Lua or leave its mutex locked. Reload cannot re-enable it.
 world.component=[](KilnEntity,uint32_t,uint32_t*)->const void*{throw 17;};setinspect::request(true,"Health",1,true);setinspect::tick(1);
 assert(setinspect::view().off&&!gBroken);setinspect::clear();assert(setinspect::view().off);must(L,one,"assert(1+1==2)");
 setinspect::clear();setinspect::request(false);world.find=savedFind;world.exists=savedExists;world.component=savedComponent;world.position=savedPosition;toolsPlayerService=false;kset::bind(&core);
 std::cout<<"SET inspector nearest/watch snapshots, stale/gone/session edits, ranges, thread guards and fault isolation: PASS\n";
}
