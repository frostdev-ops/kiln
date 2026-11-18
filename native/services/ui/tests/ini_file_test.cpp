// SPDX-License-Identifier: MIT
// Host test of kui::IniFile (ini_file.h): imgui.ini read and written on its own thread, never on the caller's (the
// game's present call), the newest settings written, what is pending written at stop.
#include "../ini_file.h"

#include <atomic>
#include <chrono>
#include <cstdio>
#include <map>
#include <vector>

int main() {
    int checks = 0, failures = 0;
    auto check = [&](bool ok, const char* what) {
        ++checks;
        if (!ok) ++failures;
        std::printf("%s: %s\n", ok ? "ok" : "FAIL", what);
    };
    const auto caller = std::this_thread::get_id();
    std::mutex mu;
    std::condition_variable cv;
    std::map<std::string, std::string> disk{{"kiln.ui/imgui.ini", "[Window][Kiln]\nPos=10,20\n"}};
    std::vector<std::string> written;
    std::atomic<bool> read_off_thread{false}, write_off_thread{true};
    bool hold_writes = false;
    {
        kui::IniFile ini(
            [&](const std::string& path) -> std::optional<std::string> {
                read_off_thread = std::this_thread::get_id() != caller;
                std::lock_guard lock(mu);
                const auto it = disk.find(path);
                return it == disk.end() ? std::nullopt : std::optional<std::string>(it->second);
            },
            [&](const std::string& path, const std::string& text) {
                if (std::this_thread::get_id() == caller) write_off_thread = false;
                std::unique_lock lock(mu);
                cv.wait(lock, [&] { return !hold_writes; });
                disk[path] = text;
                written.push_back(text);
                return true;
            });
        ini.start("kiln.ui/imgui.ini");
        const auto text = ini.text();
        check(text && *text == "[Window][Kiln]\nPos=10,20\n" && read_off_thread, "the file is read on the thread, its text handed over");
        // ImGui saves while the disk is slow: the first write is stuck, three newer texts arrive; the caller never waits
        {
            std::lock_guard lock(mu);
            hold_writes = true;
        }
        ini.save("one");
        const auto begun = std::chrono::steady_clock::now();
        for (int i = 0; i < 50 && ini.writes() == 0; ++i) std::this_thread::sleep_for(std::chrono::milliseconds(1));
        ini.save("two");
        ini.save("three");
        ini.save("four");
        check(std::chrono::steady_clock::now() - begun < std::chrono::seconds(1), "save() returns at once while a write is stuck");
        check(ini.text() == std::optional<std::string>("four"), "the text handed out is the newest saved");
        {
            std::lock_guard lock(mu);
            hold_writes = false;
        }
        cv.notify_all();
        ini.stop();
        check(!written.empty() && written.back() == "four" && written.size() <= 2 && write_off_thread,
              "the newest text is written, older ones not yet written are skipped, all on the thread");
        check(disk["kiln.ui/imgui.ini"] == "four", "the file holds the newest settings after stop");
        ini.save("after stop");
        check(disk["kiln.ui/imgui.ini"] == "four", "nothing is written after stop");
    }
    {
        kui::IniFile none([](const std::string&) -> std::optional<std::string> { return std::string("x"); },
                          [](const std::string&, const std::string&) { return true; });
        none.start("");
        none.save("ignored");
        check(!none.text() && none.writes() == 0, "no data folder: nothing read, nothing written");
        none.stop();
    }
    {
        kui::IniFile missing([](const std::string&) -> std::optional<std::string> { return std::nullopt; },
                             [](const std::string&, const std::string&) { return true; });
        missing.start("kiln.ui/imgui.ini");
        check(!missing.text(), "no file yet: no settings (ImGui's defaults)");
        missing.save("first");
        missing.stop();
        check(missing.writes() == 1, "...and the first save writes it");
    }
    std::printf("ini_file: %d/%d checks passed\n", checks - failures, checks);
    return failures ? 1 : 0;
}
