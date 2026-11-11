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
 // A native inspector failure must not poison Lua or leave its mutex locked. Reload cannot re-enable it.
 world.component=[](KilnEntity,uint32_t,uint32_t*)->const void*{throw 17;};setinspect::request(true,"Health",1,true);setinspect::tick(1);
 assert(setinspect::view().off&&!gBroken);setinspect::clear();assert(setinspect::view().off);must(L,one,"assert(1+1==2)");
 setinspect::clear();setinspect::request(false);world.find=savedFind;world.exists=savedExists;world.component=savedComponent;world.position=savedPosition;toolsPlayerService=false;kset::bind(&core);
 std::cout<<"SET inspector nearest/watch snapshots, stale/gone/session edits, ranges, thread guards and fault isolation: PASS\n";
}
