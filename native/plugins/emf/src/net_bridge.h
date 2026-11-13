// SPDX-License-Identifier: MIT
#pragma once
#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace kemf {
bool net_client_install();
void net_client_stop();
void net_client_resume();
void net_client_clear();
bool net_client_line(std::string_view line, uint64_t speaker);
std::vector<std::string> net_client_take(size_t count);
void net_client_put_back(std::vector<std::string> lines);
void net_client_reset();      // another GameClient: the client link starts over, queued client callbacks go
void net_client_new_world();  // a new client world on the same connection: the link starts over, a held greeting stays

#ifdef KEMF_NET_CLIENT_TEST
// The production detours, with only their three engine functions replaced.
using NetFlushFn = void (*)(uint8_t *);
struct NetText {
    const char *ptr;
    size_t len;
};
using NetSendFn = uint8_t *(*)(uint8_t *, uint8_t *, uint8_t, const NetText *, uint32_t, uint32_t);
using NetAddFn = uint8_t *(*)(uint8_t *, uint8_t *, uint8_t, const NetText *, uint32_t, uint32_t, uint32_t, uint64_t,
                              const void *);
struct NetClientDetours {
    NetFlushFn flush;
    NetAddFn add;
};
NetClientDetours net_client_test_install(NetFlushFn flush, NetSendFn send, NetAddFn add);
#endif
} // namespace kemf
