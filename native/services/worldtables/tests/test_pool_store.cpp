// SPDX-License-Identifier: MIT
#include "../pool_store.h"
#include <iostream>
#include <unistd.h>
using namespace kwt;
int main() {
 int checks=0,failed=0;auto check=[&](const char* name,bool ok){++checks;if(!ok)++failed;std::cout<<(ok?"PASS ":"FAIL ")<<name<<'\n';};
 namespace fs=std::filesystem;auto root=fs::temp_directory_path()/("kiln-pool-store-"+std::to_string(getpid()));fs::remove_all(root);
 kiln::capacity::pending_io::TrustedBase base{root};std::vector<std::string> errors;
 kiln::capacity::pending_io::diagnostic=[&](const auto&,const auto& why){errors.push_back(why);};
 PoolBootStore empty;empty.capture(base);check("missing store is silent",empty.files.empty() && errors.empty());
 ModFile mf;mf.mod="p";mf.clients="mod";read_blocks(parse_relaxed(R"({"buildingMaterialPools":{"walls":{"name":"Walls","materials":[{"gameMaterial":"WoodRough"}]}}})"),&mf);
 PoolRegistrations ledger;auto receipt=ledger.prepare(mf.pools.at(0),KILN_DEFINE_ALLOW_NEXT_START,false);auto path=root/"pending/p"/("@pool-"+receipt.definition.guid.text()+".json");std::string reason;
 check("durable write",kiln::capacity::pending_store_write(base,root/"pending",path,receipt.envelope(),&reason));
 check("same process file waits",empty.replay(base,{"p"}).empty());
 PoolBootStore boot;boot.capture(base);check("inactive mod not applied",boot.replay(base,{}).empty());check("an inactive mod's file is not refused as unsafe (the service notes it kept, once)",errors.empty());check("inactive file retained",fs::exists(path));
 auto rows=boot.replay(base,{"p"});check("loaded mod replays exactly one",rows.size()==1 && rows[0].definition==receipt.definition.definition);
 // kiln#152: a pool kept for the next start is read back at that start (read_mod) from its own section. A
 // ToolMaterialPoolBlock's envelope ("entries") re-wrapped as buildingMaterialPools was refused there ("unknown field
 // entries", "materials takes 1..64 entries") at every start, so it never became Registered and its tool never lowered.
 {
  auto start_two=[&](const std::string& mod,const char* doc,const char* label) {
   errors.clear();
   ModFile first;first.mod=mod;first.clients="mod";read_blocks(parse_relaxed(doc),&first);
   PoolRegistrations start1;auto kept=start1.prepare(first.pools.at(0),KILN_DEFINE_ALLOW_NEXT_START,false);
   auto file=root/"pending"/mod/("@pool-"+kept.definition.guid.text()+".json");
   check((std::string(label)+": kept for the next start").c_str(),kiln::capacity::pending_store_write(base,root/"pending",file,kept.envelope(),&reason));
   if(!reason.empty())std::cout<<"  "<<label<<": "<<reason<<'\n';
   PoolBootStore next;next.capture(base);auto replayed=next.replay(base,{mod});
   for(const auto& e:errors)std::cout<<"  "<<label<<" store: "<<e<<"\n";
   check((std::string(label)+": replays one").c_str(),replayed.size()==1 && errors.empty());
   if(replayed.size()!=1)return;
   ModFile again;again.mod=mod;again.clients="mod";read_blocks(pool_replay_document(replayed[0]),&again);
   for(const auto& e:again.errors)std::cout<<"  "<<label<<": "<<e<<'\n';
   check((std::string(label)+": read back without errors").c_str(),again.errors.empty() && again.pools.size()==1);
   if(again.pools.size()==1)
    check((std::string(label)+": same kind, identity and definition as kept (boot == definition, so it registers)").c_str(),
          again.pools[0].tool==first.pools[0].tool && again.pools[0].valid && again.pools[0].guid==first.pools[0].guid &&
          again.pools[0].definition==first.pools[0].definition && again.pools[0].entries.size()==first.pools[0].entries.size());
  };
  start_two("t1",R"({"toolMaterialPools":{"rubble-pool":{"name":"Rubble","entries":[{"item":"Build_TerrainMaterial_T1_Rubble","terrainMaterial":8,"voxelsPerItem":16}]}}})","tool pool");
  start_two("t2",R"({"toolMaterialPools":{"renamed":{"name":"Rubble","was":"rubble-pool","entries":[{"item":"Build_TerrainMaterial_T1_Rubble","terrainMaterial":8,"voxelsPerItem":16},{"item":"Build_TerrainMaterial_T1_Dirt","terrainMaterial":2,"voxelsPerItem":8}]}}})","renamed tool pool");
  start_two("t3",R"({"buildingMaterialPools":{"walls":{"name":"Walls","roof":true,"materials":[{"gameMaterial":"WoodRough"}]}}})","building pool");
  // A tool pool kept by 0.2.0-dev.31.14 to 31.20, byte for byte as c38729f1 writes it (kiln#152's file name): the first
  // start after the update reads that file as it is, so the tool registers without the mod defining the pool again.
  {
   errors.clear();const std::string mod="kiln-rubble-tool",name="@pool-6c931340-eb76-5df4-972e-0919e292ecba.json";
   const char* stored=R"({"block":"rubble-pool","canonical":{"entries":[{"item":"Build_TerrainMaterial_T1_Rubble","terrainMaterial":8,"voxelsPerItem":16}],"name":"Rubble"},"definition":"6c931340-eb76-5df4-972e-0919e292ecba","format":1,"mod":"kiln-rubble-tool","registration":"a0cdcbe2d90856a4bac2399b02b5e196"})";
   check("31.19 tool pool file written",kiln::capacity::pending_store_write(base,root/"pending",root/"pending"/mod/name,stored,&reason));
   PoolBootStore next;next.capture(base);auto replayed=next.replay(base,{mod});
   for(const auto& e:errors)std::cout<<"  31.19 store: "<<e<<"\n";
   check("31.19 tool pool file replays as a tool pool",replayed.size()==1 && errors.empty() && replayed[0].tool && replayed[0].block=="rubble-pool");
   ModFile now;now.mod=mod;now.clients="mod";
   read_blocks(parse_relaxed(R"({"toolMaterialPools":{"rubble-pool":{"name":"Rubble","entries":[{"item":"Build_TerrainMaterial_T1_Rubble","terrainMaterial":8,"voxelsPerItem":16}]}}})"),&now);
   ModFile again;again.mod=mod;again.clients="mod";if(replayed.size()==1)read_blocks(pool_replay_document(replayed[0]),&again);
   for(const auto& e:again.errors)std::cout<<"  31.19: "<<e<<'\n';
   check("31.19 tool pool file reads back as the definition the mod gives now (boot == definition, so it registers)",
         again.errors.empty() && again.pools.size()==1 && now.pools.size()==1 && again.pools[0].tool && again.pools[0].guid==now.pools[0].guid &&
         again.pools[0].definition==now.pools[0].definition && PoolRegistrations{}.prepare(again.pools[0],KILN_DEFINE_ALLOW_NEXT_START,false).key()=="a0cdcbe2d90856a4bac2399b02b5e196");
   fs::remove_all(root/"pending"/mod);
  }
 }
 auto wrong=parse_relaxed(receipt.envelope());wrong["format"]="1";PoolDefinition def;check("wrong envelope type refused",!pool_envelope(wrong,"p",&def,&reason));
 check("oversize refused on write",!kiln::capacity::pending_store_write(base,root/"pending",path,std::string(65537,'x'),&reason));
 auto newer=receipt;newer.definition.definition+=" ";check("changed file write",kiln::capacity::pending_store_write(base,root/"pending",path,newer.envelope()+" ",&reason));
 check("changed boot fingerprint waits",boot.replay(base,{"p"}).empty());
 check("torn file write",kiln::capacity::pending_store_write(base,root/"pending",path,"{",&reason));PoolBootStore torn;torn.capture(base);check("torn never replays",torn.replay(base,{"p"}).empty() && fs::exists(path));
 fs::remove(path);auto bad=path.parent_path()/"@pool-wrong.json";kiln::capacity::pending_store_write(base,root/"pending",bad,receipt.envelope(),&reason);PoolBootStore names;names.capture(base);check("wrong filename refused",names.replay(base,{"p"}).empty());
 fs::remove(bad);fs::remove_all(root/"pending/p");fs::create_directory_symlink(root,root/"pending/p");check("link write refused",!kiln::capacity::pending_store_write(base,root/"pending",path,receipt.envelope(),&reason));
 fs::remove_all(root);std::cout<<checks<<" checks, "<<failed<<" failures\n";return failed?1:0;
}
