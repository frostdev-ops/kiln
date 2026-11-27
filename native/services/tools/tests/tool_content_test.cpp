// SPDX-License-Identifier: MIT
// kiln#130: everything ToolCompiler hands kiln.content for a registered building tool, through kiln.content's own
// resolvers, against the game client's real reflection and the real data of the brush base, a game item naming the
// TerrainMaterials pool, and the item registry. No hand-written replacement of the compiler's output and no game run:
//  - item references (refs): refs.cpp kitbash(), the path a definition's "refs" take, with kiln.assets serving the
//    terrain model kiln.worldtables serves under each registered brush's GUID;
//  - item rules: a content schema field, else the gamedata item path (gd::resolve + leaf + parse_leaf, as gd_item_path);
//  - itemdata rules: gd::resolve + leaf + parse_leaf, the itemdata writer's own steps.
// Negative controls prove the check refuses what the game refused in kiln#130: a radius rule named "0x5258131e", a root
// GUID as an item tag, and a terrain model nobody serves. Inputs: test-tool-content.sh.
#include <windows.h>
#include "../../content/refs.cpp"
#include "reflection_fixture.h"
#include <fstream>
#include <iostream>
#include <map>
#include <set>
#include <stdexcept>

namespace probe {
using json=nlohmann::json;
std::set<std::string> sequences,served_models;
std::vector<uint8_t> registry;
const uint8_t registry_key[16]={0x72,0xab,0x1c,0xb6,1};
uint32_t asked_type=0;
std::string pool_status=R"({"state":"merged","customPools":[],"shapeBindings":[]})";
KilnWorldTablesApi tables{};
KilnAssetsApi assets{};
KilnApi api{};
}
namespace kc {
const KilnApi* g_api=nullptr;Engine E;
void logf(int,const char*,...){}
bool rd(uintptr_t p,void* out,size_t n){if(!p)return false;std::memcpy(out,reinterpret_cast<const void*>(p),n);return true;}
std::string read_text(uintptr_t p,uint64_t n){return p?std::string(reinterpret_cast<const char*>(p),static_cast<size_t>(n)):std::string();}
std::string read_blob_string(uintptr_t){return {};}
std::string data_dir(){return {};}
bool path_exists(const std::string&){return false;}
std::string mods_dir(){return {};}
uint32_t fnv1a(const void* p,size_t n){uint32_t h=2166136261u;for(size_t i=0;i<n;++i)h=(h^static_cast<const uint8_t*>(p)[i])*16777619u;return h;}
std::string lower(std::string s){for(char& c:s)if(c>='A'&&c<='Z')c=static_cast<char>(c+'a'-'A');return s;}
uintptr_t type_by_hash(uint32_t h){for(auto& [_,x]:fixture::types)if(x.walk.hash==h)return reinterpret_cast<uintptr_t>(x.raw.data());return 0;}
bool guid_parse(const std::string& s,uint8_t out[16]){
    unsigned a,b,c,d[8];if(s.size()!=36||std::sscanf(s.c_str(),"%8x-%4x-%4x-%2x%2x-%2x%2x%2x%2x%2x%2x",&a,&b,&c,&d[0],&d[1],&d[2],&d[3],&d[4],&d[5],&d[6],&d[7])!=11)return false;
    fixture::put(out,0,uint32_t(a));fixture::put(out,4,uint16_t(b));fixture::put(out,6,uint16_t(c));for(int i=0;i<8;++i)out[8+i]=static_cast<uint8_t>(d[i]);return true;
}
std::string guid_text(const uint8_t g[16]){char t[40];std::snprintf(t,sizeof t,"%08x-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x",
    uint32_t(g[0]|g[1]<<8|g[2]<<16|uint32_t(g[3])<<24),unsigned(g[4]|g[5]<<8),unsigned(g[6]|g[7]<<8),g[8],g[9],g[10],g[11],g[12],g[13],g[14],g[15]);return t;}
const KilnAssetsApi* assets_api(){return &probe::assets;}
void* near_alloc(uintptr_t,size_t){return nullptr;}  // the model bundle's list copies: no bundle key is emitted
const Entry* Registry::lookup(const std::string&)const{return nullptr;}
Registry& reg(uint32_t){static Registry r;return r;}
namespace gd {
const TType* TType::inner()const{return reinterpret_cast<const TType*>(inner_rec);}
const TType* TType::resolved()const{auto* t=this;for(int i=0;i<16&&t&&t->kind==K_TYPEDEF;++i)t=t->inner();return t;}
const TType* TField::type()const{return reinterpret_cast<const TType*>(type_rec);}
const TField* TType::field(const std::string& n)const{for(const auto& f:fields)if(f.name==n)return &f;return nullptr;}
const TType* type_named(const std::string& n){auto i=fixture::types.find(n);return i==fixture::types.end()?nullptr:&i->second.walk;}
const TType* type_hash(uint32_t h){for(const auto& [_,x]:fixture::types)if(x.walk.hash==h)return &x.walk;return nullptr;}
const GEntry* GRegistry::get(uint32_t)const{return nullptr;}
const GEntry* GRegistry::lookup(const std::string&)const{return nullptr;}
GRegistry& greg(uint32_t){static GRegistry r;return r;}
bool buff_guid(uint32_t,uint8_t[16]){return false;}
bool res_list(uint32_t h,std::vector<std::array<uint8_t,16>>* out){
    out->clear();
    if(h==fnv1a(std::string("keen::ItemRegistryResource"))){std::array<uint8_t,16> k{};std::memcpy(k.data(),probe::registry_key,16);out->push_back(k);}
    return true;
}
uintptr_t res_data(const uint8_t g[16],uint32_t h,uint64_t* size){
    if(h==fnv1a(std::string("keen::ItemRegistryResource"))&&!std::memcmp(g,probe::registry_key,16)){
        if(size)*size=probe::registry.size();return reinterpret_cast<uintptr_t>(probe::registry.data());}
    if(h==fnv1a(std::string("keen::actor::ActorSequenceResource"))&&probe::sequences.count(guid_text(g)))return 1;  // the clones this compile defines
    return 0;
}
}
}

namespace {
using namespace kc;using namespace kc::gd;using Doc=nlohmann::json;
int checks=0;
void check(bool ok,const std::string& why){if(!ok)throw std::runtime_error(why);++checks;}
std::vector<uint8_t> read_file(const char* path){std::ifstream f(path,std::ios::binary);return {std::istreambuf_iterator<char>(f),{}};}
Schema schema;
const TType* item_type=nullptr;
std::vector<uint8_t> brush,pool_item,shape_medium,shape_big;
std::vector<uintptr_t> world_items;
// "item:<name>" in refs: the vanilla Overgrowth shapes a brush takes its cursor preview from (kiln#153).
std::map<std::string,const std::vector<uint8_t>*> named_items;
uintptr_t find_item(const std::string& name){auto i=named_items.find(name);return i==named_items.end()?0:reinterpret_cast<uintptr_t>(i->second->data());}

// kitbash() as newcontent.cpp calls it for a definition's "refs": every key resolves and writes the GUID it names.
std::string refs_problem(const std::string& key,const Doc& value,const uint8_t* self,uint8_t written[16]){
    t_kitbash_item=self;
    KitbashOut out;kitbash(schema,key,nlohmann::ordered_json::parse(value.dump()),&find_item,world_items,&out,reinterpret_cast<uintptr_t>(self));
    t_kitbash_item=nullptr;
    if(!out.problems.empty())return out.problems[0];
    const Field* f=schema.find(key);
    for(const auto& w:out.writes)if(w.f==f){std::memcpy(written,w.bytes,16);return {};}
    return key+": no write";
}
// One leaf write as the gamedata paths make it: resolve over the reflected item, then the leaf's own encoder.
std::string leaf_problem(std::vector<uint8_t>& bytes,const std::string& path,const Doc& value,bool* missing=nullptr){
    std::vector<Resolved> rs;std::string why;bool miss=false;
    if(!resolve(Root::Plain,{item_type,reinterpret_cast<uintptr_t>(bytes.data())},path,&rs,&why,&miss)){if(missing)*missing=miss;return why.empty()?path+": does not resolve":why;}
    if(missing)*missing=false;
    if(rs.empty())return path+": names nothing";
    for(const auto& r:rs) {
        if(!r.node.t)return path+": names a config the base lacks (the content layer would add one)";
        Field f;if(!leaf_of(r.node.t,&f))return path+": not a leaf";
        auto* at=reinterpret_cast<uint8_t*>(r.node.addr+f.offset);uint8_t out[16]{};bool reset=false;
        if(!parse_leaf(f,nlohmann::ordered_json::parse(value.dump()),at,out,&reset,&why)||reset)return why;
        std::memcpy(at,out,f.bytes);
    }
    return {};
}
// An item rule as packs.cpp applies one: a content schema field, else the gamedata item path; neither is "unknown field".
std::string rule_problem(std::vector<uint8_t>& bytes,const std::string& path,const Doc& value){
    if(schema.find(path))return {};
    bool missing=false;const auto why=leaf_problem(bytes,path,value,&missing);
    return missing?"unknown field "+path:why;
}
double float_at(const std::vector<uint8_t>& bytes,const std::string& path){
    std::vector<Resolved> rs;std::string why;
    if(!resolve(Root::Plain,{item_type,reinterpret_cast<uintptr_t>(bytes.data())},path,&rs,&why)||rs.size()!=1||!rs[0].node.t)return -1;
    Field f;if(!leaf_of(rs[0].node.t,&f))return -1;
    return as_double(decode(f,reinterpret_cast<const uint8_t*>(rs[0].node.addr+f.offset)));
}
}

int main(int argc,char** argv){
    try{
        if(argc!=8)throw std::runtime_error("arguments: compiler.json types.json brush.bin pool-item.bin registry.bin shape-medium.bin shape-big.bin");
        Doc compiler,types;std::ifstream(argv[1])>>compiler;std::ifstream(argv[2])>>types;fixture::load_types(types);
        brush=read_file(argv[3]);pool_item=read_file(argv[4]);probe::registry=read_file(argv[5]);shape_medium=read_file(argv[6]);shape_big=read_file(argv[7]);
        named_items={{"Building_Overgrowth_Shape_Small",&brush},{"Building_Overgrowth_Shape_Medium",&shape_medium},{"Building_Overgrowth_Shape_Big",&shape_big}};
        for(const auto& s:compiler["sequences"])probe::sequences.insert(s.get<std::string>());
        for(const auto& s:compiler["servedModels"])probe::served_models.insert(s.get<std::string>());
        item_type=type_named("keen::ItemInfo");
        if(!item_type||brush.size()<item_type->size||pool_item.size()<item_type->size||shape_medium.size()<item_type->size||shape_big.size()<item_type->size)
            throw std::runtime_error("real ItemInfo inputs missing");
        std::string why;if(!build_schema("keen::ItemInfo",{},&schema,&why))throw std::runtime_error("real schema: "+why);
        world_items={reinterpret_cast<uintptr_t>(brush.data()),reinterpret_cast<uintptr_t>(pool_item.data())};
        // kiln.worldtables (no mod pool) and kiln.assets (the terrain models it serves for registered brushes)
        probe::tables.header={sizeof probe::tables,KILN_WORLDTABLES_VERSION};
        probe::tables.status_json=[](char* out,size_t capacity,size_t* count)->KilnResult{
            *count=probe::pool_status.size();if(!out||capacity<=*count)return KILN_ERR_BUFFER_TOO_SMALL;std::memcpy(out,probe::pool_status.c_str(),*count+1);return KILN_OK;};
        probe::assets.header.size=sizeof probe::assets;
        probe::assets.resource_read=[](const KilnAssetKey* k,void*,size_t,size_t* n)->KilnResult{
            probe::asked_type=k->type;*n=64;
            return k->type==0xe53afbafu&&probe::served_models.count(guid_text(k->guid))?KILN_ERR_BUFFER_TOO_SMALL:KILN_ERR_NOT_FOUND;};
        probe::api.service=[](KilnStr name,uint32_t)->const void*{return std::string(name.ptr,name.len)==KILN_WORLDTABLES_SERVICE?&probe::tables:nullptr;};
        g_api=&probe::api;

        // 1. Item definitions: every field and reference names something the content layer resolves.
        size_t refs=0,brushes=0;std::set<std::string> previews;
        for(const auto& item:compiler["items"]) {
            const std::string name=item["name"];
            for(const auto& [path,value]:item["fields"].items())check(schema.find(path)!=nullptr,name+": field "+path+" is no ItemInfo field");
            for(const auto& [path,value]:item["refs"].items()) {
                uint8_t written[16]{},want[16]{};const auto problem=refs_problem(path,value,brush.data(),written);
                check(problem.empty(),name+": refs "+path+" refused: "+problem);
                const std::string spec=value.get<std::string>();
                if(spec.rfind("item:",0)==0) { // copied from that item's own field
                    const Field* f=schema.find(path);const uintptr_t from=find_item(spec.substr(5));
                    check(f&&f->bytes==16&&from,name+": refs "+path+": "+spec+" names no input item");
                    std::memcpy(want,reinterpret_cast<const uint8_t*>(from)+f->offset,16);
                    check(!std::memcmp(written,want,16),name+": refs "+path+" writes another GUID than "+spec+"'s");
                    if(path=="equipment.cursorVfx")previews.insert(guid_text(written));
                }
                else check(guid_parse(spec,want)&&!std::memcmp(written,want,16),name+": refs "+path+" writes another GUID");
                ++refs;
            }
            if(item["refs"].contains("equipment.terrainVoxelObject"))++brushes;
        }
        check(brushes==compiler["servedModels"].size()&&brushes>0,"every brush names its served terrain model");
        // the fixture's brushes are 0.5, 1.5 and 2.5 m: Small's, Medium's and Big's cursor effects, three distinct ones
        check(previews.size()==3&&!previews.count("00000000-0000-0000-0000-000000000000"),"each brush previews as the vanilla shape of its size");
        // 2. Item rules on a copy of the brush base each (the radius config).
        size_t rules=0;
        for(const auto& [game,set]:compiler["itemRules"].items()) {
            auto bytes=brush;
            for(const auto& [path,value]:set.items()) {
                const auto problem=rule_problem(bytes,path,value);
                check(problem.empty(),game+": item rule "+path+" refused: "+problem);
                if(path.rfind("impactValues.",0)==0)check(std::abs(float_at(bytes,path)-value.get<double>())<1e-6,game+": "+path+" does not read back as "+value.dump());
                ++rules;
            }
        }
        // 3. Itemdata rules (the two sequence slots and their ids).
        size_t itemdata=0;
        for(const auto& [game,set]:compiler["itemdata"].items()) {
            auto bytes=brush;
            for(const auto& [path,value]:set.items()){const auto problem=leaf_problem(bytes,path,value);check(problem.empty(),game+": itemdata "+path+" refused: "+problem);++itemdata;}
        }
        check(rules==brushes&&itemdata==4*brushes,"one radius rule and four sequence rules per brush");

        // Negative controls: the check refuses what the game refused in kiln#130.
        {auto bytes=brush;const auto p=rule_problem(bytes,"0x5258131e",0.5);check(p.rfind("unknown field",0)==0,"a radius rule on a bare config id is an unknown field: "+p);}
        {auto bytes=brush;const auto root=compiler["items"][0]["guid"];
         const auto p=leaf_problem(bytes,"tags[0].tag",root);check(p.find("is not an item tag the game knows")!=std::string::npos,"a root GUID as an item tag is refused: "+p);
         check(leaf_problem(bytes,"tags[0].tag","08aea6cf-e0e8-4d6c-8a29-a21f4693e006").empty(),"a tag the item registry lists is accepted");}
        {uint8_t w[16];const auto p=refs_problem("equipment.terrainVoxelObject","9a9a9a9a-0000-4000-8000-000000000001",brush.data(),w);
         check(!p.empty()&&probe::asked_type==0xe53afbafu,"a terrain model nobody serves is refused, asked as keen::VoxelModelResource: "+p);}
        {uint8_t w[16];const auto p=refs_problem("equipment.voxelBlueprintMaterialPool","256c43ba-dd20-4369-b3bf-f49c671a59ec",brush.data(),w);
         check(p.empty(),"the vanilla TerrainMaterials pool is a loaded reference: "+p);}
        std::cout<<"tool content: "<<refs<<" references, "<<rules<<" item rules, "<<itemdata<<" itemdata rules accepted by kiln.content's resolvers; "
                 <<"4 kiln#130 refusals reproduced as negative controls; "<<checks<<" checks\n";
        return 0;
    }catch(const std::exception& e){std::cerr<<"tool content: FAIL: "<<e.what()<<'\n';return 1;}
}
