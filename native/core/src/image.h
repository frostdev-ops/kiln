// SPDX-License-Identifier: MIT
// The game executable as loaded in memory: section ranges and build identity.
#pragma once
#include <string>

#include "common.h"

namespace kiln {

struct ImageInfo {
    uintptr_t base = 0;
    size_t size = 0;
    const uint8_t* text = nullptr;
    size_t text_size = 0;
    std::string sha256;     // of the file on disk, lower-case hex
    std::string codeview;   // RSDS GUID + age, e.g. "8F0C...-2"; empty when absent
    std::string pdb;        // file name of the RSDS PDB path, e.g. "enshrouded.pdb"; empty when absent
    uint32_t timestamp = 0; // COFF TimeDateStamp
    uint32_t side = KILN_SIDE_SERVER;  // KILN_SIDE_*: from the PDB name, else the exe file name
    std::string side_source;           // what decided the side ("pdb enshrouded.pdb", "exe name ...", "default")
};

// "server" / "client": the sigdb definitions (<side>.json), build records (builds/<side>/) and kiln.json section.
const char* side_name(uint32_t side);

bool init_image();  // parses the main module and hashes the file; logs failures
const ImageInfo& image();

inline uintptr_t rva_of(uintptr_t address) { return address - image().base; }

}  // namespace kiln
