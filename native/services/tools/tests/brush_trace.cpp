// SPDX-License-Identifier: MIT
// The brush-frame adapter on the host: every exit it can take is named and counted, an admitted frame receives the
// family's own rows (the query scans only the game data's terraformingTools list, which never holds runtime items),
// and the trace reports a change of state once, however many frames repeat it. No game, no hooks.
#include "../brush_trace.h"
#include <cstdlib>
#include <iostream>
#include <vector>
using namespace kiln::tools;
static unsigned checks;
#define CHECK(x) do {++checks;if(!(x)){std::cerr<<__LINE__<<": " #x "\n";std::exit(1);}}while(false)
namespace {
constexpr uint32_t root=0x1234abcd;
constexpr std::array<uint8_t,16> family_tag{1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16};
struct Frame {
    alignas(16) std::array<uint8_t,0x2b28> bytes{};
    uint32_t info{root};
    BrushFrame f{};
    Frame() {
        f.stack=reinterpret_cast<uintptr_t>(bytes.data());f.frame=f.stack+0x100;f.output=f.frame+0x690;
        f.exclude=f.stack+0x40;f.ratio=f.frame+0x100;f.include=f.stack+0x30;
        const uintptr_t ptr=reinterpret_cast<uintptr_t>(&info),data=f.frame+0x6b8;const uint64_t cap=16;
        std::memcpy(at(f.frame+0x118),&root,4);std::memcpy(at(f.frame+0xe0),&ptr,sizeof ptr);
        std::memcpy(at(f.output),&data,sizeof data);std::memcpy(at(f.output+16),&cap,8);
        std::memcpy(at(f.include),overgrowth_brush_tag.data(),16);std::memcpy(at(f.ratio),overgrowth_brush_tag.data(),16);
    }
    static void* at(uintptr_t p) {return reinterpret_cast<void*>(p);}
    uint64_t count() const {uint64_t c;std::memcpy(&c,reinterpret_cast<const void*>(f.output+8),8);return c;}
    BrushRow row(size_t i) const {BrushRow r;std::memcpy(&r,reinterpret_cast<const void*>(f.frame+0x6b8+i*sizeof r),sizeof r);return r;}
    bool tags(const std::array<uint8_t,16>& t) const {return !std::memcmp(reinterpret_cast<const void*>(f.include),t.data(),16)&&!std::memcmp(reinterpret_cast<const void*>(f.ratio),t.data(),16);}
};
const std::vector<uint32_t> three{0xb00b0001,0xb00b0002,0xb00b0003};
BrushFrameOutcome run(Frame& frame,const std::vector<uint32_t>& brushes=three,uint32_t id=root) {
    return patch_brush_frame(frame.f,id,family_tag,{brushes.data(),brushes.size()});
}
void exits() {
    {Frame a;const auto before=a.bytes;const auto o=run(a);
     CHECK(o.result==BrushFrameResult::Patched&&o.exit==BrushFrameExit::Patched&&o.rows==3&&!o.truncated);
     CHECK(a.count()==3&&a.tags(family_tag));
     for(size_t i=0;i<3;++i){const auto r=a.row(i);CHECK(r.lead==0&&r.item==three[i]&&r.nominator==i+1&&r.denominator==1);
         for(auto z:r.zero)CHECK(z==0);}
     // Nothing outside the two tags, the three rows and the count changed.
     size_t changed=0;for(size_t i=0;i<a.bytes.size();++i)if(a.bytes[i]!=before[i])++changed;CHECK(changed<=16+16+3*72+8);}
    {Frame a;std::vector<uint32_t> many;for(uint32_t i=1;i<=20;++i)many.push_back(0xc0000000+i);const auto o=run(a,many);
     CHECK(o.exit==BrushFrameExit::Patched&&o.rows==16&&o.truncated&&a.count()==16&&a.row(15).item==0xc0000010&&a.row(15).nominator==16);}
    auto same=[](Frame& a,BrushFrameExit want,const std::vector<uint32_t>& b=three,uint32_t id=root){
        const auto before=a.bytes;const auto o=run(a,b,id);
        CHECK(o.result==BrushFrameResult::Unchanged&&o.exit==want&&o.rows==0&&a.bytes==before);};
    {Frame a;same(a,BrushFrameExit::NoFamily,{});}
    {Frame a;same(a,BrushFrameExit::NoFamily,three,0);}
    {Frame a;a.f.include=a.f.exclude;same(a,BrushFrameExit::FrameMismatch);}
    {Frame a;std::swap(a.f.include,a.f.exclude);same(a,BrushFrameExit::FrameMismatch);} // the Decay branch's registers
    {Frame a;const uint32_t other=7;std::memcpy(Frame::at(a.f.frame+0x118),&other,4);same(a,BrushFrameExit::NotSelected);}
    {Frame a;const uintptr_t none=0;std::memcpy(Frame::at(a.f.frame+0xe0),&none,sizeof none);same(a,BrushFrameExit::NotSelected);}
    {Frame a;a.info=99;same(a,BrushFrameExit::NotResolved);}
    // kiln#153: after a pick the slot holds the picked brush (its ItemInfo resolved): still the family's frame
    for(const auto brush:three) {Frame a;std::memcpy(Frame::at(a.f.frame+0x118),&brush,4);a.info=brush;const auto o=run(a);
        CHECK(o.exit==BrushFrameExit::Patched&&a.count()==3&&a.tags(family_tag)&&a.row(0).item==three[0]);}
    {Frame a;std::memcpy(Frame::at(a.f.frame+0x118),&three[1],4);same(a,BrushFrameExit::NotResolved);} // ItemInfo still the root's
    {Frame a;const uint32_t none=0;std::memcpy(Frame::at(a.f.frame+0x118),&none,4);a.info=0;same(a,BrushFrameExit::NotSelected);}
    {Frame a;std::vector<uint32_t> too_many(65,0xd0000000);for(uint32_t i=0;i<65;++i)too_many[i]+=i;same(a,BrushFrameExit::NoFamily,too_many);}
    {Frame a;const uint64_t one=1;std::memcpy(Frame::at(a.f.output+8),&one,8);same(a,BrushFrameExit::OutputNotFresh);}
    {Frame a;const uint64_t eight=8;std::memcpy(Frame::at(a.f.output+16),&eight,8);same(a,BrushFrameExit::OutputNotFresh);}
    {Frame a;const uintptr_t heap=0x1000;std::memcpy(Frame::at(a.f.output),&heap,sizeof heap);same(a,BrushFrameExit::OutputNotFresh);}
    {Frame a;*static_cast<uint8_t*>(Frame::at(a.f.include))^=1;same(a,BrushFrameExit::TagMismatch);}
    {Frame a;*static_cast<uint8_t*>(Frame::at(a.f.ratio+15))^=1;same(a,BrushFrameExit::TagMismatch);}
}
void trace() {
    BrushTrace t;BrushFrameExit state{};uint64_t covered=0;
    CHECK(!t.any()&&!t.take(&state,&covered)&&t.summary()=="no picker frames yet");
    for(int i=0;i<500;++i)t.record(BrushFrameExit::NoFamily);
    CHECK(t.count(BrushFrameExit::NoFamily)==500&&t.changes()==1);
    CHECK(t.take(&state,&covered)&&state==BrushFrameExit::NoFamily&&covered==1);
    CHECK(!t.take(&state,&covered)); // 500 frames of one state: one publication
    t.record({BrushFrameResult::Patched,BrushFrameExit::Patched,3,false});
    CHECK(t.take(&state,&covered)&&state==BrushFrameExit::Patched&&covered==1);
    // Faster than the publisher: three changes become one line that says so, naming the state now.
    t.record(BrushFrameExit::NoFamily);t.record({BrushFrameResult::Patched,BrushFrameExit::Patched,3,false});t.record(BrushFrameExit::NotSelected);
    CHECK(t.take(&state,&covered)&&state==BrushFrameExit::NotSelected&&covered==3);
    t.record({BrushFrameResult::Patched,BrushFrameExit::Patched,16,true});
    const auto s=t.summary();
    CHECK(s.find("no held family 501")!=std::string::npos&&s.find("patched 3 (16 rows)")!=std::string::npos&&
          s.find("not selected 1")!=std::string::npos&&s.find("truncated to 16 rows 1")!=std::string::npos&&s.find("tag mismatch")==std::string::npos);
    CHECK(s.find("patched")>s.find("no held family")); // exit order, the order the checks run
    for(size_t i=0;i<size_t(BrushFrameExit::Count);++i){
        CHECK(std::string(brush_exit_name(BrushFrameExit(i)))!="unknown");CHECK(std::string(brush_exit_meaning(BrushFrameExit(i)))!="unknown");}
    t.reset();CHECK(!t.any()&&t.count(BrushFrameExit::Patched)==0&&!t.take(&state,&covered));
}
}
int main() {
    exits();trace();
    std::cout<<"brush trace: "<<checks<<" checks passed\n";
}
