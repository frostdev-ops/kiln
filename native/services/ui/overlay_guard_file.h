// SPDX-License-Identifier: MIT
// How kiln/state/overlay-start.guard is written (overlay_guard.cpp save()): a temporary file, then a rename over the
// record. Header-only and without windows.h (the host test drives it with a fake file system).
//
// The record must be there at the next start after the game died during the overlay's start (a crash, or a hung game
// that was killed). A dead game process loses nothing it already wrote: Windows has it and puts it on the disk. So the
// write itself forces nothing to the disk; the disk catches up off the caller's thread (flush_later). Until 29.15 every
// write went through to the disk on the game's present call (FILE_FLAG_WRITE_THROUGH, FlushFileBuffers,
// MOVEFILE_WRITE_THROUGH): on a hard disk under the game's own loading, a quarter to half a second of a frozen frame
// per stage (perfdiag 2026-11-17, inbox 50: CreateFileW/WriteFile/FlushFileBuffers/MoveFileExW < kui save <
// overlay_guard_stage < hk_present).
#pragma once
#include <string>
#include <string_view>

namespace kui::overlay_guard {

// Fs: bool write_new(path, text): create or truncate, write, close; no write-through, no flush.
//     bool replace(from, to): rename over the record (MOVEFILE_REPLACE_EXISTING), no write-through.
//     void flush_later(path): ask for the record to be forced to the disk on another thread; returns at once.
template <class Fs>
bool write_record(Fs& fs, const std::wstring& path, std::string_view text) {
    if (path.empty()) return true;  // no state folder: nothing to keep
    const std::wstring temp = path + L".tmp";
    if (!fs.write_new(temp, text)) return false;
    if (!fs.replace(temp, path)) return false;
    fs.flush_later(path);
    return true;
}

}  // namespace kui::overlay_guard
