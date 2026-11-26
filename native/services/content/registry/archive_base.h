// SPDX-License-Identifier: MIT
// kiln.content registry stage: who published the archive this start's base is read from (registry.json base.writer and
// base.archive; registry-followups 2).
//
// The stage reads its base as the live bytes the game will read (kiln.assets at resources-ready), so its values are whatever
// this start's archive writer published. registry.json says which writer that was, and what Kiln knows it from, so a reader
// (the launcher's Conflicts view, kilnctl's values diff) never takes another loader's pregame values for the game's own data.
// Until registry-followups it said "none" (as shipped) on every start but a kiln-mode one, also where kiln-eml's own EML pass or
// hosted ShroudForge had patched the archive.
//
// Inputs, all Kiln's own (never ShroudForge's state or config files):
//  - kiln.launch: which ShroudForge backend has this start ("kiln", "hosted", "installed", "none");
//  - kiln.eml's state_json (the text of kiln/state/eml.json): what kiln-eml's startup run did (phase done / aside / idle /
//    failed, patched, cached, restored, the loaders it stood aside for, its reason);
//  - whether kiln-eml's ownership receipt (kiln/eml/ownership/<stem>.json, the SHA-256 of what it published) and ShroudForge's
//    record of its pass (<stem>.kfc.prepared.json, which kiln-eml checks hosted output against) exist: only their names, never
//    their contents.
// Writers: "kiln-eml" (its EML pass, or Kiln's interpreter in kiln mode), "shroudforge-hosted" / "shroudforge-installed"
// (ShroudForge reserved the startup data writer; kiln-eml stood aside), "external" (another loader's proxy ran instead of
// kiln-eml, or the game data is not the game's own and kiln-eml cannot say who wrote it), "none" (the game's data as shipped,
// as far as kiln-eml can tell). kilnctl's values diff takes "none" as the game's own data and names any other writer in a note
// (runner/kilnctl/src/regproof/values.rs), so its check is unchanged.
// Header-only, plain data: host-tested (tests/registry_writers_test.cpp).
#pragma once
#include <functional>
#include <string>

#include "nlohmann/json.hpp"

namespace kc::registry {

struct ArchiveBaseInputs {
    std::string backend = "none";  // kiln.launch: "kiln", "hosted", "installed", "none" (also: no kiln.launch)
    bool eml = false;              // kiln.eml answered with its state
    nlohmann::json eml_state;      // its state_json, parsed (kiln/state/eml.json's text)
    std::string stem;              // the game's data file stem ("enshrouded_server"), when eml_state has none
    std::function<bool(const std::string& rel)> exists;  // a file of the game folder (relative, '/') is there
};

struct ArchiveBase {
    std::string writer = "none";
    nlohmann::json archive;  // registry.json base.archive; null: nothing to say
};

// The sentence kiln-eml starts an idle start's reason with when the game data differs from its vanilla backup and is not
// output it can prove it wrote (gpl/kiln-eml/kiln-eml/src/runner.rs unproven_sentence): the base is not the game's own then.
inline constexpr const char* kEmlDataDiffers = "The game data differs from its vanilla backup";

inline ArchiveBase archive_base(const ArchiveBaseInputs& in) {
    using nlohmann::json;
    ArchiveBase out;
    json& a = out.archive;
    a = json::object();
    const json& st = in.eml_state;
    auto text_of = [&st](const char* k) {
        return st.is_object() && st.contains(k) && st[k].is_string() ? st[k].get<std::string>() : std::string();
    };
    auto flag = [&st](const char* k) { return st.is_object() && st.contains(k) && st[k].is_boolean() && st[k].get<bool>(); };
    auto count = [&st](const char* k) -> int64_t {
        return st.is_object() && st.contains(k) && st[k].is_number_integer() ? st[k].get<int64_t>() : 0;
    };
    const bool sf = in.backend == "hosted" || in.backend == "installed";
    if (!in.eml || !st.is_object()) {
        // kiln-eml did not answer: in kiln mode Kiln's interpreter runs through it all the same (as before this table)
        out.writer = in.backend == "kiln" ? "kiln-eml" : "none";
        a["source"] = "none";
        a["text"] = in.backend == "kiln"
                        ? "Kiln's interpreter for ShroudForge mods (kiln mode) has this start; kiln-eml's state was not readable, so Kiln cannot say what it published"
                        : "kiln-eml is not running: Kiln cannot tell whether another loader patched the archive; it reads it as the game's own";
        return out;
    }
    std::string stem = text_of("file");
    if (stem.empty()) stem = in.stem;
    const std::string phase = text_of("phase");
    a["source"] = "kiln-eml";
    a["phase"] = phase;
    a["patched"] = count("patched");
    a["cached"] = flag("cached");
    a["restored"] = flag("restored");
    if (st.contains("mods") && st["mods"].is_array()) {
        int64_t loaded = 0;
        for (const json& m : st["mods"])
            if (m.is_object() && m.contains("state") && m["state"] == "loaded") ++loaded;
        a["mods"] = loaded;
    }
    auto file = [&](const std::string& rel) { return !stem.empty() && in.exists && in.exists(rel) ? json(rel) : json(nullptr); };
    const std::string receipt = "kiln/eml/ownership/" + stem + ".json";
    const bool loaders = st.contains("loaders") && st["loaders"].is_array() && !st["loaders"].empty();
    if (in.backend == "kiln") {
        // the writer stays kiln-eml whatever the pass did (as before); the sentence says what it did (startup_seam.rs
        // settle_entries: done = committed or cached, idle = unchanged or restored, failed = refused or failed; run_kiln's
        // start that ran no pass is idle or failed too). Review: it said "published the archive" on every phase.
        out.writer = "kiln-eml";
        a["receipt"] = file(receipt);
        const std::string who = "Kiln's interpreter for ShroudForge mods (kiln mode)";
        a["text"] = phase == "done"                         ? who + " published the archive through kiln-eml (its change journal names the packages)"
                    : phase == "idle" && flag("restored") ? who + " put the game's own data back from kiln-eml's vanilla backup this start"
                    : phase == "idle"                     ? who + " published nothing this start (no startup pass ran, or it changed nothing): "
                                                              "the archive is as an earlier start left it"
                    : phase == "failed"                   ? who + ": its startup pass failed this start and published nothing; the archive is "
                                                              "as an earlier start left it"
                                                          : who + " has this start; kiln-eml's phase " + (phase.empty() ? std::string("is not known") : phase) +
                                                                " says nothing of what it published";
    } else if (phase == "done") {
        out.writer = "kiln-eml";
        a["receipt"] = file(receipt);
        a["text"] = flag("cached") ? "kiln-eml's EML pass: nothing changed since the last run, the archive keeps its patches"
                                   : "kiln-eml's EML pass patched " + std::to_string(count("patched")) + " resource(s) of the archive this start";
    } else if (phase == "aside" && !loaders && sf) {
        out.writer = in.backend == "hosted" ? "shroudforge-hosted" : "shroudforge-installed";
        a["record"] = file(stem + ".kfc.prepared.json");
        a["text"] = std::string(in.backend == "hosted" ? "ShroudForge (hosted)" : "the installed ShroudForge") +
                    " reserved the startup data writer and published the archive (kiln-eml stood aside); ShroudForge's record of its "
                    "pass is named, not read, and no package is named in its values";
    } else if (phase == "aside") {
        out.writer = "external";
        a["text"] = "another loader published the archive instead of kiln-eml (" +
                    (loaders ? std::string("its proxy ran in this process") : std::string("kiln-eml stood aside")) + ")";
    } else if (phase == "failed" && !flag("restored")) {
        out.writer = "external";
        a["text"] = "kiln-eml's run failed and left the game data as it was: not known to be the game's own";
    } else if (phase == "idle" && !flag("restored") && text_of("reason").rfind(kEmlDataDiffers, 0) == 0) {
        out.writer = "external";
        a["text"] = "the game data differs from kiln-eml's vanilla backup and is not output kiln-eml can prove it wrote "
                    "(another loader or an earlier start wrote it)";
    } else {
        out.writer = "none";
        a["text"] = flag("restored") ? "kiln-eml put the game's own data back from its vanilla backup this start"
                                     : "no archive writer patched the archive this start, as far as kiln-eml can tell";
    }
    return out;
}

}  // namespace kc::registry
