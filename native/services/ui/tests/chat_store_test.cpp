// SPDX-License-Identifier: MIT
// Host test: chat history's file format and its writer thread (chat_store.h).
#include "../chat_store.h"

#include <cassert>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

using kui::chat_store::parse;
using kui::chat_store::Saver;
using kui::chat_store::serialize;

int main() {
    // Round trip, oldest first, UTF-8 kept.
    const std::vector<std::string> lines{"/help", "/say café", "/tp 1 2 3"};
    assert(parse(serialize(lines)) == lines);
    assert(serialize({}) == R"({"entries":[],"version":1})");
    assert(serialize({"/a"}) == R"({"entries":[{"server":null,"text":"/a"}],"version":1})");

    // Anything unexpected reads as what it holds that is usable, never throws.
    assert(parse("").empty());
    assert(parse("not json").empty());
    assert(parse("[1,2]").empty());
    assert(parse(R"({"version":2,"entries":[{"text":"/a"}]})").empty());
    assert(parse(R"({"version":"1","entries":[{"text":"/a"}]})").empty());
    assert(parse(R"({"version":1,"entries":{}})").empty());
    assert(parse(R"({"version":1,"entries":[{"text":5},"x",{"text":"/ok"},{}]})") == std::vector<std::string>{"/ok"});
    std::string big = R"({"version":1,"entries":[{"text":")" + std::string(kui::chat_store::kMaxFileBytes, 'a') + R"("}]})";
    assert(parse(big).empty());
    std::string many = R"({"version":1,"entries":[)";
    for (size_t i = 0; i < kui::chat_store::kMaxLines + 10; ++i) many += std::string(i ? "," : "") + R"({"text":"/x"})";
    many += "]}";
    assert(parse(many).size() == kui::chat_store::kMaxLines);

    // The writer: the caller never waits for a write; lists that arrive meanwhile collapse into the newest.
    std::mutex mu;
    std::condition_variable cv;
    bool release = false, writing = false;
    std::vector<std::string> written;
    auto* saver = new Saver([&](const std::string& text) {
        std::unique_lock lock(mu);
        writing = true;
        cv.notify_all();
        cv.wait(lock, [&] { return release; });
        written.push_back(text);
    });
    saver->request({"/one"});
    {
        std::unique_lock lock(mu);
        cv.wait(lock, [&] { return writing; });  // the first write is in progress and blocked
    }
    const auto t0 = std::chrono::steady_clock::now();
    saver->request({"/one", "/two"});
    saver->request({"/one", "/two", "/three"});
    assert(std::chrono::steady_clock::now() - t0 < std::chrono::milliseconds(500));  // did not wait for the disk
    {
        std::lock_guard lock(mu);
        release = true;
    }
    cv.notify_all();
    saver->stop();  // writes what is pending, then joins
    assert(written.size() == 2);
    assert(parse(written[0]) == std::vector<std::string>{"/one"});
    assert(parse(written[1]) == (std::vector<std::string>{"/one", "/two", "/three"}));
    assert(saver->writes() == 2);
    saver->request({"/late"});  // after stop: ignored, no new thread
    saver->stop();
    assert(written.size() == 2);
    delete saver;

    // stop() with nothing ever requested starts no thread and returns.
    Saver idle([](const std::string&) { assert(false); });
    idle.stop();
    std::puts("chat_store: all checks passed");
}
