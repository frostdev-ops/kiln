// SPDX-License-Identifier: MIT
// See merge.h. The port of tools/animcook/anicook/playergraph.py: the functions below carry the Python's names and
// follow its statements in order, including the order in which it raises, so that the same input gives the same bytes,
// the same report and the same reason. What a new state N whose neighbour (`like`) is L in machine M gets, in order:
//  1. clip: a copy of L's clip node with the clip's GUID, hashes, duration, root motion, sequences; gender
//     alternatives cleared; loop and speed from the definition when given.
//  2. pose: a PoseResult over it (a copy of L's LayerBlend over a cached pose when L has one, with the clip as layer).
//  3. state: a copy of L's state node. 4. entry: one global transition of M, a copy of L's. 5. local transitions.
//  6. N's id appended to the lists on the state input that hold all of L's entry ids. 7. selects on the state input.
//  8. past 34 global transitions: nested machines. 9. placement before M. 10. node ids. 11. order. 12. nothing to do.
#include "merge.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <chrono>
#include <cstring>
#include <iterator>
#include <map>
#include <memory>
#include <unordered_map>
#include <unordered_set>

namespace kpa {

namespace {

using Id = uint64_t;
using ojson = nlohmann::ordered_json;

[[noreturn]] void off(std::string text) { throw PyError("MergeOff", std::move(text)); }

std::string full_type(std::string_view kind) { return std::string(KPA_NODE) + std::string(kind) + "NodeDefinition"; }

// ---- tables (playergraph.py, in its order) ----

const char* const kBlockHint =
    "the legs while blocking, chosen by movement (a float input), never by a state id; a block pose of your own is \"like\": \"Block_FullBody\", which keeps the legs "
    "and plays your clip as the guard";
const char* hint_of(std::string_view state) { return state == "Idle_Block" || state == "Move_Block" ? kBlockHint : nullptr; }

// Rule 1: the inputs a walk from a state's pose follows, in field order. Any other node kind ends the walk.
const std::vector<const char*>* pose_inputs(std::string_view kind) {
    static const std::vector<const char*> pose = {"input"}, layer = {"base", "layer"}, by_bool = {"inputA", "inputB"}, by_id = {"inputPoses[]"};
    if (kind == "PoseResult") return &pose;
    if (kind == "LayerBlend") return &layer;
    if (kind == "SelectPoseByBool") return &by_bool;
    if (kind == "SelectPoseByParameterId") return &by_id;
    return nullptr;
}

// The fields of each node kind that hold a node id.
const std::vector<const char*>* refs_of(std::string_view kind) {
    static const std::map<std::string_view, std::vector<const char*>> refs = {
        {"AnimationStateProgression", {"nodeStateHash"}},
        {"BipedFeetIKSolver", {"inputPose", "disableIK", "allowAdvancedFootPosition"}},
        {"BlendSpace_1D", {"items[].inputId", "parameterPinInputHash", "speedController.rawValueController.inputHash"}},
        {"BoolAnd", {"lhs", "rhs"}},
        {"BoolNegate", {"input"}},
        {"BoolOr", {"lhs", "rhs"}},
        {"BoolOrMultiple", {"entries[]"}},
        {"ConstraintOrientation", {"inputPose", "constraintAxisWS"}},
        {"Container", {"poseResult"}},
        {"ContainerInput", {"inputPose"}},
        {"FloatEqualsConstant", {"input"}},
        {"FloatGreaterThanConstant", {"input"}},
        {"FloatLessThanConstant", {"input"}},
        {"FloatMultiply", {"lhs", "rhs"}},
        {"LayerBlend", {"base", "layer"}},
        {"OffsetBone", {"inputPose", "offset"}},
        {"PoseResult", {"input"}},
        {"RelevantAnimTimeRemainingRatio", {"nodeStateHash"}},
        {"RetargetSolver", {"inputPose", "applyLeftHandIk", "applyRightHandIk"}},
        {"SampleAnimationClip", {"progressionPin", "speedController.rawValueController.inputHash"}},
        {"SaveCachedFloatValue", {"inputValueId"}},
        {"SaveCachedPose", {"inputPose"}},
        {"SelectPoseByBool", {"inputA", "inputB", "selectA"}},
        {"SelectPoseByParameterId", {"inputPoses[]"}},
        {"StateMachine", {"states[]", "transitions[]", "entryState", "parentStateMachineStateId", "globalTransitions[].toState", "globalTransitions[].conditionNode"}},
        {"StateMachineState", {"poseResult", "parentStateMachineId"}},
        {"StateMachineTransition", {"fromState", "toState", "conditionNode"}},
        {"UseCachedFloatValue", {"cachedFloatValueId"}},
        {"UseCachedPose", {"cachedPose"}},
    };
    const auto it = refs.find(kind);
    return it == refs.end() ? nullptr : &it->second;
}
bool parent_ref(std::string_view kind, std::string_view path) {
    return (kind == "StateMachineState" && path == "parentStateMachineId") || (kind == "StateMachine" && path == "parentStateMachineStateId");
}
const char* const kHeaderRefs[] = {"rootNode", "postProcessRootNode", "bipedFeetIkNodeId", "genericFeetIkNodeId"};
const char* const kGenderGuids[] = {"animationGenderAlternative", "rootMotionAnimationGenderAlternative", "additiveReferenceAnimationGenderAlternative"};
const char* const kGenderHashes[] = {"animationGenderAlternativeHash", "rootMotionAnimationGenderAlternativeHash", "additiveReferenceAnimationGenderAlternativeHash"};
const char* const kGtFields[] = {"toState", "duration", "rootMotionMode", "eventMode", "conditionNode", "blendCurve", "blendProfile"};
const char* const kGtFromLocal[] = {"duration", "rootMotionMode", "eventMode", "blendCurve", "blendProfile"};

// Every field the merge reads or writes, by type, in USES' order.
const std::vector<std::pair<std::string, std::vector<const char*>>>& uses() {
    static const std::vector<std::pair<std::string, std::vector<const char*>>> u = {
        {kGraphType, {"usedInputIds", "nodeDefinitions", "syncNodes", "rootNode", "postProcessRootNode", "bipedFeetIkNodeId", "genericFeetIkNodeId"}},
        {KPA_NODE "ParameterList", {"parameterIds"}},
        {KPA_NODE "SyncNodesItem_Runtime", {"node1", "node2"}},
        {KPA_NODE "SampleAnimationClipNodeDefinition",
         {"id", "type", "loop", "animationDuration", "animation", "animationHash", "rootMotionAnimation", "rootMotionAnimationHash", "additiveType",
          "additiveReferenceAnimation", "additiveReferenceAnimationHash", "hasSequences", "speedController", "animationGenderAlternative",
          "rootMotionAnimationGenderAlternative", "additiveReferenceAnimationGenderAlternative", "animationGenderAlternativeHash",
          "rootMotionAnimationGenderAlternativeHash", "additiveReferenceAnimationGenderAlternativeHash"}},
        {"keen::anim_graph::AnimationSpeedController", {"rawValueController"}},
        {"keen::anim_graph::AnimationSpeedControllerRawValue", {"speedMultiplier"}},
        {KPA_NODE "PoseResultNodeDefinition", {"id", "type", "input"}},
        {KPA_NODE "LayerBlendNodeDefinition", {"id", "type", "base", "layer", "blendOptions"}},
        {KPA_NODE "UseCachedPoseNodeDefinition", {"id", "type"}},
        {KPA_NODE "StateMachineStateNodeDefinition", {"id", "type", "debugName", "poseResult", "parentStateMachineId", "retriggerBlendDuration", "restartOnRetrigger"}},
        {KPA_NODE "StateMachineNodeDefinition",
         {"id", "type", "states", "transitions", "transitionRefInfo", "entryState", "parentStateMachineStateId", "globalTransitions", "numGlobalTransitions",
          "globalTransitionNodeId", "debugName"}},
        {KPA_NODE "TransitionRefInfo", {"fromState", "toState", "duration"}},
        {kGtType, {"toState", "duration", "rootMotionMode", "eventMode", "conditionNode", "blendCurve", "blendProfile"}},
        {KPA_NODE "StateMachineTransitionNodeDefinition",
         {"id", "type", "fromState", "toState", "conditionNode", "duration", "rootMotionMode", "eventMode", "blendCurve", "blendProfile"}},
        {KPA_NODE "IdParameterEqualsConstantNodeDefinition", {"id", "type", "parameterHash", "parameterId", "value", "ignoreForAnimationSequences"}},
        {KPA_NODE "IdParameterEqualsConstantListNodeDefinition", {"id", "type", "parameterHash", "parameterId", "values", "ignoreForAnimationSequences"}},
        {KPA_NODE "SelectPoseByParameterIdNodeDefinition", {"id", "type", "parameterHash", "inputPoses", "values"}},
        {KPA_NODE "IsOverlayActiveNodeDefinition", {"id", "type", "overlay"}},
        {KPA_NODE "IsOverlayActiveListNodeDefinition", {"id", "type", "overlays"}},
    };
    return u;
}

// ---- Python's view of the tree ----

// str(v) of a scalar, as an f-string writes it.
std::string py_str(const Value& v) {
    switch (v.k) {
        case VK::None: return "None";
        case VK::Bool: return v.u ? "True" : "False";
        case VK::Int: return v.sign ? std::to_string(static_cast<int64_t>(v.u)) : std::to_string(v.u);
        case VK::Float: return py_float_repr(v.f);
        case VK::Enum: return v.name >= 0 ? std::string(v.enum_name()) : (v.sign ? std::to_string(static_cast<int64_t>(v.u)) : std::to_string(v.u));
        case VK::Str: return v.s;
        case VK::Guid: return v.g.text();
        default: return "<value>";
    }
}
// v == "text" for a str of the Python's tree (a named enum, a string, a GUID).
bool text_is(const Value& v, std::string_view s) {
    if (v.k == VK::Enum) return v.name >= 0 && v.enum_name() == s;
    if (v.k == VK::Str) return v.s == s;
    if (v.k == VK::Guid) return v.g.text() == s;
    return false;
}
bool type_is(const Value& node, std::string_view kind) { return text_is(node.at("type"), kind); }
std::string type_text(const Value& node) { return py_str(node.at("type")); }
Id id_of(const Value& node) { return node.at("id").id(); }
uint64_t int_of(const Value& v) {
    if (v.k == VK::Int || v.k == VK::Bool || (v.k == VK::Enum && v.name < 0)) return v.u;
    throw PyError("TypeError", "an integer was expected");
}
int64_t sint_of(const Value& v) {
    const uint64_t u = int_of(v);
    return v.sign ? static_cast<int64_t>(u) : static_cast<int64_t>(std::min<uint64_t>(u, INT64_MAX));
}
// n["value"] of a wrapped node (a variant): the node itself.
Value& wrapped_value(Value& w) {
    if (w.k == VK::Variant && !w.items.empty()) return w.items[0];
    if (w.k == VK::Unknown) throw PyError("KeyError", "'value'");
    if (w.k == VK::None) throw PyError("TypeError", "'NoneType' object is not subscriptable");
    return w.at("value");
}
const Value& wrapped_value(const Value& w) { return wrapped_value(const_cast<Value&>(w)); }
std::string wrapped_type(const Value& w) {  // n["$type"]
    if (w.k == VK::Variant) return w.s;
    if (w.k == VK::Unknown) return hex8(w.u);
    if (w.k == VK::None) throw PyError("TypeError", "'NoneType' object is not subscriptable");
    throw PyError("KeyError", "'$type'");
}
Value& list_of(Value& v) {
    if (v.k != VK::List) throw PyError("TypeError", "a list was expected");
    return v;
}
const Value& list_of(const Value& v) { return list_of(const_cast<Value&>(v)); }
Value wrap(std::string_view kind, Value v) {
    Value w;
    w.k = VK::Variant;
    w.s = full_type(kind);
    w.items.push_back(std::move(v));
    return w;
}

// The {"value": id} leaves at `path` ("a.b", "a[].b", "a[]"), as _get_path finds them.
std::vector<Id> get_path(const Value& value, std::string_view path) {
    std::vector<const Value*> cur = {&value};
    size_t from = 0;
    while (from <= path.size()) {
        size_t dot = path.find('.', from);
        if (dot == std::string_view::npos) dot = path.size();
        std::string_view seg = path.substr(from, dot - from);
        const bool arr = seg.size() >= 2 && seg.substr(seg.size() - 2) == "[]";
        if (arr) seg.remove_suffix(2);
        std::vector<const Value*> nxt;
        for (const Value* c : cur) {
            const Value* x = c->k == VK::Struct ? c->get(seg) : nullptr;  // c.get(name) of a dict
            if (!x || x->k == VK::None) continue;
            if (!arr) {
                nxt.push_back(x);
            } else if (x->k == VK::List) {
                for (const Value& e : x->items) nxt.push_back(&e);
            } else if (x->k == VK::Int || x->k == VK::Bool || x->k == VK::Float || (x->k == VK::Enum && x->name < 0)) {
                throw PyError("TypeError", "'int' object is not iterable");
            }  // a dict or a str iterates to strings, which the next step drops
        }
        cur = std::move(nxt);
        from = dot + 1;
    }
    std::vector<Id> out;
    for (const Value* c : cur)
        if (c->k == VK::Struct)
            if (const Value* v = c->get("value")) out.push_back(int_of(*v));
    return out;
}

// sorted(...) of Python str and its repr as a list: "['a', 'b']".
std::string repr_list(std::vector<std::string> v) {
    std::sort(v.begin(), v.end());
    std::string out = "[";
    for (size_t i = 0; i < v.size(); ++i) out += (i ? ", " : "") + py_repr(std::string_view(v[i]));
    return out + "]";
}

// ---- the graph, indexed ----

struct Graph {
    Value* value = nullptr;
    std::vector<Value>* wrapped = nullptr;
    std::vector<Value*> nodes;
    std::unordered_map<Id, size_t> by_id;  // also pos
    std::vector<std::string_view> kinds;  // nodes[i]'s type name ("" when the Python has a number there)
    std::vector<Value*> machines, transitions;
    std::unordered_map<std::string, const Value*> first;  // node kind -> the first node of that kind (wrapped)
    std::unordered_set<Id> ids, compared;

    explicit Graph(Value& v) : value(&v) {
        wrapped = &list_of(v.at("nodeDefinitions")).items;
        nodes.reserve(wrapped->size());
        for (Value& w : *wrapped) nodes.push_back(&wrapped_value(w));
        for (size_t i = 0; i < nodes.size(); ++i) {
            const Id nid = id_of(*nodes[i]);
            if (!by_id.emplace(nid, i).second) off("the game's graph has node id " + hex8(nid) + " twice");
        }
        kinds.reserve(nodes.size());
        for (Value* n : nodes) {  // v["type"] as the Python's tree holds it: a name, or no name (a number)
            const Value& t = n->at("type");
            kinds.push_back(t.k == VK::Enum && t.name >= 0 ? t.enum_name() : t.k == VK::Str ? std::string_view(t.s) : std::string_view());
        }
        for (Value* n : nodes)
            if (type_is(*n, "StateMachine")) machines.push_back(n);
        for (const Value& w : *wrapped) {
            const Value& t = wrapped_value(w).at("type");
            if (t.k == VK::Enum && t.name >= 0) first.emplace(std::string(t.enum_name()), &w);
            else if (t.k == VK::Str) first.emplace(t.s, &w);
        }
        for (const auto& [nid, i] : by_id) ids.insert(nid);
        for (Value* m : machines) ids.insert(m->at("globalTransitionNodeId").id());
        for (Value* n : nodes)
            if (type_is(*n, "StateMachineTransition")) transitions.push_back(n);
        for (const Value& x : list_of(v.at("usedInputIds").at("parameterIds")).items) compared.insert(x.id());
        for (Value* n : nodes) {
            const Value& t = n->at("type");
            if (text_is(t, "IdParameterEqualsConstant")) {
                compared.insert(n->at("value").id());
            } else if (text_is(t, "IdParameterEqualsConstantList") || text_is(t, "SelectPoseByParameterId")) {
                for (const Value& x : list_of(n->at("values")).items) compared.insert(x.id());
            } else if (text_is(t, "IsOverlayActive")) {
                compared.insert(n->at("overlay").id());
            } else if (text_is(t, "IsOverlayActiveList")) {
                for (const Value& x : list_of(n->at("overlays")).items) compared.insert(x.id());
            }
        }
    }

    // The ids a list node holds, as a set: built once per node (rule 6 asks it for every neighbour; the lists change only
    // after every neighbour is read).
    const std::unordered_set<Id>& list_ids(const Value& v, const char* field) const {
        auto it = list_ids_.find(&v);
        if (it == list_ids_.end()) {
            std::unordered_set<Id> have;
            for (const Value& x : list_of(v.at(field)).items) have.insert(x.id());
            it = list_ids_.emplace(&v, std::move(have)).first;
        }
        return it->second;
    }
    mutable std::unordered_map<const Value*, std::unordered_set<Id>> list_ids_;

    Value* node(Id nid) const {
        const auto it = by_id.find(nid);
        return it == by_id.end() ? nullptr : nodes[it->second];
    }
    Value* machine_of(const Value& state) const { return node(state.at("parentStateMachineId").id()); }

    std::vector<std::pair<Value*, Value*>> states_named(std::string_view name, const std::string* machine) const {
        std::vector<std::pair<Value*, Value*>> out;
        for (size_t i = 0; i < nodes.size(); ++i) {
            Value* v = nodes[i];
            if (kinds[i] != "StateMachineState" || !text_is(v->at("debugName"), name)) continue;
            Value* m = machine_of(*v);
            if (!m || !type_is(*m, "StateMachine")) continue;
            if (!machine || text_is(m->at("debugName"), *machine)) out.emplace_back(v, m);
        }
        return out;
    }

    std::vector<Value*> machine_transitions(const Value& m) const {
        std::vector<Value*> out;
        for (const Value& t : list_of(m.at("transitions")).items) {
            const Id x = t.id();
            Value* n = node(x);
            if (!n) throw PyError("KeyError", std::to_string(x));
            out.push_back(n);
        }
        return out;
    }

    // Node ids reached from `pose_id` through POSE_INPUTS, depth first in field order (no recursion: a graph made to be
    // deep cannot overflow the stack). With stop_at_clip: the first clip's id.
    std::optional<Id> walk(Id pose_id, bool stop_at_clip, std::vector<Id>* order) const {
        std::unordered_set<Id> seen;
        struct Frame {
            const Value* v;
            const std::vector<const char*>* paths;
            size_t path;
            std::vector<Id> kids;
            size_t kid;
        };
        std::vector<Frame> stack;
        auto go = [&](Id nid) -> std::optional<Id> {
            if (seen.count(nid) || !by_id.count(nid)) return std::nullopt;
            seen.insert(nid);
            if (order) order->push_back(nid);
            const Value* v = node(nid);
            if (stop_at_clip && type_is(*v, "SampleAnimationClip")) return nid;
            const Value& t = v->at("type");
            const std::vector<const char*>* paths = t.k == VK::Enum && t.name >= 0 ? pose_inputs(t.enum_name()) : t.k == VK::Str ? pose_inputs(t.s) : nullptr;
            stack.push_back({v, paths, 0, {}, 0});
            return std::nullopt;
        };
        if (auto hit = go(pose_id)) return hit;
        while (!stack.empty()) {
            Frame& f = stack.back();
            if (f.kid < f.kids.size()) {
                const Id r = f.kids[f.kid++];
                if (auto hit = go(r)) return hit;  // may push: `f` is not used after this
                continue;
            }
            if (!f.paths || f.path >= f.paths->size()) {
                stack.pop_back();
                continue;
            }
            f.kids = get_path(*f.v, (*f.paths)[f.path++]);
            f.kid = 0;
        }
        return std::nullopt;
    }
};

// ---- entry kinds (ENTRIES): a table of two rows ----

// Kind "state": a state is entered when the state input holds its id. A second kind (an overlay machine: IsOverlayActive,
// IsOverlayActiveList) is a second row with the same members.
class Entry {
public:
    virtual ~Entry() = default;
    const char* kind = "";
    const char* single = "";
    const char* multi = "";
    const char* values = "";
    bool selects = false;   // rule 7
    bool used_ids = false;  // the id goes into usedInputIds
    bool nests = false;     // rule 8: past 34, nested machines
    // Why a state of a supported machine cannot be a neighbour of this kind, before rules 1 to 7 look at it; nullopt: it
    // can. `gts`: the indexes of M's global transitions into the state.
    virtual std::optional<std::string> refusal(const Graph& g, const Value& state, const Value& machine, const std::vector<size_t>& gts) const = 0;
    // The ids a condition node compares on this kind's input, or nullopt when it is no such compare.
    virtual std::optional<std::vector<Id>> ids(const Value* node) const = 0;
    virtual bool is_list(const Value& node) const = 0;
    // A condition on `value`, a copy of `tmpl` (the graph's first node of the single kind, wrapped).
    virtual Value make(const Value& tmpl, Id nid, Id value) const = 0;
    virtual Value make_list(const Value& tmpl, Id nid, const std::vector<Id>& values) const = 0;
};

class StateEntry final : public Entry {
public:
    StateEntry() {
        kind = "state";
        single = "IdParameterEqualsConstant";
        multi = "IdParameterEqualsConstantList";
        values = "values";
        selects = true;
        used_ids = true;
        nests = true;
    }
    std::optional<std::string> refusal(const Graph&, const Value&, const Value&, const std::vector<size_t>&) const override { return std::nullopt; }
    std::optional<std::vector<Id>> ids(const Value* node) const override {
        if (!node) return std::nullopt;
        const Value* ph = node->k == VK::Struct ? node->get("parameterHash") : nullptr;  // node.get("parameterHash", {}).get("value")
        const Value* phv = ph && ph->k == VK::Struct ? ph->get("value") : nullptr;
        if (!phv || !equal(*phv, Value::integer(kStateInput))) return std::nullopt;
        if (type_is(*node, single)) return std::vector<Id>{node->at("value").id()};
        if (type_is(*node, multi)) {
            std::vector<Id> out;
            for (const Value& x : list_of(node->at("values")).items) out.push_back(x.id());
            return out;
        }
        return std::nullopt;
    }
    bool is_list(const Value& node) const override { return type_is(node, multi) && node.at("parameterHash").id() == kStateInput; }
    Value make(const Value& tmpl, Id nid, Id value) const override {
        Value w = tmpl;
        Value& c = wrapped_value(w);
        c.set("id", Value::hk(nid));
        c.set("parameterHash", Value::hk(kStateInput));
        c.set("parameterId", Value::hk(kStateInput));
        c.set("value", Value::hk(value));
        c.set("ignoreForAnimationSequences", Value::boolean(false));  // a sequence has to be able to enter the state
        return w;
    }
    Value make_list(const Value& tmpl, Id nid, const std::vector<Id>& ids_) const override {
        Value w = tmpl;
        Value& c = wrapped_value(w);
        c.set("id", Value::hk(nid));
        c.set("parameterHash", Value::hk(kStateInput));
        c.set("parameterId", Value::hk(kStateInput));
        Value l = Value::list();
        for (Id v : ids_) l.items.push_back(Value::hk(v));
        c.set("values", std::move(l));
        c.set("ignoreForAnimationSequences", Value::boolean(false));
        return w;
    }
};

// Kind "overlay": a state of an overlay machine is entered when an overlay is active (a sequence's
// SetAnimationOverlayEvent.overlayName, a GUID whose FNV-1a-32 is the overlay id). Conditions: IsOverlayActive and
// IsOverlayActiveList. No select picks a pose by an overlay, overlay ids are not in usedInputIds, and an overlay machine
// does not nest.
class OverlayEntry final : public Entry {
public:
    OverlayEntry() {
        kind = "overlay";
        single = "IsOverlayActive";
        multi = "IsOverlayActiveList";
        values = "overlays";
    }
    std::optional<std::vector<Id>> ids(const Value* node) const override {
        if (!node) return std::nullopt;
        if (type_is(*node, single)) return std::vector<Id>{node->at("overlay").id()};
        if (type_is(*node, multi)) {
            std::vector<Id> out;
            for (const Value& x : list_of(node->at("overlays")).items) out.push_back(x.id());
            return out;
        }
        return std::nullopt;
    }
    bool is_list(const Value& node) const override { return type_is(node, multi); }
    std::optional<std::string> refusal(const Graph& g, const Value& state, const Value& machine, const std::vector<size_t>& gts) const override;
    Value make(const Value& tmpl, Id nid, Id value) const override {
        Value w = tmpl;
        Value& c = wrapped_value(w);
        c.set("id", Value::hk(nid));
        c.set("overlay", Value::hk(value));
        return w;
    }
    Value make_list(const Value& tmpl, Id nid, const std::vector<Id>& ids_) const override {
        Value w = tmpl;
        Value& c = wrapped_value(w);
        c.set("id", Value::hk(nid));
        Value l = Value::list();
        for (Id v : ids_) l.items.push_back(Value::hk(v));
        c.set("overlays", std::move(l));
        return w;
    }
};

const Entry& entry_of(const std::string& kind) {
    static const StateEntry state;
    static const OverlayEntry overlay;
    if (kind == state.kind) return state;
    if (kind == overlay.kind) return overlay;
    throw PyError("KeyError", py_repr(std::string_view(kind)));
}
const Entry* const kEntries[] = {&entry_of("state"), &entry_of("overlay")};

std::optional<std::string> OverlayEntry::refusal(const Graph& g, const Value& state, const Value& machine, const std::vector<size_t>& gts) const {
    const Value* pose = g.node(state.at("poseResult").id());
    const Value* top = pose && type_is(*pose, "PoseResult") ? g.node(pose->at("input").id()) : nullptr;
    if (top && type_is(*top, "StateMachine"))
        return "it holds the nested machine " + py_str(top->at("debugName")) + ": a state can be like one of that machine's states";
    if (gts.empty()) {
        if (machine.at("entryState").id() == id_of(state))
            return "it is the rest state of " + py_str(machine.at("debugName")) + ": no overlay enters it, the overlay states go back to it when they end";
        return std::string("it has no overlay condition of its own: it is entered only from other states, by local transitions");
    }
    const Value& all = list_of(machine.at("globalTransitions"));
    for (size_t i : gts) {  // (g.by_id.get(e["conditionNode"]["value"]) or {}).get("type") == single
        const Value* cn = g.node(all.items[i].at("conditionNode").id());
        const Value* t = cn ? cn->get("type") : nullptr;
        if (t && text_is(*t, single)) return std::nullopt;
    }
    return std::string("it is entered on a list of overlays (IsOverlayActiveList), not on an overlay of its own");
}

// ---- a neighbour: what rules 1 to 7 take from L in M (read from the game's graph as decoded) ----

struct Neighbour {
    Value* state = nullptr;
    Value* machine = nullptr;
    std::string kind;
    std::optional<std::string> error;
    Value* pose = nullptr;
    Value* clip = nullptr;
    Value* layer = nullptr;
    std::optional<size_t> entry;  // index of L's first global transition in M's globalTransitions
    Value* entry_local = nullptr;
    std::vector<Id> entry_ids;
    std::vector<Value*> outs, ins, lists;
    std::vector<std::pair<Value*, Id>> selects;

    Neighbour(const Graph& g, Value* st, Value* m, const std::string& kind_) : state(st), machine(m), kind(kind_) {
        const Entry& cond = entry_of(kind);
        const Id sid = id_of(*state);
        const Value& all_gts = list_of(machine->at("globalTransitions"));
        const int64_t num = sint_of(machine->at("numGlobalTransitions"));
        const size_t lim = static_cast<size_t>(std::clamp<int64_t>(num, 0, static_cast<int64_t>(all_gts.items.size())));
        std::vector<size_t> gts;
        for (size_t i = 0; i < lim; ++i)
            if (all_gts.items[i].at("toState").id() == sid) gts.push_back(i);
        error = cond.refusal(g, *state, *machine, gts);
        if (error) return;
        // rules 1, 2
        Value* p = g.node(state->at("poseResult").id());
        if (!p || !type_is(*p, "PoseResult")) {
            error = "its pose is not a PoseResult";
            return;
        }
        pose = p;
        Value* top = g.node(pose->at("input").id());
        const std::optional<Id> c = g.walk(id_of(*pose), true, nullptr);
        if (!c) {
            error = "no SampleAnimationClip under its pose (its clip is not played by a clip node)";
            return;
        }
        clip = g.node(*c);
        if (top && type_is(*top, "LayerBlend")) {
            Value* base = g.node(top->at("base").id());
            if (!base || !type_is(*base, "UseCachedPose")) {
                error = "its pose is a LayerBlend over a " + (base ? type_text(*base) : std::string("missing node")) + ", not over a cached pose";
                return;
            }
            layer = top;
        }
        // rule 4
        const std::vector<Value*> trans = g.machine_transitions(*machine);
        std::vector<Id> ids;
        if (!gts.empty()) {
            entry = gts[0];
            for (size_t i : gts)
                if (auto x = cond.ids(g.node(all_gts.items[i].at("conditionNode").id()))) ids.insert(ids.end(), x->begin(), x->end());
        } else {
            std::vector<Value*> into;
            for (Value* t : trans) {
                if (t->at("toState").id() != sid) continue;
                const auto x = cond.ids(g.node(t->at("conditionNode").id()));
                if (x && !x->empty()) into.push_back(t);
            }
            if (into.empty()) {
                error = "it has no global transition and no local transition into it on an id";
                return;
            }
            entry_local = into[0];
            for (Value* t : into) {
                const Id cn = t->at("conditionNode").id();
                Value* cv = g.node(cn);
                if (!cv) throw PyError("KeyError", std::to_string(cn));
                const auto x = cond.ids(cv);
                ids.insert(ids.end(), x->begin(), x->end());
            }
        }
        for (Id i : ids)  // dict.fromkeys: first occurrence order
            if (std::find(entry_ids.begin(), entry_ids.end(), i) == entry_ids.end()) entry_ids.push_back(i);
        if (entry_ids.empty()) {
            error = "its entry condition does not compare the " + kind + " input";
            return;
        }
        const std::unordered_set<Id> entry_set(entry_ids.begin(), entry_ids.end());
        std::unordered_set<Id> own;
        for (size_t i : gts) own.insert(all_gts.items[i].at("conditionNode").id());
        for (Value* t : g.transitions)
            if (t->at("toState").id() == sid) own.insert(t->at("conditionNode").id());
        // rule 5
        for (Value* t : trans)
            if (t->at("fromState").id() == sid) outs.push_back(t);
        for (Value* t : trans) {
            if (t->at("toState").id() != sid) continue;
            const auto x = cond.ids(g.node(t->at("conditionNode").id()));
            if (x && !x->empty() && std::all_of(x->begin(), x->end(), [&](Id i) { return entry_set.count(i) > 0; })) ins.push_back(t);
        }
        // rule 6
        for (size_t i = 0; i < g.nodes.size(); ++i) {
            Value* v = g.nodes[i];
            if (g.kinds[i] != cond.multi || !cond.is_list(*v) || own.count(id_of(*v))) continue;
            const std::unordered_set<Id>& have = g.list_ids(*v, cond.values);
            if (std::all_of(entry_set.begin(), entry_set.end(), [&](Id i) { return have.count(i) > 0; })) lists.push_back(v);
        }
        // rule 7
        if (cond.selects) {
            std::vector<Id> mine_order;
            g.walk(id_of(*pose), false, &mine_order);
            const std::unordered_set<Id> mine(mine_order.begin(), mine_order.end());
            for (size_t i = 0; i < g.nodes.size(); ++i) {
                Value* v = g.nodes[i];
                if (g.kinds[i] != "SelectPoseByParameterId" || v->at("parameterHash").id() != kStateInput || mine.count(id_of(*v))) continue;
                const Value& vals = list_of(v->at("values"));
                std::optional<size_t> at;
                for (size_t i = 0; i < vals.items.size(); ++i)
                    if (entry_set.count(vals.items[i].id())) {
                        at = i;
                        break;
                    }
                if (!at) continue;
                const Value& poses = list_of(v->at("inputPoses"));
                if (*at >= poses.items.size()) throw PyError("IndexError", "list index out of range");
                selects.emplace_back(v, poses.items[*at].id());
            }
        }
    }

    // The role names of a state beside this neighbour, as if it is not nested.
    std::vector<std::string> roles() const {
        std::vector<std::string> r = {"clip"};
        if (layer) r.push_back("layer");
        r.insert(r.end(), {"pose", "state", "cond"});
        for (size_t i = 0; i < outs.size(); ++i) r.push_back("out." + std::to_string(i));
        for (size_t i = 0; i < ins.size(); ++i) r.push_back("in." + std::to_string(i) + ".cond"), r.push_back("in." + std::to_string(i));
        return r;
    }

    // Rule 4's transition to copy, as a global transition entry (toState and conditionNode still L's, or 0).
    Value entry_fields() const {
        if (entry) return list_of(machine->at("globalTransitions")).items[*entry];
        Value e = Value::strukt();
        e.set("toState", Value::hk(0));
        e.set("conditionNode", Value::hk(0));
        for (const char* f : kGtFromLocal) e.set(f, entry_local->at(f));
        Value out = Value::strukt();
        for (const char* f : kGtFields) out.set(f, *e.get(f));
        return out;
    }
};

// -> (state, machine), or a reason.
std::optional<std::pair<Value*, Value*>> resolve_like(const Graph& g, const std::string& like, std::string* why) {
    std::vector<std::pair<Value*, Value*>> found;
    std::string sname;
    const size_t slash = like.find('/');
    if (slash != std::string::npos) {
        const std::string mname = like.substr(0, slash);
        sname = like.substr(slash + 1);
        found = g.states_named(sname, &mname);
    } else {
        sname = like;
        found = g.states_named(sname, nullptr);
    }
    if (found.empty()) {
        const char* hint = hint_of(sname);
        *why = "like " + py_repr(std::string_view(like)) + ": no state of the player's graph has that name" + (hint ? " (" + std::string(hint) + ")" : std::string());
        return std::nullopt;
    }
    if (found.size() > 1) {
        std::vector<std::string> names;
        for (const auto& [s, m] : found) names.push_back(py_str(m->at("debugName")) + "/" + py_str(s->at("debugName")));
        std::sort(names.begin(), names.end());
        std::string joined;
        for (size_t i = 0; i < names.size(); ++i) joined += (i ? ", " : "") + names[i];
        *why = "like " + py_repr(std::string_view(like)) + ": several states have that name: " + joined + "; write Machine/State";
        return std::nullopt;
    }
    return found[0];
}

// -> kind, or a reason.
std::optional<std::string> support(const Value& state, const Value& machine, std::string* why) {
    const std::string mname = py_str(machine.at("debugName")), sname = py_str(state.at("debugName"));
    for (const SupportRow& row : supported())
        if (mname == row.machine) {
            if (row.every_state || std::any_of(row.states.begin(), row.states.end(), [&](const char* s) { return sname == s; })) return std::string(row.kind);
            break;
        }
    if (const char* hint = hint_of(sname)) {
        *why = mname + "/" + sname + " is " + hint;
        return std::nullopt;
    }
    std::string allowed, main;
    for (const SupportRow& row : supported()) {
        if (row.every_state) {
            allowed += (allowed.empty() ? "" : ", ") + std::string(row.machine);
        } else {
            std::string list;
            for (const char* s : row.states) list += (list.empty() ? "" : ", ") + std::string(s);
            main += (main.empty() ? "" : "; ") + std::string("in ") + row.machine + ": " + list;
        }
    }
    *why = mname + "/" + sname + " cannot be a neighbour yet: a state may be like any state of " + allowed + "; " + main;
    return std::nullopt;
}

// ---- checks ----

void check_uses(const TypeDb& types, const std::string& missing_field) {
    for (const auto& [tname, fields] : uses()) {
        const Type* t = types.get(tname);
        if (!t) off("the game's reflection has no " + tname);
        std::unordered_set<std::string_view> have;
        for (const Field* f : types.all_fields(t))
            if (f->name != missing_field) have.insert(f->name);
        std::string missing;
        for (const char* f : fields)
            if (!have.count(f)) missing += (missing.empty() ? "" : ", ") + std::string(f);
        if (!missing.empty()) off("the game's " + tname + " has no field " + missing + " (renamed?): the merge would lose it");
    }
}

// The keys of a Python dict for this value (a variant and the raw kinds are dicts there too), or false: not a dict.
bool py_keys(const Value& v, std::vector<std::string>* out) {
    out->clear();
    switch (v.k) {
        case VK::Struct:
            for (std::string_view n : v.names()) out->emplace_back(n);
            return true;
        case VK::Variant: *out = {"$type", "value"}; return true;
        case VK::Unknown: *out = {"$type", "$size"}; return true;
        case VK::Hex: *out = {"bytes"}; return true;
        case VK::Raw: *out = {"$raw", "$kind"}; return true;
        default: return false;
    }
}

// A value has exactly the fields of its type, all the way down (a renamed field in a copy must not vanish).
void check_shape(const TypeDb& types, const Type* t, const Value& v, const std::string& where) {
    t = types.resolve(t);
    if (!t) return;
    const uint32_t k = t->kind;
    if (k == K_STRUCT) {
        std::vector<std::string> want, got;
        for (const Field* f : types.all_fields(t)) want.push_back(f->name);
        const bool is_dict = py_keys(v, &got);
        const std::set<std::string> w(want.begin(), want.end()), gs(got.begin(), got.end());
        if (!is_dict || w != gs) {
            std::vector<std::string> diff;
            std::set_symmetric_difference(w.begin(), w.end(), gs.begin(), gs.end(), std::back_inserter(diff));
            off(where + ": fields differ from " + t->name + "'s: " + repr_list(diff));
        }
        for (const Field* f : types.all_fields(t)) {
            const Value* m = v.get(f->name);
            if (!m) throw PyError("KeyError", py_repr(std::string_view(f->name)));
            check_shape(types, f->t, *m, where + "." + f->name);
        }
    } else if (k == K_STATIC_ARRAY || k == K_BLOB_ARRAY) {
        if (v.k == VK::Hex) return;
        if (v.k != VK::List || (k == K_STATIC_ARRAY && v.items.size() != t->count)) off(where + ": not an array of " + t->name);
        const Type* el = t->inner_t;
        for (size_t i = 0; i < v.items.size(); ++i) check_shape(types, el, v.items[i], where + "[" + std::to_string(i) + "]");
    } else if (k == K_BLOB_VARIANT) {
        if (v.k == VK::None) return;
        if (v.k == VK::Unknown) throw PyError("KeyError", "'value'");
        if (v.k != VK::Variant) throw PyError("KeyError", "'$type'");
        check_shape(types, types.get(v.s), v.items[0], where);
    }
}

}  // namespace

void validate(const TypeDb& types, const Value& pristine, const Value& merged, const std::set<uint64_t>& touched_machines, const std::set<uint64_t>& touched_lists,
              const std::set<uint64_t>& touched_selects, const std::set<uint64_t>& new_ids) {
    const std::vector<Value>& wrapped = list_of(merged.at("nodeDefinitions")).items;
    std::vector<const Value*> nodes;
    for (const Value& w : wrapped) nodes.push_back(&wrapped_value(w));
    std::unordered_map<Id, size_t> pos;
    for (size_t i = 0; i < nodes.size(); ++i) {
        const Id nid = id_of(*nodes[i]);
        if (!pos.emplace(nid, i).second) off("node id " + hex8(nid) + " twice");
    }
    std::unordered_set<Id> gtn;
    for (const Value* v : nodes)
        if (type_is(*v, "StateMachine")) {
            const Id x = v->at("globalTransitionNodeId").id();
            if (pos.count(x) || gtn.count(x)) off("machine " + py_str(v->at("debugName")) + ": globalTransitionNodeId " + hex8(x) + " is not unique");
            gtn.insert(x);
        }
    for (size_t i = 0; i < nodes.size(); ++i) {
        const Value& v = *nodes[i];
        const Value& t = v.at("type");
        const std::vector<const char*>* paths = t.k == VK::Enum && t.name >= 0 ? refs_of(t.enum_name()) : t.k == VK::Str ? refs_of(t.s) : nullptr;
        if (!paths) continue;
        const std::string kind = py_str(t);
        for (const char* path : *paths)
            for (Id r : get_path(v, path)) {
                if (r == 0) continue;
                const auto it = pos.find(r);
                if (it == pos.end()) off(kind + " " + hex8(id_of(v)) + ": " + path + " = " + hex8(r) + " is no node");
                if (it->second >= i && !parent_ref(kind, path)) off(kind + " " + hex8(id_of(v)) + ": " + path + " points forward");
            }
    }
    for (const char* f : kHeaderRefs) {
        const Id r = merged.at(f).id();
        if (r && !pos.count(r)) off(std::string(f) + " = " + hex8(r) + " is no node");
    }
    for (const Value& s : list_of(merged.at("syncNodes")).items)
        for (const char* f : {"node1", "node2"}) {
            const Id r = s.at(f).id();
            if (r && !pos.count(r)) off(std::string("syncNodes: ") + f + " = " + hex8(r) + " is no node");
        }
    const Type* gtt = types.get(kGtType);
    if (!gtt) throw PyError("TypeError", "'NoneType' object is not subscriptable");
    const Bytes zeros(gtt->size, 0);
    const Value blank = Decoder(types).decode(gtt, zeros);
    for (const Value* v : nodes) {
        if (!type_is(*v, "StateMachine")) continue;
        const int64_t n = sint_of(v->at("numGlobalTransitions"));
        const Value& g = list_of(v->at("globalTransitions"));
        if (n > static_cast<int64_t>(kMaxGlobal) || g.items.size() != kMaxGlobal)
            off("machine " + py_str(v->at("debugName")) + ": " + std::to_string(n) + " global transitions, the game holds " + std::to_string(kMaxGlobal));
        const size_t lim = static_cast<size_t>(std::max<int64_t>(n, 0));
        bool bad = false;
        for (size_t i = 0; i < lim && i < g.items.size() && !bad; ++i) bad = g.items[i].at("toState").id() == 0;
        for (size_t i = lim; i < g.items.size() && !bad; ++i) bad = !equal(g.items[i], blank);
        if (bad) off("machine " + py_str(v->at("debugName")) + ": numGlobalTransitions " + std::to_string(n) + " does not match its entries");
    }
    // the game's nodes: all there, in order, equal but for what took states and ids
    const std::vector<Value>& before = list_of(pristine.at("nodeDefinitions")).items;
    std::vector<const Value*> by_pos;
    for (const Value& w : wrapped)
        if (!new_ids.count(id_of(wrapped_value(w)))) by_pos.push_back(&w);
    if (by_pos.size() != before.size())
        off(std::to_string(before.size()) + " nodes in the game's graph, " + std::to_string(by_pos.size()) + " of them after the merge");
    for (size_t i = 0; i < before.size(); ++i) {
        const Value &a = before[i], &b = *by_pos[i];
        const Value &va = wrapped_value(a), &vb = wrapped_value(b);
        const Id nid = id_of(va);
        if (wrapped_type(a) != wrapped_type(b) || nid != id_of(vb)) off("node " + hex8(nid) + " moved or changed its type");
        if (equal(va, vb)) continue;
        // lists = {e.multi: (e.values,) for e in ENTRIES}: the list kinds that may take ids, and their field
        const Entry* lst = nullptr;
        for (const Entry* e : kEntries)
            if (type_is(va, e->multi)) lst = e;
        const bool machine = type_is(va, "StateMachine"), sel = type_is(va, "SelectPoseByParameterId");
        const bool allowed = (touched_machines.count(nid) && machine) || (touched_lists.count(nid) && lst) || (touched_selects.count(nid) && sel);
        if (!allowed) off("the game's " + type_text(va) + " " + hex8(nid) + " changed");
        std::vector<const char*> grow = machine ? std::vector<const char*>{"states", "transitions"} : lst ? std::vector<const char*>{lst->values} : std::vector<const char*>{"values", "inputPoses"};
        std::vector<const char*> fields = grow;
        if (machine) fields.insert(fields.end(), {"globalTransitions", "numGlobalTransitions"});
        for (size_t f = 0; f < va.names().size(); ++f) {
            const std::string_view name = va.names()[f];
            if (std::any_of(fields.begin(), fields.end(), [&](const char* x) { return name == x; })) continue;
            if (!equal(va.items[f], vb.at(name))) off("the game's " + type_text(va) + " " + hex8(nid) + ": " + std::string(name) + " changed");
        }
        for (const char* f : grow) {
            const Value &la = list_of(va.at(f)), &lb = list_of(vb.at(f));
            bool same = lb.items.size() >= la.items.size();  // vb[f][:len(va[f])] == va[f]
            for (size_t k = 0; same && k < la.items.size(); ++k) same = equal(lb.items[k], la.items[k]);
            if (!same) off("the game's " + type_text(va) + " " + hex8(nid) + ": " + f + " lost or reordered entries");
        }
        if (machine) {
            const int64_t n = sint_of(va.at("numGlobalTransitions"));
            const Value &ga = list_of(va.at("globalTransitions")), &gb = list_of(vb.at("globalTransitions"));
            const size_t la = std::min<size_t>(static_cast<size_t>(std::max<int64_t>(n, 0)), ga.items.size()),
                         lb = std::min<size_t>(static_cast<size_t>(std::max<int64_t>(n, 0)), gb.items.size());
            bool same = la == lb;
            for (size_t k = 0; same && k < la; ++k) same = equal(ga.items[k], gb.items[k]);
            if (!same || sint_of(vb.at("numGlobalTransitions")) < n) off("machine " + py_str(va.at("debugName")) + ": its own global transitions changed");
        }
    }
    for (size_t f = 0; f < pristine.names().size(); ++f) {
        const std::string_view name = pristine.names()[f];
        if (name == "nodeDefinitions" || name == "usedInputIds") continue;
        if (!equal(pristine.items[f], merged.at(name))) off("the graph's " + std::string(name) + " changed");
    }
    const Value &ua = list_of(pristine.at("usedInputIds").at("parameterIds")), &ub = list_of(merged.at("usedInputIds").at("parameterIds"));
    bool same = ub.items.size() >= ua.items.size();
    for (size_t k = 0; same && k < ua.items.size(); ++k) same = equal(ub.items[k], ua.items[k]);
    if (!same) off("usedInputIds lost or reordered entries");
    for (const Value& w : wrapped) {
        const Value& v = wrapped_value(w);
        const Id nid = id_of(v);
        if (new_ids.count(nid) || touched_machines.count(nid) || touched_lists.count(nid) || touched_selects.count(nid))
            check_shape(types, types.get(wrapped_type(w)), v, type_text(v) + " " + hex8(nid));
    }
    check_shape(types, types.get(KPA_NODE "ParameterList"), merged.at("usedInputIds"), "usedInputIds");
}

std::string graph_hash(const Bytes& merged) {
    const std::array<uint8_t, 32> h = sha256(merged.data(), merged.size());
    static const char* d = "0123456789abcdef";
    std::string out;
    for (int i = 0; i < 8; ++i) out.push_back(d[h[i] >> 4]), out.push_back(d[h[i] & 15]);
    return out;
}

uint32_t node_id(const std::vector<std::string>& parts) {
    std::string s = "kiln.player";
    for (const std::string& p : parts) s += "." + p;
    return fnv1a(s);
}

const std::vector<SupportRow>& supported() {
    static const std::vector<SupportRow> rows = {
        {"SM_Attack_Unarmed_FullBody", "state", true, {}},
        {"SM_Attack_Sword_Shield_FullBody", "state", true, {}},
        {"SM_Attack_Hammer_FullBody", "state", true, {}},
        {"SM_Attack_Greatsword_FullBody", "state", true, {}},
        {"SM_Attack_Dual_Daggers_FullBody", "state", true, {}},
        {"SM_Jump_Attack_FullBody", "state", true, {}},
        {"SM_Main", "state", false, {"Block_FullBody", "Parry_FullBody", "DodgeRoll_FullBody", "Dodge_Attack_FullBody", "Sneak_Attack_FullBody", "Attack_Skill_Kick_FullBody"}},
        {"SM_Attack_Staff_FullBody", "state", true, {}},
        {"SM_Attack_Wand_FullBody", "state", true, {}},
        // overlay machines: every state with a global transition on an IsOverlayActive of its own (OverlayEntry::refusal says which are not)
        {"SM_Staff_Overlay", "overlay", true, {}},
        {"SM_Wand_Overlay", "overlay", true, {}},
        {"SM_Ground_Bow_Overlay", "overlay", true, {}},
        {"SM_Air_Bow_Overlay", "overlay", true, {}},
        {"SM_Ranged_Attack_Additive", "overlay", true, {}},
    };
    return rows;
}

ClipFacts joint_animation_facts(const TypeDb& types, const uint8_t* data, size_t len) {
    // _scalars(types, JOINT_ANIM, raw, names): the named fields at their offsets, by their kind's struct format
    static const char* const names[] = {"hierarchyHash", "fps", "frameCount", "jointCount", "flags"};
    const Type* t = types.get(kJointAnim);
    uint64_t got[5] = {};
    bool have[5] = {};
    for (const Field* f : types.all_fields(t)) {
        for (int i = 0; i < 5; ++i) {
            if (f->name != names[i]) continue;
            const Type* ft = types.resolve(f->t);
            if (!ft) throw PyError("TypeError", "'NoneType' object is not subscriptable");
            // fmt = {u8, u16, u32, u64, bool}: any other kind is a KeyError in the Python
            if (ft->kind != K_U8 && ft->kind != K_BOOL && ft->kind != K_U16 && ft->kind != K_U32 && ft->kind != K_U64)
                throw PyError("KeyError", std::to_string(ft->kind));
            const Value v = Decoder(types).decode(ft, data, len, f->offset);
            got[i] = v.u;
            have[i] = true;
        }
    }
    std::string missing;
    for (int i = 0; i < 5; ++i)
        if (!have[i]) missing += (missing.empty() ? "" : ", ") + std::string(names[i]);
    if (!missing.empty()) off(std::string("the game's ") + kJointAnim + " has no field " + missing);
    ClipFacts c;
    c.hierarchy_hash = got[0];
    c.fps = got[1];
    c.frame_count = got[2];
    c.joint_count = got[3];
    c.loop = (got[4] & 1) != 0;
    return c;
}

// ---- the lookup ----

GameStates::GameStates(const TypeDb& types, const Bytes& raw) {
    const Type* gt = types.get(kGraphType);
    if (!gt) off(std::string("the reflection has no ") + kGraphType);
    Value value = Decoder(types, kMaxArray).decode(gt, raw.data(), raw.size(), 0, kGraphType);
    const Graph g(value);
    for (size_t i = 0; i < g.nodes.size(); ++i) {
        const Value& st = *g.nodes[i];
        if (g.kinds[i] != "StateMachineState") continue;
        const Value* m = g.machine_of(st);
        if (!m || !type_is(*m, "StateMachine")) continue;
        GameState out;
        out.machine = py_str(m->at("debugName"));
        out.name = py_str(st.at("debugName"));
        std::vector<std::string> kinds = {"state", "overlay"};
        for (const SupportRow& row : supported())
            if (out.machine == row.machine) kinds = {row.kind};
        const Id sid = id_of(st);
        const Value& all = list_of(m->at("globalTransitions"));
        const size_t lim = static_cast<size_t>(std::clamp<int64_t>(sint_of(m->at("numGlobalTransitions")), 0, static_cast<int64_t>(all.items.size())));
        for (const std::string& kind : kinds) {
            const Entry& cond = entry_of(kind);
            std::vector<Id> ids;
            bool any_gt = false;
            for (size_t k = 0; k < lim; ++k)
                if (all.items[k].at("toState").id() == sid) {
                    any_gt = true;
                    if (auto x = cond.ids(g.node(all.items[k].at("conditionNode").id()))) ids.insert(ids.end(), x->begin(), x->end());
                }
            if (!any_gt)
                for (const Value* t : g.machine_transitions(*m))
                    if (t->at("toState").id() == sid)
                        if (auto x = cond.ids(g.node(t->at("conditionNode").id()))) ids.insert(ids.end(), x->begin(), x->end());
            out.ids.clear();
            for (Id x : ids)
                if (std::find(out.ids.begin(), out.ids.end(), static_cast<uint32_t>(x)) == out.ids.end()) out.ids.push_back(static_cast<uint32_t>(x));
            out.kind = kind;
            if (!out.ids.empty()) break;
        }
        if (out.ids.empty()) out.kind = kinds[0];
        all_.push_back(std::move(out));
    }
}

std::vector<const GameState*> GameStates::find(std::string_view name) const {
    std::string_view machine, state = name;
    const size_t slash = name.find('/');
    if (slash != std::string_view::npos) machine = name.substr(0, slash), state = name.substr(slash + 1);
    std::vector<const GameState*> out;
    for (const GameState& s : all_)
        if (s.name == state && (slash == std::string_view::npos || s.machine == machine)) out.push_back(&s);
    return out;
}

// ---- clip facts (playergraph.ClipFacts) ----

namespace {

// str(uuid.UUID(text)), or false where uuid.UUID raises ValueError: "urn:" and "uuid:" removed, braces stripped at the ends,
// hyphens removed, 32 hex digits.
bool uuid_text(std::string s, std::string* out) {
    for (const char* cut : {"urn:", "uuid:"})
        for (size_t at; (at = s.find(cut)) != std::string::npos;) s.erase(at, std::strlen(cut));
    while (!s.empty() && (s.front() == '{' || s.front() == '}')) s.erase(s.begin());
    while (!s.empty() && (s.back() == '{' || s.back() == '}')) s.pop_back();
    std::string hex;
    for (char c : s)
        if (c != '-') hex.push_back(c);
    if (hex.size() != 32) return false;
    for (char& c : hex) {
        if (!std::isxdigit(static_cast<unsigned char>(c))) return false;
        c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    }
    *out = hex.substr(0, 8) + "-" + hex.substr(8, 4) + "-" + hex.substr(12, 4) + "-" + hex.substr(16, 4) + "-" + hex.substr(20);
    return true;
}

// _type_hash: "0x..." a number, a name of the reflection its hash, else FNV-1a of the name.
uint32_t type_hash_of(const TypeDb& types, const std::string& name) {
    if (name.size() > 2 && name[0] == '0' && name[1] == 'x') return static_cast<uint32_t>(std::stoull(name.substr(2), nullptr, 16));
    const Type* t = types.get(name);
    return t ? t->hash : fnv1a(name);
}

}  // namespace

ClipFactsProvider::ClipFactsProvider(const TypeDb& types, const std::vector<Mod>& mods, std::function<bool(const std::string&, Bytes*)> read_file, Game game)
    : types_(types), read_file_(std::move(read_file)), game_(std::move(game)) {
    ja_ = type_hash_of(types, kJointAnim), rm_ = type_hash_of(types, kRootMotion), sq_ = type_hash_of(types, kSequences);
    for (const Mod& m : mods) {
        if (!m.has_json) continue;
        // json.loads(re.sub(r"^\s*//.*$", "", text, flags=re.M)): whole-line comments, no trailing commas
        Json doc;
        try {
            doc = parse_json(strip_line_comments(m.assets_json), m.assets_dir + "/assets.json", false, "");
        } catch (const DefinitionError& e) {
            warnings.push_back(e.what());
            continue;
        }
        const Json* entries = doc.t == Json::Obj ? doc.find("entries") : nullptr;
        if (!entries || entries->t != Json::Arr) continue;
        for (const Json& e : entries->items) {
            if (e.t != Json::Obj) continue;
            const Json* kind = e.find("kind");
            if (!kind || kind->t != Json::Str || kind->s != "resource") continue;
            const Json* side = e.find("side");
            if (side && side->t == Json::Str && side->s == "client") continue;
            if (const Json* part = e.find("part")) {  // int(e.get("part", 0)) != 0
                const bool zero = (part->t == Json::Int && part->s == "0") || (part->t == Json::Float && part->f > -1.0 && part->f < 1.0) || part->t == Json::False ||
                                  (part->t == Json::Str && part->s == "0");
                if (!zero) continue;
            }
            const Json *guid = e.find("guid"), *type = e.find("type"), *file = e.find("file");
            if (!guid || guid->t != Json::Str || !type || type->t != Json::Str || !file || file->t != Json::Str) continue;
            std::string g;
            if (!uuid_text(guid->s, &g)) continue;
            const uint32_t th = type_hash_of(types, type->s);
            if (th == ja_ || th == rm_ || th == sq_) served_[{g, th}].push_back(m.assets_dir + "/" + file->s);
        }
    }
}

std::optional<ClipFacts> ClipFactsProvider::operator()(const std::string& guid_in) {
    std::string guid = guid_in;
    for (char& c : guid) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    if (const auto it = cache_.find(guid); it != cache_.end()) return it->second;
    Guid g;
    const bool parsed = Guid::parse(guid, &g);
    auto game_has = [&](uint32_t th) { return parsed && game_.has && game_.has(g, th); };
    std::optional<ClipFacts> facts;
    const auto files = served_.find({guid, ja_});
    if (files != served_.end() && !files->second.empty()) {
        std::vector<std::optional<ClipFacts>> heads;
        for (const std::string& f : files->second) {
            Bytes b;
            if (!read_file_(f, &b)) {  // OSError
                heads.push_back(std::nullopt);
                continue;
            }
            try {
                heads.push_back(joint_animation_facts(types_, b.data(), b.size()));
            } catch (const PyError& e) {
                if (e.type != "MergeOff" && e.type != "error") throw;  // the Python catches OSError, struct.error, MergeOff
                heads.push_back(std::nullopt);
            }
        }
        if (!heads.empty() && std::all_of(heads.begin(), heads.end(), [&](const auto& h) { return h && *h == *heads[0]; })) facts = heads[0];
    } else if (game_has(ja_)) {
        Bytes b;
        if (!game_.read || !game_.read(g, ja_, &b)) throw PyError("KeyError", "the game's " + std::string(kJointAnim) + " " + guid + " cannot be read");
        facts = joint_animation_facts(types_, b.data(), b.size());
    }
    if (facts) {
        const auto rm = served_.find({guid, rm_}), sq = served_.find({guid, sq_});
        facts->root_motion = (rm != served_.end() && !rm->second.empty()) || game_has(rm_);
        facts->sequences = (sq != served_.end() && !sq->second.empty()) || game_has(sq_);
    }
    cache_[guid] = facts;
    return facts;
}

// ---- the merge ----

namespace {

struct Work {
    State st;
    size_t from = 0;  // index of its ModStates
    ClipFacts facts;
    std::vector<std::pair<std::string, uint32_t>> rids;  // role -> node id, in roles() order
    uint32_t rid(std::string_view role) const {
        for (const auto& [r, id] : rids)
            if (r == role) return id;
        throw PyError("KeyError", py_repr(role));
    }
};
bool key_less(const Work* a, const Work* b) { return a->st.mod != b->st.mod ? a->st.mod < b->st.mod : a->st.name < b->st.name; }

struct Item {
    Work* w;
    Neighbour* nb;
};
struct Group {
    size_t gi;
    std::vector<Item> items;
    std::array<Id, 5> nids;  // machine, pose, wrapper, list, global
};
struct MachinePlan {
    Value* M;
    Id mid;
    std::vector<Item> items;
    std::optional<std::vector<Group>> groups;
};

ojson new_report() {
    ojson r;
    r["format"] = 1;
    r["graph"] = "untouched";
    r["reason"] = nullptr;
    r["graphHash"] = std::string(16, '0');
    r["states"] = ojson::array();
    r["errors"] = ojson::array();
    r["machines"] = ojson::array();
    r["nodesAdded"] = 0;
    r["lists"] = 0;
    r["listEntries"] = 0;
    r["selects"] = 0;
    r["usedInputIds"] = 0;
    r["bytes"] = {{"before", nullptr}, {"after", nullptr}};
    return r;
}

class Merger {
public:
    Merger(const TypeDb& types, const ClipFactsFn& facts, const MergeHooks* hooks) : types_(types), facts_(facts), hooks_(hooks) {}

    uint32_t nid(const std::vector<std::string>& parts) const { return hooks_ && hooks_->ids.node_id ? hooks_->ids.node_id(parts) : node_id(parts); }

    MergeResult run(const GraphSource& src, const std::vector<ModStates>& mods) {
        start_ = last_ = std::chrono::steady_clock::now();
        report_ = new_report();
        // the definitions are not changed: they can be merged again
        for (size_t i = 0; i < mods.size(); ++i)
            for (const State& st : mods[i].states) work_.push_back(std::make_unique<Work>(Work{st, i, {}, {}}));
        std::vector<std::pair<std::string, std::vector<size_t>>> by_mod;  // mod -> indexes into mods, in first-seen order
        for (size_t i = 0; i < mods.size(); ++i) {
            auto it = std::find_if(by_mod.begin(), by_mod.end(), [&](const auto& x) { return x.first == mods[i].mod; });
            if (it == by_mod.end()) by_mod.push_back({mods[i].mod, {i}});
            else it->second.push_back(i);
        }
        for (const auto& [mod, ms] : by_mod) {
            if (ms.size() < 2) continue;
            std::vector<std::string> sources;
            for (size_t i : ms) sources.push_back(mods[i].source);
            std::sort(sources.begin(), sources.end());
            std::string where;
            for (size_t i = 0; i < sources.size(); ++i) where += (i ? ", " : "") + sources[i];
            for (auto& w : work_)
                if (std::find(ms.begin(), ms.end(), w->from) != ms.end()) w->st.refuse("mod " + mod + " is given more than once (" + where + ")");
        }
        if (std::none_of(work_.begin(), work_.end(), [](const auto& w) { return !w->st.status; })) return finish("untouched", std::nullopt, src.bytes ? src.bytes->size() : std::optional<size_t>());
        Bytes read;
        const Bytes* raw = src.bytes;
        if (!raw) {
            if (!src.read) return finish("off", "no graph to merge into", std::nullopt);
            GraphRead r = src.read();
            if (r.kind == GraphRead::Error) return finish("off", "the game's graph cannot be read: " + r.error, std::nullopt);
            if (r.kind == GraphRead::None) return finish("off", "no graph to merge into", std::nullopt);
            read = std::move(r.bytes);
            raw = &read;
        }
        try {
            const Type* gt = types_.get(kGraphType);
            if (!gt) off(std::string("the reflection has no ") + kGraphType);
            check_uses(types_, hooks_ ? hooks_->missing_field : std::string());
            const Decoder dec(types_, kMaxArray);
            const Encoder enc(types_);
            Value value = dec.decode(gt, raw->data(), raw->size(), 0, kGraphType);
            if (encode(enc, gt, value) != *raw) off("the game's graph does not encode back to its own bytes with this reflection");
            const Value pristine = value;  // the Python decodes the same bytes again: the same value
            lap(&times_.read);
            Graph g(value);
            if (hooks_)
                for (const std::string& k : hooks_->drop_first) g.first.erase(k);
            std::vector<MachinePlan> plan = accept(g);
            lap(&times_.accept);
            if (plan.empty()) return finish("untouched", std::nullopt, raw->size());
            std::set<Id> new_ids, tm, tl, ts;
            build(g, plan, &new_ids, &tm, &tl, &ts);
            lap(&times_.build);
            validate(types_, pristine, value, tm, tl, ts, new_ids);
            lap(&times_.validate);
            Bytes merged = encode(enc, gt, value);
            const Value back = dec.decode(gt, merged.data(), merged.size(), 0, kGraphType);
            if (!equal(back, value) || encode(enc, gt, back) != merged) off("the merged graph does not decode to itself");
            lap(&times_.encode);
            return finish("merged", std::nullopt, raw->size(), std::move(merged));
        } catch (const PyError& e) {
            return finish("off", e.type == "MergeOff" ? e.text : e.type + ": " + e.text, raw->size());
        } catch (const std::exception& e) {
            return finish("off", std::string("internal: ") + e.what(), raw->size());
        }
    }

private:
    void lap(double* into) {
        const auto now = std::chrono::steady_clock::now();
        *into += std::chrono::duration<double, std::milli>(now - last_).count();
        last_ = now;
    }

    Bytes encode(const Encoder& enc, const Type* gt, const Value& v) {
        Bytes out = enc.encode(gt, v, kGraphType);
        ++encodes_;
        if (hooks_ && hooks_->after_encode) hooks_->after_encode(encodes_, &out);
        return out;
    }

    MergeResult finish(const char* graph, const std::optional<std::string>& reason, std::optional<size_t> before, std::optional<Bytes> out = std::nullopt) {
        const bool merged = std::string_view(graph) == "merged";
        if (!merged) {
            const ojson fresh = new_report();
            for (const char* k : {"machines", "nodesAdded", "lists", "listEntries", "selects", "usedInputIds"}) report_[k] = fresh[k];
        }
        for (auto& w : work_)
            if (!w->st.status) {
                if (merged) {
                    w->st.status = "live";
                    w->st.reason.reset();
                } else {
                    w->st.status = "refused";
                    w->st.reason = "mod " + w->st.mod + ", state " + w->st.name + ": the merge is off: " + (reason ? *reason : std::string("None"));
                }
            }
        report_["graph"] = graph;
        report_["reason"] = reason ? ojson(*reason) : ojson(nullptr);
        report_["graphHash"] = merged && out ? graph_hash(*out) : std::string(16, '0');
        std::vector<Work*> sorted;
        for (auto& w : work_) sorted.push_back(w.get());
        std::stable_sort(sorted.begin(), sorted.end(), key_less);
        MergeResult r;
        times_.total = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start_).count();
        r.times = times_;
        ojson states = ojson::array();
        for (Work* w : sorted) {
            states.push_back(w->st.entry());
            r.states.push_back(w->st);
        }
        report_["states"] = std::move(states);
        report_["bytes"] = {{"before", before ? ojson(*before) : ojson(nullptr)}, {"after", merged && out ? ojson(out->size()) : ojson(nullptr)}};
        if (merged) r.graph = std::move(out);
        r.report = std::move(report_);
        return r;
    }

    // Rules on each state alone, then between states, then per machine (rule 8). -> the machines that take states, in
    // the graph's order.
    std::vector<MachinePlan> accept(const Graph& g) {
        std::unordered_map<Id, std::unique_ptr<Neighbour>>& neighbours = neighbours_;
        std::vector<Work*> order;
        for (auto& w : work_)
            if (!w->st.status) order.push_back(w.get());
        std::stable_sort(order.begin(), order.end(), key_less);
        std::vector<Item> pending;
        for (Work* w : order) {
            State& st = w->st;
            if (!st.like || !st.clip || !st.clip_ref) throw PyError("TypeError", "a state with no like or clip was not refused");
            std::string why;
            const auto found = resolve_like(g, *st.like, &why);
            if (!found) {
                st.refuse(why);
                continue;
            }
            Value* L = found->first;
            Value* M = found->second;
            st.machine = py_str(M->at("debugName"));
            const std::optional<std::string> kind = support(*L, *M, &why);
            if (!kind) {
                st.refuse("like " + py_repr(std::string_view(*st.like)) + ": " + why);
                continue;
            }
            st.kind = *kind;
            const Id lid = id_of(*L);
            auto it = neighbours.find(lid);
            if (it == neighbours.end()) it = neighbours.emplace(lid, std::make_unique<Neighbour>(g, L, M, *kind)).first;
            Neighbour* nb = it->second.get();
            if (nb->error) {
                st.refuse("like " + py_repr(std::string_view(*st.like)) + " (" + py_str(M->at("debugName")) + "/" + py_str(L->at("debugName")) +
                          ") cannot be a neighbour: " + *nb->error);
                continue;
            }
            const Entry& entry = entry_of(*kind);
            if (!g.first.count(entry.single)) {
                st.refuse(std::string("the game's graph has no ") + entry.single + " node to copy as its condition");
                continue;
            }
            const std::optional<ClipFacts> facts = facts_(*st.clip);
            if (!facts) {
                st.refuse("clip " + py_repr(std::string_view(*st.clip_ref)) + " (" + *st.clip + ") is not served: no " + kJointAnim +
                          " under that GUID in the mods' assets or the game");
                continue;
            }
            if (!facts->fps) {
                st.refuse("clip " + py_repr(std::string_view(*st.clip_ref)) + " (" + *st.clip + ") has 0 frames a second");
                continue;
            }
            const std::string own_guid = py_str(nb->clip->at("animation"));
            const std::optional<ClipFacts> own = facts_(own_guid);
            if (!own) {
                st.refuse("the neighbour's clip " + own_guid + " cannot be read, so the skeleton cannot be checked");
                continue;
            }
            if (facts->hierarchy_hash != own->hierarchy_hash || facts->joint_count != own->joint_count) {
                char b[256];
                std::snprintf(b, sizeof b, "hierarchyHash 0x%016llx, %llu joints; the player's is 0x%016llx, %llu joints",
                              static_cast<unsigned long long>(facts->hierarchy_hash), static_cast<unsigned long long>(facts->joint_count),
                              static_cast<unsigned long long>(own->hierarchy_hash), static_cast<unsigned long long>(own->joint_count));
                st.refuse("clip " + py_repr(std::string_view(*st.clip_ref)) + " (" + *st.clip + ") is made for another skeleton: " + b);
                continue;
            }
            if (g.compared.count(st.id)) {
                st.refuse("its state id " + hex8(st.id) + " is an id the game's graph already compares: rename the state");
                continue;
            }
            const std::vector<std::string> roles = nb->roles();
            std::vector<uint32_t> rids;
            for (const std::string& r : roles) rids.push_back(nid({st.mod, st.name, r}));
            const std::unordered_set<uint32_t> uniq(rids.begin(), rids.end());
            if (uniq.size() != rids.size() || std::any_of(rids.begin(), rids.end(), [&](uint32_t r) { return g.ids.count(r) > 0; })) {
                st.refuse("one of its node ids is taken in the game's graph: rename the state");
                continue;
            }
            w->facts = *facts;
            w->rids.clear();
            for (size_t i = 0; i < roles.size(); ++i) w->rids.emplace_back(roles[i], rids[i]);
            pending.push_back({w, nb});
        }
        // between states (rule 10): two of ours alike refuses all of them
        for (int pass = 0; pass < 2; ++pass) {
            const char* what = pass == 0 ? "state id" : "node id";
            std::vector<std::pair<Id, std::vector<Work*>>> by;
            std::unordered_map<Id, size_t> at;
            for (const Item& it : pending) {
                std::vector<Id> keys;
                if (pass == 0) keys.push_back(it.w->st.id);
                else
                    for (const auto& [r, id] : it.w->rids) keys.push_back(id);
                for (Id x : keys) {
                    auto f = at.find(x);
                    if (f == at.end()) at.emplace(x, by.size()), by.push_back({x, {it.w}});
                    else by[f->second].second.push_back(it.w);
                }
            }
            for (const auto& [x, same] : by) {
                std::vector<std::pair<std::string, std::string>> names;
                for (Work* s : same) names.emplace_back(s->st.mod, s->st.name);
                std::sort(names.begin(), names.end());
                names.erase(std::unique(names.begin(), names.end()), names.end());
                if (names.size() < 2) continue;
                for (Work* s : same) {
                    std::string others;
                    for (const auto& [m, n] : names)
                        if (!(m == s->st.mod && n == s->st.name)) others += (others.empty() ? "" : ", ") + m + "/" + n;
                    s->st.refuse(std::string("its ") + what + " is the same as " + others + "'s: rename one");
                }
            }
        }
        std::vector<Item> left;
        for (const Item& it : pending)
            if (!it.w->st.status) left.push_back(it);
        // rule 8, per machine in graph order
        std::unordered_set<Id> taken;
        for (const Item& it : left)
            for (const auto& [r, id] : it.w->rids) taken.insert(id);
        std::vector<std::pair<Id, std::vector<Item>>> by_machine;
        for (const Item& it : left) {
            const Id mid = id_of(*it.nb->machine);
            auto f = std::find_if(by_machine.begin(), by_machine.end(), [&](const auto& x) { return x.first == mid; });
            if (f == by_machine.end()) by_machine.push_back({mid, {it}});
            else f->second.push_back(it);
        }
        std::vector<MachinePlan> out;
        for (Value* M : g.machines) {
            const Id mid = id_of(*M);
            auto f = std::find_if(by_machine.begin(), by_machine.end(), [&](const auto& x) { return x.first == mid; });
            if (f == by_machine.end()) continue;
            const std::vector<Item>& items = f->second;
            const int64_t num = sint_of(M->at("numGlobalTransitions"));
            if (num + static_cast<int64_t>(items.size()) <= static_cast<int64_t>(kMaxGlobal)) {
                out.push_back({M, mid, items, std::nullopt});
                continue;
            }
            const int64_t free = static_cast<int64_t>(kMaxGlobal) - num;
            const std::string mname = py_str(M->at("debugName"));
            if (!entry_of(items[0].nb->kind).nests) {
                // items[free:] refused (a Python slice: a negative start counts from the end), items[:free] kept when free > 0
                const int64_t n = static_cast<int64_t>(items.size());
                const size_t from = static_cast<size_t>(free >= 0 ? std::min(free, n) : std::max<int64_t>(0, n + free));
                for (size_t i = from; i < items.size(); ++i)
                    items[i].w->st.refuse("machine " + mname + " has room for " + std::to_string(free) + " new states across all mods (" + std::to_string(kMaxGlobal) +
                                          " global transitions, " + std::to_string(num) + " its own) and an overlay machine does not nest: the states before this one in "
                                          "mod and name order take them");
                if (free > 0) out.push_back({M, mid, std::vector<Item>(items.begin(), items.begin() + static_cast<ptrdiff_t>(std::min(free, n))), std::nullopt});
                continue;
            }
            std::vector<Group> kept;
            for (size_t gi = 0; gi * kMaxGlobal < items.size(); ++gi) {
                Group group{gi, std::vector<Item>(items.begin() + static_cast<ptrdiff_t>(gi * kMaxGlobal),
                                                  items.begin() + static_cast<ptrdiff_t>(std::min(items.size(), (gi + 1) * kMaxGlobal))),
                            {}};
                if (static_cast<int64_t>(gi) >= free) {
                    for (const Item& it : group.items)
                        it.w->st.refuse("machine " + mname + " is full: " + std::to_string(num) + " global transitions of its own, " + std::to_string(free) +
                                        " left for nested machines of " + std::to_string(kMaxGlobal) +
                                        " states each, and the states before this one in mod and name order fill them");
                    continue;
                }
                const char* multi = entry_of(group.items[0].nb->kind).multi;
                if (!g.first.count(multi)) {
                    for (const Item& it : group.items)
                        it.w->st.refuse("machine " + mname + " is full and the game's graph has no " + multi + " node to copy for a nested machine's entry");
                    continue;
                }
                const char* roles[5] = {"machine", "pose", "wrapper", "list", "global"};
                for (int r = 0; r < 5; ++r) group.nids[r] = nid({"nest", mname, std::to_string(gi), roles[r]});
                const std::unordered_set<Id> uniq(group.nids.begin(), group.nids.end());
                if (uniq.size() != 5 || std::any_of(group.nids.begin(), group.nids.end(), [&](Id r) { return g.ids.count(r) || taken.count(r); })) {
                    for (const Item& it : group.items)
                        it.w->st.refuse("the node ids of nested machine " + std::to_string(gi) + " of " + mname + " are taken in the game's graph");
                    continue;
                }
                taken.insert(group.nids.begin(), group.nids.end());
                kept.push_back(std::move(group));
            }
            if (!kept.empty()) {
                std::vector<Item> flat;
                for (const Group& gr : kept) flat.insert(flat.end(), gr.items.begin(), gr.items.end());
                out.push_back({M, mid, std::move(flat), std::move(kept)});
            }
        }
        return out;
    }

    Value clip_node(const Work& w, const Neighbour& nb) const {
        const State& st = w.st;
        const ClipFacts& f = w.facts;
        Value c = *nb.clip;
        const uint32_t h = fnv_guid_text(*st.clip);
        Guid probe;
        if (!Guid::parse(*st.clip, &probe)) throw PyError("ValueError", "badly formed hexadecimal UUID string");
        c.set("id", Value::hk(w.rid("clip")));
        c.set("animation", Value::text(*st.clip));
        c.set("animationHash", Value::hk(h));
        float d = 0;
        if (!to_f32(static_cast<double>(f.frame_count) / static_cast<double>(f.fps), &d)) throw PyError("OverflowError", "float too large to pack with f format");
        c.set("animationDuration", Value::real(static_cast<double>(d)));
        c.set("rootMotionAnimation", Value::text(f.root_motion ? *st.clip : std::string(kZeroGuid)));
        c.set("rootMotionAnimationHash", Value::hk(f.root_motion ? h : 0));
        c.set("hasSequences", Value::boolean(f.sequences));
        for (const char* x : kGenderGuids) c.set(x, Value::text(kZeroGuid));
        for (const char* x : kGenderHashes) c.set(x, Value::hk(0));
        c.set("loop", Value::boolean(st.loop ? *st.loop : f.loop));
        if (text_is(c.at("additiveType"), "None")) {
            c.set("additiveReferenceAnimation", Value::text(*st.clip));
            c.set("additiveReferenceAnimationHash", Value::hk(h));
        }
        if (st.speed) c.at("speedController").at("rawValueController").set("speedMultiplier", Value::real(*st.speed));
        return wrap("SampleAnimationClip", std::move(c));
    }

    // Rules 1 to 5 for one state: its nodes in rule 9's order, its global transition entry, its new transition ids.
    void state_nodes(const Graph& g, const Work& w, const Neighbour& nb, Id parent, bool local, std::vector<Value>* nodes, Value* gt, std::vector<Id>* trans) const {
        const State& st = w.st;
        const Entry& entry = entry_of(nb.kind);
        nodes->push_back(clip_node(w, nb));
        Id top = w.rid("clip");
        if (nb.layer) {
            Value lb = *nb.layer;
            lb.set("id", Value::hk(w.rid("layer")));
            lb.set("layer", Value::hk(w.rid("clip")));
            nodes->push_back(wrap("LayerBlend", std::move(lb)));
            top = w.rid("layer");
        }
        Value p = *nb.pose;
        p.set("id", Value::hk(w.rid("pose")));
        p.set("input", Value::hk(top));
        nodes->push_back(wrap("PoseResult", std::move(p)));
        Value s = *nb.state;
        s.set("id", Value::hk(w.rid("state")));
        s.set("debugName", Value::text(st.mod + "." + st.name));
        s.set("poseResult", Value::hk(w.rid("pose")));
        s.set("parentStateMachineId", Value::hk(parent));
        if (st.blend_in) s.set("retriggerBlendDuration", Value::real(*st.blend_in));
        nodes->push_back(wrap("StateMachineState", std::move(s)));
        nodes->push_back(entry.make(*g.first.at(entry.single), w.rid("cond"), st.id));
        *gt = nb.entry_fields();
        gt->set("toState", Value::hk(w.rid("state")));
        gt->set("conditionNode", Value::hk(w.rid("cond")));
        if (st.blend_in) gt->set("duration", Value::real(*st.blend_in));
        if (!local) return;
        for (size_t i = 0; i < nb.outs.size(); ++i) {
            const std::string role = "out." + std::to_string(i);
            Value c = *nb.outs[i];
            c.set("id", Value::hk(w.rid(role)));
            c.set("fromState", Value::hk(w.rid("state")));
            nodes->push_back(wrap("StateMachineTransition", std::move(c)));
            trans->push_back(w.rid(role));
        }
        for (size_t i = 0; i < nb.ins.size(); ++i) {
            const std::string role = "in." + std::to_string(i);
            nodes->push_back(entry.make(*g.first.at(entry.single), w.rid(role + ".cond"), st.id));
            Value c = *nb.ins[i];
            c.set("id", Value::hk(w.rid(role)));
            c.set("toState", Value::hk(w.rid("state")));
            c.set("conditionNode", Value::hk(w.rid(role + ".cond")));
            nodes->push_back(wrap("StateMachineTransition", std::move(c)));
            trans->push_back(w.rid(role));
        }
    }

    // Writes the accepted states into the graph.
    void build(Graph& g, std::vector<MachinePlan>& plan, std::set<Id>* new_ids, std::set<Id>* tm, std::set<Id>* tl, std::set<Id>* ts) {
        const Type* gtt = types_.get(kGtType);
        if (!gtt) throw PyError("TypeError", "'NoneType' object is not subscriptable");
        const Value blank = Decoder(types_).decode(gtt, Bytes(gtt->size, 0));
        std::unordered_map<Id, std::vector<Value>> blocks;
        std::vector<std::pair<Id, uint64_t>> touched_lists, touched_selects;  // insertion-ordered counts
        auto bump = [](std::vector<std::pair<Id, uint64_t>>& v, Id id) {
            auto f = std::find_if(v.begin(), v.end(), [&](const auto& x) { return x.first == id; });
            if (f == v.end()) v.push_back({id, 1});
            else ++f->second;
        };
        Value& used = list_of(g.value->at("usedInputIds").at("parameterIds"));
        const size_t used_before = used.items.size();
        auto take_ids = [&](const Work& w, const Neighbour& nb) {
            const Entry& entry = entry_of(nb.kind);
            for (Value* lst : nb.lists) {
                list_of(lst->at(entry.values)).items.push_back(Value::hk(w.st.id));
                bump(touched_lists, id_of(*lst));
            }
            for (const auto& [sel, pose] : nb.selects) {
                list_of(sel->at("values")).items.push_back(Value::hk(w.st.id));
                list_of(sel->at("inputPoses")).items.push_back(Value::hk(pose));
                bump(touched_selects, id_of(*sel));
            }
            if (entry.used_ids) used.items.push_back(Value::hk(w.st.id));
        };
        for (MachinePlan& mp : plan) {  // rule 11: machines in graph order (accept() made them so)
            Value* M = mp.M;
            const Id mid = mp.mid;
            std::vector<Value> block, gts;
            std::vector<Id> new_states, new_trans;
            if (!mp.groups) {
                for (const Item& it : mp.items) {
                    Value gt;
                    state_nodes(g, *it.w, *it.nb, mid, true, &block, &gt, &new_trans);
                    gts.push_back(std::move(gt));
                    new_states.push_back(it.w->rid("state"));
                    take_ids(*it.w, *it.nb);
                }
            } else {
                for (const Group& gr : *mp.groups) {
                    const auto [inner_id, pose_id, wrapper_id, list_id, global_id] = gr.nids;
                    std::vector<Value> inner_gts;
                    std::vector<Id> inner_states, unused;
                    for (const Item& it : gr.items) {
                        Value gt;
                        state_nodes(g, *it.w, *it.nb, inner_id, false, &block, &gt, &unused);
                        inner_gts.push_back(std::move(gt));
                        inner_states.push_back(it.w->rid("state"));
                        take_ids(*it.w, *it.nb);
                    }
                    const Neighbour& first_nb = *gr.items[0].nb;
                    const std::string mname = py_str(M->at("debugName"));
                    const std::string name = "kiln.nest." + mname + "." + std::to_string(gr.gi);
                    Value ref = M->at("transitionRefInfo");
                    ref.set("fromState", Value::list());
                    ref.set("toState", Value::list());
                    ref.set("duration", Value::list());
                    Value inner = *M;
                    inner.set("id", Value::hk(inner_id));
                    Value states = Value::list();
                    for (Id x : inner_states) states.items.push_back(Value::hk(x));
                    inner.set("states", std::move(states));
                    inner.set("transitions", Value::list());
                    inner.set("transitionRefInfo", std::move(ref));
                    inner.set("entryState", Value::hk(inner_states[0]));
                    inner.set("parentStateMachineStateId", Value::hk(wrapper_id));
                    const size_t n_inner = inner_gts.size();
                    Value all = Value::list(std::move(inner_gts));
                    for (size_t k = n_inner; k < kMaxGlobal; ++k) all.items.push_back(blank);
                    inner.set("globalTransitions", std::move(all));
                    inner.set("numGlobalTransitions", Value::integer(n_inner));
                    inner.set("globalTransitionNodeId", Value::hk(global_id));
                    inner.set("debugName", Value::text(name));
                    Value wgt = first_nb.entry_fields();
                    wgt.set("toState", Value::hk(wrapper_id));
                    wgt.set("conditionNode", Value::hk(list_id));
                    Value wrapper = *first_nb.state;
                    wrapper.set("id", Value::hk(wrapper_id));
                    wrapper.set("debugName", Value::text(name));
                    wrapper.set("poseResult", Value::hk(pose_id));
                    wrapper.set("parentStateMachineId", Value::hk(mid));
                    wrapper.set("retriggerBlendDuration", wgt.at("duration"));
                    wrapper.set("restartOnRetrigger", Value::boolean(false));
                    Value pose = *first_nb.pose;
                    pose.set("id", Value::hk(pose_id));
                    pose.set("input", Value::hk(inner_id));
                    const Entry& entry = entry_of(first_nb.kind);
                    std::vector<Id> ids;
                    for (const Item& it : gr.items) ids.push_back(it.w->st.id);
                    block.push_back(wrap("StateMachine", std::move(inner)));
                    block.push_back(wrap("PoseResult", std::move(pose)));
                    block.push_back(wrap("StateMachineState", std::move(wrapper)));
                    block.push_back(entry.make_list(*g.first.at(entry.multi), list_id, ids));
                    gts.push_back(std::move(wgt));
                    new_states.push_back(wrapper_id);
                }
            }
            const int64_t num = sint_of(M->at("numGlobalTransitions"));
            if (num + static_cast<int64_t>(gts.size()) > static_cast<int64_t>(kMaxGlobal))
                off("machine " + py_str(M->at("debugName")) + ": " + std::to_string(num + static_cast<int64_t>(gts.size())) + " global transitions");
            for (Id x : new_states) list_of(M->at("states")).items.push_back(Value::hk(x));
            for (Id x : new_trans) list_of(M->at("transitions")).items.push_back(Value::hk(x));
            {  // M["globalTransitions"][num:num + len(gts)] = gts
                std::vector<Value>& v = list_of(M->at("globalTransitions")).items;
                const size_t lo = std::min<size_t>(static_cast<size_t>(std::max<int64_t>(num, 0)), v.size());
                const size_t hi = std::min<size_t>(static_cast<size_t>(std::max<int64_t>(num, 0)) + gts.size(), v.size());
                v.erase(v.begin() + static_cast<ptrdiff_t>(lo), v.begin() + static_cast<ptrdiff_t>(hi));
                v.insert(v.begin() + static_cast<ptrdiff_t>(lo), std::make_move_iterator(gts.begin()), std::make_move_iterator(gts.end()));
            }
            M->set("numGlobalTransitions", Value::integer(static_cast<uint64_t>(num + static_cast<int64_t>(gts.size()))));
            for (const Value& n : block) new_ids->insert(id_of(wrapped_value(n)));
            ojson row;
            row["machine"] = py_str(M->at("debugName"));
            row["states"] = mp.items.size();
            row["nested"] = mp.groups ? mp.groups->size() : 0;
            row["globalTransitions"] = {num, num + static_cast<int64_t>(gts.size())};
            row["nodesAdded"] = block.size();
            report_["machines"].push_back(std::move(row));
            tm->insert(mid);
            blocks.emplace(mid, std::move(block));
        }
        // rule 9: each block just before its machine
        std::vector<Value> merged;
        merged.reserve(g.wrapped->size() + new_ids->size());
        for (Value& n : *g.wrapped) {
            const Value& v = wrapped_value(n);
            const auto b = blocks.find(id_of(v));
            if (b != blocks.end() && type_is(v, "StateMachine"))
                for (Value& x : b->second) merged.push_back(std::move(x));
            merged.push_back(std::move(n));
        }
        *g.wrapped = std::move(merged);  // g's node pointers are stale from here on
        report_["nodesAdded"] = new_ids->size();
        uint64_t entries = 0;
        for (const auto& [id, c] : touched_lists) entries += c, tl->insert(id);
        for (const auto& [id, c] : touched_selects) ts->insert(id);
        report_["lists"] = touched_lists.size();
        report_["listEntries"] = entries;
        report_["selects"] = touched_selects.size();
        report_["usedInputIds"] = used.items.size() - used_before;
    }

    const TypeDb& types_;
    const ClipFactsFn& facts_;
    const MergeHooks* hooks_;
    ojson report_;
    std::vector<std::unique_ptr<Work>> work_;
    std::unordered_map<Id, std::unique_ptr<Neighbour>> neighbours_;
    int encodes_ = 0;
    MergeTimes times_;
    std::chrono::steady_clock::time_point start_, last_;
};

}  // namespace

MergeResult merge(const GraphSource& raw, const TypeDb& types, const std::vector<ModStates>& mods, const ClipFactsFn& clip_facts, const MergeHooks* hooks) {
    Merger m(types, clip_facts, hooks);
    return m.run(raw, mods);
}

}  // namespace kpa
