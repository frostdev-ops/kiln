#!/usr/bin/env python3
"""Linux host regression: compile the production config and scheduling code, no Wine/game.
Only platform I/O and foreign-call guards are substituted; config mutations and lifecycle are real.
Run through the review slot, with KILN_OUT set to this task's build folder.
"""
import os
import re
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[4]
context = (root / 'native/plugins/shroudtopia/src/context.cpp').read_text()
plugin = Path(os.environ.get('KILN_SHROUDTOPIA_SOURCE', root / 'native/plugins/shroudtopia/src/plugin.cpp')).read_text()
loop = plugin[plugin.index('int json_int('):plugin.index('DWORD WINAPI thread_main')]
config = context[context.index('namespace config {'):context.index('// ---------------------------------------------------------------------------------------------- memory')]
loaded = plugin[plugin.index('struct Loaded {'):plugin.index('ModContext* g_ctx')]
if 'HMODULE module' not in loaded: loaded=loaded.replace('struct Loaded {','struct Loaded { HMODULE module=nullptr;')
scheduling = plugin[plugin.index('void drive('):plugin.index('int json_int(')]
flow = plugin[plugin.index('void prepare_original_code('):plugin.index('void drive(')] if 'void prepare_original_code(' in plugin else 'void prepare_original_code(Loaded&) {}'

# Exercise the exact registration hook, including the origin gate and metadata name: the plugin's own statement, taken
# as it is (a fixed copy of it went stale when the gate learned "profile-eml" and then silently tested no hook at all).
hooks = re.findall(r'config::ensure_profile_entry\(m->name,[^;]*\);',
                   plugin[plugin.index('m->mod = inst;'):plugin.index('// Shroudtopia\'s UpdateMods.')])
if len(hooks) != 1: raise SystemExit(f'FAIL registration: expected one config::ensure_profile_entry(m->name, ...) call, found {len(hooks)}')
registration = hooks[0]
preamble = r'''
#include <filesystem>
#include <fstream>
#include <mutex>
#include <atomic>
#include <set>
#include <vector>
#include <memory>
#include <functional>
#include <iostream>
#include <algorithm>
#include <stdexcept>
#include <cstdio>
#include <nlohmann/json.hpp>
using json = nlohmann::json;
namespace fs = std::filesystem;
constexpr int KILN_LOG_INFO=1, KILN_LOG_WARN=2, KILN_LOG_ERROR=3;

using HMODULE=void*;
struct KilnStr {const char* ptr=nullptr;size_t len=0;};
namespace kiln::compat {
constexpr unsigned kOriginalCodeVersion=2;
constexpr const char* kOriginalCodeService="kiln.internal.original-code";
struct OriginalCodeApi {KilnStr (*rule)(KilnStr);};
}
std::vector<void*> patched;
std::vector<std::string> compat_logs;
KilnStr selected_rule(KilnStr mod){return std::string(mod.ptr,mod.len)=="shroudtopia:item-skill-text"?KilnStr{"test-rule",9}:KilnStr{};}
const kiln::compat::OriginalCodeApi rules{selected_rule};
bool have_rules=true,have_memcmp=true;
struct Host {
 template<class T> const T* service(const char*,unsigned){return have_rules?reinterpret_cast<const T*>(&rules):nullptr;}
 template<class... T> void logf(int,const char* fmt,T... args){char b[1024];std::snprintf(b,sizeof b,fmt,args...);compat_logs.emplace_back(b);}
} H;
namespace original {size_t install(HMODULE h,const kiln::compat::OriginalCodeApi*){if(!have_memcmp)return 0;patched.push_back(h);return 1;}}

std::atomic<bool> g_log_enabled{true};
namespace kcompat {
KilnStr kstr(const std::string& s){return {s.data(),s.size()};}
std::string_view sv(KilnStr s){return {s.ptr?s.ptr:"",s.len};}
bool fail_write=false;
bool exists(const fs::path& p) { return fs::exists(p); }
std::string path_utf8(const fs::path& p) { return p.string(); }
bool read_file(const fs::path& p, std::string* s) { std::ifstream f(p); if (!f) return false; *s={std::istreambuf_iterator<char>(f),{}}; return true; }
bool write_file_atomic(const fs::path& p, const std::string& s) {
 if (fail_write) return false;
 fs::create_directories(p.parent_path()); auto temp=p; temp += ".tmp";
 std::ofstream f(temp); f<<s; f.close(); if (!f) return false; fs::rename(temp,p); return true;
}
}
using DWORD=unsigned;
template<class F> bool guarded(F f, DWORD* code) { try { f(); return true; } catch (...) { *code=1; return false; } }
void set_current_mod(const std::string&) {}
std::string fault_text(DWORD) { return "fault"; }
constexpr const char* kShroudtopiaVersion="0.1.1";
std::vector<std::string> g_lines;  // what the loader logged, warnings marked
void loader_log(int level, const std::string& s) { g_lines.push_back((level==KILN_LOG_WARN?"W ":"I ")+s); }
struct ModContext { std::set<void*> loadedMods; } ctx, *g_ctx=&ctx;
struct Mod {
 bool loaded=false, active=false;
 bool keeps_flag=true;  // false: a mod that never writes its own `active` field
 bool fault_activate=false;
 int activations=0, deactivations=0, updates=0;
 void* module=nullptr;
 void Load(ModContext*) {if(module && std::find(patched.begin(),patched.end(),module)==patched.end())throw std::runtime_error("Load ran before IAT patch");} void Activate(ModContext*) {++activations; if (fault_activate) throw std::runtime_error("test"); if (keeps_flag) active=true;}
 void Update(ModContext*) {++updates;} void Deactivate(ModContext*) {++deactivations; if (keeps_flag) active=false;}
};
'''
main = r'''
std::mutex g_mu;
std::vector<std::unique_ptr<Loaded>> g_mods;
bool g_server=false;
'''+flow+scheduling+r'''
// Run the real first-pass loop with a virtual clock and no Windows worker.
uint64_t clock_ms=0;
unsigned waits=0, scans=0;
bool external=false;
void* g_stop=nullptr;
std::atomic<uint64_t> g_cycles{0};
bool g_handoff=false;
constexpr unsigned WAIT_OBJECT_0=0;
uint64_t GetTickCount64(){return clock_ms;}
unsigned WaitForSingleObject(void*,unsigned ms){clock_ms+=ms;return ++waits>20?WAIT_OBJECT_0:258;}
bool observe_own_loader(){return external;}
void check_and_load(){++scans;}
void write_status(){}
struct FirstPass {
 bool completed=false;
 std::function<void()> validate;
 void done(){if(!completed){completed=true;if(validate)validate();}}
}g_first_pass;
'''+loop+r'''
int failures=0, checks=0;
void check(bool ok, const char* label) { ++checks; if (!ok) { ++failures; std::cerr<<"FAIL "<<label<<"\n"; } }
int main(int argc, char** argv) {
 fs::path file=fs::path(argv[1])/"shroudtopia.json";
 Mod mod;
 auto run=[&](json original, std::string origin, json manifest) {
  kcompat::write_file_atomic(file,original.dump());
  config::init(file,file.parent_path()/"absent.json");
  config::apply_manifest_defaults("library-id",manifest);
  auto m=std::make_unique<Loaded>(); mod=Mod{};
  m->mod=&mod; m->name="Reported Name.With Dot"; m->id="library-id"; m->origin=origin; m->client=true;
  REGISTRATION
  g_mods.clear(); g_mods.push_back(std::move(m)); update_mods();
  std::string text; kcompat::read_file(file,&text);
  return std::pair{g_mods[0]->active,json::parse(text)};
 };
 json base={{"active",true},{"unknown",{{"keep",json::array({1,"text",false})}}},{"mods",{{"Other",{{"active",false},{"custom",42}}}}}};
 auto [active,doc]=run(base,"profile",nullptr);
 check(active,"DLL-only profile mod activates first start");
 check(doc["mods"]["Reported Name.With Dot"]==json({{"active",true}}),"persists exact reported name, not id/stem/dotted path");
 doc["mods"].erase("Reported Name.With Dot"); check(doc==base,"all other content survives");
 auto off=base; off["mods"]["Reported Name.With Dot"]={{"active",false},{"custom",9}};
 auto [disabled,unchanged]=run(off,"profile",nullptr);
 check(!disabled && unchanged==off,"existing false entry untouched");
 auto [dropin,unlisted]=run(base,"folder",nullptr);
 check(!dropin && unlisted==base,"not profile-enabled: no entry or activation");
 auto [eml,emlDoc]=run(base,"profile-eml",nullptr);
 check(eml && emlDoc["mods"]["Reported Name.With Dot"]==json({{"active",true}}),"a profile's EML entry activates first start too");
 auto [folder,folderDoc]=run(base,"profile",json::object());
 check(folder && folderDoc["mods"].contains("Reported Name.With Dot"),"profile folder without defaults activates");
 auto existing=base; existing["mods"]["Reported Name.With Dot"]={{"custom",7}};
 auto [missingFlag,kept]=run(existing,"profile",nullptr);
 check(!missingFlag && kept==existing,"existing entry without active untouched");
 auto defaults=json{{"shroudtopia",{{"default",{{"active",false},{"option",4}}}}}};
 auto compatible=base; compatible["mods"]["Reported Name.With Dot"]={{"active",true}};
 auto [oldActive,oldDoc]=run(compatible,"profile",defaults);
 check(oldActive && oldDoc["mods"]["library-id"]==defaults["shroudtopia"]["default"],"existing activation and folder defaults preserved");
 // Live file reload still deactivates the mod.
 oldDoc["mods"]["Reported Name.With Dot"]["active"]=false;
 kcompat::write_file_atomic(file,oldDoc.dump()); config::reload_if_changed(); update_mods();
 check(!g_mods[0]->active,"live reload deactivates");
 // Creation must not overwrite external edits or invalid settings observed after init.
 auto m=std::make_unique<Loaded>(); m->name="New Name"; m->origin="profile";
 auto ensure=[&] { REGISTRATION }; // same hook as registration
 config::init(file,file.parent_path()/"absent.json");
 auto fresh=oldDoc; fresh["external"]="new edit";
 kcompat::write_file_atomic(file,fresh.dump()); ensure();
 std::string text; kcompat::read_file(file,&text); auto merged=json::parse(text);
 merged["mods"].erase("New Name"); check(merged==fresh,"merges latest disk values");
 std::string first; kcompat::read_file(file,&first); ensure();
 kcompat::read_file(file,&text); check(text==first,"repeat registration makes no write");
 m->name="Another Name";
 for (const std::string broken : {"{invalid", "{\"mods\":false}"}) {
  kcompat::write_file_atomic(file,broken); ensure();
  kcompat::read_file(file,&text); check(text==broken,"invalid settings not overwritten");
 }
 kcompat::write_file_atomic(file,fresh.dump()); kcompat::fail_write=true; ensure(); kcompat::fail_write=false;
 kcompat::read_file(file,&text);
 check(json::parse(text)==fresh && config::mod_get("Another Name","active").is_null(),"failed save does not activate in memory");
 // A mod that never writes its own `active` field (x-01 task 9b: "Activating:" every half second, "inactive" for good).
 // Shroudtopia's loader calls Activate on every update for such a mod; so does Kiln, but it says so once and does not
 // call the mod switched off.
 {
  auto lines=[&](const char* part) { int n=0; for (auto& l : g_lines) if (l.find(part)!=std::string::npos) ++n; return n; };
  json on=base; on["mods"]["Flagless"]={{"active",true}};
  kcompat::write_file_atomic(file,on.dump()); config::init(file,file.parent_path()/"absent.json");
  auto q=std::make_unique<Loaded>(); mod=Mod{}; mod.keeps_flag=false;
  q->mod=&mod; q->name="Flagless"; q->id="flagless"; q->origin="profile"; q->client=true; q->target="0.2.1";
  g_mods.clear(); g_mods.push_back(std::move(q)); g_lines.clear();
  for (int i=0;i<3;++i) update_mods();
  Loaded& f=*g_mods[0];
  check(mod.activations==3 && mod.updates==3,"a mod that leaves its flag off is activated and updated on every cycle, as upstream");
  check(lines("Activating: Flagless")==1,"'Activating' is logged once, not once per cycle");
  check(lines("W Flagless: switched on, but the mod does not report itself active")==1,"one warning says why");
  check(f.status=="activating","switched on and not reporting active reads 'activating', not 'inactive'");
  check(f.reason.find("does not report itself active")!=std::string::npos && f.reason.find("targets Shroudtopia 0.2.1")!=std::string::npos,
        "the reason names the flag and the unknown target version");
  check(known_target("0.1.1") && known_target("0.0.3") && known_target("1.0") && known_target("") && !known_target("0.2.1") && !known_target("2.0"),
        "known Shroudtopia interface versions");
  mod.keeps_flag=true; update_mods();
  check(f.status=="active" && f.reason.empty() && mod.activations==4 && lines("Activating: Flagless")==1,"once the mod reports active: 'active', the reason is gone");
  update_mods(); check(mod.activations==4,"an active mod is not activated again");
  // Switched off while it never reported active: no Deactivate (as upstream), plain 'inactive'.
  mod=Mod{}; mod.keeps_flag=false; f.active=false; f.activate_repeats=false; f.reason.clear(); g_lines.clear();
  update_mods(); check(f.status=="activating","again not reporting");
  on["mods"]["Flagless"]["active"]=false; kcompat::write_file_atomic(file,on.dump()); config::reload_if_changed(); update_mods();
  check(f.status=="inactive" && f.reason.empty() && mod.deactivations==0,"switched off: 'inactive', no reason, no Deactivate for a mod that never reported active");
  // A mod that sets the flag in Activate and leaves it on after Deactivate: Deactivate every cycle, said once.
  on["mods"]["Flagless"]["active"]=true; kcompat::write_file_atomic(file,on.dump()); config::reload_if_changed();
  mod=Mod{}; update_mods(); check(f.status=="active","a flag-keeping mod is active");
  mod.keeps_flag=false; g_lines.clear();
  on["mods"]["Flagless"]["active"]=false; kcompat::write_file_atomic(file,on.dump()); config::reload_if_changed();
  for (int i=0;i<3;++i) update_mods();
  check(mod.deactivations==3 && lines("Deactivating: Flagless")==1 && lines("W Flagless: switched off, but the mod leaves its own 'active' flag on")==1,
        "a mod that leaves its flag on is deactivated on every cycle, said once");
  check(f.status=="active","and it still reads as the mod reports itself");
 }


 // The production per-mod rule query/patch must precede the first Load, and only run once.
 {
  auto run_flow=[&](const char* id,bool service,bool import){
   patched.clear();compat_logs.clear();have_rules=service;have_memcmp=import;
   Mod m;auto q=std::make_unique<Loaded>();q->mod=&m;q->id=id;q->name=id;q->client=true;
   q->module=reinterpret_cast<void*>(0x1234);
   if(std::string(id)=="item-skill-text" && service && import)m.module=q->module;
   auto settings=base;settings["mods"][id]={{"active",true}};
   kcompat::write_file_atomic(file,settings.dump());config::init(file,file.parent_path()/"absent.json");
   g_mods.clear();g_mods.push_back(std::move(q));update_mods();update_mods();
   check(g_mods[0]->status=="active","per-mod flow still loads and activates");
  };
  run_flow("item-skill-text",true,true);
  check(patched.size()==1 && compat_logs.size()==1,"selected mod is patched and logged once before Load");
  check(!compat_logs.empty() && compat_logs[0].find("test-rule")!=std::string::npos,"patch log names the matching rule");
  run_flow("other",true,true);check(patched.empty() && compat_logs.empty(),"unnamed mod has no IAT change or compat log");
  run_flow("item-skill-text",false,true);check(patched.empty() && compat_logs.empty(),"older core without private service is unchanged");
  run_flow("item-skill-text",true,false);check(patched.empty() && compat_logs.empty(),"no memcmp import leaves the DLL alone");
  have_rules=have_memcmp=true;
 }

 // Completion must follow every first Load/Activate, including a failing mod, and the configured delay.
 {
  json settings={{"active",true},{"bootDelay",100},{"updateDelay",10},{"mods",{{"One",{{"active",true}}},{"Fault",{{"active",true}}},{"Two",{{"active",true}}}}}};
  kcompat::write_file_atomic(file,settings.dump());config::init(file,file.parent_path()/"absent.json");
  Mod one, fault, two;fault.fault_activate=true;
  g_mods.clear();
  for(auto item: {std::pair{"One",&one},std::pair{"Fault",&fault},std::pair{"Two",&two}}){
   auto m=std::make_unique<Loaded>();m->name=item.first;m->mod=item.second;m->client=true;g_mods.push_back(std::move(m));
  }
  g_first_pass={};clock_ms=0;waits=scans=0;external=false;g_handoff=false;
  g_first_pass.validate=[&]{
   check(clock_ms>=100,"first pass includes bootDelay");
   check(one.loaded && fault.loaded && two.loaded && one.activations==1 && fault.activations==1 && two.activations==1,
         "completion follows all first Load/Activate attempts, including failure");
   check(g_mods[1]->status=="faulted" && g_mods[2]->status=="active","fault does not prevent later activation or completion");
  };
  loop();check(g_first_pass.completed,"first pass signals completion");
  g_first_pass={};clock_ms=0;waits=scans=0;external=true;
  loop();check(!g_first_pass.completed && scans==0,"external loader during delay does not claim a completed pass");
  external=false;g_first_pass={};clock_ms=0;waits=scans=0;
  settings["active"]=false;kcompat::write_file_atomic(file,settings.dump());config::init(file,file.parent_path()/"absent.json");
  loop();check(g_first_pass.completed && scans==0,"globally disabled host completes a no-op first pass");
 }
 std::cout<<checks-failures<<"/"<<checks<<" checks passed\n"; return failures?1:0;
}
'''
out = Path(os.environ['KILN_OUT']) / 'shroudtopia-host'
out.mkdir(parents=True, exist_ok=True)
# kcompat::ExportCache (compat/export_cache.h): the loader thread's look every updateDelay reads a candidate DLL's export
# table once per version of the file, not on every look (inbox 50)
cache_test = root / 'native/plugins/compat/tests/export_cache_test.cpp'
subprocess.run(['c++', '-std=c++20', '-O1', '-g0', '-pthread', str(cache_test), '-o', str(out / 'export_cache_test')], check=True)
subprocess.run([str(out / 'export_cache_test'), str(out / 'export-cache')], check=True)
source = out / 'test.cpp'
source.write_text(preamble + config + loaded + main.replace('REGISTRATION',registration))
subprocess.run(['c++','-std=c++20','-O0','-g0','-pthread','-I'+str(root/'native/third_party'),str(source),'-o',str(out/'test')],check=True)
subprocess.run([str(out/'test'),str(out/'settings')],check=True)
